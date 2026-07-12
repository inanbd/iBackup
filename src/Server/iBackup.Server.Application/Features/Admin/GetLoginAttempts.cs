using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

public sealed record LoginAttemptRow(
    DateTime TimestampUtc,
    string? Email,
    string? IpAddress,
    bool Success,
    string? Reason,
    string? Source);

/// <summary>Admin: paged login-attempt history, filterable by IP/email and outcome.</summary>
public sealed record GetLoginAttemptsQuery(
    string? IpFilter,
    string? EmailFilter,
    bool FailuresOnly,
    int Page = 1,
    int PageSize = 50) : IRequest<PagedResult<LoginAttemptRow>>;

internal sealed class GetLoginAttemptsValidator : AbstractValidator<GetLoginAttemptsQuery>
{
    public GetLoginAttemptsValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500);
        RuleFor(x => x.IpFilter).MaximumLength(64);
        RuleFor(x => x.EmailFilter).MaximumLength(256);
    }
}

internal sealed class GetLoginAttemptsHandler : IRequestHandler<GetLoginAttemptsQuery, PagedResult<LoginAttemptRow>>
{
    private readonly ISqlConnectionFactory _connections;

    public GetLoginAttemptsHandler(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<PagedResult<LoginAttemptRow>> Handle(GetLoginAttemptsQuery request, CancellationToken ct)
    {
        static string? Like(string? term) => string.IsNullOrWhiteSpace(term)
            ? null
            : "%" + term.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

        var ipLike = Like(request.IpFilter);
        var emailLike = Like(request.EmailFilter);

        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string filter = """
            WHERE (@IpLike IS NULL OR IpAddress LIKE @IpLike)
              AND (@EmailLike IS NULL OR Email LIKE @EmailLike)
              AND (@FailuresOnly = 0 OR Success = 0)
            """;

        int totalCount;
        await using (var count = Sql.Command(connection, $"SELECT COUNT(*) FROM dbo.LoginAttempts {filter};")
            .With("@IpLike", ipLike)
            .With("@EmailLike", emailLike)
            .With("@FailuresOnly", request.FailuresOnly))
        {
            totalCount = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        var pageSql = $"""
            SELECT CreatedAtUtc, Email, IpAddress, Success, Reason, Source
            FROM dbo.LoginAttempts
            {filter}
            ORDER BY CreatedAtUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var rows = new List<LoginAttemptRow>();
        await using (var page = Sql.Command(connection, pageSql)
            .With("@IpLike", ipLike)
            .With("@EmailLike", emailLike)
            .With("@FailuresOnly", request.FailuresOnly)
            .With("@Offset", (request.Page - 1) * request.PageSize)
            .With("@PageSize", request.PageSize))
        await using (var reader = await page.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new LoginAttemptRow(
                    reader.GetDateTime(0),
                    reader.GetStringOrNull(1),
                    reader.GetStringOrNull(2),
                    reader.GetBoolean(3),
                    reader.GetStringOrNull(4),
                    reader.GetStringOrNull(5)));
            }
        }

        return new PagedResult<LoginAttemptRow>(rows, request.Page, request.PageSize, totalCount);
    }
}
