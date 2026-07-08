using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

public sealed record AdminUserRow(
    Guid Id,
    string Email,
    string DisplayName,
    long QuotaBytes,
    long UsedBytes,
    bool IsActive,
    bool IsAdmin,
    int DeviceCount,
    int FileCount,
    DateTime CreatedAtUtc,
    DateTime? LastBackupAtUtc);

/// <summary>Admin: paged, searchable user list.</summary>
public sealed record GetAdminUsersQuery(string? Search, int Page = 1, int PageSize = 25)
    : IRequest<PagedResult<AdminUserRow>>;

internal sealed class GetAdminUsersValidator : AbstractValidator<GetAdminUsersQuery>
{
    public GetAdminUsersValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.Search).MaximumLength(256);
    }
}

internal sealed class GetAdminUsersHandler : IRequestHandler<GetAdminUsersQuery, PagedResult<AdminUserRow>>
{
    private readonly ISqlConnectionFactory _connections;

    public GetAdminUsersHandler(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<PagedResult<AdminUserRow>> Handle(GetAdminUsersQuery request, CancellationToken ct)
    {
        var like = string.IsNullOrWhiteSpace(request.Search)
            ? null
            : "%" + request.Search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string countSql = """
            SELECT COUNT(*) FROM dbo.Users
            WHERE (@Like IS NULL OR Email LIKE @Like OR DisplayName LIKE @Like);
            """;
        int totalCount;
        await using (var count = Sql.Command(connection, countSql).With("@Like", like))
        {
            totalCount = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        const string pageSql = """
            SELECT u.Id, u.Email, u.DisplayName, u.QuotaBytes, u.UsedBytes, u.IsActive, u.IsAdmin,
                   (SELECT COUNT(*) FROM dbo.Devices d WHERE d.UserId = u.Id AND d.IsDeleted = 0),
                   (SELECT COUNT(*) FROM dbo.Files f WHERE f.UserId = u.Id AND f.IsDeleted = 0),
                   u.CreatedAtUtc,
                   (SELECT MAX(d.LastBackupAtUtc) FROM dbo.Devices d WHERE d.UserId = u.Id)
            FROM dbo.Users u
            WHERE (@Like IS NULL OR u.Email LIKE @Like OR u.DisplayName LIKE @Like)
            ORDER BY u.CreatedAtUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var rows = new List<AdminUserRow>();
        await using (var page = Sql.Command(connection, pageSql)
            .With("@Like", like)
            .With("@Offset", (request.Page - 1) * request.PageSize)
            .With("@PageSize", request.PageSize))
        await using (var reader = await page.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new AdminUserRow(
                    Id: reader.GetGuid(0),
                    Email: reader.GetString(1),
                    DisplayName: reader.GetString(2),
                    QuotaBytes: reader.GetInt64(3),
                    UsedBytes: reader.GetInt64(4),
                    IsActive: reader.GetBoolean(5),
                    IsAdmin: reader.GetBoolean(6),
                    DeviceCount: reader.GetInt32(7),
                    FileCount: reader.GetInt32(8),
                    CreatedAtUtc: reader.GetDateTime(9),
                    LastBackupAtUtc: reader.GetDateTimeOrNull(10)));
            }
        }

        return new PagedResult<AdminUserRow>(rows, request.Page, request.PageSize, totalCount);
    }
}
