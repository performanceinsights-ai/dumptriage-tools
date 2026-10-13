using DumpTriage.Common.Models;
using DumpTriage.Common.Queue;

namespace DumpTriage.Common.Tests;

public class DumpQueueStoreTests
{
    [Fact]
    public async Task UpsertThenGetByState_RoundTrips()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var record = new DumpRecord
            {
                Id = "abc123",
                FilePath = "C:\\Windows\\MEMORY.DMP",
                SizeBytes = 4096,
                DumpType = DumpType.KernelMode,
                State = DumpQueueState.Detected,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            };

            await store.UpsertAsync(record);

            var detected = await store.GetByStateAsync(DumpQueueState.Detected);
            Assert.Single(detected);
            Assert.Equal("abc123", detected[0].Id);
            Assert.Equal(DumpType.KernelMode, detected[0].DumpType);

            record.State = DumpQueueState.Submitted;
            record.SubmittedAtUtc = DateTimeOffset.UtcNow;
            await store.UpsertAsync(record);

            Assert.Empty(await store.GetByStateAsync(DumpQueueState.Detected));
            var submitted = await store.GetByStateAsync(DumpQueueState.Submitted);
            Assert.Single(submitted);
            Assert.NotNull(submitted[0].SubmittedAtUtc);
        }
        finally
        {
            // Windows won't delete a file while SQLite's connection still
            // holds it open (unlike POSIX, where unlink succeeds regardless)
            // — dispose before deleting, not after via `await using`, which
            // would only run once this whole method returns.
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task FindByFilePath_ReturnsExistingRecordForDedup()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            await store.UpsertAsync(new DumpRecord
            {
                Id = "1",
                FilePath = "C:\\Dumps\\a.dmp",
                SizeBytes = 10,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            });

            var found = await store.FindByFilePathAsync("C:\\Dumps\\a.dmp");
            Assert.NotNull(found);

            var notFound = await store.FindByFilePathAsync("C:\\Dumps\\missing.dmp");
            Assert.Null(notFound);
        }
        finally
        {
            // Windows won't delete a file while SQLite's connection still
            // holds it open (unlike POSIX, where unlink succeeds regardless)
            // — dispose before deleting, not after via `await using`, which
            // would only run once this whole method returns.
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }
}
