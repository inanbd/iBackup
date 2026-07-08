using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Restore;

/// <summary>
/// POST /api/restore — validates the requested versions and returns download
/// tickets. The client streams each item from the download endpoint and
/// decrypts/decompresses locally.
/// </summary>
public sealed record CreateRestoreCommand(IReadOnlyList<Guid> FileVersionIds) : IRequest<CreateRestoreResponse>;

internal sealed class CreateRestoreValidator : AbstractValidator<CreateRestoreCommand>
{
    public CreateRestoreValidator()
    {
        RuleFor(x => x.FileVersionIds).NotEmpty()
            .Must(ids => ids.Count <= 10_000).WithMessage("A restore request may contain at most 10000 items.");
    }
}

internal sealed class CreateRestoreHandler : IRequestHandler<CreateRestoreCommand, CreateRestoreResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public CreateRestoreHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task<CreateRestoreResponse> Handle(CreateRestoreCommand request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string sql = """
            SELECT v.Id, v.FileId, f.RelativePath, f.FileName, v.OriginalSize, v.EncryptedSize,
                   o.CompressionMethod, o.EncryptionMethod, o.ChunkSize
            FROM dbo.FileVersions v
            JOIN dbo.Files f ON f.Id = v.FileId
            JOIN dbo.StorageObjects o ON o.Id = v.StorageObjectId
            WHERE f.UserId = @UserId AND v.Status = 1
              AND v.Id IN (SELECT value FROM STRING_SPLIT(@VersionIds, ','));
            """;

        var items = new List<RestoreItemDto>();
        long totalBytes = 0;
        await using (var command = Sql.Command(connection, sql)
            .With("@UserId", _currentUser.UserId)
            .With("@VersionIds", string.Join(',', request.FileVersionIds.Distinct())))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var versionId = reader.GetGuid(0);
                var originalSize = reader.GetInt64(4);
                totalBytes += originalSize;
                items.Add(new RestoreItemDto(
                    FileVersionId: versionId,
                    FileId: reader.GetGuid(1),
                    RelativePath: reader.GetString(2),
                    FileName: reader.GetString(3),
                    OriginalSize: originalSize,
                    EncryptedSize: reader.GetInt64(5),
                    Compression: (CompressionMethod)reader.GetByte(6),
                    Encryption: (EncryptionMethod)reader.GetByte(7),
                    ChunkSize: reader.GetInt32(8),
                    DownloadUrl: $"/api/restore/download/{versionId}"));
            }
        }

        if (items.Count != request.FileVersionIds.Distinct().Count())
        {
            throw AppException.NotFound("One or more requested versions were not found.");
        }

        var restoreId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO dbo.RestoreRequests (Id, UserId, DeviceId, ItemCount, TotalBytes)
            VALUES (@Id, @UserId, @DeviceId, @ItemCount, @TotalBytes);
            """;
        await using (var insert = Sql.Command(connection, insertSql)
            .With("@Id", restoreId)
            .With("@UserId", _currentUser.UserId)
            .With("@DeviceId", _currentUser.DeviceId)
            .With("@ItemCount", items.Count)
            .With("@TotalBytes", totalBytes))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await _audit.LogAsync(_currentUser.UserId, _currentUser.DeviceId, "restore.requested",
            $"{items.Count} items, {totalBytes} bytes", _currentUser.IpAddress, ct);

        return new CreateRestoreResponse(restoreId, items);
    }
}
