using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Auth;

/// <summary>
/// POST /api/auth/refresh — rotates a refresh token and issues a new access token.
/// Reuse of an already-rotated token is treated as theft: the whole device session is revoked.
/// </summary>
public sealed record RefreshCommand(string RefreshToken) : IRequest<AuthTokensResponse>;

internal sealed class RefreshValidator : AbstractValidator<RefreshCommand>
{
    public RefreshValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}

internal sealed class RefreshHandler : IRequestHandler<RefreshCommand, AuthTokensResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IJwtTokenService _tokens;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;

    public RefreshHandler(ISqlConnectionFactory connections, IJwtTokenService tokens, IAuditLogger audit, ICurrentUser currentUser)
    {
        _connections = connections;
        _tokens = tokens;
        _audit = audit;
        _currentUser = currentUser;
    }

    public async Task<AuthTokensResponse> Handle(RefreshCommand request, CancellationToken ct)
    {
        var ip = _currentUser.IpAddress;
        var tokenHash = _tokens.HashToken(request.RefreshToken);

        await using var connection = await _connections.OpenConnectionAsync(ct);

        var stored = await RefreshTokenData.GetByHashAsync(connection, tokenHash, ct);
        if (stored is null)
        {
            throw AppException.Unauthorized("Invalid refresh token.");
        }

        if (stored.IsRevoked)
        {
            // Rotated token replayed - revoke everything issued to this device.
            await RefreshTokenData.RevokeAllAsync(connection, stored.UserId, stored.DeviceId, ct);
            await _audit.LogAsync(stored.UserId, stored.DeviceId, "auth.refresh_reuse_detected",
                "Revoked refresh token was replayed; device session revoked.", ip, ct);
            throw AppException.Unauthorized("Refresh token is no longer valid.");
        }

        if (stored.IsExpired(DateTime.UtcNow))
        {
            throw AppException.Unauthorized("Refresh token has expired.");
        }

        const string userSql = """
            SELECT u.Email, u.DisplayName, u.IsActive,
                   d.IsActive, d.IsDeleted
            FROM dbo.Users u
            LEFT JOIN dbo.Devices d ON d.Id = @DeviceId
            WHERE u.Id = @UserId;
            """;

        string email;
        string displayName;
        await using (var command = Sql.Command(connection, userSql)
            .With("@UserId", stored.UserId)
            .With("@DeviceId", stored.DeviceId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(2))
            {
                throw AppException.Unauthorized("Account is not available.");
            }
            if (stored.DeviceId is not null && (!reader.IsDBNull(4) && reader.GetBoolean(4) || !reader.IsDBNull(3) && !reader.GetBoolean(3)))
            {
                throw AppException.Forbidden("This device has been disabled or removed.");
            }
            email = reader.GetString(0);
            displayName = reader.GetString(1);
        }

        var deviceId = stored.DeviceId ?? Guid.Empty;
        var access = _tokens.CreateAccessToken(stored.UserId, email, deviceId);
        var newRefreshToken = _tokens.CreateRefreshToken();
        var newHash = _tokens.HashToken(newRefreshToken);
        var refreshExpires = DateTime.UtcNow.Add(_tokens.RefreshTokenLifetime);

        await RefreshTokenData.RevokeAsync(connection, tokenHash, newHash, ct);
        await RefreshTokenData.InsertAsync(connection, stored.UserId, stored.DeviceId, newHash, refreshExpires, ip, ct);

        return new AuthTokensResponse(
            stored.UserId, email, displayName, deviceId,
            access.Token, access.ExpiresAtUtc,
            newRefreshToken, refreshExpires);
    }
}
