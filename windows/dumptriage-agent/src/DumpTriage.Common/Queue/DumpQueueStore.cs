using System.Globalization;
using DumpTriage.Common.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DumpTriage.Common.Queue;

/// <summary>
/// Durable local queue backed by SQLite (%ProgramData%\DumpTriage\queue.db in
/// production). Survives a service restart mid-upload and is what makes
/// scheduled-batch mode possible — dumps sit here in the Queued state until
/// BatchScheduler says it's time to flush.
/// </summary>
public sealed class DumpQueueStore : IAsyncDisposable
{
    // SQLITE_CORRUPT and SQLITE_NOTADB: the file is damaged or isn't a
    // database at all. Anything else (locked, disk full, permissions) is not
    // a reason to set the queue aside.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DumpQueueStore(SqliteConnection connection, ILogger logger, bool createdNew)
    {
        _connection = connection;
        _logger = logger;
        CreatedNew = createdNew;
    }

    /// <summary>True when this open created the queue file: a first run, not an upgrade.</summary>
    public bool CreatedNew { get; }

    /// <summary>
    /// Meta key: the folder scans take only files written since then. Set when the queue file is
    /// created; a queue without it predates the setting (an upgrade).
    /// </summary>
    public const string WatchingSinceKey = "watching_since_utc";

