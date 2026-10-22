using System.IO.Compression;
using System.Text.Json;
using DumpTriage.Common.Config;
using DumpTriage.Common.Ipc;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Tracing;
using DumpTriageAgent.Runtime;
using DumpTriageAgent.Tracing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DumpTriageAgent.Tests;

internal sealed class FakeWpr : IWpr
{
    public List<string> Calls { get; } = new();
    public WprResult StartResult { get; set; } = new(0, "");
    public bool StopFails { get; set; }
    public int EtlBytes { get; set; } = 64 * 1024;
    private int _stops;

    public Task<WprResult> SetProfileIntervalAsync(int interval, CancellationToken ct) { Calls.Add($"setprofint {interval}"); return Task.FromResult(new WprResult(0, "")); }
    public Task<WprResult> StartAsync(string profile, CancellationToken ct) { Calls.Add("start " + Path.GetFileName(profile)); return Task.FromResult(StartResult); }
    public Task<WprResult> CancelAsync(CancellationToken ct) { Calls.Add("cancel"); return Task.FromResult(new WprResult(0, "")); }
    public Task<string> VersionAsync(CancellationToken ct) => Task.FromResult("10.0.20348");

    public Task<WprResult> StopAsync(string etl, CancellationToken ct)
    {
        Calls.Add("stop");
        if (StopFails) return Task.FromResult(new WprResult(-2147024891, "Access is denied."));
        var bytes = new byte[EtlBytes];
        new Random(++_stops).NextBytes(bytes); // incompressible, so the zip is about this size; different per capture
        File.WriteAllBytes(etl, bytes);
        return Task.FromResult(new WprResult(0, "The trace was successfully saved."));
    }
}

internal sealed class FakeMetrics : IMetricSource
{
    public Metrics Next { get; set; } = new(null, null, null, new Dictionary<string, double>(), 0);
    public Metrics Read() => Next;
}

internal sealed class RecordingTray : ITrayChannel
{
    public List<IpcMessage> Sent { get; } = new();
    public bool IsInteractive => true;
    public event Action? TrayConnected { add { } remove { } }
    public Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default) { Sent.Add(message); return Task.CompletedTask; }
}

public sealed class TracingServiceTests : IAsyncLifetime
{
    private string _dir = "";
    private DumpQueueStore _store = null!;
    private readonly FakeWpr _wpr = new();
    private readonly FakeMetrics _metrics = new();
    private readonly RecordingTray _tray = new();
    private readonly ApprovalCoordinator _approvals = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        _dir = Directory.CreateTempSubdirectory("dt-trace-").FullName;
        _store = await DumpQueueStore.OpenAsync(Path.Combine(_dir, "queue.db"));
        File.WriteAllText(Path.Combine(_dir, "PerfInsights.wprp"), "<WindowsPerformanceRecorder><SystemCollector Id=\"SC\"><Buffers Value=\"64\"/></SystemCollector></WindowsPerformanceRecorder>");
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private TracingService Service(List<TraceTrigger>? triggers = null, TraceLimits? limits = null, DecisionMode submission = DecisionMode.AskFirst, int askTimeoutSeconds = 300,
        List<DumpRule>? rules = null)
    {
        var tc = new TracingConfig
        {
            Enabled = true,
            Submission = submission,
            Triggers = triggers ?? new List<TraceTrigger> { new() { Name = "cpu-high", Type = "cpuPercent", Threshold = 90, SustainSec = 10 } },
            Limits = limits ?? new TraceLimits { MaxDiskMB = 64, CooldownMin = 60, DailyCap = 4 },
        };
        var config = new AgentConfig { Tracing = tc, Policy = new AgentPolicyConfig { AskTimeoutSeconds = askTimeoutSeconds, Rules = rules ?? new List<DumpRule>() } };
        var service = new TracingService(config, _store, _approvals, _tray, _wpr, _metrics, NullLogger<TracingService>.Instance, _time,
            Path.Combine(_dir, "traces"), Path.Combine(_dir, "PerfInsights.wprp")) { RequireWindows = false };
        Assert.True(service.Setup());
        return service;
    }

