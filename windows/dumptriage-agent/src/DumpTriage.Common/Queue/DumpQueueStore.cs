using DumpTriage.Common.Models;
using Microsoft.Data.Sqlite;

namespace DumpTriage.Common.Queue;

/// <summary>
/// Durable local queue backed by SQLite (%ProgramData%\DumpTriage\queue.db in
/// production). Survives a service restart mid-upload and is what makes
/// scheduled-batch mode possible — dumps sit here in the Queued state until
/// BatchScheduler says it's time to flush.
/// </summary>
public sealed class DumpQueueStore : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    // SqliteConnection is not thread-safe, and the watcher, upload worker, IPC
    // handlers and status broadcaster all share this one instance.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DumpQueueStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static async Task<DumpQueueStore> OpenAsync(string dbPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

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
        var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(cancellationToken);

        var store = new DumpQueueStore(connection);
        await store.EnsureSchemaAsync(cancellationToken);
        return store;
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
            """;

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAsync(DumpRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dumps (id, file_path, size_bytes, sha256, dump_type, state, detected_at_utc, submitted_at_utc, retry_count, last_error, central_copied_at_utc, central_copy_error)
            VALUES ($id, $filePath, $sizeBytes, $sha256, $dumpType, $state, $detectedAtUtc, $submittedAtUtc, $retryCount, $lastError, $centralCopiedAtUtc, $centralCopyError)
            ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                size_bytes = excluded.size_bytes,
                sha256 = excluded.sha256,
                dump_type = excluded.dump_type,
                state = excluded.state,
                submitted_at_utc = excluded.submitted_at_utc,
                retry_count = excluded.retry_count,
                last_error = excluded.last_error,
                central_copied_at_utc = excluded.central_copied_at_utc,
                central_copy_error = excluded.central_copy_error;
            """;

        await _gate.WaitAsync(cancellationToken);
        try
        {
        using var command = _connection.CreateCommand();
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
        await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<DumpRecord>> GetByStateAsync(DumpQueueState state, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM dumps WHERE state = $state ORDER BY detected_at_utc ASC;";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$state", state.ToString());

            var results = new List<DumpRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(Map(reader));
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<DumpRecord?> FindByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM dumps WHERE file_path = $filePath ORDER BY detected_at_utc DESC LIMIT 1;";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$filePath", filePath);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<DumpRecord?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM dumps WHERE id = $id LIMIT 1;";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Moves records stranded in Submitting (service stopped or crashed
    /// mid-upload) back to Queued. Nothing else ever polls that state.
    /// Call once at startup, before the upload worker runs.
    /// </summary>
    public async Task<int> RecoverInterruptedSubmissionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE dumps SET state = $queued WHERE state = $submitting;";
            command.Parameters.AddWithValue("$queued", DumpQueueState.Queued.ToString());
            command.Parameters.AddWithValue("$submitting", DumpQueueState.Submitting.ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static DumpRecord Map(SqliteDataReader reader)
    {
        return new DumpRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
            Sha256 = reader.IsDBNull(reader.GetOrdinal("sha256")) ? null : reader.GetString(reader.GetOrdinal("sha256")),
            DumpType = Enum.Parse<DumpType>(reader.GetString(reader.GetOrdinal("dump_type"))),
            State = Enum.Parse<DumpQueueState>(reader.GetString(reader.GetOrdinal("state"))),
            DetectedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("detected_at_utc"))),
            SubmittedAtUtc = reader.IsDBNull(reader.GetOrdinal("submitted_at_utc"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("submitted_at_utc"))),
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            LastError = reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error")),
            CentralCopiedAtUtc = reader.IsDBNull(reader.GetOrdinal("central_copied_at_utc"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("central_copied_at_utc"))),
            CentralCopyError = reader.IsDBNull(reader.GetOrdinal("central_copy_error")) ? null : reader.GetString(reader.GetOrdinal("central_copy_error")),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
