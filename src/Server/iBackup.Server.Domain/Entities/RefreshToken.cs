namespace iBackup.Server.Domain.Entities;

/// <summary>A persisted refresh token. Only the SHA-256 hash of the token is stored.</summary>
public sealed class RefreshToken
{
    public long Id { get; init; }
    public Guid UserId { get; init; }
    public Guid? DeviceId { get; init; }
    public required string TokenHash { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string? CreatedByIp { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
    public string? ReplacedByTokenHash { get; init; }

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
    public bool IsRevoked => RevokedAtUtc is not null;
    public bool IsUsable(DateTime nowUtc) => !IsRevoked && !IsExpired(nowUtc);
}
