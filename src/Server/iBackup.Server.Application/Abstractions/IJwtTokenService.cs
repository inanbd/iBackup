namespace iBackup.Server.Application.Abstractions;

public sealed record AccessTokenResult(string Token, DateTime ExpiresAtUtc);

/// <summary>Issues JWT access tokens and opaque refresh tokens.</summary>
public interface IJwtTokenService
{
    AccessTokenResult CreateAccessToken(Guid userId, string email, Guid deviceId);

    /// <summary>Cryptographically random opaque refresh token (base64url).</summary>
    string CreateRefreshToken();

    /// <summary>SHA-256 hex digest used to store/lookup refresh tokens.</summary>
    string HashToken(string token);

    TimeSpan RefreshTokenLifetime { get; }
}
