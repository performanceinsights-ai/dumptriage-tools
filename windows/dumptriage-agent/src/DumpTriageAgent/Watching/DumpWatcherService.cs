using System.Threading.Channels;
using DumpTriage.Common.Config;
using DumpTriage.Common.Detection;
using DumpTriage.Common.Ipc;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriageAgent.Ipc;
using DumpTriageAgent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DumpTriageAgent.Watching;

/// <summary>
/// Watches the configured directories for new dump files, waits for each one
/// to finish being written, classifies it, records it in the durable queue,
/// and runs it through the policy engine. Detection and policy evaluation are
/// deliberately kept separate from submission (UploadWorkerService) so a
/// crashed/restarted service picks up exactly where it left off from the
/// queue, not from FileSystemWatcher replaying events it never saw.
/// </summary>
public sealed class DumpWatcherService : BackgroundService
{
    private static readonly TimeSpan StabilityPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StabilityQuietPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StabilityTimeout = TimeSpan.FromMinutes(30); // kernel dumps can take a while

    private readonly AgentConfig _config;
    private readonly DumpQueueStore _queueStore;
    private readonly ApprovalCoordinator _approvalCoordinator;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ITrayChannel _pipeServer;
    private readonly ILogger<DumpWatcherService> _logger;

    private readonly Channel<string> _candidatePaths = Channel.CreateUnbounded<string>();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _missingPaths = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _inFlight = new();

    // Files older than the agent's first start are not picked up by a folder scan (#5): a fresh
    // install mustn't upload years of old minidumps. Set from the queue in ExecuteAsync.
    private DateTimeOffset _watchingSinceUtc = DateTimeOffset.MaxValue;
    private const string WatchingSinceKey = "watching_since_utc";

    // (path, size, mtime) whose content is already tracked under another path: not rehashed again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), bool> _knownDuplicates = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _loggedOld = new();
    private CancellationToken _stoppingToken;
    private int _retryLoopRunning;

    public DumpWatcherService(
        AgentConfig config,
        DumpQueueStore queueStore,
        ApprovalCoordinator approvalCoordinator,
        AgentRuntimeState runtimeState,
        ITrayChannel pipeServer,
        ILogger<DumpWatcherService> logger)
    {
        _config = config;
        _queueStore = queueStore;
        _approvalCoordinator = approvalCoordinator;
        _runtimeState = runtimeState;
        _pipeServer = pipeServer;
        _logger = logger;

        _pipeServer.TrayConnected += () => _ = ResendPendingApprovalsAsync();
    }

