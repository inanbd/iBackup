using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Entities;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>
/// POST /api/backup/chunk — receives one encrypted chunk (streamed, never buffered
/// in memory), verifies its SHA-256, and assembles the file once all chunks arrived.
/// Chunks are idempotent: re-uploading an index overwrites it.
/// </summary>
public sealed record UploadChunkCommand(Guid UploadSessionId, int ChunkIndex, string ChunkSha256, Stream Content)
    : IRequest<ChunkUploadResponse>;

internal sealed class UploadChunkHandler : IRequestHandler<UploadChunkCommand, ChunkUploadResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IFileStorage _storage;
    private readonly ILogger<UploadChunkHandler> _logger;

    public UploadChunkHandler(
        ISqlConnectionFactory connections,
        ICurrentUser currentUser,
        IFileStorage storage,
        ILogger<UploadChunkHandler> logger)
    {
        _connections = connections;
        _currentUser = currentUser;
        _storage = storage;
        _logger = logger;
    }

    public async Task<ChunkUploadResponse> Handle(UploadChunkCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        await using var connection = await _connections.OpenConnectionAsync(ct);

        var session = await LoadSessionAsync(connection, command.UploadSessionId, userId, ct);
        if (session.Status != UploadSessionStatus.Active)
        {
            throw AppException.Conflict("Upload session is not active.");
        }
        if (command.ChunkIndex < 0 || command.ChunkIndex >= session.TotalChunks)
        {
            throw AppException.BadRequest($"Chunk index must be between 0 and {session.TotalChunks - 1}.");
        }

        // Stream the chunk to disk while hashing it.
        var (actualSha256, size) = await _storage.SaveChunkAsync(userId, session.Id, command.ChunkIndex, command.Content, ct);

        if (!string.Equals(actualSha256, command.ChunkSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Chunk {Index} of session {SessionId} failed hash verification", command.ChunkIndex, session.Id);
            throw AppException.BadRequest("Chunk hash verification failed; please retry the chunk.");
        }

        // Register the chunk (idempotent) and read back progress.
        int received, total;
        await using (var register = Sql.StoredProcedure(connection, "dbo.usp_RegisterUploadChunk")
            .With("@UploadSessionId", session.Id)
            .With("@ChunkIndex", command.ChunkIndex)
            .With("@SizeBytes", size)
            .With("@Sha256", actualSha256.ToLowerInvariant()))
        await using (var reader = await register.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                throw AppException.Conflict("Upload session disappeared.");
            }
            received = reader.GetInt32(0);
            total = reader.GetInt32(1);
        }

        if (received < total)
        {
            return new ChunkUploadResponse(session.Id, command.ChunkIndex, true, false, null);
        }

        var versionId = await CompleteSessionAsync(connection, session, ct);
        return new ChunkUploadResponse(session.Id, command.ChunkIndex, true, true, versionId);
    }

    private async Task<Guid> CompleteSessionAsync(SqlConnection connection, UploadSession session, CancellationToken ct)
    {
        var userId = session.UserId;

        // Another upload of identical content may have won the race; reuse its object.
        Guid storageObjectId;
        long storedSize;
        const string existingSql = "SELECT Id, StoredSize FROM dbo.StorageObjects WHERE UserId = @UserId AND Sha256 = @Sha256;";
        await using (var existing = Sql.Command(connection, existingSql)
            .With("@UserId", userId)
            .With("@Sha256", session.Sha256))
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                storageObjectId = reader.GetGuid(0);
                storedSize = reader.GetInt64(1);
                await reader.CloseAsync();
                await _storage.DeleteChunksAsync(userId, session.Id, ct);
            }
            else
            {
                await reader.CloseAsync();
                var assembled = await _storage.AssembleAsync(userId, session.Id, session.TotalChunks, session.Sha256, ct);
                storedSize = assembled.StoredSize;
                storageObjectId = Guid.NewGuid();

                const string insertSql = """
                    INSERT INTO dbo.StorageObjects
                        (Id, UserId, Sha256, OriginalSize, StoredSize, CompressionMethod, EncryptionMethod, ChunkSize, StoragePath)
                    VALUES
                        (@Id, @UserId, @Sha256, @OriginalSize, @StoredSize, @Compression, @Encryption, @ChunkSize, @StoragePath);
                    """;
                try
                {
                    await using var insert = Sql.Command(connection, insertSql)
                        .With("@Id", storageObjectId)
                        .With("@UserId", userId)
                        .With("@Sha256", session.Sha256)
                        .With("@OriginalSize", session.OriginalSize)
                        .With("@StoredSize", storedSize)
                        .With("@Compression", (byte)session.CompressionMethod)
                        .With("@Encryption", (byte)session.EncryptionMethod)
                        .With("@ChunkSize", session.ChunkSize)
                        .With("@StoragePath", assembled.RelativeStoragePath);
                    await insert.ExecuteNonQueryAsync(ct);

                    // Bill the stored bytes against the user's quota.
                    const string usedSql = "UPDATE dbo.Users SET UsedBytes = UsedBytes + @Bytes, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @UserId;";
                    await using var used = Sql.Command(connection, usedSql)
                        .With("@Bytes", storedSize)
                        .With("@UserId", userId);
                    await used.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    // Unique (UserId, Sha256) race: keep the winner, drop our copy.
                    await _storage.DeleteObjectAsync(userId, assembled.RelativeStoragePath, ct);
                    await using var winner = Sql.Command(connection, existingSql)
                        .With("@UserId", userId)
                        .With("@Sha256", session.Sha256);
                    await using var winnerReader = await winner.ExecuteReaderAsync(ct);
                    if (!await winnerReader.ReadAsync(ct))
                    {
                        throw;
                    }
                    storageObjectId = winnerReader.GetGuid(0);
                }
            }
        }

        var versionId = await BackupData.CommitFileVersionAsync(
            connection, userId, session.DeviceId, session.FolderId,
            session.RelativePath, session.FileName, storageObjectId, session.BackupJobId,
            session.OriginalSize, session.EncryptedSize, session.Sha256, session.FileModifiedAtUtc, ct);

        const string completeSql = """
            UPDATE dbo.UploadSessions
            SET Status = 1, CompletedAtUtc = SYSUTCDATETIME(), LastActivityAtUtc = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;
        await using (var complete = Sql.Command(connection, completeSql).With("@Id", session.Id))
        {
            await complete.ExecuteNonQueryAsync(ct);
        }

        await _storage.DeleteChunksAsync(userId, session.Id, ct);

        _logger.LogInformation("Upload session {SessionId} assembled into version {VersionId}", session.Id, versionId);
        return versionId;
    }

    private static async Task<UploadSession> LoadSessionAsync(SqlConnection connection, Guid sessionId, Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT Id, UserId, DeviceId, FolderId, BackupJobId, RelativePath, FileName, Sha256,
                   OriginalSize, EncryptedSize, FileModifiedAtUtc, CompressionMethod, EncryptionMethod,
                   ChunkSize, TotalChunks, ReceivedChunks, Status, CreatedAtUtc, LastActivityAtUtc, CompletedAtUtc
            FROM dbo.UploadSessions
            WHERE Id = @Id AND UserId = @UserId;
            """;

        await using var command = Sql.Command(connection, sql)
            .With("@Id", sessionId)
            .With("@UserId", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw AppException.NotFound("Upload session not found.");
        }

        return new UploadSession
        {
            Id = reader.GetGuid(0),
            UserId = reader.GetGuid(1),
            DeviceId = reader.GetGuid(2),
            FolderId = reader.GetGuid(3),
            BackupJobId = reader.GetGuidOrNull(4),
            RelativePath = reader.GetString(5),
            FileName = reader.GetString(6),
            Sha256 = reader.GetString(7),
            OriginalSize = reader.GetInt64(8),
            EncryptedSize = reader.GetInt64(9),
            FileModifiedAtUtc = reader.GetDateTime(10),
            CompressionMethod = (CompressionMethod)reader.GetByte(11),
            EncryptionMethod = (EncryptionMethod)reader.GetByte(12),
            ChunkSize = reader.GetInt32(13),
            TotalChunks = reader.GetInt32(14),
            ReceivedChunks = reader.GetInt32(15),
            Status = (UploadSessionStatus)reader.GetByte(16),
            CreatedAtUtc = reader.GetDateTime(17),
            LastActivityAtUtc = reader.GetDateTime(18),
            CompletedAtUtc = reader.GetDateTimeOrNull(19)
        };
    }
}
