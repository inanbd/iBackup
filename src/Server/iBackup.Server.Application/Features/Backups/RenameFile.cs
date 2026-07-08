using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>
/// POST /api/backup/rename-file — records a rename/move without re-uploading content.
/// </summary>
public sealed record RenameFileCommand(Guid BackupJobId, Guid FolderId, string OldRelativePath, string NewRelativePath, string NewFileName)
    : IRequest;

internal sealed class RenameFileValidator : AbstractValidator<RenameFileCommand>
{
    public RenameFileValidator()
    {
        RuleFor(x => x.BackupJobId).NotEmpty();
        RuleFor(x => x.FolderId).NotEmpty();
        RuleFor(x => x.OldRelativePath).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.NewRelativePath).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.NewFileName).NotEmpty().MaximumLength(255);
    }
}

internal sealed class RenameFileHandler : IRequestHandler<RenameFileCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public RenameFileHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task Handle(RenameFileCommand request, CancellationToken ct)
    {
        var oldPath = PathSanitizer.NormalizeRelativePath(request.OldRelativePath);
        var newPath = PathSanitizer.NormalizeRelativePath(request.NewRelativePath);
        var newName = PathSanitizer.ValidateFileName(request.NewFileName);

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await BackupData.EnsureRunningJobAsync(connection, request.BackupJobId, _currentUser.UserId, ct);

        const string sql = """
            UPDATE dbo.Files
            SET RelativePath = @NewPath, FileName = @NewName, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE UserId = @UserId AND FolderId = @FolderId
              AND RelativePathHash = CONVERT(BINARY(32), HASHBYTES('SHA2_256', LOWER(@OldPath)));
            """;

        try
        {
            await using var command = Sql.Command(connection, sql)
                .With("@UserId", _currentUser.UserId)
                .With("@FolderId", request.FolderId)
                .With("@OldPath", oldPath)
                .With("@NewPath", newPath)
                .With("@NewName", newName);

            if (await command.ExecuteNonQueryAsync(ct) == 0)
            {
                throw AppException.NotFound("File not found.");
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw AppException.Conflict("A backed-up file already exists at the new path.");
        }
    }
}
