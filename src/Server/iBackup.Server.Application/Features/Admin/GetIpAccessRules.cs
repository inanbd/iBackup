using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

public sealed record IpRuleRow(long Id, string IpAddress, string? Reason, bool IsAuto, DateTime CreatedAtUtc);

/// <summary>Whitelist and blacklist rules, split for the admin IP-access page.</summary>
public sealed record IpAccessRules(IReadOnlyList<IpRuleRow> Whitelist, IReadOnlyList<IpRuleRow> Blacklist);

public sealed record GetIpAccessRulesQuery : IRequest<IpAccessRules>;

internal sealed class GetIpAccessRulesHandler : IRequestHandler<GetIpAccessRulesQuery, IpAccessRules>
{
    private readonly ISqlConnectionFactory _connections;

    public GetIpAccessRulesHandler(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<IpAccessRules> Handle(GetIpAccessRulesQuery request, CancellationToken ct)
    {
        const string sql = """
            SELECT Id, IpAddress, Kind, Reason, IsAuto, CreatedAtUtc
            FROM dbo.IpAccessRules
            ORDER BY Kind, CreatedAtUtc DESC;
            """;

        var whitelist = new List<IpRuleRow>();
        var blacklist = new List<IpRuleRow>();

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new IpRuleRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetStringOrNull(3),
                reader.GetBoolean(4), reader.GetDateTime(5));
            if (reader.GetByte(2) == 0)
            {
                whitelist.Add(row);
            }
            else
            {
                blacklist.Add(row);
            }
        }

        return new IpAccessRules(whitelist, blacklist);
    }
}
