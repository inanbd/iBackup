using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Folders;

/// <summary>PUT /api/folders — updates the configuration of a backup folder.</summary>
public sealed record UpdateFolderCommand(UpdateFolderRequest Folder) : IRequest<BackupFolderDto>;

internal sealed class UpdateFolderValidator : AbstractValidator<UpdateFolderCommand>
{
    public UpdateFolderValidator()
    {
        RuleFor(x => x.Folder.Id).NotEmpty();
        RuleFor(x => x.Folder.Path).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.Folder.Priority).InclusiveBetween(-100, 100);
        RuleFor(x => x.Folder.MaxFileSizeBytes).GreaterThan(0).When(x => x.Folder.MaxFileSizeBytes.HasValue);
        RuleFor(x => x.Folder.Schedule).SetValidator(new ScheduleValidator());
        RuleFor(x => x.Folder.Retention).SetValidator(new RetentionValidator());
    }
}

internal sealed class UpdateFolderHandler : IRequestHandler<UpdateFolderCommand, BackupFolderDto>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public UpdateFolderHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<BackupFolderDto> Handle(UpdateFolderCommand request, CancellationToken ct)
    {
        var folder = request.Folder;
        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string updateSql = """
            UPDATE dbo.BackupFolders
            SET FolderPath = @Path, IsEnabled = @IsEnabled, Recursive = @Recursive,
                IncludeHidden = @IncludeHidden, IncludeSystem = @IncludeSystem,
                ExcludedFolders = @ExFolders, ExcludedExtensions = @ExExtensions, ExcludedFileNames = @ExNames,
                MaxFileSizeBytes = @MaxFileSize, Priority = @Priority,
                ScheduleType = @SchedType, ScheduleIntervalMinutes = @SchedInterval,
                ScheduleTimeOfDayMinutes = @SchedTimeOfDay, ScheduleDayOfWeek = @SchedDayOfWeek, ScheduleDayOfMonth = @SchedDayOfMonth,
                RetentionMode = @RetMode, RetentionValue = @RetValue, CompressionMethod = @Compression,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;
            """;

        await using (var update = Sql.Command(connection, updateSql)
            .With("@Id", folder.Id)
            .With("@UserId", _currentUser.UserId)
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
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                throw AppException.NotFound("Folder not found.");
            }
        }

        var selectSql = $"SELECT {FolderData.SelectColumns} FROM dbo.BackupFolders WHERE Id = @Id;";
        await using var select = Sql.Command(connection, selectSql).With("@Id", folder.Id);
        await using var reader = await select.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return FolderData.Map(reader);
    }
}
