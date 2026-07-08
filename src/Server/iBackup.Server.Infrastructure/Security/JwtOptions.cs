namespace iBackup.Server.Infrastructure.Security;

/// <summary>Bound from the "Jwt" configuration section.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC-SHA256 signing key; must be at least 32 bytes. Keep in a secret store in production.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "iBackup";
    public string Audience { get; set; } = "iBackup.Clients";
    public int AccessTokenLifetimeMinutes { get; set; } = 15;
    public int RefreshTokenLifetimeDays { get; set; } = 30;
}
