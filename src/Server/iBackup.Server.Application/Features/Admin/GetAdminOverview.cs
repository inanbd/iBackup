using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

public sealed record AdminAuditRow(
    DateTime TimestampUtc, string Action, string? Details, string? UserEmail, string? IpAddress);

public sealed record AdminOverview(
    int TotalUsers,
    int ActiveUsers,
    int AdminUsers,
    long TotalUsedBytes,
    long TotalQuotaBytes,
    int TotalDevices,
    int TotalFiles,
    long TotalStoredBytes,
    int ActiveUploadSessions,
    int JobsLast24h,
    int FailedJobsLast24h,
    IReadOnlyList<AdminAuditRow> RecentActivity);

/// <summary>Admin dashboard home: server-wide statistics in a single round trip.</summary>
public sealed record GetAdminOverviewQuery : IRequest<AdminOverview>;

internal sealed class GetAdminOverviewHandler : IRequestHandler<GetAdminOverviewQuery, AdminOverview>
{
    private readonly ISqlConnectionFactory _connections;

    public GetAdminOverviewHandler(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<AdminOverview> Handle(GetAdminOverviewQuery request, CancellationToken ct)
    {
        const string sql = """
            -- 1: users
            SELECT COUNT(*),
                   SUM(CASE WHEN IsActive = 1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN IsAdmin = 1 THEN 1 ELSE 0 END),
                   ISNULL(SUM(UsedBytes), 0),
                   ISNULL(SUM(QuotaBytes), 0)
            FROM dbo.Users;

            -- 2: devices / files / storage / uploads
            SELECT
                (SELECT COUNT(*) FROM dbo.Devices WHERE IsDeleted = 0),
                (SELECT COUNT(*) FROM dbo.Files WHERE IsDeleted = 0),
                (SELECT ISNULL(SUM(StoredSize), 0) FROM dbo.StorageObjects),
                (SELECT COUNT(*) FROM dbo.UploadSessions WHERE Status = 0);

            -- 3: jobs in the last 24 hours
            SELECT COUNT(*), SUM(CASE WHEN Status = 3 THEN 1 ELSE 0 END)
            FROM dbo.BackupJobs
            WHERE StartedAtUtc >= DATEADD(HOUR, -24, SYSUTCDATETIME());

            -- 4: recent activity across all users
            SELECT TOP (15) a.CreatedAtUtc, a.Action, a.Details, u.Email, a.IpAddress
            FROM dbo.AuditLogs a
            LEFT JOIN dbo.Users u ON u.Id = a.UserId
            ORDER BY a.CreatedAtUtc DESC;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);

        int totalUsers = 0, activeUsers = 0, adminUsers = 0;
        long usedBytes = 0, quotaBytes = 0;
        if (await reader.ReadAsync(ct))
        {
            totalUsers = reader.GetInt32(0);
            activeUsers = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            adminUsers = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            usedBytes = reader.GetInt64(3);
            quotaBytes = reader.GetInt64(4);
        }

        int devices = 0, files = 0, activeSessions = 0;
        long storedBytes = 0;
        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            devices = reader.GetInt32(0);
            files = reader.GetInt32(1);
            storedBytes = reader.GetInt64(2);
            activeSessions = reader.GetInt32(3);
        }

        int jobs24 = 0, failed24 = 0;
        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            jobs24 = reader.GetInt32(0);
            failed24 = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }

        var activity = new List<AdminAuditRow>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            activity.Add(new AdminAuditRow(
                reader.GetDateTime(0), reader.GetString(1),
                reader.GetStringOrNull(2), reader.GetStringOrNull(3), reader.GetStringOrNull(4)));
        }

        return new AdminOverview(
            totalUsers, activeUsers, adminUsers, usedBytes, quotaBytes,
            devices, files, storedBytes, activeSessions, jobs24, failed24, activity);
    }
}
