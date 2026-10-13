using DumpTriage.Common.CentralCopy;
using DumpTriage.Common.Config;
using DumpTriage.Common.Idle;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Security;
using DumpTriage.Common.Upload;
using DumpTriageAgent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DumpTriageAgent.Upload;

/// <summary>
/// Periodically drains dumps sitting in the Queued state, gated by
/// BatchScheduler (see AgentPolicyConfig.Batching) so "submit immediately" and
/// "hold until a schedule/idle window" are just different answers to "is it
/// time yet", not different code paths. Approval (should this dump be sent at
/// all) already happened in DumpWatcherService — this only decides timing and
/// does the actual POST.
/// </summary>
public sealed class UploadWorkerService : BackgroundService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly AgentConfig _config;
    private readonly DumpQueueStore _queueStore;
    private readonly DumpTriageApiClient _apiClient;
    private readonly IIdleTimeProvider _idleTimeProvider;
    private readonly IApiKeyProtector _apiKeyProtector;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ILogger<UploadWorkerService> _logger;

    private DateTimeOffset? _lastScheduledFlushUtc;
    private DateTimeOffset _authBackoffUntilUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan AuthBackoff = TimeSpan.FromMinutes(10);

    public UploadWorkerService(
        AgentConfig config,
        DumpQueueStore queueStore,
        DumpTriageApiClient apiClient,
        IIdleTimeProvider idleTimeProvider,
        IApiKeyProtector apiKeyProtector,
        AgentRuntimeState runtimeState,
        ILogger<UploadWorkerService> logger)
    {
        _config = config;
        _queueStore = queueStore;
        _apiClient = apiClient;
        _idleTimeProvider = idleTimeProvider;
        _apiKeyProtector = apiKeyProtector;
        _runtimeState = runtimeState;
        _logger = logger;

        _runtimeState.ManualSubmitRequested += dumpId => _ = SubmitByIdAsync(dumpId, CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload worker tick failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        if (_runtimeState.IsPaused) return;
        if (DateTimeOffset.UtcNow < _authBackoffUntilUtc) return; // bad API key; don't hammer the server

        var batching = _config.Policy.Batching;
        var idle = _idleTimeProvider.GetIdleDuration();
        var shouldFlush = BatchScheduler.ShouldSubmitNow(batching, DateTimeOffset.Now, idle, _lastScheduledFlushUtc);
        _logger.LogDebug("Upload tick, shouldFlush={ShouldFlush}, batchMode={Mode}", shouldFlush, batching.Mode);
        if (!shouldFlush) return;

        var queued = await _queueStore.GetByStateAsync(DumpQueueState.Queued, stoppingToken);
        _logger.LogDebug("Queued dumps ready to submit: {Count}", queued.Count);
        foreach (var record in queued)
        {
            await SubmitAsync(record, stoppingToken);
            if (DateTimeOffset.UtcNow < _authBackoffUntilUtc) break; // key rejected; rest would fail too
        }

        if (batching.Mode == BatchMode.ScheduledTimeOfDay && queued.Count > 0)
        {
            _lastScheduledFlushUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task SubmitByIdAsync(string dumpId, CancellationToken stoppingToken)
    {
        var pendingApproval = await _queueStore.GetByStateAsync(DumpQueueState.PendingApproval, stoppingToken);
        var record = pendingApproval.FirstOrDefault(d => d.Id == dumpId);
        if (record is null)
        {
            _logger.LogWarning("Manual submit requested for unknown/non-pending dump {DumpId}", dumpId);
            return;
        }

        if (DateTimeOffset.UtcNow < _authBackoffUntilUtc)
        {
            // Same rule as the tick loop: the key was just rejected, so don't
            // hammer the server. The record stays PendingApproval and listed.
            _logger.LogWarning("Manual submit of {DumpId} skipped: uploads are paused after an authentication failure", dumpId);
            record.LastError = "Submit skipped: uploads are paused after an authentication failure; try again later.";
            await _queueStore.UpsertAsync(record, stoppingToken);
            return;
        }

        await SubmitAsync(record, stoppingToken, DumpQueueState.PendingApproval);
    }

    private async Task SubmitAsync(
        DumpRecord record,
        CancellationToken stoppingToken,
        DumpQueueState stateOnRetry = DumpQueueState.Queued)
    {
        record.State = DumpQueueState.Submitting;
        await _queueStore.UpsertAsync(record, stoppingToken);

        UploadResult result;
        try
        {
            var apiKey = _apiKeyProtector.Unprotect(_config.ProtectedApiKey
                ?? throw new InvalidOperationException("No API key configured — run the installer to set one."));
            var hostnameHash = _config.SendHostname ? Environment.MachineName : null;

            var uploadTask = _apiClient.UploadDumpAsync(record.FilePath, apiKey, hostnameHash, stoppingToken);
            var copyTask = MaybeCopyToCentralLocationAsync(record, stoppingToken);
            await Task.WhenAll(uploadTask, copyTask);
            result = uploadTask.Result;
        }
        catch (OperationCanceledException)
        {
            // Service stopping mid-upload: put it back so it isn't stranded in
            // Submitting (the token is cancelled, so persist without it).
            record.State = stateOnRetry;
            await _queueStore.UpsertAsync(record, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Anything thrown here (corrupted DPAPI blob, missing source file
            // because two dumps collided on the same content hash, etc.) must
            // not leave the record stuck in Submitting forever — nothing else
            // ever polls that state again.
            record.State = DumpQueueState.Failed;
            record.LastError = ex.Message;
            _logger.LogError(ex, "Submission of {Path} failed before an upload result was available", record.FilePath);
            await _queueStore.UpsertAsync(record, stoppingToken);
            return;
        }

        switch (result.Outcome)
        {
            case UploadOutcome.Success:
                record.State = DumpQueueState.Submitted;
                record.SubmittedAtUtc = DateTimeOffset.UtcNow;
                record.LastError = null;
                _logger.LogInformation("Submitted dump {Path}", record.FilePath);
                break;

            case UploadOutcome.AuthFailed:
                // Put the dump back where it came from (not Failed, no retry
                // burned): the key may simply be rotated or mistyped, and the
                // dump is still good. A manually submitted dump returns to
                // PendingApproval so the tray keeps listing it as waiting.
                record.State = stateOnRetry;
                _authBackoffUntilUtc = DateTimeOffset.UtcNow + AuthBackoff;
                record.LastError = $"Authentication failed ({(int?)result.StatusCode}): check the configured API key. {result.ErrorMessage}";
                _logger.LogError(
                    "Upload of {Path} refused ({Status}): API key is invalid or revoked; re-provision it with --set-api-key-stdin and restart the service; uploads paused for 10 minutes",
                    record.FilePath, result.StatusCode);
                break;

            case UploadOutcome.RejectedPermanently:
            case UploadOutcome.QuotaExceeded:
                record.State = DumpQueueState.Failed;
                record.LastError = result.ErrorMessage;
                _logger.LogWarning(
                    "Dump {Path} rejected permanently ({Status}): {Error}",
                    record.FilePath, result.StatusCode, result.ErrorMessage);
                break;

            case UploadOutcome.TransientFailure:
                record.RetryCount++;
                record.LastError = result.ErrorMessage;
                if (record.RetryCount >= MaxRetries)
                {
                    record.State = DumpQueueState.Failed;
                    _logger.LogError(
                        "Dump {Path} failed permanently after {Retries} retries: {Error}",
                        record.FilePath, record.RetryCount, result.ErrorMessage);
                }
                else
                {
                    record.State = stateOnRetry; // retry on a later tick (manual submits stay pending approval)
                    _logger.LogWarning(
                        "Transient failure submitting {Path} (attempt {Retry}/{Max}): {Error}",
                        record.FilePath, record.RetryCount, MaxRetries, result.ErrorMessage);
                }
                break;
        }

        await _queueStore.UpsertAsync(record, stoppingToken);
    }

    private async Task MaybeCopyToCentralLocationAsync(DumpRecord record, CancellationToken stoppingToken)
    {
        if (record.CentralCopiedAtUtc is not null) return; // already archived on a prior attempt

        var (success, error) = await CentralCopyService.TryCopyAsync(
            record, _config.CentralCopy, Environment.MachineName, stoppingToken);

        if (success)
        {
            record.CentralCopiedAtUtc = DateTimeOffset.UtcNow;
            record.CentralCopyError = null;
            _logger.LogInformation("Archived dump {Path} to central location", record.FilePath);
        }
        else if (error is not null)
        {
            record.CentralCopyError = error;
            _logger.LogWarning("Failed to archive dump {Path} to central location: {Error}", record.FilePath, error);
        }
    }
}
