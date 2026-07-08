using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Folders;

/// <summary>POST /api/folders — adds a backup source folder for a device.</summary>
public sealed record CreateFolderCommand(CreateFolderRequest Folder) : IRequest<BackupFolderDto>;

internal sealed class CreateFolderValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderValidator()
    {
        RuleFor(x => x.Folder.DeviceId).NotEmpty();
        RuleFor(x => x.Folder.Path).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.Folder.Priority).InclusiveBetween(-100, 100);
        RuleFor(x => x.Folder.MaxFileSizeBytes).GreaterThan(0).When(x => x.Folder.MaxFileSizeBytes.HasValue);
        RuleFor(x => x.Folder.Schedule).SetValidator(new ScheduleValidator());
        RuleFor(x => x.Folder.Retention).SetValidator(new RetentionValidator());
    }
}

internal sealed class ScheduleValidator : AbstractValidator<ScheduleSettings>
{
    public ScheduleValidator()
    {
        RuleFor(x => x.IntervalMinutes).NotNull().InclusiveBetween(1, 24 * 60)
            .When(x => x.Type == ScheduleType.EveryXMinutes);
        RuleFor(x => x.DayOfMonth).InclusiveBetween(1, 31)
            .When(x => x.Type == ScheduleType.Monthly && x.DayOfMonth.HasValue);
    }
}

internal sealed class RetentionValidator : AbstractValidator<RetentionSettings>
{
    public RetentionValidator()
    {
        RuleFor(x => x.Value).NotNull().GreaterThan(0)
            .When(x => x.Mode != RetentionMode.KeepForever);
    }
}

internal sealed class CreateFolderHandler : IRequestHandler<CreateFolderCommand, BackupFolderDto>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public CreateFolderHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task<BackupFolderDto> Handle(CreateFolderCommand request, CancellationToken ct)
    {
        var folder = request.Folder;
        await using var connection = await _connections.OpenConnectionAsync(ct);

        // The device must belong to the caller.
        const string deviceSql = "SELECT 1 FROM dbo.Devices WHERE Id = @DeviceId AND UserId = @UserId AND IsDeleted = 0;";
        await using (var deviceCheck = Sql.Command(connection, deviceSql)
            .With("@DeviceId", folder.DeviceId)
            .With("@UserId", _currentUser.UserId))
        {
            if (await deviceCheck.ExecuteScalarAsync(ct) is null)
            {
                throw AppException.NotFound("Device not found.");
            }
        }

        var id = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO dbo.BackupFolders
                (Id, UserId, DeviceId, FolderPath, IsEnabled, Recursive, IncludeHidden, IncludeSystem,
                 ExcludedFolders, ExcludedExtensions, ExcludedFileNames, MaxFileSizeBytes, Priority,
                 ScheduleType, ScheduleIntervalMinutes, ScheduleTimeOfDayMinutes, ScheduleDayOfWeek, ScheduleDayOfMonth,
                 RetentionMode, RetentionValue, CompressionMethod)
            VALUES
                (@Id, @UserId, @DeviceId, @Path, @IsEnabled, @Recursive, @IncludeHidden, @IncludeSystem,
                 @ExFolders, @ExExtensions, @ExNames, @MaxFileSize, @Priority,
                 @SchedType, @SchedInterval, @SchedTimeOfDay, @SchedDayOfWeek, @SchedDayOfMonth,
                 @RetMode, @RetValue, @Compression);
            """;

        await using (var insert = Sql.Command(connection, insertSql)
            .With("@Id", id)
            .With("@UserId", _currentUser.UserId)
            .With("@DeviceId", folder.DeviceId)
            .With("@Path", folder.Path.Trim())
            .With("@IsEnabled", folder.IsEnabled)
            .With("@Recursive", folder.Recursive)
            .With("@IncludeHidden", folder.IncludeHidden)
            .With("@IncludeSystem", folder.IncludeSystem)
            .With("@ExFolders", FolderData.ToJson(folder.ExcludedFolders))
            .With("@ExExtensions", FolderData.ToJson(folder.ExcludedExtensions))
            .With("@ExNames", FolderData.ToJson(folder.ExcludedFileNames))
            .With("@MaxFileSize", folder.MaxFileSizeBytes)
            .With("@Priority", folder.Priority)
            .With("@SchedType", (byte)folder.Schedule.Type)
            .With("@SchedInterval", folder.Schedule.IntervalMinutes)
            .With("@SchedTimeOfDay", folder.Schedule.TimeOfDay is { } t ? (int)t.TotalMinutes : null)
            .With("@SchedDayOfWeek", folder.Schedule.DayOfWeek is { } d ? (byte)d : null)
            .With("@SchedDayOfMonth", folder.Schedule.DayOfMonth is { } m ? (byte)m : null)
            .With("@RetMode", (byte)folder.Retention.Mode)
            .With("@RetValue", folder.Retention.Value)
            .With("@Compression", (byte)folder.Compression))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await _audit.LogAsync(_currentUser.UserId, folder.DeviceId, "folder.created", folder.Path, _currentUser.IpAddress, ct);
        return await GetFolderById(connection, id, ct);
    }

    private static async Task<BackupFolderDto> GetFolderById(Microsoft.Data.SqlClient.SqlConnection connection, Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {FolderData.SelectColumns} FROM dbo.BackupFolders WHERE Id = @Id;";
        await using var command = Sql.Command(connection, sql).With("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw AppException.NotFound("Folder not found after insert.");
        }
        return FolderData.Map(reader);
    }
}
