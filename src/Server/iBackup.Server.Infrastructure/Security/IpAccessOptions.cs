namespace iBackup.Server.Infrastructure.Security;

/// <summary>Bound from the "IpAccessControl" configuration section.</summary>
public sealed class IpAccessOptions
{
    public const string SectionName = "IpAccessControl";

    /// <summary>
    /// Master switch. When true (default), the server enforces default-deny IP
    /// filtering: only loopback and whitelisted IPs may connect.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, the client IP is taken from the first X-Forwarded-For entry.
    /// Enable ONLY when the server sits behind a trusted reverse proxy that
    /// overwrites that header; otherwise clients could spoof their IP. Default false.
    /// </summary>
    public bool TrustForwardedFor { get; set; }

    /// <summary>Failed logins from one IP within the window that trigger an auto-blacklist.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Rolling window (minutes) over which failed logins are counted.</summary>
    public int FailedAttemptWindowMinutes { get; set; } = 60;

    /// <summary>
    /// How long the in-memory rule snapshot is reused before reloading. Rule
    /// changes made through the admin UI invalidate the cache immediately;
    /// this only bounds staleness from out-of-process changes. 0 = always reload.
    /// </summary>
    public int CacheSeconds { get; set; } = 15;
}
