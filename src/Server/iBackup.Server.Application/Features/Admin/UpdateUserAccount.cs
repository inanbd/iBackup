using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Application.Features.Auth;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

/// <summary>
/// Admin: change a user's quota, enable/disable the account, or grant/revoke
/// administrator access. Disabling revokes every session of the user.
/// Admins cannot disable or demote their own account.
/// </summary>
public sealed record UpdateUserAccountCommand(
    Guid UserId,
    long? QuotaBytes,
    bool? IsActive,
    bool? IsAdmin) : IRequest;

internal sealed class UpdateUserAccountValidator : AbstractValidator<UpdateUserAccountCommand>
{
    public UpdateUserAccountValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.QuotaBytes).GreaterThan(0).When(x => x.QuotaBytes.HasValue);
        RuleFor(x => x)
            .Must(x => x.QuotaBytes is not null || x.IsActive is not null || x.IsAdmin is not null)
            .WithMessage("Nothing to update.");
    }
}

internal sealed class UpdateUserAccountHandler : IRequestHandler<UpdateUserAccountCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public UpdateUserAccountHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task Handle(UpdateUserAccountCommand request, CancellationToken ct)
    {
        if (request.UserId == _currentUser.UserId && (request.IsActive == false || request.IsAdmin == false))
        {
            throw AppException.BadRequest("You cannot disable or demote your own account.");
        }

        const string sql = """
            UPDATE dbo.Users
            SET QuotaBytes = COALESCE(@Quota, QuotaBytes),
                IsActive = COALESCE(@IsActive, IsActive),
                IsAdmin = COALESCE(@IsAdmin, IsAdmin),
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Id = @UserId;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql)
            .With("@UserId", request.UserId)
            .With("@Quota", request.QuotaBytes)
            .With("@IsActive", request.IsActive)
            .With("@IsAdmin", request.IsAdmin);

        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw AppException.NotFound("User not found.");
        }

        if (request.IsActive == false)
        {
            await RefreshTokenData.RevokeAllAsync(connection, request.UserId, null, ct);
        }

        var changes = new List<string>();
        if (request.QuotaBytes is { } quota) changes.Add($"quota={quota}");
        if (request.IsActive is { } active) changes.Add($"active={active}");
        if (request.IsAdmin is { } admin) changes.Add($"admin={admin}");
        await _audit.LogAsync(_currentUser.UserId, null, "admin.user_updated",
            $"User {request.UserId}: {string.Join(", ", changes)}", _currentUser.IpAddress, ct);
    }
}