    private async Task Tick(TracingService s, double? cpu, int seconds = 5, IReadOnlyDictionary<string, double>? procs = null, int hangs = 0)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        _metrics.Next = new Metrics(cpu, 0.5, 2, procs ?? new Dictionary<string, double>(), hangs);
        await s.TickAsync(CancellationToken.None);
    }

    private async Task<List<DumpRecord>> Traces()
    {
        var all = new List<DumpRecord>();
        foreach (var state in Enum.GetValues<DumpQueueState>()) all.AddRange(await _store.GetByStateAsync(state));
        return all.Where(r => r.DumpType == DumpType.Trace).ToList();
    }

    [Fact]
    public async Task ASustainedTrigger_StopsTheRecorder_ZipsTheEtl_AndHoldsTheTraceForApproval()
    {
        var s = Service();
        await Tick(s, 10);
        // The interval is system-wide: set only once the session is ours (lab VM: a change after start applies to it).
        Assert.Equal(new[] { "start PerfInsights.runtime.wprp", "setprofint 40000" }, _wpr.Calls);
        Assert.Contains("<Buffers Value=\"16\"/>", File.ReadAllText(Path.Combine(s.TraceDir, "PerfInsights.runtime.wprp")));
        for (var i = 0; i < 3; i++) await Tick(s, 95);
        await Task.Delay(100);
        var trace = Assert.Single(await Traces());
        Assert.Equal(DumpQueueState.PendingApproval, trace.State);
        // The session is over, so the system-wide interval goes back until the next one starts (review of #39).
        Assert.Equal(new[] { "stop", "setprofint 10000" }, _wpr.Calls.SkipWhile(c => c != "stop").ToArray());
        Assert.EndsWith("-cpu-high.zip", trace.FilePath);
        using (var zip = ZipFile.OpenRead(trace.FilePath)) Assert.Equal("trace.etl", zip.Entries[0].FullName);
        Assert.Empty(Directory.GetFiles(s.TraceDir, "*.etl"));
        using var manifest = JsonDocument.Parse(trace.Manifest!);
        Assert.Equal("etl", manifest.RootElement.GetProperty("traceType").GetString());
        Assert.Equal("cpu-high", manifest.RootElement.GetProperty("trigger").GetProperty("name").GetString());
        Assert.Equal(95, manifest.RootElement.GetProperty("trigger").GetProperty("observed").GetDouble());
        Assert.Equal("10.0.20348", manifest.RootElement.GetProperty("tool").GetProperty("version").GetString());
        Assert.Equal(16, manifest.RootElement.GetProperty("hostToken").GetString()!.Length);
        var ask = Assert.Single(_tray.Sent);
        Assert.Equal(IpcMessageType.AskApproval, ask.Type);
        Assert.Equal(DumpType.Trace, ask.DumpType);
        Assert.False(s.Recording); // the next tick starts a new session
        await Tick(s, 10);
        Assert.True(s.Recording);
    }

    [Fact]
    public async Task ApprovedFromTheTray_TheTraceIsQueued()
    {
        var s = Service();
        await Tick(s, 10);
        for (var i = 0; i < 3; i++) await Tick(s, 95);
        var trace = Assert.Single(await Traces());
        for (var i = 0; i < 50 && !_approvals.Resolve(trace.Id, true); i++) await Task.Delay(20);
        for (var i = 0; i < 50 && (await _store.FindByIdAsync(trace.Id))!.State != DumpQueueState.Queued; i++) await Task.Delay(20);
        Assert.Equal(DumpQueueState.Queued, (await _store.FindByIdAsync(trace.Id))!.State);
    }

    [Fact]
    public async Task AlwaysSubmit_QueuesAtOnce_AndTheCooldownStopsTheNextCapture()
    {
        var s = Service(submission: DecisionMode.AlwaysSubmit);
        await Tick(s, 10);
        for (var i = 0; i < 3; i++) await Tick(s, 95);
        await Task.Delay(100);
        Assert.Equal(DumpQueueState.Queued, Assert.Single(await Traces()).State);
        await Tick(s, 95); // restart
        for (var i = 0; i < 6; i++) await Tick(s, 95);
        Assert.Single(await Traces());
        Assert.Single(await s.HistoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AProcessTrigger_ReadsThatProcessWithOrWithoutExe()
    {
        var s = Service(new List<TraceTrigger> { new() { Name = "sql", Type = "process", Process = "sqlservr.exe", Threshold = 150, SustainSec = 5 } });
        var busy = new Dictionary<string, double> { ["sqlservr"] = 200 };
        await Tick(s, 10, procs: busy);
        await Tick(s, 10, procs: busy);
        await Tick(s, 10, procs: busy);
        using var manifest = JsonDocument.Parse(Assert.Single(await Traces()).Manifest!);
        Assert.Equal("sqlservr.exe", manifest.RootElement.GetProperty("trigger").GetProperty("process").GetString());
    }

    [Fact]
    public async Task AHangEvent_AScheduleAndAManualRequest_EachCapture()
    {
        var s = Service(new List<TraceTrigger> { new() { Name = "hang", Type = "hangEvent" }, new() { Name = "nightly", Type = "schedule", IntervalMin = 10 } },
            new TraceLimits { MaxDiskMB = 64, CooldownMin = 0, DailyCap = 10 });
        await Tick(s, 10);
        await Tick(s, 10, hangs: 1);
        await Tick(s, 10); // the recorder restarts
        _time.Advance(TimeSpan.FromMinutes(11));
        await Tick(s, 10);
        await Tick(s, 10);
        File.WriteAllText(Path.Combine(s.TraceDir, "capture.request"), "");
        await Tick(s, 10);
        var names = (await Traces()).Select(r => JsonDocument.Parse(r.Manifest!).RootElement.GetProperty("trigger").GetProperty("name").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "hang", "manual", "nightly" }, names);
    }

    [Fact]
    public async Task ABusyKernelLogger_IsNotFought_ItBacksOffAndSaysWhy()
    {
        _wpr.StartResult = new WprResult(-984076288, "There are no free slots for the kernel logger session.");
        var s = Service();
        await Tick(s, 95);
        Assert.False(s.Recording);
        Assert.Contains("another tool may be tracing", s.Error);
        Assert.Contains("no free slots", File.ReadAllText(Path.Combine(s.TraceDir, "status.json")));
        Assert.DoesNotContain("cancel", _wpr.Calls); // never cancels someone else's session
        Assert.DoesNotContain("setprofint 40000", _wpr.Calls); // nor changes the sampling interval it runs at
        var starts = _wpr.Calls.Count(c => c.StartsWith("start"));
        await Tick(s, 95, seconds: 60);
        Assert.Equal(starts, _wpr.Calls.Count(c => c.StartsWith("start")));
        _wpr.StartResult = new WprResult(0, "");
        await Tick(s, 95, seconds: 300);
        Assert.True(s.Recording);
        Assert.Null(s.Error);
    }

    [Fact]
    public async Task StoppingTheService_CancelsItsSession_AndPutsTheIntervalBack()
    {
        var s = Service();
        await Tick(s, 10);
        await s.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { "start PerfInsights.runtime.wprp", "setprofint 40000", "cancel", "setprofint 10000" }, _wpr.Calls);
    }

    [Fact]
    public async Task AFailedStop_QueuesNothing_ButStillCounts()
    {
        _wpr.StopFails = true;
        var s = Service(submission: DecisionMode.AlwaysSubmit);
        await Tick(s, 10);
        for (var i = 0; i < 3; i++) await Tick(s, 95);
        Assert.Empty(await Traces());
        Assert.Contains("Access is denied", s.Error);
        Assert.Single(await s.HistoryAsync(CancellationToken.None));
        // A session that would not stop is cancelled, or it would hold the kernel logger and every later start would fail.
        Assert.Equal(new[] { "stop", "cancel", "setprofint 10000" }, _wpr.Calls.SkipWhile(c => c != "stop").Take(3).ToArray());
    }

    [Fact]
    public async Task ATraceOverTheDiskBudget_IsDiscarded()
    {
        _wpr.EtlBytes = 2 * 1024 * 1024;
        var s = Service(limits: new TraceLimits { MaxDiskMB = 1, CooldownMin = 0, DailyCap = 9 });
        await Tick(s, 10);
        for (var i = 0; i < 3; i++) await Tick(s, 95);
        Assert.Empty(await Traces());
        Assert.Contains("over the 1 MB disk limit", s.Error);
        Assert.Empty(Directory.GetFiles(s.TraceDir, "*.zip"));
    }

    [Fact]
    public async Task Housekeeping_RemovesZipsNoLiveRecordNames()
    {
        var s = Service();
        Directory.CreateDirectory(s.TraceDir);
        var orphan = Path.Combine(s.TraceDir, "old.zip");
        File.WriteAllText(orphan, "x");
        await s.HousekeepingAsync(CancellationToken.None);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task Housekeeping_RemovesAnEtlACrashLeft_AndFailsAnOverBudgetTraceOnlyFromTheStateItRead()
    {
        var s = Service(limits: new TraceLimits { MaxDiskMB = 1, CooldownMin = 0, DailyCap = 9 });
        Directory.CreateDirectory(s.TraceDir);
        var etl = Path.Combine(s.TraceDir, "20261022T110000Z-cpu-high.etl");
        File.WriteAllText(etl, "x");
        Directory.CreateDirectory(etl + ".NGENPDB");
        var zip = Path.Combine(s.TraceDir, "big.zip");
        File.WriteAllBytes(zip, new byte[2 * 1024 * 1024]);
        await _store.UpsertAsync(new DumpRecord { Id = "t1", FilePath = zip, SizeBytes = 2 * 1024 * 1024, Sha256 = "t1", DumpType = DumpType.Trace,
            State = DumpQueueState.Queued, DetectedAtUtc = _time.GetUtcNow() });
        await s.HousekeepingAsync(CancellationToken.None);
        Assert.False(File.Exists(etl));
        Assert.False(Directory.Exists(etl + ".NGENPDB"));
        Assert.False(File.Exists(zip));
        var failed = Assert.Single(await _store.GetByStateAsync(DumpQueueState.Failed));
        Assert.Contains("maxDiskMB", failed.LastError);
    }

    [Fact]
    public async Task RuleBased_UsesOnlyRulesThatNameTraces()
    {
        // Review of #39: a dump rule with no dumpType ("anything under 200 MB: submit") sent traces without asking.
        var rules = new List<DumpRule>
        {
            new() { MaxSizeBytes = 200L * 1024 * 1024, Action = RuleAction.Submit },
        };
        var s = Service(submission: DecisionMode.RuleBased, rules: rules);
        await Tick(s, 10);
        for (var i = 0; i < 3; i++) await Tick(s, 95);
        await Task.Delay(100);
        Assert.Equal(DumpQueueState.PendingApproval, Assert.Single(await Traces()).State); // defaultRuleAction: Ask

        _wpr.Calls.Clear();
        var traceRule = new List<DumpRule> { new() { DumpType = DumpType.Trace, Action = RuleAction.Submit } };
        var t = Service(submission: DecisionMode.RuleBased, rules: traceRule, limits: new TraceLimits { MaxDiskMB = 64, CooldownMin = 0, DailyCap = 9 });
        await Tick(t, 10, seconds: 600);
        for (var i = 0; i < 3; i++) await Tick(t, 95);
        await Task.Delay(100);
        Assert.Contains(await Traces(), r => r.State == DumpQueueState.Queued);
    }

    [Fact]
    public async Task TracingTurnedOff_SkipsHeldTraces_AndRemovesTheirZips()
    {
        var dir = Path.Combine(_dir, "traces");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "held.zip");
        File.WriteAllText(zip, "x");
        await _store.UpsertAsync(new DumpRecord { Id = "held", FilePath = zip, SizeBytes = 1, Sha256 = "held", DumpType = DumpType.Trace,
            State = DumpQueueState.PendingApproval, DetectedAtUtc = _time.GetUtcNow() });
        var config = new AgentConfig { Tracing = new TracingConfig { Enabled = false } };
        var s = new TracingService(config, _store, _approvals, _tray, _wpr, _metrics, NullLogger<TracingService>.Instance, _time,
            dir, Path.Combine(_dir, "PerfInsights.wprp")) { RequireWindows = false };
        await s.StartAsync(CancellationToken.None);
        await s.ExecuteTask!;
        Assert.Equal(DumpQueueState.Skipped, Assert.Single(await _store.GetByStateAsync(DumpQueueState.Skipped)).State);
        Assert.False(File.Exists(zip));
        Assert.Empty(_wpr.Calls);
    }

    [Fact]
    public void ABadSectionTurnsTracingOff()
    {
        var config = new AgentConfig { Tracing = new TracingConfig { Enabled = true } };
        var s = new TracingService(config, _store, _approvals, _tray, _wpr, _metrics, NullLogger<TracingService>.Instance, _time,
            Path.Combine(_dir, "traces"), Path.Combine(_dir, "PerfInsights.wprp")) { RequireWindows = false };
        Assert.False(s.Setup());
        Assert.Contains("no triggers", s.Error);
    }
}

