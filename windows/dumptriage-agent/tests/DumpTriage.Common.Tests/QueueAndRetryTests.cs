using DumpTriage.Common.Models;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Upload;
using Microsoft.Data.Sqlite;

namespace DumpTriage.Common.Tests;

/// <summary>dumptriage-tools#6 and #9: retry timing, and the queue columns and meta values they use.</summary>
public sealed class QueueAndRetryTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "dt-queue-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose() => File.Delete(_db);

    [Fact]
    public void Retry_timing_matches_the_linux_agent()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.TransientDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(120), RetryPolicy.TransientDelay(2));
        Assert.Equal(TimeSpan.FromHours(1), RetryPolicy.TransientDelay(9));
        Assert.Equal(TimeSpan.FromMinutes(10), RetryPolicy.AuthPause(1));
        Assert.Equal(TimeSpan.FromMinutes(20), RetryPolicy.AuthPause(2));
        Assert.Equal(TimeSpan.FromHours(6), RetryPolicy.AuthPause(20));
    }

    [Fact]
    public async Task A_queue_from_before_the_backoff_column_is_upgraded_and_keeps_its_rows()
    {
        using (var old = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE dumps (id TEXT PRIMARY KEY, file_path TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                    sha256 TEXT NULL, dump_type TEXT NOT NULL, state TEXT NOT NULL, detected_at_utc TEXT NOT NULL,
                    submitted_at_utc TEXT NULL, retry_count INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL,
                    central_copied_at_utc TEXT NULL, central_copy_error TEXT NULL);
                INSERT INTO dumps (id, file_path, size_bytes, dump_type, state, detected_at_utc)
                    VALUES ('a', 'C:\\a.dmp', 4, 'UserMode', 'Queued', '2026-10-14T12:00:00.0000000+00:00');
                """;
            create.ExecuteNonQuery();
        }

        await using var queue = await DumpQueueStore.OpenAsync(_db);
        var a = await queue.FindByIdAsync("a");
        Assert.NotNull(a);
        Assert.Null(a!.NextAttemptUtc);

        var at = new DateTimeOffset(2026, 10, 14, 12, 1, 0, TimeSpan.Zero);
        a.NextAttemptUtc = at;
        await queue.UpsertAsync(a);
        Assert.Equal(at, (await queue.FindByIdAsync("a"))!.NextAttemptUtc);
    }

    [Fact]
    public async Task CreatedNew_tells_a_first_run_from_an_upgrade()
    {
        await using (var first = await DumpQueueStore.OpenAsync(_db))
        {
            Assert.True(first.CreatedNew);
        }
        await using var again = await DumpQueueStore.OpenAsync(_db);
        Assert.False(again.CreatedNew);
    }

    [Fact]
    public async Task Meta_values_persist()
    {
        await using (var queue = await DumpQueueStore.OpenAsync(_db))
        {
            Assert.Null(await queue.GetMetaAsync("k"));
            await queue.SetMetaAsync("k", "1");
            await queue.SetMetaAsync("k", "2");
        }
        await using var reopened = await DumpQueueStore.OpenAsync(_db);
        Assert.Equal("2", await reopened.GetMetaAsync("k"));
    }
}
