using System.Text.Json;
using DumpTriage.Common.Config;
using DumpTriage.Common.Detection;
using DumpTriage.Common.Ipc;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Tracing;
using DumpTriageAgent.Runtime;

namespace DumpTriageAgent.Tracing;

/// <summary>
/// Automated performance traces (website#1999, dumptriage-tools#29). Keeps a WPR flight recorder running (memory mode: a
/// ring of buffers that is written only on stop), watches the triggers in the <c>tracing</c> config section, and on one
/// stops the recorder into an .etl, zips it, writes manifest.json and queues it as a <see cref="DumpType.Trace"/> record.
/// The upload worker sends it to /api/v1/traces like a dump. Held for approval by default.
///
/// Limits are enforced here, not by the service: the ring is sized from maxDiskMB, a zip over maxDiskMB is discarded and
/// the trace folder kept inside it, cooldownMin and dailyCap gate captures. maxOverheadPercent cannot be measured per
/// process on Windows (the kernel logger's cost is charged to whatever runs), so the profile is chosen to stay under it
/// and the lab measurement is in the README; the status says so.
///
/// Never fights another tool for the kernel logger: if wpr refuses to start (Procmon, xperf, an EDR, another WPR
/// session), the service backs off, reports why in status.json and tries again later.
/// </summary>
public sealed class TracingService : BackgroundService
{
    public const string HistoryKey = "trace_captures";
    public const string HostTokenKey = "host_token";
    public const int ProfileInterval100ns = 40000; // 4 ms
    public const int DefaultProfileInterval100ns = 10000; // WPR's own 1 ms, put back when the service stops
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly AgentConfig _config;
    private readonly DumpQueueStore _queue;
    private readonly ApprovalCoordinator _approvals;
    private readonly ITrayChannel _tray;
    private readonly IWpr _wpr;
    private readonly IMetricSource? _metrics;
    private readonly ILogger<TracingService> _logger;
    private readonly TimeProvider _time;
    private readonly string _traceDir;
    private readonly string _profileTemplatePath;
    private readonly List<(TraceTrigger Trigger, TriggerRunner Runner)> _runners = new();

    private bool _recording;
    private bool _intervalSet;
    private DateTimeOffset _recordingSince;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private string? _error;
    private string? _wprVersion;
    private string? _lastCapture;
    private bool _disabled;
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;
    internal static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMinutes(1);

    public TracingService(
        AgentConfig config, DumpQueueStore queue, ApprovalCoordinator approvals, ITrayChannel tray, IWpr wpr,
        IMetricSource? metrics, ILogger<TracingService> logger, TimeProvider? time = null, string? traceDir = null,
        string? profileTemplatePath = null)
    {
        _config = config;
        _queue = queue;
        _approvals = approvals;
        _tray = tray;
        _wpr = wpr;
        _metrics = metrics;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _traceDir = traceDir ?? Path.Combine(Path.GetDirectoryName(AgentPaths.ForCurrentOs().DbPath)!, "traces");
        _profileTemplatePath = profileTemplatePath ?? Path.Combine(AppContext.BaseDirectory, "PerfInsights.wprp");
    }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>WPR is Windows-only; tests drive the service through a fake IWpr on any OS.</summary>
    internal bool RequireWindows { get; init; } = true;