public sealed class MetricNeedsTests
{
    [Fact]
    public void OnlyWhatTheTriggersReadIsRead()
    {
        // Review of #39: every tick read every process and the event log whatever the triggers were.
        var none = MetricNeeds.For(new[] { new TraceTrigger { Name = "b", Type = "schedule", IntervalMin = 60 } });
        Assert.Equal(new MetricNeeds(false, false, false, false, false), none);
        var all = MetricNeeds.For(new[]
        {
            new TraceTrigger { Name = "c", Type = "cpuPercent", Threshold = 90 },
            new TraceTrigger { Name = "p", Type = "cpuPercent", Process = "sqlservr", Threshold = 90 },
            new TraceTrigger { Name = "l", Type = "loadPerCpu", Threshold = 2 },
            new TraceTrigger { Name = "d", Type = "diskLatencyMs", Threshold = 50 },
            new TraceTrigger { Name = "h", Type = "hangEvent" },
        });
        Assert.Equal(new MetricNeeds(true, true, true, true, true), all);
        Assert.True(MetricNeeds.For(new[] { new TraceTrigger { Name = "p", Type = "process", Process = "x", Threshold = 9 } }).Processes);
        Assert.False(MetricNeeds.For(new[] { new TraceTrigger { Name = "p", Type = "process", Process = "x", Threshold = 9 } }).Cpu);
    }
}
