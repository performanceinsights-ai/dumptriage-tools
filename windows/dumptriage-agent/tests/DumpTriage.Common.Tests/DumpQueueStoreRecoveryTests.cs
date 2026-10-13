using DumpTriage.Common.Models;
using DumpTriage.Common.Queue;

namespace DumpTriage.Common.Tests;

public class DumpQueueStoreRecoveryTests
{
    private static DumpRecord Make(string id, string path, DumpQueueState state, DateTimeOffset detected) => new()
    {
        Id = id,
        FilePath = path,
        SizeBytes = 1,
        DumpType = DumpType.KernelMode,
        State = state,
        DetectedAtUtc = detected,
    };

    [Fact]
    public async Task RecoverInterruptedSubmissions_RequeuesOnlySubmitting()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await store.UpsertAsync(Make("a", "a.dmp", DumpQueueState.Submitting, now));
            await store.UpsertAsync(Make("b", "b.dmp", DumpQueueState.Submitted, now));

            Assert.Equal(1, await store.RecoverInterruptedSubmissionsAsync());

            Assert.Single(await store.GetByStateAsync(DumpQueueState.Queued));
            Assert.Single(await store.GetByStateAsync(DumpQueueState.Submitted));
            Assert.Empty(await store.GetByStateAsync(DumpQueueState.Submitting));
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task FindById_ReturnsRecordRegardlessOfPathHistory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await store.UpsertAsync(Make("a", "MEMORY.DMP", DumpQueueState.Submitted, now.AddDays(-2)));
            await store.UpsertAsync(Make("b", "MEMORY.DMP", DumpQueueState.Submitted, now.AddDays(-1)));

            var found = await store.FindByIdAsync("a");
            Assert.Equal(DumpQueueState.Submitted, found!.State);
            Assert.Null(await store.FindByIdAsync("missing"));
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task FindByFilePath_ReturnsMostRecentWhenPathReused()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await store.UpsertAsync(Make("new", "MEMORY.DMP", DumpQueueState.Queued, now));
            await store.UpsertAsync(Make("old", "MEMORY.DMP", DumpQueueState.Submitted, now.AddDays(-1)));

            Assert.Equal("new", (await store.FindByFilePathAsync("MEMORY.DMP"))!.Id);
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ConcurrentUpserts_DoNotCorrupt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
                Task.Run(() => store.UpsertAsync(Make($"id{i}", $"{i}.dmp", DumpQueueState.Queued, now)))));

            Assert.Equal(50, (await store.GetByStateAsync(DumpQueueState.Queued)).Count);
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }
}
