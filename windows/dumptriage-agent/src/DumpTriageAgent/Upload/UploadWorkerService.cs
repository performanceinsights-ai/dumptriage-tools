using DumpTriage.Common.CentralCopy;
using DumpTriage.Common.Config;
using DumpTriage.Common.Detection;
using DumpTriage.Common.Idle;
using DumpTriage.Common.Ipc;
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const string LastFlushKey = "last_scheduled_flush_utc";

    private readonly AgentConfig _config;
    private readonly DumpQueueStore _queueStore;
    private readonly DumpTriageApiClient _apiClient;
    private readonly IIdleTimeProvider _idleTimeProvider;
    private readonly IApiKeyProtector _apiKeyProtector;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ILogger<UploadWorkerService> _logger;
    private readonly TimeProvider _time;
    private readonly ITrayChannel _tray;

    // In memory: a new key needs a service restart (the config is read at start), which clears it.
    private DateTimeOffset _authBackoffUntilUtc = DateTimeOffset.MinValue;
    private int _authRefusalsInARow;

    public UploadWorkerService(
        AgentConfig config,
        DumpQueueStore queueStore,
        DumpTriageApiClient apiClient,
        IIdleTimeProvider idleTimeProvider,
        IApiKeyProtector apiKeyProtector,
        AgentRuntimeState runtimeState,
        ILogger<UploadWorkerService> logger,
        ITrayChannel? tray = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _tray = tray ?? new NullTrayChannel();
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

    internal async Task TickAsync(CancellationToken stoppingToken)
    {
        if (_runtimeState.IsPaused) return;
        var now = _time.GetUtcNow();
        var batching = _config.Policy.Batching;

        // Kept in the queue, so a restart neither forgets a slot nor uploads at once (#9).
        DateTimeOffset? lastFlush = await _queueStore.GetMetaAsync(LastFlushKey, stoppingToken) is { } stored
            ? DateTimeOffset.Parse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : null;
        if (batching.Mode == BatchMode.ScheduledTimeOfDay && lastFlush is null)
        {
            await _queueStore.SetMetaAsync(LastFlushKey, now.ToString("O"), stoppingToken); // first run: wait for a slot
            return;
        }

        var idle = _idleTimeProvider.GetIdleDuration();
        var shouldFlush = BatchScheduler.ShouldSubmitNow(batching, now.ToLocalTime(), idle, lastFlush);
        _logger.LogDebug("Upload tick, shouldFlush={ShouldFlush}, batchMode={Mode}", shouldFlush, batching.Mode);
        if (now < _authBackoffUntilUtc) return; // the key was refused; don't send dumps to be refused again

        // A dump whose upload already started in a slot and failed transiently is retried on its own
        // backoff, not held for the next slot (a day away for a nightly one).
        var due = (await _queueStore.GetByStateAsync(DumpQueueState.Queued, stoppingToken))
            .Where(r => shouldFlush || (batching.Mode == BatchMode.ScheduledTimeOfDay && r.RetryCount > 0))
            .Where(r => r.NextAttemptUtc is null || r.NextAttemptUtc <= now)
            .ToList();
        if (!shouldFlush && due.Count == 0) return;
        _logger.LogDebug("Queued dumps due for upload: {Count}", due.Count);
        var refused = false;
        foreach (var record in due)
        {
            await SubmitAsync(record, stoppingToken);
            if (_time.GetUtcNow() < _authBackoffUntilUtc)
            {
                refused = true;
                break; // key rejected; the rest would be refused too
            }
        }

        // The slot is used, even if nothing was due; not when the key was refused (or, above, during
        // a pause), so it is taken once that clears.
        if (batching.Mode == BatchMode.ScheduledTimeOfDay && shouldFlush && !refused)
        {
            await _queueStore.SetMetaAsync(LastFlushKey, now.ToString("O"), stoppingToken);
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

        if (_time.GetUtcNow() < _authBackoffUntilUtc)
        {
            // Same rule as the tick loop: the key was just rejected, so don't
            // hammer the server. The record stays PendingApproval and listed.
            _logger.LogWarning("Manual submit of {DumpId} skipped: uploads are paused after an authentication failure", dumpId);
            record.LastError = "Submit skipped: uploads are paused after an authentication failure; try again later.";
            await _queueStore.UpsertAsync(record, stoppingToken);
            await ListAgainInTrayAsync(record, stoppingToken);
            return;
        }

        await SubmitAsync(record, stoppingToken, DumpQueueState.PendingApproval);
        if (record.State == DumpQueueState.PendingApproval)
        {
            await ListAgainInTrayAsync(record, stoppingToken);
        }
    }

    /// <summary>
    /// The tray drops a NotifyOnly dump from its "Submit dump" menu when it is clicked; one that
    /// wasn't sent after all (paused, or a transient failure) goes back on the list (#7).
    /// </summary>
    private Task ListAgainInTrayAsync(DumpRecord record, CancellationToken stoppingToken) =>
        _tray.SendAsync(new IpcMessage
        {
            Type = IpcMessageType.AskApproval,
            DumpId = record.Id,
            DumpFileName = Path.GetFileName(record.FilePath),
            DumpSizeBytes = record.SizeBytes,
            DumpType = record.DumpType,
        }, stoppingToken);

    private async Task SubmitAsync(
        DumpRecord record,
        CancellationToken stoppingToken,
        DumpQueueState stateOnRetry = DumpQueueState.Queued)
    {
        // The file must still be the dump this record describes: one overwritten in place after
        // detection (a reused dump name, while an approval was pending) must not go up under this id.
        // The watcher sees the change and records the new dump itself.
        bool changed;
        try
        {
            var info = new FileInfo(record.FilePath);
            changed = info.Exists && (info.Length != record.SizeBytes
                // A newer write time alone may be a touch (a backup or scan tool): the hash decides.
                || (info.LastWriteTimeUtc > record.DetectedAtUtc.UtcDateTime
                    && (record.Sha256 is null || await DumpHasher.ComputeSha256Async(record.FilePath, stoppingToken) != record.Sha256)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            changed = true; // locked for writing: a new dump is being written over it
        }
        if (changed)
        {
            record.State = DumpQueueState.Skipped;
            record.LastError = "Dump file changed after it was detected; the new dump is handled separately";
            await _queueStore.UpsertAsync(record, stoppingToken);
            _logger.LogWarning("Not uploading {Path} under {Id}: the file changed after it was detected", record.FilePath, record.Id);
            return;
        }

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
                record.SubmittedAtUtc = _time.GetUtcNow();
                record.LastError = null;
                record.NextAttemptUtc = null;
                _authRefusalsInARow = 0;
                _logger.LogInformation("Submitted dump {Path}", record.FilePath);
                break;

            case UploadOutcome.AuthFailed:
                // Put the dump back where it came from (not Failed, no retry
                // burned): the key may simply be rotated or mistyped, and the
                // dump is still good. A manually submitted dump returns to
                // PendingApproval so the tray keeps listing it as waiting.
                record.State = stateOnRetry;
                var pause = RetryPolicy.AuthPause(++_authRefusalsInARow);
                _authBackoffUntilUtc = _time.GetUtcNow() + pause;
                record.LastError = $"Authentication failed ({(int?)result.StatusCode}): check the configured API key. {result.ErrorMessage}";
                _logger.LogError(
                    "Upload of {Path} refused ({Status}): API key is invalid or revoked; re-provision it with --set-api-key-stdin and restart the service; uploads paused for {Minutes} minutes",
                    record.FilePath, result.StatusCode, pause.TotalMinutes);
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
                if (record.RetryCount >= RetryPolicy.MaxAttempts)
                {
                    record.State = DumpQueueState.Failed;
                    _logger.LogError(
                        "Dump {Path} failed permanently after {Retries} retries: {Error}",
                        record.FilePath, record.RetryCount, result.ErrorMessage);
                }
                else
                {
                    record.State = stateOnRetry; // manual submits stay pending approval
                    var delay = RetryPolicy.TransientDelay(record.RetryCount);
                    record.NextAttemptUtc = _time.GetUtcNow() + delay;
                    _logger.LogWarning(
                        "Transient failure submitting {Path} (attempt {Retry}/{Max}), retrying in {Delay}: {Error}",
                        record.FilePath, record.RetryCount, RetryPolicy.MaxAttempts, delay, result.ErrorMessage);
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
