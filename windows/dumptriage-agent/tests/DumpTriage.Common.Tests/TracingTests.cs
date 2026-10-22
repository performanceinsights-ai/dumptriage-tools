using System.IO.Compression;
using System.Text.Json;
using DumpTriage.Common.Config;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Tracing;
using DumpTriage.Common.Upload;

namespace DumpTriage.Common.Tests;

/// <summary>The Windows agent's tracing pieces that need no Windows (dumptriage-tools#29).</summary>
public class TracingTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "schemas"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void SharedTriggerFixture_EveryCaseGivesItsOutcome()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("tests", "fixtures", "tracing", "triggers.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var trig = c.GetProperty("trigger");
            var lim = c.GetProperty("limits");
            var limits = new TraceLimits { CooldownMin = lim.GetProperty("cooldownMin").GetInt32(), DailyCap = lim.GetProperty("dailyCap").GetInt32() };
            var history = c.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("atSec").GetDouble()).ToList();
            var runner = new TriggerRunner(trig.GetProperty("threshold").GetDouble(), trig.GetProperty("sustainSec").GetInt32());
            string outcome = "no_fire";
            double? at = null;
            foreach (var s in c.GetProperty("series").EnumerateArray())
            {
                var t = s.GetProperty("tSec").GetDouble();
                if (runner.Feed(t, s.GetProperty("value").GetDouble()))
                {
                    at = t;
                    outcome = CaptureLimiter.Decide(t, limits, history) switch
                    {
                        CaptureOutcome.Fire => "fire",
                        CaptureOutcome.Cooldown => "cooldown",
                        _ => "capped",
                    };
                    break;
                }
            }
            var expect = c.GetProperty("expect");
            Assert.True(expect.GetProperty("outcome").GetString() == outcome, c.GetProperty("name").GetString());
            if (expect.TryGetProperty("atSec", out var atSec)) Assert.Equal(atSec.GetDouble(), at);
        }
    }

    [Fact]
    public void TriggerRunner_NoReadingRestartsTheClockAndAMetTriggerMustBeSustainedAgain()
    {
        var r = new TriggerRunner(90, 10);
        Assert.False(r.Feed(0, 95));
        Assert.False(r.Feed(5, null));
        Assert.False(r.Feed(10, 95));
        Assert.True(r.Feed(20, 95));
        Assert.False(r.Feed(21, 95));
    }

    private static TracingConfig Tracing(params TraceTrigger[] triggers) => new() { Enabled = true, Triggers = triggers.ToList() };

    [Fact]
    public void Config_DefaultIsOffAndHeldForApproval_AndValidationNamesEachProblem()
    {
        var d = new TracingConfig();
        Assert.False(d.Enabled);
        Assert.Equal(DecisionMode.AskFirst, d.Submission);
        Assert.Equal(60, d.Limits.MaxDurationSec);
        Assert.Empty(Tracing(new TraceTrigger { Name = "cpu", Type = "cpuPercent", Threshold = 90, SustainSec = 30 }).Validate());
        var cases = new (TracingConfig Config, string Fragment)[]
        {
            (new TracingConfig { Enabled = true, Backend = "perf", Triggers = { new TraceTrigger { Name = "x", Type = "hangEvent" } } }, "backend 'perf'"),
            (Tracing(), "no triggers"),
            (Tracing(new TraceTrigger { Name = "x", Type = "cpuPercent" }), "needs a threshold"),
            (Tracing(new TraceTrigger { Name = "x", Type = "process", Threshold = 5 }), "needs a process name"),
            (Tracing(new TraceTrigger { Name = "x", Type = "schedule" }), "intervalMin"),
            (Tracing(new TraceTrigger { Name = "x", Type = "psiSomeAvg10", Threshold = 5 }), "Windows has none"),
            (Tracing(new TraceTrigger { Name = "a b", Type = "hangEvent" }), "name must be"),
            (Tracing(new TraceTrigger { Name = "x", Type = "gpu", Threshold = 1 }), "type 'gpu'"),
            (Tracing(new TraceTrigger { Name = "x", Type = "hangEvent" }, new TraceTrigger { Name = "x", Type = "hangEvent" }), "used twice"),
            (Tracing(new TraceTrigger { Name = "x", Type = "hangEvent", SustainSec = -1 }), "sustainSec"),
            (new TracingConfig { Enabled = true, Triggers = { new TraceTrigger { Name = "x", Type = "hangEvent" } },
                Limits = new TraceLimits { MaxDurationSec = 0, MaxDiskMB = 0, MaxOverheadPercent = 0, CooldownMin = -1, DailyCap = -1 } }, "maxDurationSec"),
        };
        foreach (var (config, fragment) in cases)
        {
            Assert.Contains(fragment, string.Join("; ", config.Validate()));
        }
    }

    [Fact]
    public void Config_ReadsTheSchemaKeysFromConfigJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "dt-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {"tracing": {"enabled": true, "backend": "wpr", "submission": "AlwaysSubmit",
              "triggers": [{"name": "sql", "type": "process", "process": "sqlservr", "threshold": 150, "sustainSec": 20}],
              "limits": {"maxDurationSec": 30, "maxDiskMB": 256, "maxOverheadPercent": 2, "cooldownMin": 10, "dailyCap": 2}}}
            """);
        try
        {
            var config = ConfigLoader.LoadOrCreateDefaultAsync(path).GetAwaiter().GetResult();
            Assert.True(config.Tracing.Enabled);
            Assert.Equal(DecisionMode.AlwaysSubmit, config.Tracing.Submission);
            Assert.Equal("sqlservr", config.Tracing.Triggers[0].Process);
            Assert.Equal(256, config.Tracing.Limits.MaxDiskMB);
            Assert.Empty(config.Tracing.Validate());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Manifest_HasTheSchemaFieldsAndNoHostname()
    {
        var json = TraceManifest.Build(new TraceTrigger { Name = "sql", Type = "process", Process = "sqlservr", Threshold = 150 }, 212.345,
            new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 22, 10, 1, 0, TimeSpan.Zero), 90,
            "10.0.20348", "wpr -start PerfInsights.wprp!FlightRecorder; wpr -stop", "20348", "0123456789abcdef", "1.2.3");
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        using var schema = JsonDocument.Parse(File.ReadAllText(RepoFile("schemas", "trace-manifest.schema.json")));
        foreach (var required in schema.RootElement.GetProperty("required").EnumerateArray()) Assert.True(m.TryGetProperty(required.GetString()!, out _), required.GetString());
        foreach (var p in m.EnumerateObject()) Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty(p.Name, out _), p.Name);
        Assert.Equal("etl", m.GetProperty("traceType").GetString());
        Assert.Equal(212.345, m.GetProperty("trigger").GetProperty("observed").GetDouble());
        Assert.Equal("sqlservr", m.GetProperty("trigger").GetProperty("process").GetString());
        Assert.Equal("2026-10-22T10:00:00Z", m.GetProperty("window").GetProperty("startUtc").GetString());
        Assert.Equal("windows", m.GetProperty("os").GetProperty("family").GetString());
        Assert.DoesNotContain("hostname", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Archive_PutsTheEtlFirstAndCarriesNgenPdbs()
    {
        var dir = Directory.CreateTempSubdirectory("dt-zip-").FullName;
        try
        {
            var etl = Path.Combine(dir, "x.etl");
            File.WriteAllBytes(etl, new byte[4096]);
            Directory.CreateDirectory(etl + ".NGENPDB");
            File.WriteAllText(Path.Combine(etl + ".NGENPDB", "mscorlib.ni.pdb"), "pdb");
            var zip = Path.Combine(dir, "x.zip");
            TraceArchive.Create(etl, zip);
            using var archive = ZipFile.OpenRead(zip);
            Assert.Equal(new[] { "trace.etl", "trace.etl.NGENPDB/mscorlib.ni.pdb" }, archive.Entries.Select(e => e.FullName));
            // The service sniffs the first local header: name length at 26, name at 30 (website trace_ingestion._is_etl_zip).
            var head = File.ReadAllBytes(zip).AsSpan(0, 64).ToArray();
            Assert.Equal("trace.etl", System.Text.Encoding.UTF8.GetString(head, 30, BitConverter.ToUInt16(head, 26)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Profile_ShippedOneParsesAndTheRingFollowsTheDiskBudget()
    {
        var template = File.ReadAllText(RepoFile("windows", "PerfInsights.wprp"));
        System.Xml.Linq.XDocument.Parse(template);
        Assert.Contains("Id=\"FlightRecorder.Verbose.Memory\"", template);
        Assert.Contains("<Stack Value=\"CSwitch\"/>", template);
        Assert.DoesNotContain("<Stack Value=\"ReadyThread\"/>", template); // lighter than the lab draft
        Assert.Contains("<Buffers Value=\"128\"/>", WprProfile.Render(template, new TraceLimits { MaxDiskMB = 512 }));
        Assert.Contains("<Buffers Value=\"16\"/>", WprProfile.Render(template, new TraceLimits { MaxDiskMB = 10 }));
        Assert.Contains("<Buffers Value=\"256\"/>", WprProfile.Render(template, new TraceLimits { MaxDiskMB = 100000 }));
    }

    [Fact]
    public void Profile_NamesJitCompiledCode_AndOnlyTheKernelRingFollowsTheBudget()
    {
        // Without the CLR runtime and rundown providers, 85% of a PowerShell workload's busy samples had an
        // unresolved root frame on the lab VM; with them, 1% (website#2002).
        var template = File.ReadAllText(RepoFile("windows", "PerfInsights.wprp"));
        var doc = System.Xml.Linq.XDocument.Parse(template);
        var providers = doc.Descendants("EventProvider").ToDictionary(e => (string)e.Attribute("Name")!, e => e);
        Assert.True(providers.ContainsKey("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4")); // Microsoft-Windows-DotNETRuntime
        Assert.Equal("true", (string?)providers["a669021c-c450-4609-a035-5af59af4df18"].Attribute("CaptureStateOnly")); // rundown
        var rendered = System.Xml.Linq.XDocument.Parse(WprProfile.Render(template, new TraceLimits { MaxDiskMB = 1024 }));
        Assert.Equal("256", (string?)rendered.Descendants("SystemCollector").Single().Element("Buffers")!.Attribute("Value"));
        Assert.Equal("32", (string?)rendered.Descendants("EventCollector").Single().Element("Buffers")!.Attribute("Value"));
    }

    [Fact]
    public async Task Queue_KeepsATraceAndItsManifest_AndAnOldQueueGetsTheColumn()
    {
        var dir = Directory.CreateTempSubdirectory("dt-q-").FullName;
        try
        {
            var db = Path.Combine(dir, "queue.db");
            await using (var store = await DumpQueueStore.OpenAsync(db))
            {
                await store.TryInsertAsync(new DumpRecord
                {
                    Id = "t1", FilePath = "C:\\x.zip", SizeBytes = 5, DumpType = DumpType.Trace, State = DumpQueueState.Queued,
                    DetectedAtUtc = DateTimeOffset.UtcNow, Manifest = "{\"traceType\":\"etl\"}",
                });
                var back = await store.FindByIdAsync("t1");
                Assert.Equal(DumpType.Trace, back!.DumpType);
                Assert.Equal("{\"traceType\":\"etl\"}", back.Manifest);
                back.Manifest = "{\"x\":1}";
                await store.UpsertAsync(back);
                Assert.Equal("{\"x\":1}", (await store.FindByIdAsync("t1"))!.Manifest);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ApiClient_UploadsATraceWithItsManifest()
    {
        var handler = new CaptureHandler();
        var client = new DumpTriageApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example") });
        var file = Path.GetTempFileName();
        File.WriteAllBytes(file, new byte[] { 0x50, 0x4b, 3, 4 });
        try
        {
            var result = await client.UploadTraceAsync(file, "{\"traceType\":\"etl\"}", "dts_key", "host-1", "abc");
            Assert.Equal(UploadOutcome.Success, result.Outcome);
            Assert.Equal("/api/v1/traces", handler.Path);
            Assert.Contains("name=manifest", handler.Body);
            Assert.Contains("{\"traceType\":\"etl\"}", handler.Body);
            Assert.Contains("application/zip", handler.Body);
            Assert.Equal("abc", handler.Sha);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ApiClient_TracesOffIsTransientNotAKeyProblem()
    {
        Assert.Equal(UploadOutcome.TransientFailure,
            DumpTriageApiClient.Classify(System.Net.HttpStatusCode.Forbidden, "{\"detail\":\"traces_disabled: performance traces are not enabled on this service yet\"}"));
        Assert.Equal(UploadOutcome.CredentialsRejected, DumpTriageApiClient.Classify(System.Net.HttpStatusCode.Forbidden, "{}"));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Path = "", Body = "";
        public string? Sha;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            Sha = request.Headers.TryGetValues(DumpTriageApiClient.ContentSha256Header, out var v) ? v.First() : null;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent("{}") };
        }
    }
}
