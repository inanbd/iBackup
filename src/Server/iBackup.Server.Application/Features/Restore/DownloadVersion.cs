using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Restore;

public sealed record VersionDownload(Stream Content, string FileName, long EncryptedSize);

/// <summary>
/// GET /api/restore/download/{versionId} — streams the stored (encrypted) object.
/// The client performs decryption and decompression.
/// </summary>
public sealed record DownloadVersionQuery(Guid FileVersionId) : IRequest<VersionDownload>;

internal sealed class DownloadVersionHandler : IRequestHandler<DownloadVersionQuery, VersionDownload>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IFileStorage _storage;

    public DownloadVersionHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IFileStorage storage)
    {
        _connections = connections;
        _currentUser = currentUser;
        _storage = storage;
    }

    public async Task<VersionDownload> Handle(DownloadVersionQuery request, CancellationToken ct)
    {
        const string sql = """
            SELECT o.StoragePath, o.StoredSize, f.FileName
            FROM dbo.FileVersions v
            JOIN dbo.Files f ON f.Id = v.FileId
            JOIN dbo.StorageObjects o ON o.Id = v.StorageObjectId
            WHERE v.Id = @VersionId AND f.UserId = @UserId AND v.Status = 1;
            """;

        string storagePath;
        long storedSize;
        string fileName;

        await using (var connection = await _connections.OpenConnectionAsync(ct))
        await using (var command = Sql.Command(connection, sql)
            .With("@VersionId", request.FileVersionId)
            .With("@UserId", _currentUser.UserId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                throw AppException.NotFound("File version not found.");
            }
            storagePath = reader.GetString(0);
            storedSize = reader.GetInt64(1);
            fileName = reader.GetString(2);
        }

        var stream = _storage.OpenObjectRead(_currentUser.UserId, storagePath);
        return new VersionDownload(stream, fileName, storedSize);
    }
}
