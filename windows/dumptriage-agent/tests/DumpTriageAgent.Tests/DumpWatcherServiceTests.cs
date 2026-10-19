using DumpTriage.Common.Detection;
using DumpTriage.Common.Config;
using DumpTriage.Common.Models;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriageAgent.Ipc;
using DumpTriageAgent.Runtime;
using DumpTriageAgent.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace DumpTriageAgent.Tests;

public class DumpWatcherServiceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-watch-" + Guid.NewGuid().ToString("N"));
    private DumpQueueStore _store = null!;
    private DumpWatcherService _watcher = null!;
    private AgentRuntimeState _state = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await DumpQueueStore.OpenAsync(Path.Combine(_dir, "queue.db"));
        _state = new AgentRuntimeState();
        var coordinator = new ApprovalCoordinator();
        var pipe = new PipeServerHostedService(coordinator, _state, NullLogger<PipeServerHostedService>.Instance);
        var config = new AgentConfig
        {
            WatchedPaths = new List<string>(),
            Policy = new AgentPolicyConfig { Mode = DecisionMode.AlwaysSubmit },
        };
        _watcher = new DumpWatcherService(config, _store, coordinator, _state, pipe, NullLogger<DumpWatcherService>.Instance)
        {
            StabilityPollInterval = TimeSpan.FromMilliseconds(10),
            StabilityQuietPeriod = TimeSpan.FromMilliseconds(30),
            StabilityTimeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task DisposeAsync()
    {
        _watcher.Dispose();
        await _store.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> WriteDumpAsync(string name, byte[]? content = null)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, content ?? "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        return path;
    }

    private async Task<List<DumpRecord>> AllAsync()
    {
        var all = new List<DumpRecord>();
        foreach (var state in Enum.GetValues<DumpQueueState>())
        {
            all.AddRange(await _store.GetByStateAsync(state));
        }
        return all;
    }

    [Fact]
    public async Task A_new_dump_is_recorded_and_queued()
    {
        var path = await WriteDumpAsync("a.dmp");

        await _watcher.ProcessCandidateAsync(path, CancellationToken.None);

        var record = Assert.Single(await AllAsync());
        Assert.Equal(path, record.FilePath);
        Assert.Equal(DumpQueueState.Queued, record.State);
    }

    [Fact]
    public async Task A_new_crash_written_over_an_already_seen_path_is_a_new_dump()
    {
        // website#1486: MEMORY.DMP is rewritten by every kernel crash; once its path
        // was known, every later crash there was skipped silently.
        var path = await WriteDumpAsync("MEMORY.DMP");
        await _watcher.ProcessCandidateAsync(path, CancellationToken.None);
        var first = Assert.Single(await AllAsync());
        first.State = DumpQueueState.Submitted;
        await _store.UpsertAsync(first);

        await File.WriteAllBytesAsync(path, "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        await _watcher.ProcessCandidateAsync(path, CancellationToken.None);

        var all = await AllAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.Equal(path, r.FilePath));
        var second = Assert.Single(all, r => r.Id != first.Id);
        Assert.Equal(DumpQueueState.Queued, second.State);
        Assert.Equal(DumpQueueState.Submitted, Assert.Single(all, r => r.Id == first.Id).State);
    }

    [Fact]
    public async Task A_dump_rewritten_in_place_is_picked_up_by_the_running_watcher()
    {
        // website#1486: an in-place rewrite raises only Changed, which wasn't watched.
        var watched = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(watched);
        var coordinator = new ApprovalCoordinator();
        var pipe = new PipeServerHostedService(coordinator, _state, NullLogger<PipeServerHostedService>.Instance);
        var config = new AgentConfig
        {
            WatchedPaths = new List<string> { watched },
            Policy = new AgentPolicyConfig { Mode = DecisionMode.AlwaysSubmit },
        };
        using var watcher = new DumpWatcherService(config, _store, coordinator, _state, pipe, NullLogger<DumpWatcherService>.Instance)
        {
            StabilityPollInterval = TimeSpan.FromMilliseconds(20),
            StabilityQuietPeriod = TimeSpan.FromMilliseconds(100),
            StabilityTimeout = TimeSpan.FromSeconds(10),
        };
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            var path = Path.Combine(watched, "MEMORY.dmp");
            await File.WriteAllBytesAsync(path, "PAGEDU64"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            await WaitForAsync(async () => (await AllAsync()).Count == 1);

            // The next crash: same file, same length, written in place.
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync("PAGEDU64"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            await WaitForAsync(async () => (await AllAsync()).Count == 2);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the watcher");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_dump_written_while_the_service_was_stopped_is_picked_up_on_start()
    {
        // website#1514: only watcher events were acted on, and a dump already there
        // when the service starts (e.g. MEMORY.DMP written early in the boot
        // after a crash) raises none.
        var watched = Path.Combine(_dir, "watched-start");
        Directory.CreateDirectory(watched);
        var memory = Path.Combine(watched, "MEMORY.DMP");
        await File.WriteAllBytesAsync(memory, "PAGEDU64"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        var old = Path.Combine(watched, "old.dmp");
        await File.WriteAllBytesAsync(old, "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));
        await File.WriteAllTextAsync(Path.Combine(watched, "notes.txt"), "not a dump");

        using var watcher = WatcherFor(watched, startupScanMaxAgeDays: 30);
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () => (await AllAsync()).Count == 1);
            await Task.Delay(300); // nothing else turns up: the old dump and the .txt are left alone
            var record = Assert.Single(await AllAsync());
            Assert.Equal(memory, record.FilePath);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }

        // The next start finds the same file already in the queue.
        using var again = WatcherFor(watched, startupScanMaxAgeDays: 30);
        await again.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500);
            Assert.Single(await AllAsync());
        }
        finally
        {
            await again.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_copy_of_a_known_dump_is_not_hashed_again_on_the_next_start()
    {
        // website#1514 review: the scan's cheap skip matched the record's own path
        // only, so a copy (or a row from before website#1486 kept last-write times)
        // was hashed again on every start.
        var content = "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        await _watcher.ProcessCandidateAsync(await WriteDumpAsync("a.dmp", content), CancellationToken.None);
        var copy = await WriteDumpAsync("copy.dmp", content);
        await _watcher.ProcessCandidateAsync(copy, CancellationToken.None);

        var seen = FileIdentity.Read(copy);
        var found = await _store.FindByFileIdentityAsync(copy, seen.SizeBytes, seen.LastWriteUtc);
        Assert.NotNull(found);
        Assert.Single(await AllAsync());
    }

    [Fact]
    public async Task The_startup_scan_can_be_turned_off()
    {
        var watched = Path.Combine(_dir, "watched-off");
        Directory.CreateDirectory(watched);
        await File.WriteAllBytesAsync(Path.Combine(watched, "a.dmp"), "MDMP"u8.ToArray());

        using var watcher = WatcherFor(watched, startupScanMaxAgeDays: 0);
        Assert.Equal(0, watcher.ScanExisting(watched));
        Assert.False(watcher.Candidates.TryRead(out _));
    }

    private DumpWatcherService WatcherFor(string watched, int startupScanMaxAgeDays)
    {
        var coordinator = new ApprovalCoordinator();
        var pipe = new PipeServerHostedService(coordinator, _state, NullLogger<PipeServerHostedService>.Instance);
        var config = new AgentConfig
        {
            WatchedPaths = new List<string> { watched },
            StartupScanMaxAgeDays = startupScanMaxAgeDays,
            Policy = new AgentPolicyConfig { Mode = DecisionMode.AlwaysSubmit },
        };
        return new DumpWatcherService(config, _store, coordinator, _state, pipe, NullLogger<DumpWatcherService>.Instance)
        {
            StabilityPollInterval = TimeSpan.FromMilliseconds(20),
            StabilityQuietPeriod = TimeSpan.FromMilliseconds(100),
            StabilityTimeout = TimeSpan.FromSeconds(10),
        };
    }

    [Fact]
    public void One_check_per_path_at_a_time_and_a_rerun_if_events_arrived_meanwhile()
    {
        Assert.True(_watcher.TryStartCandidate("C:\\Windows\\MEMORY.DMP"));
        Assert.False(_watcher.TryStartCandidate("C:\\Windows\\MEMORY.DMP"));
        Assert.True(_watcher.FinishCandidate("C:\\Windows\\MEMORY.DMP")); // an event arrived: run again

        Assert.True(_watcher.TryStartCandidate("C:\\Windows\\MEMORY.DMP"));
        Assert.False(_watcher.FinishCandidate("C:\\Windows\\MEMORY.DMP"));
    }

    [Fact]
    public async Task The_same_unchanged_file_seen_twice_is_one_record()
    {
        var path = await WriteDumpAsync("a.dmp");

        await _watcher.ProcessCandidateAsync(path, CancellationToken.None);
        await _watcher.ProcessCandidateAsync(path, CancellationToken.None);

        var record = Assert.Single(await AllAsync());
        Assert.NotNull(record.FileLastWriteUtc);
    }

    [Fact]
    public async Task A_dump_still_held_open_past_the_stability_timeout_is_looked_at_again_later()
    {
        // website#1493: a slow writer that ran past the timeout was never looked at again.
        _watcher.StabilityTimeout = TimeSpan.FromMilliseconds(200);
        _watcher.StabilityRetryDelay = TimeSpan.FromMilliseconds(50);
        var path = await WriteDumpAsync("slow.dmp");

        await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await _watcher.ProcessCandidateAsync(path, CancellationToken.None);
        }

        Assert.Empty(await AllAsync());
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(path, await _watcher.Candidates.ReadAsync(wait.Token));

        _watcher.StabilityTimeout = TimeSpan.FromSeconds(5);
        await _watcher.ProcessCandidateAsync(path, CancellationToken.None);
        Assert.Single(await AllAsync());
    }

    [Fact]
    public async Task A_rescan_request_from_the_upload_worker_queues_the_path()
    {
        var path = await WriteDumpAsync("a.dmp");

        _state.RequestRescan(path);

        Assert.True(_watcher.Candidates.TryRead(out var queued));
        Assert.Equal(path, queued);
    }

    [Fact]
    public async Task The_same_bytes_at_a_second_path_leave_the_finished_record_alone()
    {
        // website#1491: the upsert reset the Submitted row to Detected (then Queued)
        // with a fresh retry count, and the dump was uploaded again.
        var content = "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        var first = await WriteDumpAsync("a.dmp", content);
        await _watcher.ProcessCandidateAsync(first, CancellationToken.None);
        var record = Assert.Single(await AllAsync());
        record.State = DumpQueueState.Submitted;
        record.RetryCount = 2;
        record.SubmittedAtUtc = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(record);

        var copy = await WriteDumpAsync("copy.dmp", content);
        await _watcher.ProcessCandidateAsync(copy, CancellationToken.None);

        var after = Assert.Single(await AllAsync());
        Assert.Equal(DumpQueueState.Submitted, after.State);
        Assert.Equal(first, after.FilePath);
        Assert.Equal(2, after.RetryCount);
    }

    [Fact]
    public async Task A_fresh_install_leaves_dumps_from_before_its_first_start_alone()
    {
        // dumptriage-tools#5: a new install offered every old dump in its folders.
        var watched = Path.Combine(_dir, "watched-fresh");
        Directory.CreateDirectory(watched);
        await File.WriteAllBytesAsync(Path.Combine(watched, "before.dmp"), "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        await Task.Delay(50);
        await _store.SetMetaAsync(DumpQueueStore.WatchingSinceKey, DateTimeOffset.UtcNow.ToString("O"));
        await Task.Delay(50);
        var after = Path.Combine(watched, "after.dmp");
        await File.WriteAllBytesAsync(after, "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

        using var watcher = WatcherFor(watched, startupScanMaxAgeDays: 30);
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () => (await AllAsync()).Count == 1);
            await Task.Delay(300);
            Assert.Equal(after, Assert.Single(await AllAsync()).FilePath);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_upgrade_from_a_queue_without_a_first_start_filters_nothing()
    {
        // A queue.db from before the setting has seen everything in its folders,
        // so a dump written while the service was down for the upgrade still counts.
        var watched = Path.Combine(_dir, "watched-upgrade");
        Directory.CreateDirectory(watched);
        var dump = Path.Combine(watched, "a.dmp");
        await File.WriteAllBytesAsync(dump, "MDMP"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "queue.db")};Pooling=False"))
        {
            await db.OpenAsync();
            var delete = db.CreateCommand();
            delete.CommandText = "DELETE FROM meta;";
            await delete.ExecuteNonQueryAsync();
        }
        await Task.Delay(50);

        using var watcher = WatcherFor(watched, startupScanMaxAgeDays: 30);
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () => (await AllAsync()).Count == 1);
            Assert.Equal(DateTimeOffset.UnixEpoch.ToString("O"), await _store.GetMetaAsync(DumpQueueStore.WatchingSinceKey));
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_dump_left_waiting_for_a_decision_is_decided_on_the_next_start()
    {
        // Its approval wait lived only in memory; nothing picked it up again.
        var path = await WriteDumpAsync("pending.dmp");
        var identity = FileIdentity.Read(path);
        var sha256 = await DumpHasher.ComputeSha256Async(path);
        await _store.UpsertAsync(new DumpRecord
        {
            Id = sha256,
            FilePath = path,
            SizeBytes = identity.SizeBytes,
            FileLastWriteUtc = identity.LastWriteUtc,
            Sha256 = sha256,
            DumpType = DumpType.UserMode,
            State = DumpQueueState.PendingApproval,
            DetectedAtUtc = DateTimeOffset.UtcNow,
        });

        using var watcher = WatcherFor(Path.Combine(_dir, "nothing-here"), startupScanMaxAgeDays: 0);
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () => Assert.Single(await AllAsync()).State == DumpQueueState.Queued);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }
}
