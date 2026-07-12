using System.Text.Json;
using iBackup.Server.Api.Services;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Infrastructure.Security;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace iBackup.Server.Api.Middleware;

/// <summary>
/// Enforces the default-deny IP access policy on every request. Loopback and
/// whitelisted IPs pass; blacklisted and unlisted remote IPs get 403.
///
/// The resolved client IP is always stashed (even when enforcement is disabled)
/// so downstream audit and login-attempt recording use the same value.
/// </summary>
public sealed class IpAccessMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly IIpAccessControl _ipAccess;
    private readonly IpAccessOptions _options;
    private readonly ILogger<IpAccessMiddleware> _logger;

    public IpAccessMiddleware(
        RequestDelegate next,
        IIpAccessControl ipAccess,
        IOptions<IpAccessOptions> options,
        ILogger<IpAccessMiddleware> logger)
    {
        _next = next;
        _ipAccess = ipAccess;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = ClientIp.Resolve(context, _options.TrustForwardedFor);

        if (_options.Enabled && !await _ipAccess.IsAllowedAsync(ip, context.RequestAborted))
        {
            _logger.LogWarning("Blocked request from {Ip} to {Path}", ip ?? "unknown", context.Request.Path);
            await WriteForbiddenAsync(context);
            return;
        }

        await _next(context);
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        // JSON for API callers, a short plain page for the admin dashboard/browsers.
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.ContentType = "application/json";
            var error = new ApiError("ip_blocked", "Access from your IP address is not permitted.");
            await context.Response.WriteAsync(JsonSerializer.Serialize(error, JsonOptions), context.RequestAborted);
        }
        else
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Access from your IP address is not permitted.", context.RequestAborted);
        }
    }
}