    internal string TraceDir => _traceDir;
    internal bool Recording => _recording;
    internal string? Error => _error;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Tracing.Enabled || !Setup())
        {
            await RetireHeldAsync(stoppingToken);
            return;
        }
        await ResumeHeldAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Tracing tick failed; dumps are unaffected");
                }
                await Task.Delay(PollInterval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_recording)
        {
            await _wpr.CancelAsync(CancellationToken.None);
            _recording = false;
        }
        await RestoreIntervalAsync();
    }

    /// <summary>The interval is system-wide: back to WPR's default whenever this service's session ends, so a tool that
    /// takes the kernel logger in between samples at its own rate.</summary>
    private async Task RestoreIntervalAsync()
    {
        if (!_intervalSet) return;
        await _wpr.SetProfileIntervalAsync(DefaultProfileInterval100ns, CancellationToken.None);
        _intervalSet = false;
    }

    /// <summary>With tracing off (or its section unreadable), traces left Detected or held are never decided: they are
    /// skipped and their zips removed, with any zip no live record names. Queued ones still upload.</summary>
    private async Task RetireHeldAsync(CancellationToken ct)
    {
        foreach (var state in new[] { DumpQueueState.Detected, DumpQueueState.PendingApproval })
        {
            foreach (var record in (await _queue.GetByStateAsync(state, ct)).Where(r => r.DumpType == DumpType.Trace))
            {
                if (await _queue.TransitionStateAsync(record.Id, state, DumpQueueState.Skipped, ct, "tracing is off"))
                {
                    TryDelete(record.FilePath);
                }
            }
        }
        await HousekeepingAsync(ct, enforceBudget: false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Validates the section and builds the trigger runners. False (and logged) when tracing cannot run.</summary>
    internal bool Setup()
    {
        var problems = _config.Tracing.Validate();
        if (RequireWindows && !OperatingSystem.IsWindows()) problems.Add("WPR tracing needs Windows");
        if (!File.Exists(_profileTemplatePath)) problems.Add($"the WPR profile {_profileTemplatePath} is missing");
        if (problems.Count > 0)
        {
            _disabled = true;
            _error = string.Join("; ", problems);
            foreach (var p in problems) _logger.LogError("Tracing is off: {Problem}", p);
            WriteStatus();
            return false;
        }
        foreach (var t in _config.Tracing.Triggers)
        {
            if (t.Type is "schedule" or "hangEvent") continue;
            _runners.Add((t, new TriggerRunner(t.Threshold ?? 0, t.SustainSec)));
        }
        Directory.CreateDirectory(_traceDir);
        _logger.LogInformation(
            "Tracing is on: {Count} trigger(s), submission {Submission}; recorder overhead is not measurable per process on Windows (see the README)",
            _config.Tracing.Triggers.Count, _config.Tracing.Submission);
        return true;
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        if (_disabled) return;
        var now = _time.GetUtcNow();
        var t = now.ToUnixTimeMilliseconds() / 1000.0;
        if (now - _lastHousekeeping >= HousekeepingInterval)
        {
            await HousekeepingAsync(ct);
            _lastHousekeeping = now;
        }
        if (!_recording)
        {
            if (now < _retryAt)
            {
                WriteStatus();
                return;
            }
            await StartRecorderAsync(ct);
            if (!_recording)
            {
                WriteStatus();
                return;
            }
        }

        var metrics = _metrics?.Read();
        var met = new List<(TraceTrigger, double?)>();
        if (metrics != null)
        {
            foreach (var (trigger, runner) in _runners)
            {
                var value = Value(trigger, metrics);
                if (runner.Feed(t, value)) met.Add((trigger, value));
            }
            foreach (var trigger in _config.Tracing.Triggers.Where(x => x.Type == "hangEvent"))
            {
                if (metrics.NewHangEvents > 0) met.Add((trigger, metrics.NewHangEvents));
            }
        }
        foreach (var trigger in _config.Tracing.Triggers.Where(x => x.Type == "schedule"))
        {
            var key = "trace_last_" + trigger.Name;
            var last = await _queue.GetMetaAsync(key, ct);
            if (last is null)
            {
                await _queue.SetMetaAsync(key, t.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            }
            else if (t - double.Parse(last, System.Globalization.CultureInfo.InvariantCulture) >= trigger.IntervalMin!.Value * 60)
            {
                await _queue.SetMetaAsync(key, t.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
                met.Add((trigger, null));
            }
        }
        var request = Path.Combine(_traceDir, "capture.request");
        if (File.Exists(request))
        {
            File.Delete(request);
            met.Add((new TraceTrigger { Name = "manual", Type = "manual" }, null));
        }

        foreach (var (trigger, observed) in met)
        {
            if (!_recording)
            {
                _logger.LogInformation("Trigger {Name} met while no recorder session is running; no capture", trigger.Name);
                continue;
            }
            var outcome = CaptureLimiter.Decide(t, _config.Tracing.Limits, await HistoryAsync(ct));
            if (outcome != CaptureOutcome.Fire)
            {
                _logger.LogInformation("Trigger {Name} met, no capture: {Outcome}", trigger.Name, outcome);
                if (trigger.Type == "manual") _lastCapture = $"{now:O} manual: {outcome}";
                continue;
            }
            await CaptureAsync(trigger, observed, now, ct);
        }
        WriteStatus();
    }

    private static double? Value(TraceTrigger trigger, Metrics m) => trigger.Type switch
    {
        "cpuPercent" when !string.IsNullOrWhiteSpace(trigger.Process) => Proc(m, trigger.Process!),
        "process" => Proc(m, trigger.Process!),
        "cpuPercent" => m.CpuPercent,
        "loadPerCpu" => m.LoadPerCpu,
        "diskLatencyMs" => m.DiskLatencyMs,
        _ => null,
    };

    private static double? Proc(Metrics m, string name)
    {
        var key = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return m.ProcessCpuPercent.TryGetValue(key, out var v) ? v : null;
    }

    private async Task StartRecorderAsync(CancellationToken ct)
    {
        var profile = Path.Combine(_traceDir, "PerfInsights.runtime.wprp");
        await File.WriteAllTextAsync(profile, WprProfile.Render(await File.ReadAllTextAsync(_profileTemplatePath, ct), _config.Tracing.Limits), ct);
        _wprVersion ??= await _wpr.VersionAsync(ct);
        var result = await _wpr.StartAsync(profile, ct);
        if (result.Ok)
        {
            // The interval is system-wide, so it is set only once the kernel logger is ours (a change after the start applies
            // to the running session, checked on the lab VM): a tool that holds it keeps sampling at its own rate.
            _recording = true;
            _intervalSet = true;
            try
            {
                await _wpr.SetProfileIntervalAsync(ProfileInterval100ns, ct);
            }
            catch (Exception)
            {
                await _wpr.CancelAsync(CancellationToken.None); // never leave our session holding the kernel logger
                _recording = false;
                await RestoreIntervalAsync();
                throw;
            }
            _recordingSince = _time.GetUtcNow();
            _error = null;
            _logger.LogInformation("Trace recorder started ({Buffers} MB ring)", WprProfile.BufferCount(_config.Tracing.Limits));
            return;
        }
        // Another tool holds the kernel logger, or a WPR session is already running: back off, never cancel theirs.
        _error = $"wpr could not start ({result.ExitCode}): {Short(result.Output)}; another tool may be tracing (Procmon, xperf, an EDR, another WPR session); trying again in {RetryAfterFailure.TotalMinutes:0} min";
        _retryAt = _time.GetUtcNow() + RetryAfterFailure;
        _logger.LogWarning("{Error}", _error);
    }

    private static string Short(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 300 ? flat[..300] : flat;
    }

    internal async Task<string?> CaptureAsync(TraceTrigger trigger, double? observed, DateTimeOffset now, CancellationToken ct)
    {
        var limits = _config.Tracing.Limits;
        var t = now.ToUnixTimeMilliseconds() / 1000.0;
        // Counted before it is taken: a capture that fails still counts, so a broken recorder is not asked in a loop.
        var history = (await HistoryAsync(ct)).Where(a => a > t - 2 * CaptureLimiter.SecondsPerDay).Append(t).ToList();
        await _queue.SetMetaAsync(HistoryKey, JsonSerializer.Serialize(history), ct);

        var stamp = now.ToString("yyyyMMddTHHmmssZ");
        var etl = Path.Combine(_traceDir, $"{stamp}-{trigger.Name}.etl");
        var started = _recordingSince;
        var stop = await _wpr.StopAsync(etl, ct);
        _recording = false; // the next tick starts a new session
        if (!stop.Ok)
        {
            // A stop that failed or timed out can leave the session running, holding the kernel logger: every later start
            // would fail as if another tool had it.
            await _wpr.CancelAsync(CancellationToken.None);
        }
        await RestoreIntervalAsync();
        if (!stop.Ok || !File.Exists(etl))
        {
            _error = $"capture for trigger {trigger.Name} failed: wpr -stop ({stop.ExitCode}): {Short(stop.Output)}";
            _logger.LogError("{Error}", _error);
            DeleteEtl(etl);
            return null;
        }

        var zip = Path.Combine(_traceDir, $"{stamp}-{trigger.Name}.zip");
        try
        {
            TraceArchive.Create(etl, zip);
        }
        finally
        {
            DeleteEtl(etl);
        }
        var size = new FileInfo(zip).Length;
        if (size > limits.MaxDiskMB * 1024L * 1024L)
        {
            File.Delete(zip);
            _error = $"the trace is {size / (1024 * 1024)} MB zipped, over the {limits.MaxDiskMB} MB disk limit, and was discarded";
            _logger.LogError("{Error}", _error);
            return null;
        }

        var sha256 = await DumpHasher.ComputeSha256Async(zip, ct);
        var lookback = (int)Math.Max(0, (now - started).TotalSeconds);
        var manifest = TraceManifest.Build(
            trigger, observed, now - TimeSpan.FromSeconds(Math.Min(lookback, limits.MaxDurationSec)), now, lookback,
            _wprVersion ?? "unknown", $"wpr -start PerfInsights.wprp!{WprProfile.ProfileName} (memory mode, {WprProfile.BufferCount(limits)} MB ring, 4 ms sampling); wpr -stop",
            Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture), await HostTokenAsync(ct),
            typeof(TracingService).Assembly.GetName().Version?.ToString(3) ?? "unknown");
        var record = new DumpRecord
        {
            Id = sha256,
            FilePath = zip,
            SizeBytes = size,
            FileLastWriteUtc = File.GetLastWriteTimeUtc(zip),
            Sha256 = sha256,
            DumpType = DumpType.Trace,
            State = DumpQueueState.Detected,
            DetectedAtUtc = now,
            Manifest = manifest,
        };
        if (!await _queue.TryInsertAsync(record, ct))
        {
            File.Delete(zip);
            return null;
        }
        _lastCapture = $"{now:O} {trigger.Name}";
        _logger.LogInformation("Captured trace {Path} for trigger {Name} ({Size} bytes zipped)", zip, trigger.Name, size);
        _ = ApplyAsync(record, Decide(record, now), ct);
        return sha256;
    }

    /// <summary>The submission mode for traces, with the dump policy's other settings. RuleBased uses only the rules that
    /// name traces (<c>"dumpType": "Trace"</c>): a dump rule ("anything under 200 MB: submit") must not send a trace,
    /// which holds every process's names and stacks, without asking.</summary>
    private ApprovalOutcome Decide(DumpRecord record, DateTimeOffset now) =>
        PolicyEngine.Evaluate(record, new AgentPolicyConfig
        {
            Mode = _config.Tracing.Submission,
            Rules = _config.Policy.Rules.Where(r => r.DumpType == DumpType.Trace).ToList(),
            DefaultRuleAction = _config.Policy.DefaultRuleAction,
            AskTimeoutSeconds = _config.Policy.AskTimeoutSeconds,
            AskTimeoutDefaultAction = _config.Policy.AskTimeoutDefaultAction,
        }, now.ToLocalTime());

    private static void DeleteEtl(string etl)
    {
        try
        {
            if (File.Exists(etl)) File.Delete(etl);
            if (Directory.Exists(etl + ".NGENPDB")) Directory.Delete(etl + ".NGENPDB", recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The same states a dump goes through (the tray asks, a timeout takes the configured default).</summary>
    internal async Task ApplyAsync(DumpRecord record, ApprovalOutcome outcome, CancellationToken ct)
    {
        try
        {
            switch (outcome)
            {
                case ApprovalOutcome.AutoSubmit:
                    await _queue.TransitionStateAsync(record.Id, record.State, DumpQueueState.Queued, ct);
                    break;
                case ApprovalOutcome.Skip:
                    await _queue.TransitionStateAsync(record.Id, record.State, DumpQueueState.Skipped, ct);
                    break;
                case ApprovalOutcome.ManualOnly:
                    await _queue.TransitionStateAsync(record.Id, record.State, DumpQueueState.PendingApproval, ct);
                    await AskAsync(record, null, "Performance trace captured: manual submit required", ct);
                    break;
                case ApprovalOutcome.RequiresApproval:
                    if (!await _queue.TransitionStateAsync(record.Id, record.State, DumpQueueState.PendingApproval, ct)) return;
                    record.State = DumpQueueState.PendingApproval;
                    await AskAsync(record, _config.Policy.AskTimeoutSeconds, "Performance trace captured", ct);
                    var approved = await _approvals.WaitForApprovalAsync(record.Id, TimeSpan.FromSeconds(_config.Policy.AskTimeoutSeconds), ct);
                    if (approved is null && ct.IsCancellationRequested) return;
                    var queue = approved ?? PolicyEngine.ResolveAskTimeout(_config.Policy) == ApprovalOutcome.AutoSubmit;
                    await _queue.TransitionStateAsync(record.Id, DumpQueueState.PendingApproval, queue ? DumpQueueState.Queued : DumpQueueState.Skipped, ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task AskAsync(DumpRecord record, int? timeoutSeconds, string note, CancellationToken ct) =>
        _tray.SendAsync(new IpcMessage
        {
            Type = IpcMessageType.AskApproval,
            DumpId = record.Id,
            DumpFileName = Path.GetFileName(record.FilePath),
            DumpSizeBytes = record.SizeBytes,
            DumpType = DumpType.Trace,
            AskTimeoutSeconds = timeoutSeconds,
            StatusMessage = note,
        }, ct);

    /// <summary>Traces a previous run left Detected or held (the approval wait lived in memory) are decided again.</summary>
    private async Task ResumeHeldAsync(CancellationToken ct)
    {
        foreach (var state in new[] { DumpQueueState.Detected, DumpQueueState.PendingApproval })
        {
            foreach (var record in (await _queue.GetByStateAsync(state, ct)).Where(r => r.DumpType == DumpType.Trace))
            {
                if (!File.Exists(record.FilePath))
                {
                    await _queue.TransitionStateAsync(record.Id, record.State, DumpQueueState.Skipped, ct);
                    continue;
                }
                var outcome = Decide(record, _time.GetUtcNow());
                if (state == DumpQueueState.PendingApproval && outcome == ApprovalOutcome.ManualOnly) continue;
                if (state == DumpQueueState.PendingApproval && outcome == ApprovalOutcome.RequiresApproval)
                {
                    record.State = DumpQueueState.Detected; // so ApplyAsync's first transition finds it
                    await _queue.TransitionStateAsync(record.Id, DumpQueueState.PendingApproval, DumpQueueState.Detected, ct);
                }
                _ = ApplyAsync(record, outcome, ct);
            }
        }
    }

    internal async Task<List<double>> HistoryAsync(CancellationToken ct)
    {
        var raw = await _queue.GetMetaAsync(HistoryKey, ct);
        try
        {
            return raw is null ? new List<double>() : JsonSerializer.Deserialize<List<double>>(raw) ?? new List<double>();
        }
        catch (JsonException)
        {
            return new List<double>();
        }
    }

    internal async Task<string> HostTokenAsync(CancellationToken ct)
    {
        var token = await _queue.GetMetaAsync(HostTokenKey, ct);
        if (token is null)
        {
            token = Guid.NewGuid().ToString("N")[..16];
            await _queue.SetMetaAsync(HostTokenKey, token, ct);
        }
        return token;
    }

    /// <summary>Zips that no live record names go, and any .etl a crash left between stop and zip (housekeeping never
    /// runs during a capture); then, over maxDiskMB, the oldest live zips (their records fail).</summary>
    internal async Task HousekeepingAsync(CancellationToken ct, bool enforceBudget = true)
    {
        if (!Directory.Exists(_traceDir)) return;
        foreach (var etl in Directory.GetFiles(_traceDir, "*.etl")) DeleteEtl(etl);
        foreach (var ngen in Directory.GetDirectories(_traceDir, "*.etl.NGENPDB"))
        {
            try { Directory.Delete(ngen, recursive: true); } catch (IOException) { }
        }
        var live = new Dictionary<string, DumpRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in new[] { DumpQueueState.Detected, DumpQueueState.PendingApproval, DumpQueueState.Queued, DumpQueueState.Submitting })
        {
            foreach (var r in await _queue.GetByStateAsync(state, ct)) live[r.FilePath] = r;
        }
        var zips = new DirectoryInfo(_traceDir).GetFiles("*.zip").OrderBy(f => f.LastWriteTimeUtc).ToList();
        foreach (var zip in zips.Where(z => !live.ContainsKey(z.FullName)).ToList())
        {
            zip.Delete();
            zips.Remove(zip);
        }
        if (!enforceBudget) return;
        var budget = _config.Tracing.Limits.MaxDiskMB * 1024L * 1024L;
        var total = zips.Sum(z => z.Length);
        foreach (var zip in zips)
        {
            if (total <= budget) break;
            total -= zip.Length;
            var record = live[zip.FullName];
            if (record.State == DumpQueueState.Submitting) continue;
            // Only from the state read above: the upload worker may have taken it since, and then it is left to finish.
            if (!await _queue.TransitionStateAsync(record.Id, record.State, DumpQueueState.Failed, ct,
                    "dropped: the trace folder was over the maxDiskMB limit")) continue;
            zip.Delete();
            _logger.LogWarning("Dropped {Path}: the trace folder was over the {Limit} MB limit", zip.FullName, _config.Tracing.Limits.MaxDiskMB);
        }
    }

    private void WriteStatus()
    {
        try
        {
            Directory.CreateDirectory(_traceDir);
            var status = new
            {
                enabled = _config.Tracing.Enabled,
                backend = "wpr",
                recorder = _recording ? "running" : "stopped",
                error = _error,
                overhead = "not measured per process on Windows; see the README",
                lastCapture = _lastCapture,
                updatedUtc = _time.GetUtcNow().ToString("O"),
            };
            File.WriteAllText(Path.Combine(_traceDir, "status.json"), JsonSerializer.Serialize(status));
        }
        catch (IOException)
        {
        }
    }
}
