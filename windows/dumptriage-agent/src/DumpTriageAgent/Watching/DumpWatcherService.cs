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
    internal TimeSpan StabilityPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan StabilityQuietPeriod { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan StabilityTimeout { get; set; } = TimeSpan.FromMinutes(30); // kernel dumps can take a while
    internal TimeSpan StabilityRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    private readonly AgentConfig _config;
    private readonly DumpQueueStore _queueStore;
    private readonly ApprovalCoordinator _approvalCoordinator;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ITrayChannel _pipeServer;
    private readonly ILogger<DumpWatcherService> _logger;

    private readonly Channel<string> _candidatePaths = Channel.CreateUnbounded<string>();
    private readonly List<FileSystemWatcher> _watchers = new();
    // Watched paths that don't exist (yet): /var/crash before the first crash, or a folder a cleanup
    // tool removed. Retried until they do.
    private readonly List<string> _missingPaths = new();
    private CancellationToken _stoppingToken;
    private int _retryLoopRunning;

    // Folder scans take only files written since the agent's first start (dumptriage-tools#5): a
    // fresh install mustn't offer old dumps. Read from the queue in ExecuteAsync; until then nothing.
    private DateTimeOffset _watchingSinceUtc = DateTimeOffset.MaxValue;

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
        // The upload worker found a queued file changed since it was hashed
        // (website#1493): detect it again, as the new dump it now is.
        _runtimeState.RescanRequested += path => _candidatePaths.Writer.TryWrite(path);
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
                    // the full timeout (dumptriage-tools#12) — a ManualOnly one never
                    // registered there and has no timeout to report.
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
        // A dump rewritten in place (a later crash's MEMORY.DMP) raises
        // only Changed (website#1486). Many fire while it's written; ExecuteAsync
        // runs one check per path at a time.
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
            ScanExisting(expanded);
        };
        watcher.EnableRaisingEvents = true;

        lock (_watchers) { _watchers.Add(watcher); }
        _logger.LogInformation("Watching {Path} for dump files (filters: {Filters}, raising: {Raising})", expanded, string.Join(",", watcher.Filters), watcher.EnableRaisingEvents);
        return true;
    }

    /// <summary>
    /// A directory like /var/crash may not exist until the first crash. Keep
    /// retrying; attaching scans what landed before the watcher did.
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
                    ScanExisting(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
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

    /// <summary>
    /// Queues the dumps already in a watched folder (website#1514): nothing raises an
    /// event for a dump written while the service wasn't running. Only those
    /// written within StartupScanMaxAgeDays and since the agent first started
    /// (dumptriage-tools#5: a fresh install leaves older dumps alone); ones the
    /// queue already knows (path, size, last-write time) are skipped by
    /// ProcessCandidateAsync.
    /// </summary>
    internal int ScanExisting(string directory)
    {
        if (_config.StartupScanMaxAgeDays <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-_config.StartupScanMaxAgeDays);
        var options = new EnumerationOptions
        {
            // The watcher's own filters, matched without regard to case.
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
        };
        var queued = 0;
        var olderThanFirstStart = 0;
        try
        {
            var paths = _config.WatchedFilters
                .SelectMany(filter => Directory.EnumerateFiles(directory, filter, options))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (written < cutoff) continue;
                // The later of write and creation time: a dump copied in on purpose keeps its old
                // write time but gets a new creation time.
                var created = File.GetCreationTimeUtc(path);
                if ((written > created ? written : created) < _watchingSinceUtc.UtcDateTime)
                {
                    olderThanFirstStart++;
                    continue;
                }
                if (_candidatePaths.Writer.TryWrite(path)) queued++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list existing dumps in {Path}", directory);
        }
        _logger.LogInformation(
            "Found {Count} existing dump(s) from the last {Days} days in {Path}; left {Old} older than this agent's first start alone",
            queued, _config.StartupScanMaxAgeDays, directory, olderThanFirstStart);
        return queued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        await LoadWatchingSinceAsync(stoppingToken);
        await RecoverUnfinishedAsync(stoppingToken);

        // After each watcher is up (StartAsync), so a dump written in between
        // is seen by one or the other (twice is harmless: same file, one record).
        List<string> attached;
        lock (_watchers) { attached = _watchers.Select(w => w.Path).ToList(); }
        foreach (var path in attached) ScanExisting(path);
        StartRetryLoop();

        await foreach (var path in _candidatePaths.Reader.ReadAllAsync(stoppingToken))
        {
            if (!TryStartCandidate(path)) continue; // already being looked at; it runs again after
            // Fire-and-forget so one slow stability check (large kernel dump)
            // doesn't block detection of the next candidate.
            _ = RunCandidateAsync(path, stoppingToken);
        }
    }

    private async Task LoadWatchingSinceAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (await _queueStore.GetMetaAsync(DumpQueueStore.WatchingSinceKey, stoppingToken) is { } since)
        {
            _watchingSinceUtc = DateTimeOffset.Parse(since, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (_watchingSinceUtc > now)
            {
                _watchingSinceUtc = now; // the clock was ahead when it was recorded
                await _queueStore.SetMetaAsync(DumpQueueStore.WatchingSinceKey, now.ToString("O"), stoppingToken);
            }
        }
        else
        {
            // A queue from before this setting (a new one gets it when the file is created): an
            // upgrade from a version that has already seen everything in its folders, so filter
            // nothing, and dumps written while the service was down for the upgrade still count.
            _watchingSinceUtc = DateTimeOffset.UnixEpoch;
            await _queueStore.SetMetaAsync(DumpQueueStore.WatchingSinceKey, _watchingSinceUtc.ToString("O"), stoppingToken);
        }
    }

    /// <summary>
    /// Picks up records a previous run left mid-pipeline: `Detected` (stopped
    /// between recording and the policy decision) is decided again, and
    /// `PendingApproval` under an ask-style policy gets a fresh approval wait,
    /// since the original one lived only in memory. Manual-only pending records
    /// are left for the user; they're offered again when the tray connects. A
    /// record whose file is gone is skipped. One whose file changed is caught
    /// at upload (CheckFileUnchangedAsync), which detects the file again.
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
                    await MoveAsync(record, DumpQueueState.Skipped, stoppingToken);
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
    }

    private async Task ResumeAsync(DumpRecord record, ApprovalOutcome outcome, CancellationToken stoppingToken)
    {
        try
        {
            await ApplyOutcomeAsync(record, outcome, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the record stays as it is and is resumed next start.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume dump {Path} after restart", record.FilePath);
        }
    }

    private async Task RunCandidateAsync(string path, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessCandidateAsync(path, stoppingToken);
        }
        finally
        {
            if (FinishCandidate(path))
            {
                // Events arrived while it ran: look once more. Cheap if the
                // file is unchanged (path, size and time match a record).
                _candidatePaths.Writer.TryWrite(path);
            }
        }
    }

    // Path -> whether another event for it arrived while it was in flight.
    private readonly Dictionary<string, bool> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryStartCandidate(string path)
    {
        lock (_inFlight)
        {
            if (_inFlight.ContainsKey(path))
            {
                _inFlight[path] = true;
                return false;
            }
            _inFlight[path] = false;
            return true;
        }
    }

    /// <summary>Returns whether another event for the path arrived while it was in flight.</summary>
    internal bool FinishCandidate(string path)
    {
        lock (_inFlight)
        {
            return _inFlight.Remove(path, out var again) && again;
        }
    }

    internal async Task ProcessCandidateAsync(string path, CancellationToken stoppingToken)
    {
        try
        {
            // Already seen exactly this file (e.g. both Created and Renamed
            // fired for it)? Path, size and last-write time, not the path
            // alone: MEMORY.DMP is rewritten by every kernel crash, and a
            // known path used to hide every later crash written there (website#1486).
            if (FileIdentity.TryRead(path) is { } seen
                && await _queueStore.FindByFileIdentityAsync(path, seen.SizeBytes, seen.LastWriteUtc, stoppingToken) is not null)
            {
                return;
            }

            var stable = await StabilityChecker.WaitUntilStableAsync(
                path, StabilityPollInterval, StabilityQuietPeriod, StabilityTimeout, stoppingToken);
            if (!stable)
            {
                if (!File.Exists(path))
                {
                    _logger.LogWarning("Dump at {Path} disappeared before it stabilized, giving up", path);
                    return;
                }
                // A slow writer that runs past the timeout used to be dropped
                // for good (website#1493): look at it again later.
                _logger.LogWarning(
                    "Dump at {Path} not stable within {Timeout}; looking again in {Delay}", path, StabilityTimeout, StabilityRetryDelay);
                _ = RequeueLaterAsync(path, stoppingToken);
                return;
            }

            var dumpType = await DumpSniffer.DetectAsync(path, stoppingToken);
            var identity = FileIdentity.Read(path);
            var sha256 = await DumpHasher.ComputeSha256Async(path, stoppingToken);
            if (FileIdentity.TryRead(path) != identity)
            {
                // Written to while we hashed it: the hash isn't of the final
                // bytes. Start over, which waits for it to settle (website#1493).
                _logger.LogInformation("Dump at {Path} changed while being hashed; looking again", path);
                _candidatePaths.Writer.TryWrite(path);
                return;
            }

            var record = new DumpRecord
            {
                Id = sha256,
                FilePath = path,
                SizeBytes = identity.SizeBytes,
                FileLastWriteUtc = identity.LastWriteUtc,
                Sha256 = sha256,
                DumpType = dumpType,
                State = DumpQueueState.Detected,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            };
            if (!await _queueStore.TryInsertAsync(record, stoppingToken))
            {
                // Same bytes as a dump already in the queue (a copy, or the
                // same file seen twice): that record keeps its state (website#1491).
                // Remembered, so the next start's scan doesn't hash it again (website#1514).
                await _queueStore.RememberFileAsync(path, identity.SizeBytes, identity.LastWriteUtc, sha256, stoppingToken);
                _logger.LogInformation("Dump {Path} has the same content as queued dump {Id}; not added again", path, sha256);
                return;
            }
            _logger.LogInformation("Detected dump {Path} ({Size} bytes, {Type})", path, identity.SizeBytes, dumpType);

            var outcome = PolicyEngine.Evaluate(record, _config.Policy, DateTimeOffset.Now);
            await ApplyOutcomeAsync(record, outcome, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process candidate dump at {Path}", path);
        }
    }

    private async Task RequeueLaterAsync(string path, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StabilityRetryDelay, stoppingToken);
            _candidatePaths.Writer.TryWrite(path);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the next start's scan looks at it again (website#1514).
        }
    }

    /// <summary>Paths waiting to be looked at; read by ExecuteAsync (and tests).</summary>
    internal ChannelReader<string> Candidates => _candidatePaths.Reader;

    private async Task ApplyOutcomeAsync(DumpRecord record, ApprovalOutcome outcome, CancellationToken stoppingToken)
    {
        // Only ever moves the record this watcher just inserted, and only from
        // the state it left it in: never over a state set elsewhere (website#1491).
        switch (outcome)
        {
            case ApprovalOutcome.AutoSubmit:
                await MoveAsync(record, DumpQueueState.Queued, stoppingToken);
                _logger.LogDebug("Set state Queued for {Id}", record.Id);
                break;

            case ApprovalOutcome.RequiresApproval:
                if (!await MoveAsync(record, DumpQueueState.PendingApproval, stoppingToken)) return;
                await RequestApprovalAsync(record, stoppingToken);
                break;

            case ApprovalOutcome.ManualOnly:
                if (!_pipeServer.IsInteractive)
                {
                    _logger.LogWarning(
                        "Dump {Path} needs manual submission but this host has no tray UI; it stays PendingApproval. Use an automatic policy mode on headless hosts.",
                        record.FilePath);
                }
                if (!await MoveAsync(record, DumpQueueState.PendingApproval, stoppingToken)) return;
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
                await MoveAsync(record, DumpQueueState.Skipped, stoppingToken);
                break;
        }
    }

    private async Task<bool> MoveAsync(DumpRecord record, DumpQueueState to, CancellationToken stoppingToken)
    {
        if (!await _queueStore.TransitionStateAsync(record.Id, record.State, to, stoppingToken))
        {
            _logger.LogInformation("Dump {Id} left {State} elsewhere; not moving it to {To}", record.Id, record.State, to);
            return false;
        }
        record.State = to;
        return true;
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

        DumpQueueState decided;
        if (approved is null)
        {
            // Timed out with no response.
            var timeoutOutcome = PolicyEngine.ResolveAskTimeout(_config.Policy);
            decided = timeoutOutcome == ApprovalOutcome.AutoSubmit ? DumpQueueState.Queued : DumpQueueState.Skipped;
        }
        else
        {
            decided = approved.Value ? DumpQueueState.Queued : DumpQueueState.Skipped;
        }

        // A manual submit from the tray may have sent it while we waited.
        await MoveAsync(record, decided, stoppingToken);
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
