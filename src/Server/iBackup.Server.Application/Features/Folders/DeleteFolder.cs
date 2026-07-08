using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Folders;

/// <summary>
/// DELETE /api/folders/{id} — soft-deletes a backup folder configuration.
/// Backed-up data stays recoverable until retention removes it.
/// </summary>
public sealed record DeleteFolderCommand(Guid FolderId) : IRequest;

internal sealed class DeleteFolderValidator : AbstractValidator<DeleteFolderCommand>
{
    public DeleteFolderValidator()
    {
        RuleFor(x => x.FolderId).NotEmpty();
    }
}

internal sealed class DeleteFolderHandler : IRequestHandler<DeleteFolderCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public DeleteFolderHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task Handle(DeleteFolderCommand request, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.BackupFolders
            SET IsDeleted = 1, IsEnabled = 0, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql)
            .With("@Id", request.FolderId)
            .With("@UserId", _currentUser.UserId);

        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw AppException.NotFound("Folder not found.");
        }

        await _audit.LogAsync(_currentUser.UserId, _currentUser.DeviceId, "folder.deleted", request.FolderId.ToString(), _currentUser.IpAddress, ct);
    }
}