    /// <summary>
    /// A dump can finish detection and be marked PendingApproval before any
    /// tray is connected to receive the AskApproval prompt (confirmed live:
    /// right after a fresh service start, or any time the tray isn't running
    /// yet) — SendAsync silently drops messages with nobody listening, so
    /// without this the prompt is lost forever and the dump just times out
    /// to its default action with the user never having seen it. Re-sends
    /// on every (re)connect, not just the first, since the tray itself may
    /// restart independently of the service.
    /// </summary>
    private async Task ResendPendingApprovalsAsync()
    {
        try
        {
            var pending = await _queueStore.GetByStateAsync(DumpQueueState.PendingApproval, CancellationToken.None);
            var activeAskIds = _approvalCoordinator.GetPendingDumpIds();
            foreach (var record in pending)
            {
                await _pipeServer.SendAsync(new IpcMessage
                {
                    Type = IpcMessageType.AskApproval,
                    DumpId = record.Id,
                    DumpFileName = Path.GetFileName(record.FilePath),
                    DumpSizeBytes = record.SizeBytes,
                    DumpType = record.DumpType,
                    // Only set for a dump still mid-timeout in ApprovalCoordinator
                    // (the AskFirst path), and to the time that wait has left, not
                    // the full timeout (#12) — a ManualOnly one never registered
                    // there and has no timeout to report.
                    AskTimeoutSeconds = activeAskIds.Contains(record.Id) ? _approvalCoordinator.SecondsLeft(record.Id) : null,
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-send pending approvals to newly-connected tray");
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var configuredPath in _config.WatchedPaths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            if (!TryAttachWatcher(expanded))
            {
                _logger.LogWarning("Watched path {Path} does not exist yet, will retry", expanded);
                lock (_missingPaths) { _missingPaths.Add(expanded); }
            }
        }

        return base.StartAsync(cancellationToken);
    }

    private bool TryAttachWatcher(string expanded)
    {
        if (!Directory.Exists(expanded)) return false;

        // Non-recursive: WER's CrashDumps and Minidump folders are flat,
        // and %WINDIR% is watched only for MEMORY.DMP appearing directly
        // in it, not its entire subtree.
        var watcher = new FileSystemWatcher(expanded)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        foreach (var filter in _config.WatchedFilters)
        {
            watcher.Filters.Add(filter);
        }
        watcher.Created += (_, e) => { _logger.LogDebug("FileSystemWatcher Created event for {Path}", e.FullPath); _candidatePaths.Writer.TryWrite(e.FullPath); };
        watcher.Renamed += (_, e) => { _logger.LogDebug("FileSystemWatcher Renamed event for {Path}", e.FullPath); _candidatePaths.Writer.TryWrite(e.FullPath); };
        // MEMORY.DMP is overwritten in place by the next bugcheck: that is a Changed, not a Created (#5).
        // IsAlreadyRecordedAsync makes the many Changed events of one write cheap (size + mtime, no rehash).
        watcher.Changed += (_, e) => _candidatePaths.Writer.TryWrite(e.FullPath);
        watcher.Error += (_, e) =>
        {
            if (!Directory.Exists(expanded))
            {
                // The folder was deleted (a cleanup tool on %WINDIR%\Minidump): this watcher is dead.
                // Wait for the folder to come back, as for one missing at start.
                _logger.LogWarning("Watched path {Path} was removed; will watch it again once it exists", expanded);
                lock (_watchers) { _watchers.Remove(watcher); }
                watcher.Dispose();
                lock (_missingPaths) { if (!_missingPaths.Contains(expanded)) _missingPaths.Add(expanded); }
                StartRetryLoop();
                return;
            }

            // Usually a buffer overflow: events were lost, so look at everything again.
            _logger.LogError(e.GetException(), "FileSystemWatcher error for {Path}; rescanning it", expanded);
            EnqueueExisting(expanded);
        };
        watcher.EnableRaisingEvents = true;

        lock (_watchers) { _watchers.Add(watcher); }
        _logger.LogInformation("Watching {Path} for dump files (filters: {Filters}, raising: {Raising})", expanded, string.Join(",", watcher.Filters), watcher.EnableRaisingEvents);

        return true;
    }

    /// <summary>
    /// Queues the dumps already in a folder: a dump written while the service wasn't running
    /// (MEMORY.DMP during boot, a crash during an upgrade) raises no event (#5). Only files written
    /// since the agent first started: a fresh install leaves older dumps alone. Known files are
    /// skipped cheaply (size + mtime).
    /// </summary>
    private void EnqueueExisting(string path)
    {
        try
        {
            foreach (var filter in _config.WatchedFilters)
            {
                foreach (var file in Directory.EnumerateFiles(path, filter))
                {
                    // The later of write and creation time: a dump copied in on purpose keeps its old
                    // write time but gets a new creation time.
                    var latest = File.GetLastWriteTimeUtc(file) > File.GetCreationTimeUtc(file)
                        ? File.GetLastWriteTimeUtc(file) : File.GetCreationTimeUtc(file);
                    if (latest >= _watchingSinceUtc.UtcDateTime)
                    {
                        _candidatePaths.Writer.TryWrite(file);
                    }
                    else if (_loggedOld.TryAdd(file, true))
                    {
                        _logger.LogInformation("Ignoring {Path}: older than this agent's first start", file);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not scan {Path} for existing dumps", path);
        }
    }

    /// <summary>
    /// A directory like /var/crash may not exist until the first crash. Keep
    /// retrying; attaching picks up files that landed before the watcher did
    /// (ProcessCandidateAsync dedups).
    /// </summary>
    private void StartRetryLoop()
    {
        if (Interlocked.Exchange(ref _retryLoopRunning, 1) == 1) return;
        _ = RetryMissingPathsAsync(_stoppingToken).ContinueWith(
            t => _logger.LogError(t.Exception, "Watch-path retry loop failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task RetryMissingPathsAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                List<string> missing;
                lock (_missingPaths) { missing = _missingPaths.ToList(); }
                if (missing.Count == 0) return;

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                foreach (var path in missing)
                {
                    if (!TryAttachWatcher(path)) continue;
                    lock (_missingPaths) { _missingPaths.Remove(path); }
                    EnqueueExisting(path);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _retryLoopRunning, 0);
            // A path removed while the loop was finishing: start again rather than lose it.
            bool more;
            lock (_missingPaths) { more = _missingPaths.Count > 0; }
            if (more && !stoppingToken.IsCancellationRequested) StartRetryLoop();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var now = DateTimeOffset.UtcNow;
        if (await _queueStore.GetMetaAsync(WatchingSinceKey, stoppingToken) is { } since)
        {
            _watchingSinceUtc = DateTimeOffset.Parse(since, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (_watchingSinceUtc > now)
            {
                _watchingSinceUtc = now; // the clock was ahead when it was recorded
                await _queueStore.SetMetaAsync(WatchingSinceKey, now.ToString("O"), stoppingToken);
            }
        }
        else
        {
            // A new queue is a fresh install: start now. An existing one is an upgrade from a version
            // that has already seen everything in its folders, so it filters nothing, and dumps written
            // while the service was down for the upgrade still count.
            _watchingSinceUtc = _queueStore.CreatedNew ? now : DateTimeOffset.UnixEpoch;
            await _queueStore.SetMetaAsync(WatchingSinceKey, _watchingSinceUtc.ToString("O"), stoppingToken);
        }

        await RecoverUnfinishedAsync(stoppingToken);
        List<string> attached;
        lock (_watchers) { attached = _watchers.Select(w => w.Path).ToList(); }
        foreach (var path in attached) EnqueueExisting(path);
        StartRetryLoop();

        await foreach (var path in _candidatePaths.Reader.ReadAllAsync(stoppingToken))
        {
            // Fire-and-forget so one slow stability check (large kernel dump)
            // doesn't block detection of the next candidate.
            _ = ProcessCandidateAsync(path, stoppingToken);
        }
    }

    /// <summary>
    /// Picks up records a previous run left mid-pipeline: `Detected` (crashed
    /// between recording and the policy decision) is re-evaluated, and
    /// `PendingApproval` under an ask-style policy gets a fresh approval
    /// wait, since the original one lived only in memory. Manual-only
    /// pending records are left for the user; they're re-announced when the
    /// tray connects.
    /// </summary>
    private async Task RecoverUnfinishedAsync(CancellationToken stoppingToken)
    {
        try
        {
            var unfinished = (await _queueStore.GetByStateAsync(DumpQueueState.Detected, stoppingToken))
                .Concat(await _queueStore.GetByStateAsync(DumpQueueState.PendingApproval, stoppingToken))
                .ToList();
            foreach (var record in unfinished)
            {
                if (!File.Exists(record.FilePath))
                {
                    record.State = DumpQueueState.Skipped;
                    record.LastError = "Dump file no longer exists";
                    await _queueStore.UpsertAsync(record, stoppingToken);
                    continue;
                }

                if (await ChangedSinceDetectedOrUnknownAsync(record, stoppingToken) is not false)
                {
                    // Path reused (e.g. MEMORY.DMP rewritten by a new crash)
                    // while we were down: this record no longer describes the
                    // file. Retire it and treat the file as a fresh candidate.
                    record.State = DumpQueueState.Skipped;
                    record.LastError = "Dump file changed while the service was stopped";
                    await _queueStore.UpsertAsync(record, stoppingToken);
                    _candidatePaths.Writer.TryWrite(record.FilePath);
                    continue;
                }

                var outcome = PolicyEngine.Evaluate(record, _config.Policy, DateTimeOffset.Now);
                if (record.State == DumpQueueState.PendingApproval && outcome == ApprovalOutcome.ManualOnly)
                {
                    continue;
                }

                _logger.LogInformation("Resuming {State} dump {Path} after restart", record.State, record.FilePath);
                _ = ResumeAsync(record, outcome, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to recover unfinished dumps at startup");
        }

        try
        {
            // Queued dumps overwritten while the service was stopped would upload the new file under
            // the old record: retire them and look at the file afresh.
            foreach (var record in await _queueStore.GetByStateAsync(DumpQueueState.Queued, stoppingToken))
            {
                if (File.Exists(record.FilePath) && await ChangedSinceDetectedOrUnknownAsync(record, stoppingToken) is true)
                {
                    await RetireAsync(record, "Overwritten by a newer dump at the same path before it was uploaded", stoppingToken);
                    _candidatePaths.Writer.TryWrite(record.FilePath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to check queued dumps at startup");
        }
    }

    /// <summary>
    /// The file at the record's path is no longer the dump it describes: MEMORY.DMP rewritten by a
    /// new crash, possibly to the same size. A changed size or write time is only a hint; the content
    /// hash decides, so a file that was merely touched keeps its record.
    /// </summary>
    private async Task<bool?> ChangedSinceDetectedOrUnknownAsync(DumpRecord record, CancellationToken stoppingToken)
    {
        try
        {
            var info = new FileInfo(record.FilePath);
            if (info.Length == record.SizeBytes && info.LastWriteTimeUtc <= record.DetectedAtUtc.UtcDateTime) return false;
            if (info.Length != record.SizeBytes || record.Sha256 is null) return true;
            return await DumpHasher.ComputeSha256Async(record.FilePath, stoppingToken) != record.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being written right now (a new dump at this path): unknown. Callers leave the record
            // alone or retire it, but one unreadable file never stops the recovery of the rest.
            _logger.LogWarning(ex, "Could not check whether {Path} changed", record.FilePath);
            return null;
        }
    }

    private async Task ResumeAsync(DumpRecord record, ApprovalOutcome outcome, CancellationToken stoppingToken)
    {
        try
        {
            await ApplyOutcomeAsync(record, outcome, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the record stays as-is and is resumed next start.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume dump {Path} after restart", record.FilePath);
        }
    }

    private async Task ProcessCandidateAsync(string path, CancellationToken stoppingToken)
    {
        if (!_inFlight.TryAdd(path, false))
        {
            // Already being processed (e.g. both Created and Renamed fired for
            // it). Flag it so a genuinely new write is re-checked afterwards.
            _inFlight[path] = true;
            return;
        }

        try
        {
            if (await IsAlreadyRecordedAsync(path, stoppingToken))
            {
                return;
            }
            try
            {
                var info = new FileInfo(path);
                if (_knownDuplicates.ContainsKey((path, info.Length, info.LastWriteTimeUtc)))
                {
                    return; // a copy of a tracked dump, already hashed once
                }
            }
            catch (IOException)
            {
                // Vanished or locked: the checks below report it.
            }

            var stable = await StabilityChecker.WaitUntilStableAsync(
                path, StabilityPollInterval, StabilityQuietPeriod, StabilityTimeout, stoppingToken);
            if (!stable)
            {
                _logger.LogWarning("Dump at {Path} never stabilized within timeout, giving up", path);
                return;
            }

            var dumpType = await DumpSniffer.DetectAsync(path, stoppingToken);
            var sizeBytes = new FileInfo(path).Length;
            var sha256 = await DumpHasher.ComputeSha256Async(path, stoppingToken);

            var existing = await _queueStore.FindByFilePathAsync(path, stoppingToken);
            if (existing?.Sha256 == sha256)
            {
                return; // same content re-touched; nothing new to submit
            }
            if (existing is { State: DumpQueueState.Detected or DumpQueueState.PendingApproval or DumpQueueState.Queued })
            {
                // Overwritten in place (MEMORY.DMP) before the old dump was uploaded: that one is gone,
                // and its record must not upload the new file under its id.
                await RetireAsync(existing, "Overwritten by a newer dump at the same path before it was uploaded", stoppingToken);
            }

            // Id is the content hash, so a path that goes A -> B -> A lands on
            // an existing record. Upserting would reset its state (e.g.
            // Submitted -> Detected) and re-process an already handled dump.
            // Failed/Skipped are terminal-but-retryable: a re-generated dump
            // with the same content is allowed back through policy.
            var byId = await _queueStore.FindByIdAsync(sha256, stoppingToken);
            if (byId is not null && byId.State is not (DumpQueueState.Failed or DumpQueueState.Skipped))
            {
                _logger.LogDebug("Dump {Path} matches already-tracked record {Id}; skipping", path, sha256);
                var seen = new FileInfo(path);
                _knownDuplicates[(path, seen.Length, seen.LastWriteTimeUtc)] = true;
                return;
            }

            var record = new DumpRecord
            {
                Id = sha256,
                FilePath = path,
                SizeBytes = sizeBytes,
                Sha256 = sha256,
                DumpType = dumpType,
                State = DumpQueueState.Detected,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            };
            await _queueStore.UpsertAsync(record, stoppingToken);
            _logger.LogInformation("Detected dump {Path} ({Size} bytes, {Type})", path, sizeBytes, dumpType);

            var outcome = PolicyEngine.Evaluate(record, _config.Policy, DateTimeOffset.Now);
            await ApplyOutcomeAsync(record, outcome, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process candidate dump at {Path}", path);
        }
        finally
        {
            if (_inFlight.TryRemove(path, out var rerun) && rerun)
            {
                _candidatePaths.Writer.TryWrite(path);
            }
        }
    }

    /// <summary>
    /// Cheap dedup that still lets a reused path through: MEMORY.DMP is
    /// rewritten in place by every kernel crash, so "path seen before" alone
    /// would silently drop all but the first one.
    /// </summary>
    private async Task<bool> IsAlreadyRecordedAsync(string path, CancellationToken stoppingToken)
    {
        var existing = await _queueStore.FindByFilePathAsync(path, stoppingToken);
        if (existing is null) return false;

        try
        {
            var info = new FileInfo(path);
            return info.Length == existing.SizeBytes && info.LastWriteTimeUtc <= existing.DetectedAtUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RetireAsync(DumpRecord record, string reason, CancellationToken stoppingToken)
    {
        _approvalCoordinator.Resolve(record.Id, approved: false); // an approval wait for it ends here
        record.State = DumpQueueState.Skipped;
        record.LastError = reason;
        await _queueStore.UpsertAsync(record, stoppingToken);
        _logger.LogWarning("Dump {Path} ({Id}) retired: {Reason}", record.FilePath, record.Id, reason);
    }

    private async Task ApplyOutcomeAsync(DumpRecord record, ApprovalOutcome outcome, CancellationToken stoppingToken)
    {
        switch (outcome)
        {
            case ApprovalOutcome.AutoSubmit:
                record.State = DumpQueueState.Queued;
                await _queueStore.UpsertAsync(record, stoppingToken);
                _logger.LogDebug("Set state Queued for {Id}", record.Id);
                break;

            case ApprovalOutcome.RequiresApproval:
                record.State = DumpQueueState.PendingApproval;
                await _queueStore.UpsertAsync(record, stoppingToken);
                await RequestApprovalAsync(record, stoppingToken);
                break;

            case ApprovalOutcome.ManualOnly:
                if (!_pipeServer.IsInteractive)
                {
                    _logger.LogWarning(
                        "Dump {Path} needs manual submission but this host has no tray UI; it stays PendingApproval. Use an automatic policy mode on headless hosts.",
                        record.FilePath);
                }
                record.State = DumpQueueState.PendingApproval;
                await _queueStore.UpsertAsync(record, stoppingToken);
                await _pipeServer.SendAsync(new IpcMessage
                {
                    Type = IpcMessageType.AskApproval,
                    DumpId = record.Id,
                    DumpFileName = Path.GetFileName(record.FilePath),
                    DumpSizeBytes = record.SizeBytes,
                    DumpType = record.DumpType,
                    StatusMessage = "Detected — manual submit required",
                }, stoppingToken);
                break;

            case ApprovalOutcome.Skip:
                record.State = DumpQueueState.Skipped;
                await _queueStore.UpsertAsync(record, stoppingToken);
                break;
        }
    }

    private async Task RequestApprovalAsync(DumpRecord record, CancellationToken stoppingToken)
    {
        await _pipeServer.SendAsync(new IpcMessage
        {
            Type = IpcMessageType.AskApproval,
            DumpId = record.Id,
            DumpFileName = Path.GetFileName(record.FilePath),
            DumpSizeBytes = record.SizeBytes,
            DumpType = record.DumpType,
            AskTimeoutSeconds = _config.Policy.AskTimeoutSeconds,
        }, stoppingToken);

        var timeout = TimeSpan.FromSeconds(_config.Policy.AskTimeoutSeconds);
        var approved = await _approvalCoordinator.WaitForApprovalAsync(record.Id, timeout, stoppingToken);

        if (approved is null && stoppingToken.IsCancellationRequested)
        {
            // Service is stopping, not a real timeout: leave it PendingApproval
            // so it's resumed on next start rather than applying the default.
            return;
        }

        if (approved is null)
        {
            // Timed out with no response.
            var timeoutOutcome = PolicyEngine.ResolveAskTimeout(_config.Policy);
            record.State = timeoutOutcome == ApprovalOutcome.AutoSubmit ? DumpQueueState.Queued : DumpQueueState.Skipped;
        }
        else
        {
            record.State = approved.Value ? DumpQueueState.Queued : DumpQueueState.Skipped;
        }

        await _queueStore.UpsertAsync(record, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }
        _candidatePaths.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}
