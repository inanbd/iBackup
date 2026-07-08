using Microsoft.Data.Sqlite;

namespace iBackup.Client.Core.State;

/// <summary>Snapshot of a local file at the time of its last successful backup.</summary>
public sealed record IndexedFile(Guid FolderId, string RelativePath, long Size, long ModifiedTicksUtc, string Sha256);

/// <summary>
/// Local SQLite state (raw ADO.NET, no ORM):
///  - FileIndex: last backed-up snapshot per file, used for incremental change detection
///    and for detecting deletions/renames when a scheduled scan runs.
///  - PendingUploads: files queued for upload, persisted so the queue survives restarts.
/// Scales to millions of rows; all lookups are indexed.
/// </summary>
public sealed class LocalStateStore : IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly object _initLock = new();

    public LocalStateStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private SqliteConnection Open()
    {
        EnsureInitialized();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS FileIndex (
                    FolderId TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    Size INTEGER NOT NULL,
                    ModifiedTicksUtc INTEGER NOT NULL,
                    Sha256 TEXT NOT NULL,
                    PRIMARY KEY (FolderId, RelativePath)
                );

                CREATE TABLE IF NOT EXISTS PendingUploads (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FolderId TEXT NOT NULL,
                    FullPath TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    Priority INTEGER NOT NULL DEFAULT 0,
                    Attempts INTEGER NOT NULL DEFAULT 0,
                    EnqueuedAtUtc TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE (FolderId, RelativePath)
                );

                CREATE INDEX IF NOT EXISTS IX_PendingUploads_Priority
                    ON PendingUploads (Priority DESC, Id);
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    // ------------------------------------------------------------- FileIndex

    public async Task<IndexedFile?> GetIndexedFileAsync(Guid folderId, string relativePath, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Size, ModifiedTicksUtc, Sha256 FROM FileIndex
            WHERE FolderId = $folder AND RelativePath = $path;
            """;
        command.Parameters.AddWithValue("$folder", folderId.ToString());
        command.Parameters.AddWithValue("$path", relativePath);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new IndexedFile(folderId, relativePath, reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
    }

    public async Task<Dictionary<string, IndexedFile>> GetFolderIndexAsync(Guid folderId, CancellationToken ct)
    {
        var result = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath, Size, ModifiedTicksUtc, Sha256 FROM FileIndex WHERE FolderId = $folder;";
        command.Parameters.AddWithValue("$folder", folderId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var path = reader.GetString(0);
            result[path] = new IndexedFile(folderId, path, reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        }
        return result;
    }

    public async Task UpsertIndexedFileAsync(IndexedFile file, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FileIndex (FolderId, RelativePath, Size, ModifiedTicksUtc, Sha256)
            VALUES ($folder, $path, $size, $ticks, $hash)
            ON CONFLICT (FolderId, RelativePath)
            DO UPDATE SET Size = $size, ModifiedTicksUtc = $ticks, Sha256 = $hash;
            """;
        command.Parameters.AddWithValue("$folder", file.FolderId.ToString());
        command.Parameters.AddWithValue("$path", file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$ticks", file.ModifiedTicksUtc);
        command.Parameters.AddWithValue("$hash", file.Sha256);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveIndexedFileAsync(Guid folderId, string relativePath, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FileIndex WHERE FolderId = $folder AND RelativePath = $path;";
        command.Parameters.AddWithValue("$folder", folderId.ToString());
        command.Parameters.AddWithValue("$path", relativePath);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RenameIndexedFileAsync(Guid folderId, string oldRelativePath, string newRelativePath, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OR REPLACE FileIndex SET RelativePath = $new
            WHERE FolderId = $folder AND RelativePath = $old;
            """;
        command.Parameters.AddWithValue("$folder", folderId.ToString());
        command.Parameters.AddWithValue("$old", oldRelativePath);
        command.Parameters.AddWithValue("$new", newRelativePath);
        await command.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------- PendingUploads

    public async Task EnqueueUploadAsync(Guid folderId, string fullPath, string relativePath, int priority, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PendingUploads (FolderId, FullPath, RelativePath, Priority)
            VALUES ($folder, $full, $path, $priority)
            ON CONFLICT (FolderId, RelativePath) DO UPDATE SET Priority = MAX(Priority, $priority);
            """;
        command.Parameters.AddWithValue("$folder", folderId.ToString());
        command.Parameters.AddWithValue("$full", fullPath);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$priority", priority);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(long Id, Guid FolderId, string FullPath, string RelativePath, int Attempts)>> DequeueBatchAsync(
        int batchSize, CancellationToken ct)
    {
        var items = new List<(long, Guid, string, string, int)>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, FolderId, FullPath, RelativePath, Attempts FROM PendingUploads
            ORDER BY Priority DESC, Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", batchSize);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add((reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        return items;
    }

    public async Task CompletePendingUploadAsync(long id, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingUploads WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkUploadAttemptAsync(long id, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PendingUploads SET Attempts = Attempts + 1 WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> GetPendingCountAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PendingUploads;";
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
