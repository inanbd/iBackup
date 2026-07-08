using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>GET /api/backup/history — paged backup job history.</summary>
public sealed record GetBackupHistoryQuery(Guid? DeviceId, int Page = 1, int PageSize = 50)
    : IRequest<PagedResult<BackupHistoryItemDto>>;

internal sealed class GetBackupHistoryValidator : AbstractValidator<GetBackupHistoryQuery>
{
    public GetBackupHistoryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500);
    }
}

internal sealed class GetBackupHistoryHandler : IRequestHandler<GetBackupHistoryQuery, PagedResult<BackupHistoryItemDto>>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public GetBackupHistoryHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<PagedResult<BackupHistoryItemDto>> Handle(GetBackupHistoryQuery request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string countSql = """
            SELECT COUNT(*) FROM dbo.BackupJobs
            WHERE UserId = @UserId AND (@DeviceId IS NULL OR DeviceId = @DeviceId);
            """;
        int totalCount;
        await using (var count = Sql.Command(connection, countSql)
            .With("@UserId", _currentUser.UserId)
            .With("@DeviceId", request.DeviceId))
        {
            totalCount = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        const string pageSql = """
            SELECT j.Id, j.DeviceId, d.DeviceName, j.FolderId, f.FolderPath,
                   j.BackupType, j.Status, j.StartedAtUtc, j.CompletedAtUtc,
                   j.UploadedFiles, j.SkippedFiles, j.FailedFiles, j.TotalBytes, j.ErrorMessage
            FROM dbo.BackupJobs j
            JOIN dbo.Devices d ON d.Id = j.DeviceId
            LEFT JOIN dbo.BackupFolders f ON f.Id = j.FolderId
            WHERE j.UserId = @UserId AND (@DeviceId IS NULL OR j.DeviceId = @DeviceId)
            ORDER BY j.StartedAtUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var items = new List<BackupHistoryItemDto>();
        await using (var page = Sql.Command(connection, pageSql)
            .With("@UserId", _currentUser.UserId)
            .With("@DeviceId", request.DeviceId)
            .With("@Offset", (request.Page - 1) * request.PageSize)
            .With("@PageSize", request.PageSize))
        await using (var reader = await page.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var started = reader.GetDateTime(7);
                var completed = reader.GetDateTimeOrNull(8);
                items.Add(new BackupHistoryItemDto(
                    BackupJobId: reader.GetGuid(0),
                    DeviceId: reader.GetGuid(1),
                    DeviceName: reader.GetString(2),
                    FolderId: reader.GetGuidOrNull(3),
                    FolderPath: reader.GetStringOrNull(4),
                    Type: (BackupType)reader.GetByte(5),
                    Status: (BackupJobStatus)reader.GetByte(6),
                    StartedAtUtc: started,
                    CompletedAtUtc: completed,
                    DurationSeconds: completed is null ? null : (completed.Value - started).TotalSeconds,
                    UploadedFiles: reader.GetInt32(9),
                    SkippedFiles: reader.GetInt32(10),
                    FailedFiles: reader.GetInt32(11),
                    TotalBytes: reader.GetInt64(12),
                    ErrorMessage: reader.GetStringOrNull(13)));
            }
        }

        return new PagedResult<BackupHistoryItemDto>(items, request.Page, request.PageSize, totalCount);
    }
}
