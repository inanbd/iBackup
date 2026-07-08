using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>POST /api/backup/finish — closes a backup job and stores its statistics.</summary>
public sealed record FinishBackupCommand(FinishBackupRequest Request) : IRequest;

internal sealed class FinishBackupValidator : AbstractValidator<FinishBackupCommand>
{
    public FinishBackupValidator()
    {
        RuleFor(x => x.Request.BackupJobId).NotEmpty();
        RuleFor(x => x.Request.Status).IsInEnum()
            .Must(s => s != BackupJobStatus.Running).WithMessage("Finished status cannot be Running.");
        RuleFor(x => x.Request.UploadedFiles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.SkippedFiles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.FailedFiles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.TotalBytes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.ErrorMessage).MaximumLength(2000);
    }
}

internal sealed class FinishBackupHandler : IRequestHandler<FinishBackupCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public FinishBackupHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task Handle(FinishBackupCommand command, CancellationToken ct)
    {
        var request = command.Request;
        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string sql = """
            UPDATE dbo.BackupJobs
            SET Status = @Status, CompletedAtUtc = SYSUTCDATETIME(),
                UploadedFiles = @Uploaded, SkippedFiles = @Skipped, FailedFiles = @Failed,
                TotalBytes = @TotalBytes, ErrorMessage = @Error
            WHERE Id = @Id AND UserId = @UserId AND Status = 0;
            """;

        await using var command2 = Sql.Command(connection, sql)
            .With("@Id", request.BackupJobId)
            .With("@UserId", _currentUser.UserId)
            .With("@Status", (byte)request.Status)
            .With("@Uploaded", request.UploadedFiles)
            .With("@Skipped", request.SkippedFiles)
            .With("@Failed", request.FailedFiles)
            .With("@TotalBytes", request.TotalBytes)
            .With("@Error", request.ErrorMessage);

        if (await command2.ExecuteNonQueryAsync(ct) != 1)
        {
            throw AppException.NotFound("Running backup job not found.");
        }

        const string deviceSql = """
            UPDATE d
            SET d.LastBackupAtUtc = SYSUTCDATETIME()
            FROM dbo.Devices d
            JOIN dbo.BackupJobs j ON j.DeviceId = d.Id
            WHERE j.Id = @JobId AND @Status IN (1, 2);
            """;
        await using (var device = Sql.Command(connection, deviceSql)
            .With("@JobId", request.BackupJobId)
            .With("@Status", (byte)request.Status))
        {
            await device.ExecuteNonQueryAsync(ct);
        }

        await _audit.LogAsync(_currentUser.UserId, _currentUser.DeviceId, "backup.finished",
            $"Job {request.BackupJobId}: {request.Status}, {request.UploadedFiles} uploaded, {request.FailedFiles} failed",
            _currentUser.IpAddress, ct);
    }
}
