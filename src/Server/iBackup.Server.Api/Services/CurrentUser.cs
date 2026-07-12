using iBackup.Server.Application.Abstractions;
using iBackup.Server.Infrastructure.Security;

namespace iBackup.Server.Api.Services;

/// <summary>Resolves the caller's identity from the validated JWT on the current request.</summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private HttpContext? Context => _httpContextAccessor.HttpContext;

    public bool IsAuthenticated => Context?.User.Identity?.IsAuthenticated == true;

    public Guid UserId
    {
        get
        {
            var sub = Context?.User.FindFirst("sub")?.Value;
            return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
        }
    }

    public Guid? DeviceId
    {
        get
        {
            var claim = Context?.User.FindFirst(JwtTokenService.DeviceIdClaim)?.Value;
            return Guid.TryParse(claim, out var id) && id != Guid.Empty ? id : null;
        }
    }

    public string? IpAddress
    {
        get
        {
            var context = Context;
            if (context is null)
            {
                return null;
            }
            // Use the IP the filter middleware resolved and stashed, so audit and
            // login-attempt records match what access control actually enforced.
            if (context.Items.TryGetValue(ClientIp.ItemKey, out var stashed))
            {
                return stashed as string;
            }
            return context.Connection.RemoteIpAddress?.ToString();
        }
    }
}
