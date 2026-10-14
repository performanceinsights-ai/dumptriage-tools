using System.Net;
using DumpTriage.Common.Config;
using DumpTriage.Common.Idle;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Security;
using DumpTriage.Common.Upload;
using DumpTriageAgent.Runtime;
using DumpTriageAgent.Upload;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DumpTriageAgent.Tests;

/// <summary>Upload timing: backoff (#6), refused keys, scheduled slots (#9).</summary>
public sealed class UploadWorkerServiceTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-upload-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly Queue<HttpStatusCode> _responses = new();
    private int _requests;
    private DumpQueueStore? _queue;

    public async ValueTask DisposeAsync()
    {
        if (_queue is not null) await _queue.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Handler(UploadWorkerServiceTests t) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            t._requests++;
            var status = t._responses.Count > 0 ? t._responses.Dequeue() : HttpStatusCode.Created;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private sealed class PlainKey : IApiKeyProtector
    {
        public string Protect(string plaintextApiKey) => plaintextApiKey;
        public string Unprotect(string protectedApiKey) => protectedApiKey;
    }

    private sealed class NotIdle : IIdleTimeProvider
    {
        public TimeSpan GetIdleDuration() => TimeSpan.Zero;
    }

    private async Task<UploadWorkerService> Worker(BatchingConfig? batching = null)
    {
        Directory.CreateDirectory(_dir);
        _queue = await DumpQueueStore.OpenAsync(Path.Combine(_dir, "queue.db"));
        var config = new AgentConfig
        {
            ApiBaseUrl = "http://test",
            ProtectedApiKey = "dts_test",
            SendHostname = false,
            Policy = new AgentPolicyConfig { Batching = batching ?? new BatchingConfig() },
        };
        var client = new DumpTriageApiClient(new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://test") });
        return new UploadWorkerService(config, _queue, client, new NotIdle(), new PlainKey(), new AgentRuntimeState(),
            NullLogger<UploadWorkerService>.Instance, time: _time);
    }

    private async Task<DumpRecord> Queued(string name)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, "MDMP"u8.ToArray());
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime.AddMinutes(-1)); // written before detection
        var record = new DumpRecord
        {
            Id = name, FilePath = path, SizeBytes = 4, State = DumpQueueState.Queued, DetectedAtUtc = _time.GetUtcNow(),
        };
        await _queue!.UpsertAsync(record);
        return record;
    }

    private async Task<DumpRecord> Get(string id) => (await _queue!.FindByIdAsync(id))!;

    [Fact]
    public async Task Transient_failures_back_off_instead_of_failing_within_minutes()
    {
        var worker = await Worker();
        await Queued("a.dmp");
        _responses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _responses.Enqueue(HttpStatusCode.ServiceUnavailable);

        await worker.TickAsync(default);
        var a = await Get("a.dmp");
        Assert.Equal((DumpQueueState.Queued, 1), (a.State, a.RetryCount));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(60), a.NextAttemptUtc);

        _time.Advance(TimeSpan.FromSeconds(30));
        await worker.TickAsync(default);
        Assert.Equal(1, _requests); // not due yet

        _time.Advance(TimeSpan.FromSeconds(31));
        await worker.TickAsync(default); // 2nd failure: 120 s
        _time.Advance(TimeSpan.FromSeconds(121));
        await worker.TickAsync(default);
        Assert.Equal(DumpQueueState.Submitted, (await Get("a.dmp")).State);
        Assert.Null((await Get("a.dmp")).NextAttemptUtc);
    }

    [Fact]
    public async Task A_dump_changed_after_detection_is_not_uploaded_under_the_old_id()
    {
        var worker = await Worker();
        var a = await Queued("a.dmp");
        File.SetLastWriteTimeUtc(a.FilePath, _time.GetUtcNow().UtcDateTime.AddMinutes(1)); // overwritten in place
        await worker.TickAsync(default);
        Assert.Equal(0, _requests);
        Assert.Equal(DumpQueueState.Skipped, (await Get("a.dmp")).State);
    }

    [Fact]
    public async Task A_dump_only_touched_after_detection_is_still_uploaded()
    {
        var worker = await Worker();
        var a = await Queued("a.dmp");
        a.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("MDMP"u8.ToArray())).ToLowerInvariant();
        await _queue!.UpsertAsync(a);
        File.SetLastWriteTimeUtc(a.FilePath, _time.GetUtcNow().UtcDateTime.AddMinutes(1)); // same content, newer time
        await worker.TickAsync(default);
        Assert.Equal(DumpQueueState.Submitted, (await Get("a.dmp")).State);
    }

    [Fact]
    public async Task Fails_only_after_the_last_attempt()
    {
        var worker = await Worker();
        await Queued("a.dmp");
        for (var i = 0; i < RetryPolicy.MaxAttempts; i++) _responses.Enqueue(HttpStatusCode.BadGateway);
        for (var i = 0; i < RetryPolicy.MaxAttempts; i++)
        {
            await worker.TickAsync(default);
            _time.Advance(TimeSpan.FromHours(1));
        }
        Assert.Equal((DumpQueueState.Failed, RetryPolicy.MaxAttempts), ((await Get("a.dmp")).State, (await Get("a.dmp")).RetryCount));
    }

    [Fact]
    public async Task A_refused_key_pauses_uploads_longer_each_time_and_uses_no_attempt()
    {
        var worker = await Worker();
        await Queued("a.dmp");
        await Queued("b.dmp");
        _responses.Enqueue(HttpStatusCode.Unauthorized);
        _responses.Enqueue(HttpStatusCode.Unauthorized);

        await worker.TickAsync(default);
        Assert.Equal(1, _requests); // b isn't tried with a refused key
        Assert.Equal(0, (await Get("a.dmp")).RetryCount);
        _time.Advance(TimeSpan.FromMinutes(11));
        await worker.TickAsync(default); // refused again: now 20 minutes
        _time.Advance(TimeSpan.FromMinutes(15));
        await worker.TickAsync(default);
        Assert.Equal(2, _requests);
        _time.Advance(TimeSpan.FromMinutes(6));
        await worker.TickAsync(default);
        Assert.Equal(DumpQueueState.Submitted, (await Get("a.dmp")).State);
        Assert.Equal(DumpQueueState.Submitted, (await Get("b.dmp")).State);
    }

    [Fact]
    public async Task A_scheduled_slot_with_nothing_queued_still_counts()
    {
        // Local time = UTC here only if the machine is UTC; use the slot relative to local midnight.
        var local = _time.GetUtcNow().ToLocalTime();
        var slot = TimeOnly.FromDateTime(local.AddHours(1).DateTime);
        var worker = await Worker(new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { slot } });

        await worker.TickAsync(default); // first run: waits for the next slot
        _time.Advance(TimeSpan.FromHours(2));
        await worker.TickAsync(default); // the slot passes with nothing queued
        await Queued("a.dmp");
        await worker.TickAsync(default);
        Assert.Equal(0, _requests); // waits for tomorrow's slot

        _time.Advance(TimeSpan.FromHours(23));
        await worker.TickAsync(default);
        Assert.Equal(DumpQueueState.Submitted, (await Get("a.dmp")).State);
    }

    [Fact]
    public async Task A_transient_failure_in_a_scheduled_slot_is_retried_on_its_backoff_not_the_next_slot()
    {
        var local = _time.GetUtcNow().ToLocalTime();
        var slot = TimeOnly.FromDateTime(local.AddHours(1).DateTime);
        var worker = await Worker(new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { slot } });
        await worker.TickAsync(default); // first run
        await Queued("a.dmp");
        _responses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _time.Advance(TimeSpan.FromHours(1));
        await worker.TickAsync(default); // the slot: 503
        Assert.Equal(1, _requests);
        _time.Advance(TimeSpan.FromSeconds(61));
        await worker.TickAsync(default); // retried within minutes, not tomorrow
        Assert.Equal(DumpQueueState.Submitted, (await Get("a.dmp")).State);
    }
}
