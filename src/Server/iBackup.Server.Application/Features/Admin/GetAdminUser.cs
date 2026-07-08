using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

public sealed record AdminUserDetail(
    AdminUserRow User,
    IReadOnlyList<DeviceDto> Devices,
    IReadOnlyList<BackupHistoryItemDto> RecentJobs,
    IReadOnlyList<AdminAuditRow> RecentActivity);

/// <summary>Admin: one user's account, devices, recent jobs and audit trail.</summary>
public sealed record GetAdminUserQuery(Guid UserId) : IRequest<AdminUserDetail>;

internal sealed class GetAdminUserValidator : AbstractValidator<GetAdminUserQuery>
{
    public GetAdminUserValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}

internal sealed class GetAdminUserHandler : IRequestHandler<GetAdminUserQuery, AdminUserDetail>
{
    private readonly ISqlConnectionFactory _connections;

    public GetAdminUserHandler(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<AdminUserDetail> Handle(GetAdminUserQuery request, CancellationToken ct)
    {
        const string sql = """
            -- 1: account
            SELECT u.Id, u.Email, u.DisplayName, u.QuotaBytes, u.UsedBytes, u.IsActive, u.IsAdmin,
                   (SELECT COUNT(*) FROM dbo.Devices d WHERE d.UserId = u.Id AND d.IsDeleted = 0),
                   (SELECT COUNT(*) FROM dbo.Files f WHERE f.UserId = u.Id AND f.IsDeleted = 0),
                   u.CreatedAtUtc,
                   (SELECT MAX(d.LastBackupAtUtc) FROM dbo.Devices d WHERE d.UserId = u.Id)
            FROM dbo.Users u
            WHERE u.Id = @UserId;

            -- 2: devices
            SELECT Id, DeviceName, OperatingSystem, ClientVersion, LastBackupAtUtc,
                   LastOnlineAtUtc, IpAddress, RegisteredAtUtc, IsActive
            FROM dbo.Devices
            WHERE UserId = @UserId AND IsDeleted = 0
            ORDER BY RegisteredAtUtc;

            -- 3: recent jobs
            SELECT TOP (10) j.Id, j.DeviceId, d.DeviceName, j.FolderId, f.FolderPath,
                   j.BackupType, j.Status, j.StartedAtUtc, j.CompletedAtUtc,
                   j.UploadedFiles, j.SkippedFiles, j.FailedFiles, j.TotalBytes, j.ErrorMessage
            FROM dbo.BackupJobs j
            JOIN dbo.Devices d ON d.Id = j.DeviceId
            LEFT JOIN dbo.BackupFolders f ON f.Id = j.FolderId
            WHERE j.UserId = @UserId
            ORDER BY j.StartedAtUtc DESC;

            -- 4: recent audit
            SELECT TOP (15) a.CreatedAtUtc, a.Action, a.Details, CAST(NULL AS NVARCHAR(256)), a.IpAddress
            FROM dbo.AuditLogs a
            WHERE a.UserId = @UserId
            ORDER BY a.CreatedAtUtc DESC;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql).With("@UserId", request.UserId);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            throw AppException.NotFound("User not found.");
        }

        var user = new AdminUserRow(
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
            LastBackupAtUtc: reader.GetDateTimeOrNull(10));

        var devices = new List<DeviceDto>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            devices.Add(new DeviceDto(
                Id: reader.GetGuid(0),
                DeviceName: reader.GetString(1),
                OperatingSystem: reader.GetString(2),
                ClientVersion: reader.GetString(3),
                LastBackupAtUtc: reader.GetDateTimeOrNull(4),
                LastOnlineAtUtc: reader.GetDateTimeOrNull(5),
                IpAddress: reader.GetStringOrNull(6),
                RegisteredAtUtc: reader.GetDateTime(7),
                IsActive: reader.GetBoolean(8),
                IsCurrentDevice: false));
        }

        var jobs = new List<BackupHistoryItemDto>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var started = reader.GetDateTime(7);
            var completed = reader.GetDateTimeOrNull(8);
            jobs.Add(new BackupHistoryItemDto(
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

        var activity = new List<AdminAuditRow>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            activity.Add(new AdminAuditRow(
                reader.GetDateTime(0), reader.GetString(1),
                reader.GetStringOrNull(2), reader.GetStringOrNull(3), reader.GetStringOrNull(4)));
        }

        return new AdminUserDetail(user, devices, jobs, activity);
    }
}
