using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Restore;

/// <summary>
/// GET /api/restore/files — browse backed-up files with their version history.
/// Filter by folder and/or a path prefix; deleted files are included on request.
/// </summary>
public sealed record GetRestoreFilesQuery(
    Guid? FolderId,
    string? PathPrefix,
    bool IncludeDeleted,
    int Page = 1,
    int PageSize = 200) : IRequest<PagedResult<RestoreFileItemDto>>;

internal sealed class GetRestoreFilesValidator : AbstractValidator<GetRestoreFilesQuery>
{
    public GetRestoreFilesValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 1000);
        RuleFor(x => x.PathPrefix).MaximumLength(1024);
    }
}

internal sealed class GetRestoreFilesHandler : IRequestHandler<GetRestoreFilesQuery, PagedResult<RestoreFileItemDto>>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public GetRestoreFilesHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<PagedResult<RestoreFileItemDto>> Handle(GetRestoreFilesQuery request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);

        // ESCAPE '\' lets us match a literal prefix safely.
        var prefix = request.PathPrefix?.Replace('\\', '/').TrimStart('/');
        var likePattern = prefix is null
            ? null
            : prefix.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

        const string countSql = """
            SELECT COUNT(*)
            FROM dbo.Files f
            WHERE f.UserId = @UserId
              AND (@FolderId IS NULL OR f.FolderId = @FolderId)
              AND (@Like IS NULL OR f.RelativePath LIKE @Like)
              AND (@IncludeDeleted = 1 OR f.IsDeleted = 0);
            """;
        int totalCount;
        await using (var count = Sql.Command(connection, countSql)
            .With("@UserId", _currentUser.UserId)
            .With("@FolderId", request.FolderId)
            .With("@Like", likePattern)
            .With("@IncludeDeleted", request.IncludeDeleted))
        {
            totalCount = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        const string filesSql = """
            SELECT f.Id, f.FolderId, f.RelativePath, f.FileName, f.IsDeleted, f.DeletedAtUtc
            FROM dbo.Files f
            WHERE f.UserId = @UserId
              AND (@FolderId IS NULL OR f.FolderId = @FolderId)
              AND (@Like IS NULL OR f.RelativePath LIKE @Like)
              AND (@IncludeDeleted = 1 OR f.IsDeleted = 0)
            ORDER BY f.RelativePath
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var files = new List<(Guid Id, Guid FolderId, string Path, string Name, bool Deleted, DateTime? DeletedAt)>();
        await using (var filesCommand = Sql.Command(connection, filesSql)
            .With("@UserId", _currentUser.UserId)
            .With("@FolderId", request.FolderId)
            .With("@Like", likePattern)
            .With("@IncludeDeleted", request.IncludeDeleted)
            .With("@Offset", (request.Page - 1) * request.PageSize)
            .With("@PageSize", request.PageSize))
        await using (var reader = await filesCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                files.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.GetBoolean(4), reader.GetDateTimeOrNull(5)));
            }
        }

        if (files.Count == 0)
        {
            return new PagedResult<RestoreFileItemDto>([], request.Page, request.PageSize, totalCount);
        }

        // Load version history for the page of files in one round trip.
        var versionsByFile = new Dictionary<Guid, List<FileVersionDto>>();
        const string versionsSql = """
            SELECT v.FileId, v.Id, v.VersionNumber, v.OriginalSize, v.EncryptedSize, v.Sha256,
                   v.FileModifiedAtUtc, v.BackedUpAtUtc, o.CompressionMethod, o.EncryptionMethod, o.ChunkSize
            FROM dbo.FileVersions v
            JOIN dbo.StorageObjects o ON o.Id = v.StorageObjectId
            JOIN dbo.Files f ON f.Id = v.FileId
            WHERE f.UserId = @UserId AND v.Status = 1
              AND v.FileId IN (SELECT value FROM STRING_SPLIT(@FileIds, ','))
            ORDER BY v.FileId, v.VersionNumber DESC;
            """;
        await using (var versions = Sql.Command(connection, versionsSql)
            .With("@UserId", _currentUser.UserId)
            .With("@FileIds", string.Join(',', files.Select(f => f.Id))))
        await using (var reader = await versions.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var fileId = reader.GetGuid(0);
                if (!versionsByFile.TryGetValue(fileId, out var list))
                {
                    versionsByFile[fileId] = list = [];
                }
                list.Add(new FileVersionDto(
                    VersionId: reader.GetGuid(1),
                    VersionNumber: reader.GetInt32(2),
                    OriginalSize: reader.GetInt64(3),
                    EncryptedSize: reader.GetInt64(4),
                    Sha256: reader.GetString(5),
                    FileModifiedAtUtc: reader.GetDateTime(6),
                    BackedUpAtUtc: reader.GetDateTime(7),
                    Compression: (CompressionMethod)reader.GetByte(8),
                    Encryption: (EncryptionMethod)reader.GetByte(9),
                    ChunkSize: reader.GetInt32(10)));
            }
        }

        var items = files
            .Select(f => new RestoreFileItemDto(
                f.Id, f.FolderId, f.Path, f.Name, f.Deleted, f.DeletedAt,
                versionsByFile.TryGetValue(f.Id, out var v) ? v : []))
            .ToList();

        return new PagedResult<RestoreFileItemDto>(items, request.Page, request.PageSize, totalCount);
    }
}
