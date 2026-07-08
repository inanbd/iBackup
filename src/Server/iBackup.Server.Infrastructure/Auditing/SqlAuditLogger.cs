using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using Microsoft.Extensions.Logging;

namespace iBackup.Server.Infrastructure.Auditing;

/// <summary>Writes audit entries to the AuditLogs table. Failures never break the request.</summary>
public sealed class SqlAuditLogger : IAuditLogger
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ILogger<SqlAuditLogger> _logger;

    public SqlAuditLogger(ISqlConnectionFactory connections, ILogger<SqlAuditLogger> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public async Task LogAsync(Guid? userId, Guid? deviceId, string action, string? details, string? ipAddress, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.AuditLogs (UserId, DeviceId, Action, Details, IpAddress)
            VALUES (@UserId, @DeviceId, @Action, @Details, @Ip);
            """;

        try
        {
            await using var connection = await _connections.OpenConnectionAsync(ct);
            await using var command = Sql.Command(connection, sql)
                .With("@UserId", userId)
                .With("@DeviceId", deviceId)
                .With("@Action", action)
                .With("@Details", details is { Length: > 2000 } ? details[..2000] : details)
                .With("@Ip", ipAddress);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log entry {Action}", action);
        }
    }
}
