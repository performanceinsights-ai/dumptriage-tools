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
    public async Task FindByFileIdentity_matches_path_size_and_last_write_time()
    {
        // website#1486: the path alone hid every later crash written to MEMORY.DMP.
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var written = new DateTimeOffset(2026, 10, 14, 8, 30, 0, TimeSpan.Zero).AddTicks(1234567);
            await store.UpsertAsync(new DumpRecord
            {
                Id = "1",
                FilePath = "C:\\Windows\\MEMORY.DMP",
                SizeBytes = 10,
                FileLastWriteUtc = written,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            });

            Assert.NotNull(await store.FindByFileIdentityAsync("C:\\Windows\\MEMORY.DMP", 10, written));
            // The same instant in another offset is the same file.
            Assert.NotNull(await store.FindByFileIdentityAsync("C:\\Windows\\MEMORY.DMP", 10, written.ToOffset(TimeSpan.FromHours(2))));
            // A later crash at the same path: new time, or new size.
            Assert.Null(await store.FindByFileIdentityAsync("C:\\Windows\\MEMORY.DMP", 10, written.AddMinutes(1)));
            Assert.Null(await store.FindByFileIdentityAsync("C:\\Windows\\MEMORY.DMP", 11, written));
            Assert.Null(await store.FindByFileIdentityAsync("C:\\Dumps\\missing.dmp", 10, written));
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

    internal static string NewDbPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");

    internal static async Task ExecuteRawAsync(string dbPath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_row_with_an_unknown_state_or_type_is_skipped_not_fatal()
    {
        // website#1494: one unparseable row (e.g. a state a newer agent wrote) threw
        // on every tick and stopped the queue for good.
        var dbPath = NewDbPath();
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            await store.UpsertAsync(new DumpRecord
            {
                Id = "good",
                FilePath = "C:\\Dumps\\good.dmp",
                SizeBytes = 10,
                FileLastWriteUtc = DateTimeOffset.Parse("2026-10-14T00:00:00Z"),
                State = DumpQueueState.Queued,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            });
            await ExecuteRawAsync(dbPath, """
                INSERT INTO dumps (id, file_path, size_bytes, dump_type, state, detected_at_utc, file_last_write_utc)
                VALUES ('badtype', 'C:\Dumps\b.dmp', 1, 'Hybrid', 'Queued', '2026-10-14T00:00:00+00:00', NULL),
                       ('baddate', 'C:\Dumps\c.dmp', 1, 'UserMode', 'Queued', 'yesterday', NULL),
                       ('badstate', 'C:\Dumps\good.dmp', 10, 'UserMode', 'Superseded', '2026-10-14T00:00:00+00:00',
                        '2026-10-14T00:00:00.0000000+00:00');
                """);

            var queued = await store.GetByStateAsync(DumpQueueState.Queued);

            Assert.Equal("good", Assert.Single(queued).Id);
            Assert.Equal("good", (await store.FindByFileIdentityAsync(
                "C:\\Dumps\\good.dmp", 10, DateTimeOffset.Parse("2026-10-14T00:00:00Z")))?.Id);
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task A_detection_is_inserted_only_if_absent()
    {
        // website#1491
        var dbPath = NewDbPath();
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var original = new DumpRecord
            {
                Id = "same",
                FilePath = "C:\\Dumps\\a.dmp",
                SizeBytes = 10,
                State = DumpQueueState.Skipped,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            };
            Assert.True(await store.TryInsertAsync(original));

            Assert.False(await store.TryInsertAsync(new DumpRecord
            {
                Id = "same",
                FilePath = "C:\\Dumps\\copy.dmp",
                SizeBytes = 10,
                State = DumpQueueState.Detected,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            }));

            var row = Assert.Single(await store.GetByStateAsync(DumpQueueState.Skipped));
            Assert.Equal("C:\\Dumps\\a.dmp", row.FilePath);
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task A_state_transition_only_applies_from_the_expected_state()
    {
        var dbPath = NewDbPath();
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            await store.TryInsertAsync(new DumpRecord
            {
                Id = "x",
                FilePath = "C:\\Dumps\\x.dmp",
                SizeBytes = 1,
                State = DumpQueueState.Submitted,
                DetectedAtUtc = DateTimeOffset.UtcNow,
            });

            Assert.False(await store.TransitionStateAsync("x", DumpQueueState.PendingApproval, DumpQueueState.Skipped));
            Assert.Single(await store.GetByStateAsync(DumpQueueState.Submitted));
            Assert.True(await store.TransitionStateAsync("x", DumpQueueState.Submitted, DumpQueueState.Queued));
            Assert.Single(await store.GetByStateAsync(DumpQueueState.Queued));
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Concurrent_callers_share_the_store_safely()
    {
        // website#1492: the watcher's tasks, the upload ticks and the status
        // broadcast all use the one SqliteConnection at once, and
        // Microsoft.Data.Sqlite connections aren't thread-safe.
        var dbPath = NewDbPath();
        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
            {
                await store.UpsertAsync(new DumpRecord
                {
                    Id = $"id{i}",
                    FilePath = $"C:\\Dumps\\{i}.dmp",
                    SizeBytes = i,
                    State = DumpQueueState.Queued,
                    DetectedAtUtc = DateTimeOffset.UtcNow,
                });
                await store.GetByStateAsync(DumpQueueState.Queued);
                await store.FindByFileIdentityAsync($"C:\\Dumps\\{i}.dmp", i, DateTimeOffset.UtcNow);
            })));

            Assert.Equal(200, (await store.GetByStateAsync(DumpQueueState.Queued)).Count);
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task A_queue_made_by_an_older_agent_is_migrated_and_keeps_its_rows()
    {
        // Agents are installed at customers: a new column must not lose or
        // break the rows an older agent wrote.
        var dbPath = NewDbPath();
        await ExecuteRawAsync(dbPath, """
            CREATE TABLE dumps (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                sha256 TEXT NULL,
                dump_type TEXT NOT NULL,
                state TEXT NOT NULL,
                detected_at_utc TEXT NOT NULL,
                submitted_at_utc TEXT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                central_copied_at_utc TEXT NULL,
                central_copy_error TEXT NULL
            );
            CREATE INDEX idx_dumps_state ON dumps(state);
            INSERT INTO dumps (id, file_path, size_bytes, sha256, dump_type, state, detected_at_utc, retry_count, last_error)
            VALUES ('old1', 'C:\Windows\MEMORY.DMP', 4096, 'old1', 'KernelMode', 'Queued', '2026-10-01T08:00:00.0000000+00:00', 2, 'boom');
            """);

        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            var row = Assert.Single(await store.GetByStateAsync(DumpQueueState.Queued));
            Assert.Equal("old1", row.Id);
            Assert.Equal(2, row.RetryCount);
            Assert.Equal("boom", row.LastError);
            Assert.Null(row.NextAttemptAtUtc);

            var next = DateTimeOffset.UtcNow.AddMinutes(5);
            row.NextAttemptAtUtc = next;
            await store.UpsertAsync(row);
            Assert.Equal(next, Assert.Single(await store.GetByStateAsync(DumpQueueState.Queued)).NextAttemptAtUtc);
        }
        finally
        {
            await store.DisposeAsync();
        }

        // Opening it again is a no-op.
        store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            Assert.Single(await store.GetByStateAsync(DumpQueueState.Queued));
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task A_corrupt_database_is_moved_aside_and_an_empty_queue_started()
    {
        // website#1494: a corrupt queue.db ended the service at every start.
        var dbPath = NewDbPath();
        await File.WriteAllBytesAsync(dbPath, System.Text.Encoding.ASCII.GetBytes(new string('x', 8192)));

        var store = await DumpQueueStore.OpenAsync(dbPath);
        try
        {
            Assert.Empty(await store.GetByStateAsync(DumpQueueState.Queued));
            var aside = Directory.GetFiles(Path.GetDirectoryName(dbPath)!, Path.GetFileName(dbPath) + ".corrupt-*");
            Assert.Single(aside);
            Assert.Equal(8192, new FileInfo(aside[0]).Length);
            File.Delete(aside[0]);
            // Not a first install: the startup scan must still find the dumps the lost queue had.
            Assert.Equal(DateTimeOffset.UnixEpoch.ToString("O"), await store.GetMetaAsync(DumpQueueStore.WatchingSinceKey));
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(dbPath);
        }
    }
}
