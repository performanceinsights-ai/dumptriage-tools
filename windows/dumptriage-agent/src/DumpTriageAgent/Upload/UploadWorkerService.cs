using System.Threading.Channels;
using DumpTriage.Common.CentralCopy;
using DumpTriage.Common.Config;
using DumpTriage.Common.Detection;
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    // Meta key: when the last ScheduledTimeOfDay slot opened. Kept in the queue, so a restart
    // neither forgets a slot nor uploads at once (#9).
    internal const string LastFlushKey = "last_scheduled_flush_utc";
    // After a transient failure a dump waits RetryBackoffBase, doubling per
    // failure up to RetryBackoffMax, and gives up GiveUpAfter its first
    // failure (website#1488): a short outage no longer fails the queue for good
    // (it was 5 tries, one per 30 s poll), and a long one still ends.
    internal static readonly TimeSpan RetryBackoffBase = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan RetryBackoffMax = TimeSpan.FromHours(6);
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);
    // How long the whole queue waits after an answer that no dump can get
    // past, before one dump tries again; each try may re-send a whole dump,
    // so not every poll. Terms (website#1313) are a click in the portal away; a key
    // or a closed account needs someone to act; a monthly quota resets.
    internal static readonly TimeSpan TermsHold = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan CredentialsHold = TimeSpan.FromHours(1);
    internal static readonly TimeSpan QuotaHold = TimeSpan.FromHours(6);
    internal const string FileChangedError =
        "the dump file changed after it was detected; it is detected again as a new dump";
    internal const string NoUsableKeyReason = "Uploads paused: no usable API key on this machine; set one with the installer";

    private readonly AgentConfig _config;
    private readonly DumpQueueStore _queueStore;
    private readonly DumpTriageApiClient _apiClient;
    private readonly IIdleTimeProvider _idleTimeProvider;
    private readonly IApiKeyProtector _apiKeyProtector;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ILogger<UploadWorkerService> _logger;

    // When the last ScheduledTimeOfDay slot opened, and the ids of the dumps
    // it opened with that are still to go (null once none is left).
    private DateTimeOffset? _lastScheduledFlushUtc;
    private HashSet<string>? _openSlotDumpIds;

    /// <summary>The local clock the batching schedule is read on; tests move it to the next slot.</summary>
    internal Func<DateTimeOffset> ScheduleClock { get; set; } = () => DateTimeOffset.Now;

    // Requests from the tray, handled on the worker's loop: a dump id to
    // submit now, or RetryFailedRequest.
    private readonly Channel<object> _trayRequests = Channel.CreateUnbounded<object>();
    private sealed record RetryFailedRequest;

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

        // Handled on the worker's own loop, not fire-and-forget from the pipe
        // thread (website#1492): that path's exceptions were never observed, it raced
        // the ticks, and it ignored the pause, the terms hold and shutdown.
        _runtimeState.ManualSubmitRequested += dumpId => _trayRequests.Writer.TryWrite(dumpId);
        _runtimeState.RetryFailedRequested += () => _trayRequests.Writer.TryWrite(new RetryFailedRequest());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverInterruptedAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not requeue uploads interrupted by the last shutdown");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTrayRequestsAsync(stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Upload worker tick failed");
            }

            await WaitForNextTickAsync(stoppingToken);
        }
    }

    /// <summary>
    /// A record still in Submitting at start was cut off by a crash, reboot or
    /// service stop mid-upload (website#1485); nothing else would ever send it.
    /// </summary>
    internal async Task RecoverInterruptedAsync(CancellationToken stoppingToken)
    {
        var requeued = await _queueStore.RequeueInterruptedAsync(stoppingToken);
        if (requeued > 0)
        {
            _logger.LogInformation("Requeued {Count} dump(s) whose upload was interrupted", requeued);
        }
    }

    /// <summary>The poll interval, cut short by a request from the tray.</summary>
    private async Task WaitForNextTickAsync(CancellationToken stoppingToken)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(PollInterval, wake.Token);
        var trayRequest = _trayRequests.Reader.WaitToReadAsync(wake.Token).AsTask();
        await Task.WhenAny(delay, trayRequest);
        wake.Cancel();
        stoppingToken.ThrowIfCancellationRequested();
    }

    internal async Task ProcessTrayRequestsAsync(CancellationToken stoppingToken)
    {
        while (_trayRequests.Reader.TryRead(out var request))
        {
            switch (request)
            {
                case string dumpId:
                    await SubmitByIdAsync(dumpId, stoppingToken);
                    break;
                case RetryFailedRequest:
                    // Nothing else ever takes a dump out of Failed (website#1488).
                    var requeued = await _queueStore.RequeueFailedAsync(stoppingToken);
                    _logger.LogInformation("Retry requested from the tray: requeued {Count} failed dump(s)", requeued);
                    break;
            }
        }
    }

    internal async Task TickAsync(CancellationToken stoppingToken)
    {
        if (_runtimeState.IsPaused) return;
        if (_runtimeState.ActiveUploadHold(DateTimeOffset.UtcNow) is not null) return;

        var batching = _config.Policy.Batching;
        if (batching.Mode == BatchMode.ScheduledTimeOfDay && _lastScheduledFlushUtc is null)
        {
            if (await _queueStore.GetMetaAsync(LastFlushKey, stoppingToken) is { } stored)
            {
                _lastScheduledFlushUtc = DateTimeOffset.Parse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            else
            {
                // First run under a schedule: wait for the first slot rather than uploading at once (#9).
                await SaveSlotAsync(ScheduleClock().ToUniversalTime(), stoppingToken);
                return;
            }
        }
        var idle = _idleTimeProvider.GetIdleDuration();
        var scheduleNow = ScheduleClock();
        var shouldFlush = BatchScheduler.ShouldSubmitNow(batching, scheduleNow, idle, _lastScheduledFlushUtc);
        var scheduled = batching.Mode == BatchMode.ScheduledTimeOfDay;
        var continuingSlot = scheduled && !shouldFlush && _openSlotDumpIds is not null;
        _logger.LogDebug(
            "Upload tick, shouldFlush={ShouldFlush}, continuingSlot={ContinuingSlot}, batchMode={Mode}",
            shouldFlush, continuingSlot, batching.Mode);
        if (!shouldFlush && !continuingSlot) return;

        var queued = await _queueStore.GetByStateAsync(DumpQueueState.Queued, stoppingToken);
        if (continuingSlot)
        {
            // Only the dumps the open slot started with (website#1567): one queued
            // since waits for the next slot, so a dump stuck in its backoff
            // can't turn ScheduledTimeOfDay into Immediate.
            queued = queued.Where(r => _openSlotDumpIds!.Contains(r.Id)).ToList();
        }
        else if (scheduled)
        {
            // A slot opens: it is used up from here on, also with nothing
            // queued, so a dump queued later that day waits for the next slot
            // instead of going at once (#9). A later tick only continues the
            // dumps queued now. An earlier slot still open is replaced, so a
            // failing dump never holds one past the next slot time; it is
            // simply one of this slot's dumps.
            await SaveSlotAsync(scheduleNow.ToUniversalTime(), stoppingToken);
            if (queued.Count == 0)
            {
                _openSlotDumpIds = null;
                return;
            }
            _openSlotDumpIds = queued.Select(r => r.Id).ToHashSet();
        }
        var now = DateTimeOffset.UtcNow;
        // Dumps first: a trace the server refuses (traces off there, 403) ends the pass like any transient failure, and
        // must not hold back the dumps queued after it.
        var due = queued.Where(r => r.NextAttemptAtUtc is null || r.NextAttemptAtUtc <= now).OrderBy(r => r.DumpType == DumpType.Trace).ToList();
        _logger.LogDebug("Queued dumps: {Count}, due now: {Due}", queued.Count, due.Count);
        foreach (var record in due)
        {
            if (_runtimeState.ActiveUploadHold(DateTimeOffset.UtcNow) is not null) break;
            var outcome = await SubmitAsync(record, stoppingToken);
            if (outcome == UploadOutcome.TransientFailure)
            {
                // The server or the network is in trouble: the next dump
                // would most likely fail the same way, and each try may
                // re-send a whole dump. The rest wait for the next tick.
                break;
            }
        }

        // A slot's dump still left in the queue (SubmitAsync updates each
        // record it tries in place), because the pass was cut short by an
        // outage or a hold (website#1516 review) or because it is in its retry
        // backoff and so not due yet (website#1567), keeps the slot open for it: it
        // goes on the first tick it is due instead of at the next slot. Its
        // own backoff still paces those retries. The slot closes once none of
        // its dumps is left, or when the next slot opens.
        if (scheduled)
        {
            var left = queued.Where(r => r.State == DumpQueueState.Queued).Select(r => r.Id).ToHashSet();
            _openSlotDumpIds = left.Count > 0 ? left : null;
        }
    }

    private async Task SaveSlotAsync(DateTimeOffset slotUtc, CancellationToken stoppingToken)
    {
        _lastScheduledFlushUtc = slotUtc;
        await _queueStore.SetMetaAsync(LastFlushKey, slotUtc.ToString("O"), stoppingToken);
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

        if (_runtimeState.IsPaused || _runtimeState.ActiveUploadHold(DateTimeOffset.UtcNow) is not null)
        {
            // Approved, so it joins the queue and goes when uploads resume.
            record.State = DumpQueueState.Queued;
            await _queueStore.UpsertAsync(record, stoppingToken);
            _logger.LogInformation("Manual submit of {Path} queued: uploads are paused or held", record.FilePath);
            return;
        }

        await SubmitAsync(record, stoppingToken);
    }

    /// <summary>Exponential: RetryBackoffBase after the first failure, doubling, at most RetryBackoffMax.</summary>
    internal static TimeSpan RetryDelay(int retryCount)
    {
        var exponent = Math.Clamp(retryCount - 1, 0, 30);
        var delay = RetryBackoffBase * Math.Pow(2, exponent);
        return delay < RetryBackoffMax ? delay : RetryBackoffMax;
    }

    /// <summary>Uploads one record and stores the outcome; returns it.</summary>
    private async Task<UploadOutcome> SubmitAsync(DumpRecord record, CancellationToken stoppingToken)
    {
        // Before Submitting, so a shutdown mid-hash leaves the record as it was.
        if (await CheckFileUnchangedAsync(record, stoppingToken) is { } notSendable)
        {
            return await RecordResultAsync(record, notSendable);
        }

        record.State = DumpQueueState.Submitting;
        await _queueStore.UpsertAsync(record, stoppingToken);

        string apiKey;
        try
        {
            apiKey = _apiKeyProtector.Unprotect(_config.ProtectedApiKey
                ?? throw new InvalidOperationException("No API key configured — run the installer to set one."));
        }
        catch (Exception ex)
        {
            // No key, or a DPAPI blob this machine can't open: no dump can
            // upload, and it's not this dump's fault (website#1489).
            record.State = DumpQueueState.Queued;
            record.LastError = ex.Message;
            _runtimeState.HoldUploads(NoUsableKeyReason, CredentialsHold, DateTimeOffset.UtcNow);
            _logger.LogError(ex, "Uploads held: no usable API key");
            await _queueStore.UpsertAsync(record, stoppingToken);
            return UploadOutcome.CredentialsRejected;
        }

        // The archival copy runs alongside the upload but its outcome is its
        // own: a failed copy used to fail an upload the server had accepted
        // (website#1490). MaybeCopyToCentralLocationAsync only throws on shutdown.
        var copyTask = MaybeCopyToCentralLocationAsync(record, stoppingToken);
        UploadResult result;
        try
        {
            var hostnameHash = _config.SendHostname ? Environment.MachineName : null;
            result = record.DumpType == DumpType.Trace
                ? await _apiClient.UploadTraceAsync(
                    record.FilePath, record.Manifest ?? "{}", apiKey, hostnameHash, record.Sha256 ?? record.Id, stoppingToken)
                : await _apiClient.UploadDumpAsync(
                    record.FilePath, apiKey, hostnameHash, record.Sha256 ?? record.Id, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Must not leave the record stuck in Submitting. Not proof the
            // dump is bad either, so it is retried like a transient failure
            // rather than failed for good (website#1489).
            _logger.LogError(ex, "Submission of {Path} failed before an upload result was available", record.FilePath);
            result = new UploadResult(UploadOutcome.FileUnavailable, null, ex.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service stopping mid-upload (website#1485): back to the queue, without
            // counting a retry. CancellationToken.None: the stopping token is
            // already cancelled, and this write must still happen.
            await AwaitCopyAsync(copyTask);
            record.State = DumpQueueState.Queued;
            await _queueStore.UpsertAsync(record, CancellationToken.None);
            _logger.LogInformation("Upload of {Path} interrupted by shutdown; requeued", record.FilePath);
            throw;
        }

        // The upload's answer is recorded even if shutdown cuts the copy off.
        await AwaitCopyAsync(copyTask);

        return await RecordResultAsync(record, result);
    }

    /// <summary>
    /// The upload may come hours after the dump was hashed. If the file's size
    /// or last-write time moved since, it is hashed again: same bytes, it goes
    /// up; different bytes, this record is done and the watcher detects the
    /// file again as a new dump (website#1493). Null when the file is fine to send.
    /// </summary>
    internal async Task<UploadResult?> CheckFileUnchangedAsync(DumpRecord record, CancellationToken stoppingToken)
    {
        try
        {
            var now = FileIdentity.Read(record.FilePath);
            if (now.SizeBytes == record.SizeBytes
                && (record.FileLastWriteUtc is null || now.LastWriteUtc == record.FileLastWriteUtc))
            {
                return null;
            }

            var sha256 = await DumpHasher.ComputeSha256Async(record.FilePath, stoppingToken);
            if (sha256 == record.Id)
            {
                record.SizeBytes = now.SizeBytes;
                record.FileLastWriteUtc = now.LastWriteUtc;
                return null;
            }

            return new UploadResult(UploadOutcome.FileChanged, null, FileChangedError);
        }
        catch (FileNotFoundException ex)
        {
            return new UploadResult(UploadOutcome.FileMissing, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UploadResult(UploadOutcome.FileUnavailable, null, ex.Message);
        }
    }

    private async Task<UploadOutcome> RecordResultAsync(DumpRecord record, UploadResult result)
    {
        switch (result.Outcome)
        {
            case UploadOutcome.Success:
                _runtimeState.ClearUploadHold();
                record.State = DumpQueueState.Submitted;
                record.SubmittedAtUtc = DateTimeOffset.UtcNow;
                record.LastError = null;
                record.NextAttemptAtUtc = null;
                record.FirstFailedAtUtc = null;
                _logger.LogInformation("Submitted {Kind} {Path}", record.DumpType == DumpType.Trace ? "trace" : "dump", record.FilePath);
                if (record.DumpType == DumpType.Trace)
                {
                    // The agent made this file; once the service has it, nothing keeps it here (website#1999).
                    try { File.Delete(record.FilePath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not delete the sent trace {Path}", record.FilePath);
                    }
                }
                break;

            case UploadOutcome.FileChanged:
                // Superseded, not Failed: the file's new contents are detected
                // again as a dump of their own, and this record has nothing
                // left to retry. As Failed it sat in the tray's "Retry failed
                // uploads" count for good, and each retry re-hashed the whole
                // file only to end the same way (website#1516 review).
                record.State = DumpQueueState.Superseded;
                record.LastError = result.ErrorMessage;
                record.NextAttemptAtUtc = null;
                _runtimeState.RequestRescan(record.FilePath);
                _logger.LogWarning("Dump {Path} changed after it was detected; detecting it again", record.FilePath);
                break;

            case UploadOutcome.RejectedPermanently:
            case UploadOutcome.FileMissing:
                record.State = DumpQueueState.Failed;
                record.LastError = result.ErrorMessage;
                _logger.LogWarning(
                    "Dump {Path} rejected permanently ({Status}): {Error}",
                    record.FilePath, result.StatusCode, result.ErrorMessage);
                break;

            case UploadOutcome.AwaitingTerms:
            case UploadOutcome.CredentialsRejected:
            case UploadOutcome.AccountClosed:
            case UploadOutcome.QuotaExceeded:
                // Not this dump's fault, and every other dump would get the
                // same answer: hold the whole queue, spend no retry (website#1487).
                var (reason, duration) = QueueHoldFor(result.Outcome);
                record.State = DumpQueueState.Queued;
                record.LastError = result.ErrorMessage;
                _runtimeState.HoldUploads(reason, duration, DateTimeOffset.UtcNow);
                _logger.LogWarning(
                    "Uploads held for {Duration}: {Reason} ({Status}: {Error})",
                    duration, reason, result.StatusCode, result.ErrorMessage);
                break;

            case UploadOutcome.TransientFailure:
            case UploadOutcome.FileUnavailable:
                var now = DateTimeOffset.UtcNow;
                record.RetryCount++;
                record.LastError = result.ErrorMessage;
                record.FirstFailedAtUtc ??= now;
                if (now - record.FirstFailedAtUtc >= GiveUpAfter)
                {
                    record.State = DumpQueueState.Failed;
                    record.NextAttemptAtUtc = null;
                    _logger.LogError(
                        "Dump {Path} failed after {Retries} attempts since {Since}: {Error}",
                        record.FilePath, record.RetryCount, record.FirstFailedAtUtc, result.ErrorMessage);
                }
                else
                {
                    record.State = DumpQueueState.Queued;
                    record.NextAttemptAtUtc = now + RetryDelay(record.RetryCount);
                    _logger.LogWarning(
                        "Transient failure submitting {Path} (attempt {Retry}, next at {Next}): {Error}",
                        record.FilePath, record.RetryCount, record.NextAttemptAtUtc, result.ErrorMessage);
                }
                break;
        }

        await _queueStore.UpsertAsync(record, CancellationToken.None);
        return result.Outcome;
    }

    private static async Task AwaitCopyAsync(Task copyTask)
    {
        try
        {
            await copyTask;
        }
        catch (OperationCanceledException)
        {
            // Cut off by shutdown; TryCopyAsync removed its partial file.
        }
    }

    /// <summary>The tray text and hold length for an answer that holds the whole queue.</summary>
    internal static (string Reason, TimeSpan Duration) QueueHoldFor(UploadOutcome outcome) => outcome switch
    {
        UploadOutcome.AwaitingTerms => ("Uploads held: accept the Terms of Service in the DumpTriage portal", TermsHold),
        UploadOutcome.CredentialsRejected => ("Uploads paused: the API key was rejected; set a current key with the installer", CredentialsHold),
        UploadOutcome.AccountClosed => ("Uploads paused: this DumpTriage account has been closed", CredentialsHold),
        UploadOutcome.QuotaExceeded => ("Uploads held: the account's monthly upload quota is used up", QuotaHold),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not a queue-wide hold"),
    };

    private async Task MaybeCopyToCentralLocationAsync(DumpRecord record, CancellationToken stoppingToken)
    {
        if (record.DumpType == DumpType.Trace) return; // the archival copy is for dumps; a trace is deleted once it is sent
        if (record.CentralCopiedAtUtc is not null) return; // already archived on a prior attempt

        bool success;
        string? error;
        try
        {
            (success, error) = await CentralCopyService.TryCopyAsync(
                record, _config.CentralCopy, Environment.MachineName, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            (success, error) = (false, ex.Message);
        }

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
