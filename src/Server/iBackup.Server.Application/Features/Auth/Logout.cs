using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Auth;

/// <summary>POST /api/auth/logout — revokes the presented refresh token.</summary>
public sealed record LogoutCommand(string RefreshToken) : IRequest;

internal sealed class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}

internal sealed class LogoutHandler : IRequestHandler<LogoutCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IJwtTokenService _tokens;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;

    public LogoutHandler(ISqlConnectionFactory connections, IJwtTokenService tokens, IAuditLogger audit, ICurrentUser currentUser)
    {
        _connections = connections;
        _tokens = tokens;
        _audit = audit;
        _currentUser = currentUser;
    }

    public async Task Handle(LogoutCommand request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);
        await RefreshTokenData.RevokeAsync(connection, _tokens.HashToken(request.RefreshToken), null, ct);

        if (_currentUser.IsAuthenticated)
        {
            await _audit.LogAsync(_currentUser.UserId, _currentUser.DeviceId, "auth.logout", null, _currentUser.IpAddress, ct);
        }
    }
}
