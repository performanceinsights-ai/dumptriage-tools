using DumpTriage.Common.Policy;
using System.Net;
using DumpTriage.Common.Config;
using DumpTriage.Common.Detection;
using DumpTriage.Common.Idle;
using DumpTriage.Common.Models;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Security;
using DumpTriage.Common.Upload;
using DumpTriageAgent.Runtime;
using DumpTriageAgent.Upload;
using Microsoft.Extensions.Logging.Abstractions;

namespace DumpTriageAgent.Tests;

/// <summary>Stands in for POST /api/v1/dumps: reads the whole body like the server, then answers.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, long BodyBytes)> Requests { get; } = new();

    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });

    public void RespondWith(HttpStatusCode status, string body = "{}") =>
        Respond = _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add((request, body.LongLength));
        return await Respond(request);
    }
}

internal sealed class PlainKeyProtector : IApiKeyProtector
{
    public string Protect(string plaintextApiKey) => plaintextApiKey;
    public string Unprotect(string protectedApiKey) => protectedApiKey;
}

internal sealed class NeverIdle : IIdleTimeProvider
{
    public TimeSpan GetIdleDuration() => TimeSpan.Zero;
}

internal sealed class WorkerHarness : IAsyncDisposable
{
    public required string Dir { get; init; }
    public required DumpQueueStore Store { get; init; }
    public required FakeServer Server { get; init; }
    public required AgentRuntimeState State { get; init; }
    public required AgentConfig Config { get; init; }
    public required UploadWorkerService Worker { get; init; }

    /// <param name="firstRun">
    /// False: an agent that has taken a scheduled slot before (long ago), so the next slot time
    /// flushes. True: a fresh install, which waits for its first slot (dumptriage-tools#9).
    /// </param>
    public static async Task<WorkerHarness> CreateAsync(
        CentralCopyConfig? centralCopy = null, BatchingConfig? batching = null, bool firstRun = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dt-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = await DumpQueueStore.OpenAsync(Path.Combine(dir, "queue.db"));
        if (!firstRun)
        {
            await store.SetMetaAsync(UploadWorkerService.LastFlushKey, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("O"));
        }
        var server = new FakeServer();
        var state = new AgentRuntimeState();
        var config = new AgentConfig
        {
            ProtectedApiKey = "dts_key",
            CentralCopy = centralCopy ?? new CentralCopyConfig(),
            Policy = new AgentPolicyConfig { Batching = batching ?? new BatchingConfig() },
        };
        var worker = NewWorker(config, store, server, state);
        return new WorkerHarness { Dir = dir, Store = store, Server = server, State = state, Config = config, Worker = worker };
    }

    /// <summary>Another worker on the same queue, as after a service restart.</summary>
    public UploadWorkerService Restart() => NewWorker(Config, Store, Server, State);

    private static UploadWorkerService NewWorker(AgentConfig config, DumpQueueStore store, FakeServer server, AgentRuntimeState state)
    {
        var api = new DumpTriageApiClient(new HttpClient(server) { BaseAddress = new Uri("https://api.example") });
        return new UploadWorkerService(
            config, store, api, new NeverIdle(), new PlainKeyProtector(), state, NullLogger<UploadWorkerService>.Instance);
    }

    public async Task<DumpRecord> AddDumpAsync(
        string name, DumpQueueState state = DumpQueueState.Queued, byte[]? content = null)
    {
        var path = Path.Combine(Dir, name);
        await File.WriteAllBytesAsync(path, content ?? "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        // Clearly in the past, so a test's own rewrite gets a different time.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var sha256 = await DumpHasher.ComputeSha256Async(path);
        var record = new DumpRecord
        {
            Id = sha256,
            FilePath = path,
            SizeBytes = new FileInfo(path).Length,
            FileLastWriteUtc = FileIdentity.Read(path).LastWriteUtc,
            Sha256 = sha256,
            DumpType = DumpType.UserMode,
            State = state,
            DetectedAtUtc = DateTimeOffset.UtcNow,
        };
        await Store.UpsertAsync(record);
        return record;
    }

    public async Task<DumpRecord> ReloadAsync(DumpRecord record)
    {
        foreach (var state in Enum.GetValues<DumpQueueState>())
        {
            var match = (await Store.GetByStateAsync(state)).FirstOrDefault(r => r.Id == record.Id);
            if (match is not null) return match;
        }
        throw new InvalidOperationException($"record {record.Id} not found");
    }

    /// <summary>Moves a record's next attempt into the past, as if its retry backoff had run out.</summary>
    public async Task EndBackoffAsync(DumpRecord record)
    {
        var reloaded = await ReloadAsync(record);
        reloaded.NextAttemptAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        await Store.UpsertAsync(reloaded);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Worker.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
