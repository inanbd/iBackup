namespace iBackup.Server.Application.Abstractions;

/// <summary>One authentication attempt to record.</summary>
public sealed record LoginAttemptRecord(
    string? Email,
    string? IpAddress,
    bool Success,
    string? Reason,
    Guid? UserId,
    string Source);

/// <summary>
/// Runtime IP access control: evaluates whether a client IP may reach the server
/// and records login attempts, auto-blacklisting an IP after too many failures.
/// Rule management (list/add/remove) is done through the admin CQRS slices, which
/// call <see cref="Invalidate"/> so changes take effect immediately.
/// </summary>
public interface IIpAccessControl
{
    /// <summary>True when the (loopback-or-whitelisted, not-blacklisted) IP is allowed.</summary>
    Task<bool> IsAllowedAsync(string? ip, CancellationToken ct = default);

    /// <summary>
    /// Persists a login attempt. On a failed attempt from a non-local IP this
    /// may auto-blacklist the IP once the configured failure threshold is hit.
    /// </summary>
    Task RecordLoginAttemptAsync(LoginAttemptRecord attempt, CancellationToken ct = default);

    /// <summary>Drops the cached rule snapshot so the next evaluation reloads it.</summary>
    void Invalidate();
}
