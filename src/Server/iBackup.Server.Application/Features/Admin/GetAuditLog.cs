using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

/// <summary>Admin: paged audit log across all users, filterable by email and action.</summary>
public sealed record GetAuditLogQuery(
    string? EmailFilter,
    string? ActionFilter,
    int Page = 1,
    int PageSize = 50) : IRequest<PagedResult<AdminAuditRow>>;

internal sealed class GetAuditLogValidator : AbstractValidator<GetAuditLogQuery>
{
    public GetAuditLogValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500);
        RuleFor(x => x.EmailFilter).MaximumLength(256);
        RuleFor(x => x.ActionFilter).MaximumLength(100);
    }
}

internal sealed class GetAuditLogHandler : IRequestHandler<GetAuditLogQuery, PagedResult<AdminAuditRow>>
{
    private readonly ISqlConnectionFactory _connections;

    public GetAuditLogHandler(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<PagedResult<AdminAuditRow>> Handle(GetAuditLogQuery request, CancellationToken ct)
    {
        static string? Like(string? term) => string.IsNullOrWhiteSpace(term)
            ? null
            : "%" + term.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

        var emailLike = Like(request.EmailFilter);
        var actionLike = Like(request.ActionFilter);

        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string countSql = """
            SELECT COUNT(*)
            FROM dbo.AuditLogs a
            LEFT JOIN dbo.Users u ON u.Id = a.UserId
            WHERE (@EmailLike IS NULL OR u.Email LIKE @EmailLike)
              AND (@ActionLike IS NULL OR a.Action LIKE @ActionLike);
            """;
        int totalCount;
        await using (var count = Sql.Command(connection, countSql)
            .With("@EmailLike", emailLike)
            .With("@ActionLike", actionLike))
        {
            totalCount = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        const string pageSql = """
            SELECT a.CreatedAtUtc, a.Action, a.Details, u.Email, a.IpAddress
            FROM dbo.AuditLogs a
            LEFT JOIN dbo.Users u ON u.Id = a.UserId
            WHERE (@EmailLike IS NULL OR u.Email LIKE @EmailLike)
              AND (@ActionLike IS NULL OR a.Action LIKE @ActionLike)
            ORDER BY a.CreatedAtUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var rows = new List<AdminAuditRow>();
        await using (var page = Sql.Command(connection, pageSql)
            .With("@EmailLike", emailLike)
            .With("@ActionLike", actionLike)
            .With("@Offset", (request.Page - 1) * request.PageSize)
            .With("@PageSize", request.PageSize))
        await using (var reader = await page.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new AdminAuditRow(
                    reader.GetDateTime(0), reader.GetString(1),
                    reader.GetStringOrNull(2), reader.GetStringOrNull(3), reader.GetStringOrNull(4)));
            }
        }

        return new PagedResult<AdminAuditRow>(rows, request.Page, request.PageSize, totalCount);
    }
}
