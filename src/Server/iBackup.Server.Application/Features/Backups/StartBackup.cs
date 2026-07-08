using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>POST /api/backup/start — opens a backup job session for a device.</summary>
public sealed record StartBackupCommand(Guid DeviceId, Guid? FolderId, BackupType Type) : IRequest<StartBackupResponse>;

internal sealed class StartBackupValidator : AbstractValidator<StartBackupCommand>
{
    public StartBackupValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Type).IsInEnum();
    }
}

internal sealed class StartBackupHandler : IRequestHandler<StartBackupCommand, StartBackupResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public StartBackupHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task<StartBackupResponse> Handle(StartBackupCommand request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string deviceSql = """
            SELECT IsActive FROM dbo.Devices
            WHERE Id = @DeviceId AND UserId = @UserId AND IsDeleted = 0;
            """;
        await using (var deviceCheck = Sql.Command(connection, deviceSql)
            .With("@DeviceId", request.DeviceId)
            .With("@UserId", _currentUser.UserId))
        {
            var isActive = await deviceCheck.ExecuteScalarAsync(ct);
            if (isActive is null)
            {
                throw AppException.NotFound("Device not found.");
            }
            if (isActive is bool b && !b)
            {
                throw AppException.Forbidden("Device is disabled.");
            }
        }

        if (request.FolderId is { } folderId)
        {
            const string folderSql = "SELECT 1 FROM dbo.BackupFolders WHERE Id = @FolderId AND UserId = @UserId AND IsDeleted = 0;";
            await using var folderCheck = Sql.Command(connection, folderSql)
                .With("@FolderId", folderId)
                .With("@UserId", _currentUser.UserId);
            if (await folderCheck.ExecuteScalarAsync(ct) is null)
            {
                throw AppException.NotFound("Folder not found.");
            }
        }

        var jobId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO dbo.BackupJobs (Id, UserId, DeviceId, FolderId, BackupType)
            VALUES (@Id, @UserId, @DeviceId, @FolderId, @Type);
            """;
        await using (var insert = Sql.Command(connection, insertSql)
            .With("@Id", jobId)
            .With("@UserId", _currentUser.UserId)
            .With("@DeviceId", request.DeviceId)
            .With("@FolderId", request.FolderId)
            .With("@Type", (byte)request.Type))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await _audit.LogAsync(_currentUser.UserId, request.DeviceId, "backup.started", $"Job {jobId} ({request.Type})", _currentUser.IpAddress, ct);
        return new StartBackupResponse(jobId);
    }
}
