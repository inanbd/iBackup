using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Dashboard;

/// <summary>GET /api/dashboard — aggregated account overview in a single round trip.</summary>
public sealed record GetDashboardQuery : IRequest<DashboardResponse>;

internal sealed class GetDashboardHandler : IRequestHandler<GetDashboardQuery, DashboardResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public GetDashboardHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<DashboardResponse> Handle(GetDashboardQuery request, CancellationToken ct)
    {
        const string sql = """
            -- 1: quota / usage
            SELECT QuotaBytes, UsedBytes FROM dbo.Users WHERE Id = @UserId;

            -- 2: last completed backup + device count + total files + uploaded today
            SELECT
                (SELECT MAX(CompletedAtUtc) FROM dbo.BackupJobs WHERE UserId = @UserId AND Status IN (1, 2)),
                (SELECT COUNT(*) FROM dbo.Devices WHERE UserId = @UserId AND IsDeleted = 0),
                (SELECT COUNT(*) FROM dbo.Files WHERE UserId = @UserId AND IsDeleted = 0),
                (SELECT COUNT(*) FROM dbo.FileVersions v JOIN dbo.Files f ON f.Id = v.FileId
                  WHERE f.UserId = @UserId AND v.BackedUpAtUtc >= @TodayUtc),
                (SELECT ISNULL(SUM(v.OriginalSize), 0) FROM dbo.FileVersions v JOIN dbo.Files f ON f.Id = v.FileId
                  WHERE f.UserId = @UserId AND v.BackedUpAtUtc >= @TodayUtc);

            -- 3: currently running backup, if any
            SELECT TOP (1) j.Id, j.DeviceId, d.DeviceName, j.BackupType, j.StartedAtUtc
            FROM dbo.BackupJobs j
            JOIN dbo.Devices d ON d.Id = j.DeviceId
            WHERE j.UserId = @UserId AND j.Status = 0
            ORDER BY j.StartedAtUtc DESC;

            -- 4: recent activity
            SELECT TOP (20) a.CreatedAtUtc, a.Action, a.Details, d.DeviceName
            FROM dbo.AuditLogs a
            LEFT JOIN dbo.Devices d ON d.Id = a.DeviceId
            WHERE a.UserId = @UserId
            ORDER BY a.CreatedAtUtc DESC;

            -- 5: recent jobs
            SELECT TOP (10) j.Id, j.DeviceId, d.DeviceName, j.FolderId, f.FolderPath,
                   j.BackupType, j.Status, j.StartedAtUtc, j.CompletedAtUtc,
                   j.UploadedFiles, j.SkippedFiles, j.FailedFiles, j.TotalBytes, j.ErrorMessage
            FROM dbo.BackupJobs j
            JOIN dbo.Devices d ON d.Id = j.DeviceId
            LEFT JOIN dbo.BackupFolders f ON f.Id = j.FolderId
            WHERE j.UserId = @UserId
            ORDER BY j.StartedAtUtc DESC;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql)
            .With("@UserId", _currentUser.UserId)
            .With("@TodayUtc", DateTime.UtcNow.Date);
        await using var reader = await command.ExecuteReaderAsync(ct);

        long quotaBytes = 0, usedBytes = 0;
        if (await reader.ReadAsync(ct))
        {
            quotaBytes = reader.GetInt64(0);
            usedBytes = reader.GetInt64(1);
        }

        DateTime? lastBackup = null;
        int deviceCount = 0, totalFiles = 0, filesToday = 0;
        long bytesToday = 0;
        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            lastBackup = reader.GetDateTimeOrNull(0);
            deviceCount = reader.GetInt32(1);
            totalFiles = reader.GetInt32(2);
            filesToday = reader.GetInt32(3);
            bytesToday = reader.GetInt64(4);
        }

        CurrentBackupDto? currentBackup = null;
        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            currentBackup = new CurrentBackupDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                (BackupType)reader.GetByte(3), reader.GetDateTime(4));
        }

        var activity = new List<ActivityItemDto>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            activity.Add(new ActivityItemDto(
                reader.GetDateTime(0), reader.GetString(1),
                reader.GetStringOrNull(2), reader.GetStringOrNull(3)));
        }

        var recentJobs = new List<BackupHistoryItemDto>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var started = reader.GetDateTime(7);
            var completed = reader.GetDateTimeOrNull(8);
            recentJobs.Add(new BackupHistoryItemDto(
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

        return new DashboardResponse(
            StorageUsedBytes: usedBytes,
            QuotaBytes: quotaBytes,
            RemainingQuotaBytes: Math.Max(0, quotaBytes - usedBytes),
            LastBackupAtUtc: lastBackup,
            CurrentBackup: currentBackup,
            FilesUploadedToday: filesToday,
            BytesUploadedToday: bytesToday,
            RegisteredDevices: deviceCount,
            TotalFiles: totalFiles,
            RecentActivity: activity,
            RecentJobs: recentJobs);
    }
}
