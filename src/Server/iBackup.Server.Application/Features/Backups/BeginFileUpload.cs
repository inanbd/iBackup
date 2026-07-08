using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>
/// POST /api/backup/upload — announces a file. If the user already stores content
/// with the same SHA-256, the server commits a new version against the existing
/// storage object (deduplication) and no bytes are transferred. Otherwise an
/// upload session is created (or an interrupted one resumed) and the client pushes chunks.
/// </summary>
public sealed record BeginFileUploadCommand(BeginFileUploadRequest Request) : IRequest<BeginFileUploadResponse>;

internal sealed class BeginFileUploadValidator : AbstractValidator<BeginFileUploadCommand>
{
    public const int MaxChunkSize = 512 * 1024 * 1024;

    public BeginFileUploadValidator()
    {
        RuleFor(x => x.Request.BackupJobId).NotEmpty();
        RuleFor(x => x.Request.FolderId).NotEmpty();
        RuleFor(x => x.Request.RelativePath).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.Request.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Request.Sha256).NotEmpty().Length(64).Matches("^[0-9a-fA-F]{64}$");
        RuleFor(x => x.Request.OriginalSize).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.EncryptedSize).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.ChunkSize).InclusiveBetween(1, MaxChunkSize);
        RuleFor(x => x.Request.TotalChunks).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.Request.Compression).IsInEnum();
        RuleFor(x => x.Request.Encryption).IsInEnum();
    }
}

internal sealed class BeginFileUploadHandler : IRequestHandler<BeginFileUploadCommand, BeginFileUploadResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public BeginFileUploadHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<BeginFileUploadResponse> Handle(BeginFileUploadCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var relativePath = PathSanitizer.NormalizeRelativePath(request.RelativePath);
        var fileName = PathSanitizer.ValidateFileName(request.FileName);
        var sha256 = request.Sha256.ToLowerInvariant();
        var userId = _currentUser.UserId;

        await using var connection = await _connections.OpenConnectionAsync(ct);

        await BackupData.EnsureRunningJobAsync(connection, request.BackupJobId, userId, ct);
        var deviceId = await BackupData.EnsureFolderAsync(connection, request.FolderId, userId, ct);

        // 1. Deduplication: identical content already stored for this user?
        const string dedupSql = """
            SELECT Id FROM dbo.StorageObjects WHERE UserId = @UserId AND Sha256 = @Sha256;
            """;
        Guid? existingObjectId = null;
        await using (var dedup = Sql.Command(connection, dedupSql)
            .With("@UserId", userId)
            .With("@Sha256", sha256))
        {
            if (await dedup.ExecuteScalarAsync(ct) is Guid found)
            {
                existingObjectId = found;
            }
        }

        if (existingObjectId is { } objectId)
        {
            var versionId = await BackupData.CommitFileVersionAsync(
                connection, userId, deviceId, request.FolderId, relativePath, fileName,
                objectId, request.BackupJobId, request.OriginalSize, request.EncryptedSize,
                sha256, request.FileModifiedAtUtc, ct);

            return new BeginFileUploadResponse(
                Deduplicated: true, FileVersionId: versionId, UploadSessionId: null,
                ChunkSize: request.ChunkSize, TotalChunks: 0, ReceivedChunkIndexes: []);
        }

        // 2. Quota check before accepting new bytes.
        const string quotaSql = "SELECT QuotaBytes, UsedBytes FROM dbo.Users WHERE Id = @UserId;";
        await using (var quota = Sql.Command(connection, quotaSql).With("@UserId", userId))
        await using (var reader = await quota.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var quotaBytes = reader.GetInt64(0);
                var usedBytes = reader.GetInt64(1);
                if (usedBytes + request.EncryptedSize > quotaBytes)
                {
                    throw AppException.QuotaExceeded("Storage quota exceeded.");
                }
            }
        }

        // 3. Resume an interrupted session for the same content, if one exists.
        const string resumeSql = """
            SELECT TOP (1) Id, ChunkSize, TotalChunks
            FROM dbo.UploadSessions
            WHERE UserId = @UserId AND DeviceId = @DeviceId AND FolderId = @FolderId
              AND RelativePath = @RelativePath AND Sha256 = @Sha256 AND Status = 0
            ORDER BY CreatedAtUtc DESC;
            """;
        await using (var resume = Sql.Command(connection, resumeSql)
            .With("@UserId", userId)
            .With("@DeviceId", deviceId)
            .With("@FolderId", request.FolderId)
            .With("@RelativePath", relativePath)
            .With("@Sha256", sha256))
        await using (var reader = await resume.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var sessionId = reader.GetGuid(0);
                var chunkSize = reader.GetInt32(1);
                var totalChunks = reader.GetInt32(2);
                await reader.CloseAsync();

                var received = await GetReceivedChunksAsync(connection, sessionId, ct);
                return new BeginFileUploadResponse(false, null, sessionId, chunkSize, totalChunks, received);
            }
        }

        // 4. Fresh upload session.
        var newSessionId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO dbo.UploadSessions
                (Id, UserId, DeviceId, FolderId, BackupJobId, RelativePath, FileName, Sha256,
                 OriginalSize, EncryptedSize, FileModifiedAtUtc, CompressionMethod, EncryptionMethod,
                 ChunkSize, TotalChunks)
            VALUES
                (@Id, @UserId, @DeviceId, @FolderId, @BackupJobId, @RelativePath, @FileName, @Sha256,
                 @OriginalSize, @EncryptedSize, @FileModifiedAtUtc, @Compression, @Encryption,
                 @ChunkSize, @TotalChunks);
            """;
        await using (var insert = Sql.Command(connection, insertSql)
            .With("@Id", newSessionId)
            .With("@UserId", userId)
            .With("@DeviceId", deviceId)
            .With("@FolderId", request.FolderId)
            .With("@BackupJobId", request.BackupJobId)
            .With("@RelativePath", relativePath)
            .With("@FileName", fileName)
            .With("@Sha256", sha256)
            .With("@OriginalSize", request.OriginalSize)
            .With("@EncryptedSize", request.EncryptedSize)
            .With("@FileModifiedAtUtc", request.FileModifiedAtUtc)
            .With("@Compression", (byte)request.Compression)
            .With("@Encryption", (byte)request.Encryption)
            .With("@ChunkSize", request.ChunkSize)
            .With("@TotalChunks", request.TotalChunks))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        return new BeginFileUploadResponse(false, null, newSessionId, request.ChunkSize, request.TotalChunks, []);
    }

    private static async Task<IReadOnlyList<int>> GetReceivedChunksAsync(
        Microsoft.Data.SqlClient.SqlConnection connection, Guid sessionId, CancellationToken ct)
    {
        const string sql = "SELECT ChunkIndex FROM dbo.UploadChunks WHERE UploadSessionId = @SessionId ORDER BY ChunkIndex;";
        await using var command = Sql.Command(connection, sql).With("@SessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var indexes = new List<int>();
        while (await reader.ReadAsync(ct))
        {
            indexes.Add(reader.GetInt32(0));
        }
        return indexes;
    }
}