    public static async Task<DumpQueueStore> OpenAsync(
        string dbPath, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return await OpenFileAsync(dbPath, logger, cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADb)
        {
            // A corrupt queue.db used to end the service on every start
            // (website#1494). Keep the damaged file for diagnosis and start an empty
            // queue: the dumps themselves are still on disk.
            var asidePath = $"{dbPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            logger.LogError(ex, "Queue database {Path} is corrupt; moved it to {Aside} and started an empty queue", dbPath, asidePath);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (File.Exists(dbPath + suffix))
                {
                    File.Move(dbPath + suffix, asidePath + suffix, overwrite: true);
                }
            }
            var store = await OpenFileAsync(dbPath, logger, cancellationToken);
            // Not a first install: the dumps the lost queue still had are on disk,
            // and the startup scan must not skip them as older than "now".
            await store.SetMetaAsync(WatchingSinceKey, DateTimeOffset.UnixEpoch.ToString("O"), cancellationToken);
            return store;
        }
    }

    private static async Task<DumpQueueStore> OpenFileAsync(string dbPath, ILogger logger, CancellationToken cancellationToken)
    {
        // Pooling=False: Microsoft.Data.Sqlite pools connections by default,
        // so Dispose() returns the native handle to a pool instead of
        // releasing the OS-level file lock — invisible on Linux (unlink
        // succeeds on an open file regardless) but means a disposed store's
        // db file can't be deleted/moved on Windows (confirmed live: xUnit
        // tests cleaning up their temp .db file threw IOException on a real
        // Windows box despite disposing the store first). There's only ever
        // one connection to this exact file for the service's lifetime, so
        // pooling has no benefit here — disabling it makes Dispose actually
        // release the handle deterministically.
        var createdNew = !File.Exists(dbPath);
        var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        try
        {
            await connection.OpenAsync(cancellationToken);
            var store = new DumpQueueStore(connection, logger, createdNew);
            await store.CheckIntegrityAsync(cancellationToken);
            await store.EnsureSchemaAsync(cancellationToken);
            if (createdNew)
            {
                // Recorded with the file itself, so a first start that dies before the watcher runs
                // isn't later mistaken for an upgrade (which would filter nothing).
                await store.SetMetaAsync(WatchingSinceKey, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            }
            return store;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (result != "ok")
        {
            throw new SqliteException($"queue database failed its integrity check: {result}", SqliteCorrupt);
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS dumps (
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
            CREATE INDEX IF NOT EXISTS idx_dumps_state ON dumps(state);
            -- Files whose bytes match a dump already recorded under another
            -- row: a copy at a second path, or a row from before
            -- file_last_write_utc existed. Lets FindByFileIdentityAsync know
            -- them without hashing them again at every start (website#1514).
            -- Small persistent values: when the agent first started, the last scheduled slot.
            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS seen_files (
                file_path TEXT PRIMARY KEY,
                size_bytes INTEGER NOT NULL,
                file_last_write_utc TEXT NOT NULL,
                dump_id TEXT NOT NULL
            );
            """;

        var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await AddMissingColumnsAsync(cancellationToken);
    }

    /// <summary>
    /// Columns added after the first release. Agents are installed at
    /// customers with a queue.db made by an older agent: each missing column
    /// is added in place (nullable, so existing rows keep their values and
    /// read as "not set"). Never remove or rename an entry.
    /// </summary>
    private static readonly (string Name, string Definition)[] AddedColumns =
    {
        ("next_attempt_at_utc", "TEXT NULL"), // website#1488
        ("first_failed_at_utc", "TEXT NULL"), // website#1488
        ("file_last_write_utc", "TEXT NULL"), // website#1486
    };

    private async Task AddMissingColumnsAsync(CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableInfo = _connection.CreateCommand();
        tableInfo.CommandText = "PRAGMA table_info(dumps);";
        await using (var reader = await tableInfo.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        foreach (var (name, definition) in AddedColumns)
        {
            if (existing.Contains(name)) continue;
            var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE dumps ADD COLUMN {name} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Queue database migrated: added column {Column}", name);
        }
    }

    /// <summary>A small persistent value, such as when the last scheduled slot opened.</summary>
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT v FROM meta WHERE k = $k;";
        command.Parameters.AddWithValue("$k", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO meta (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAsync(DumpRecord record, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        const string sql = """
            INSERT INTO dumps (id, file_path, size_bytes, sha256, dump_type, state, detected_at_utc, submitted_at_utc, retry_count, last_error, central_copied_at_utc, central_copy_error, next_attempt_at_utc, first_failed_at_utc, file_last_write_utc)
            VALUES ($id, $filePath, $sizeBytes, $sha256, $dumpType, $state, $detectedAtUtc, $submittedAtUtc, $retryCount, $lastError, $centralCopiedAtUtc, $centralCopyError, $nextAttemptAtUtc, $firstFailedAtUtc, $fileLastWriteUtc)
            ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                size_bytes = excluded.size_bytes,
                file_last_write_utc = excluded.file_last_write_utc,
                sha256 = excluded.sha256,
                dump_type = excluded.dump_type,
                state = excluded.state,
                submitted_at_utc = excluded.submitted_at_utc,
                retry_count = excluded.retry_count,
                last_error = excluded.last_error,
                central_copied_at_utc = excluded.central_copied_at_utc,
                central_copy_error = excluded.central_copy_error,
                next_attempt_at_utc = excluded.next_attempt_at_utc,
                first_failed_at_utc = excluded.first_failed_at_utc;
            """;

        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$filePath", record.FilePath);
        command.Parameters.AddWithValue("$sizeBytes", record.SizeBytes);
        command.Parameters.AddWithValue("$sha256", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$dumpType", record.DumpType.ToString());
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$detectedAtUtc", record.DetectedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$submittedAtUtc", (object?)record.SubmittedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryCount", record.RetryCount);
        command.Parameters.AddWithValue("$lastError", (object?)record.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$centralCopiedAtUtc", (object?)record.CentralCopiedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$centralCopyError", (object?)record.CentralCopyError ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextAttemptAtUtc", (object?)record.NextAttemptAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$firstFailedAtUtc", (object?)record.FirstFailedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileLastWriteUtc", (object?)FormatLastWrite(record.FileLastWriteUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Stored and matched as text, so always in one form: UTC, round-trip.
    private static string? FormatLastWrite(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");

    /// <summary>
    /// A record for exactly this file: same path, size and last-write time.
    /// The path alone isn't enough, since C:\Windows\MEMORY.DMP is rewritten
    /// by every kernel crash (website#1486). A file RememberFileAsync recorded as
    /// another record's bytes finds that record.
    /// </summary>
    public async Task<DumpRecord?> FindByFileIdentityAsync(
        string filePath, long sizeBytes, DateTimeOffset lastWriteUtc, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM dumps
            WHERE file_path = $filePath AND size_bytes = $sizeBytes AND file_last_write_utc = $lastWrite
            UNION ALL
            SELECT dumps.* FROM seen_files JOIN dumps ON dumps.id = seen_files.dump_id
            WHERE seen_files.file_path = $filePath AND seen_files.size_bytes = $sizeBytes
              AND seen_files.file_last_write_utc = $lastWrite;
            """;
        command.Parameters.AddWithValue("$filePath", filePath);
        command.Parameters.AddWithValue("$sizeBytes", sizeBytes);
        command.Parameters.AddWithValue("$lastWrite", FormatLastWrite(lastWriteUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryMap(reader) is { } record)
            {
                return record;
            }
        }
        return null;
    }

    /// <summary>
    /// Remembers that the file at this path, as it is now, holds the bytes of
    /// record `dumpId`, which was recorded from another path or before its
    /// last-write time was kept (website#1514): the startup scan then skips it
    /// instead of hashing a multi-GB dump again on every start.
    /// </summary>
    public async Task RememberFileAsync(
        string filePath, long sizeBytes, DateTimeOffset lastWriteUtc, string dumpId, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO seen_files (file_path, size_bytes, file_last_write_utc, dump_id)
            VALUES ($filePath, $sizeBytes, $lastWrite, $dumpId)
            ON CONFLICT(file_path) DO UPDATE SET
                size_bytes = excluded.size_bytes,
                file_last_write_utc = excluded.file_last_write_utc,
                dump_id = excluded.dump_id;
            """;
        command.Parameters.AddWithValue("$filePath", filePath);
        command.Parameters.AddWithValue("$sizeBytes", sizeBytes);
        command.Parameters.AddWithValue("$lastWrite", FormatLastWrite(lastWriteUtc));
        command.Parameters.AddWithValue("$dumpId", dumpId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records a new detection only if no record with its id (the content's
    /// sha256) exists yet; returns whether it did. The same bytes at a second
    /// path used to upsert over the existing row, resetting a Submitted or
    /// Skipped record to Detected with a fresh retry count, so the dump went
    /// up again (website#1491).
    /// </summary>
    public async Task<bool> TryInsertAsync(DumpRecord record, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dumps (id, file_path, size_bytes, sha256, dump_type, state, detected_at_utc, retry_count, file_last_write_utc)
            VALUES ($id, $filePath, $sizeBytes, $sha256, $dumpType, $state, $detectedAtUtc, 0, $fileLastWriteUtc)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$fileLastWriteUtc", (object?)FormatLastWrite(record.FileLastWriteUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$filePath", record.FilePath);
        command.Parameters.AddWithValue("$sizeBytes", record.SizeBytes);
        command.Parameters.AddWithValue("$sha256", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$dumpType", record.DumpType.ToString());
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$detectedAtUtc", record.DetectedAtUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Moves a record from one state to another only if it is still in the
    /// first; returns whether it moved. The watcher's decisions (queue, ask,
    /// skip) go through this so they never overwrite a state the upload
    /// worker or the tray set in the meantime (website#1491).
    /// </summary>
    public async Task<bool> TransitionStateAsync(
        string id, DumpQueueState from, DumpQueueState to, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        var command = _connection.CreateCommand();
        command.CommandText = "UPDATE dumps SET state = $to WHERE id = $id AND state = $from;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$from", from.ToString());
        command.Parameters.AddWithValue("$to", to.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Puts every Failed record back in the queue with a fresh retry budget
    /// (the tray's "Retry failed uploads", website#1488). Returns the count.
    /// </summary>
    public async Task<int> RequeueFailedAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE dumps
            SET state = $queued, retry_count = 0, next_attempt_at_utc = NULL, first_failed_at_utc = NULL
            WHERE state = $failed;
            """;
        command.Parameters.AddWithValue("$queued", DumpQueueState.Queued.ToString());
        command.Parameters.AddWithValue("$failed", DumpQueueState.Failed.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Puts every record left in Submitting back to Queued, without counting
    /// a retry: nothing else ever reads that state, so a crash, reboot or
    /// service stop mid-upload stranded the dump there (website#1485). Only call it
    /// while no upload is running (at worker start). Returns the count.
    /// </summary>
    public async Task<int> RequeueInterruptedAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        var command = _connection.CreateCommand();
        command.CommandText = "UPDATE dumps SET state = $queued WHERE state = $submitting;";
        command.Parameters.AddWithValue("$queued", DumpQueueState.Queued.ToString());
        command.Parameters.AddWithValue("$submitting", DumpQueueState.Submitting.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DumpRecord?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM dumps WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? TryMap(reader) : null;
    }

    public async Task<List<DumpRecord>> GetByStateAsync(DumpQueueState state, CancellationToken cancellationToken = default)
    {
        using var _ = await LockAsync(cancellationToken);
        const string sql = "SELECT * FROM dumps WHERE state = $state ORDER BY detected_at_utc ASC;";
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$state", state.ToString());

        var results = new List<DumpRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryMap(reader) is { } record)
            {
                results.Add(record);
            }
        }
        return results;
    }

    /// <summary>
    /// One row this agent can't read (a state or dump type written by a newer
    /// agent, a mangled timestamp) used to throw on every tick and stop the
    /// whole queue (website#1494). It is logged and skipped instead.
    /// </summary>
    private DumpRecord? TryMap(SqliteDataReader reader)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var dumpTypeText = reader.GetString(reader.GetOrdinal("dump_type"));
        var stateText = reader.GetString(reader.GetOrdinal("state"));
        if (!Enum.TryParse<DumpType>(dumpTypeText, out var dumpType)
            || !Enum.TryParse<DumpQueueState>(stateText, out var state)
            || !TryReadTimestamp(reader, "detected_at_utc", out var detectedAtUtc) || detectedAtUtc is null
            || !TryReadTimestamp(reader, "submitted_at_utc", out var submittedAtUtc)
            || !TryReadTimestamp(reader, "central_copied_at_utc", out var centralCopiedAtUtc)
            || !TryReadTimestamp(reader, "next_attempt_at_utc", out var nextAttemptAtUtc)
            || !TryReadTimestamp(reader, "first_failed_at_utc", out var firstFailedAtUtc)
            || !TryReadTimestamp(reader, "file_last_write_utc", out var fileLastWriteUtc))
        {
            _logger.LogWarning(
                "Skipping queue row {Id}: unreadable values (state {State}, dump type {DumpType})", id, stateText, dumpTypeText);
            return null;
        }

        return new DumpRecord
        {
            Id = id,
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
            Sha256 = reader.IsDBNull(reader.GetOrdinal("sha256")) ? null : reader.GetString(reader.GetOrdinal("sha256")),
            DumpType = dumpType,
            State = state,
            DetectedAtUtc = detectedAtUtc.Value,
            SubmittedAtUtc = submittedAtUtc,
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            LastError = reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error")),
            CentralCopiedAtUtc = centralCopiedAtUtc,
            CentralCopyError = reader.IsDBNull(reader.GetOrdinal("central_copy_error")) ? null : reader.GetString(reader.GetOrdinal("central_copy_error")),
            NextAttemptAtUtc = nextAttemptAtUtc,
            FirstFailedAtUtc = firstFailedAtUtc,
            FileLastWriteUtc = fileLastWriteUtc,
        };
    }

    /// <summary>False only for a value that is present but unparseable; NULL reads as null.</summary>
    private static bool TryReadTimestamp(SqliteDataReader reader, string column, out DateTimeOffset? value)
    {
        value = null;
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return true;
        if (!DateTimeOffset.TryParse(
                reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    /// <summary>
    /// Every operation holds this for its whole use of the connection: the
    /// watcher's candidate tasks, the upload worker, the status broadcast and
    /// the pipe server all share the store, and a SqliteConnection isn't
    /// thread-safe (website#1492). Operations are short; an upload never holds it.
    /// </summary>
    private async Task<Releaser> LockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        await _connection.DisposeAsync();
    }
}
