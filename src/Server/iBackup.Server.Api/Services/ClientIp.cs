using iBackup.Server.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace iBackup.Server.Api.Services;

/// <summary>
/// Resolves the authoritative client IP for a request and stashes it in
/// <c>HttpContext.Items</c> so the IP filter, audit logging and login-attempt
/// recording all agree on a single value.
///
/// The socket address (<c>Connection.RemoteIpAddress</c>) is authoritative.
/// X-Forwarded-For is honored only when <c>IpAccessControl:TrustForwardedFor</c>
/// is enabled, which must be reserved for deployments behind a trusted proxy —
/// otherwise a client could spoof the header to bypass IP filtering.
/// </summary>
public static class ClientIp
{
    public const string ItemKey = "iBackup.ClientIp";

    public static string? Resolve(HttpContext context, bool trustForwardedFor)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached))
        {
            return cached as string;
        }

        string? ip = null;
        if (trustForwardedFor)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                ip = forwarded.Split(',')[0].Trim();
            }
        }

        ip ??= context.Connection.RemoteIpAddress?.ToString();
        context.Items[ItemKey] = ip;
        return ip;
    }

    public static string? Resolve(HttpContext context, IOptions<IpAccessOptions> options)
        => Resolve(context, options.Value.TrustForwardedFor);
}
