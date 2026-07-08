using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>
/// POST /api/backup/delete-file — records that a file was deleted on the client.
/// Existing versions stay recoverable until retention expires.
/// </summary>
public sealed record DeleteFileCommand(Guid BackupJobId, Guid FolderId, string RelativePath) : IRequest;

internal sealed class DeleteFileValidator : AbstractValidator<DeleteFileCommand>
{
    public DeleteFileValidator()
    {
        RuleFor(x => x.BackupJobId).NotEmpty();
        RuleFor(x => x.FolderId).NotEmpty();
        RuleFor(x => x.RelativePath).NotEmpty().MaximumLength(1024);
    }
}

internal sealed class DeleteFileHandler : IRequestHandler<DeleteFileCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public DeleteFileHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task Handle(DeleteFileCommand request, CancellationToken ct)
    {
        var relativePath = PathSanitizer.NormalizeRelativePath(request.RelativePath);

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await BackupData.EnsureRunningJobAsync(connection, request.BackupJobId, _currentUser.UserId, ct);

        const string sql = """
            UPDATE dbo.Files
            SET IsDeleted = 1, DeletedAtUtc = SYSUTCDATETIME(), UpdatedAtUtc = SYSUTCDATETIME()
            WHERE UserId = @UserId AND FolderId = @FolderId
              AND RelativePathHash = CONVERT(BINARY(32), HASHBYTES('SHA2_256', LOWER(@RelativePath)))
              AND IsDeleted = 0;
            """;

        await using var command = Sql.Command(connection, sql)
            .With("@UserId", _currentUser.UserId)
            .With("@FolderId", request.FolderId)
            .With("@RelativePath", relativePath);

        if (await command.ExecuteNonQueryAsync(ct) == 0)
        {
            throw AppException.NotFound("File not found.");
        }
    }
}
