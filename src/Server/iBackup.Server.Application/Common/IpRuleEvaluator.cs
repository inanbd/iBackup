using System.Net;

namespace iBackup.Server.Application.Common;

/// <summary>
/// Pure, dependency-free evaluation of the IP access policy. Kept separate from
/// the data/caching layer so the security-critical decision logic is fully
/// unit-testable.
///
/// Policy (default-deny):
///   1. Loopback (localhost) and unknown/in-process callers are always allowed
///      — this prevents an admin from locking themselves out of the local host.
///   2. Otherwise an explicit blacklist match ('*' or the exact IP) denies.
///   3. Otherwise a whitelist match ('*' or the exact IP) allows.
///   4. Otherwise deny (nothing is implicitly trusted).
///
/// Blacklist is evaluated before whitelist, so a blacklisted IP is denied even
/// when the wildcard '*' whitelist is present.
/// </summary>
public static class IpRuleEvaluator
{
    public const string Wildcard = "*";

    /// <summary>Evaluates the policy for a raw client IP string.</summary>
    public static bool IsAllowed(string? ip, IReadOnlySet<string> whitelist, IReadOnlySet<string> blacklist)
    {
        if (IsLoopbackOrUnknown(ip))
        {
            return true;
        }

        var normalized = Normalize(ip);
        if (normalized is null)
        {
            return false; // unparseable address: deny
        }

        if (blacklist.Contains(Wildcard) || blacklist.Contains(normalized))
        {
            return false;
        }

        if (whitelist.Contains(Wildcard) || whitelist.Contains(normalized))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True for loopback addresses (127.0.0.0/8, ::1) and for null/empty
    /// (in-process / not-yet-resolved) callers, which are treated as local.
    /// </summary>
    public static bool IsLoopbackOrUnknown(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return true;
        }
        return IPAddress.TryParse(ip, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Canonical form of an IP for storage/comparison (e.g. IPv6 casing,
    /// IPv4-mapped forms). Returns null when the value is not a valid IP.
    /// </summary>
    public static string? Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }
        return IPAddress.TryParse(ip.Trim(), out var address) ? address.ToString() : null;
    }

    /// <summary>Validates a rule value entered by an admin: either '*' or a valid IP.</summary>
    public static bool IsValidRuleValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        value = value.Trim();
        return value == Wildcard || IPAddress.TryParse(value, out _);
    }

    /// <summary>Normalizes a rule value for storage ('*' stays '*', IPs are canonicalized).</summary>
    public static string NormalizeRuleValue(string value)
    {
        value = value.Trim();
        return value == Wildcard ? Wildcard : Normalize(value) ?? value;
    }
}
