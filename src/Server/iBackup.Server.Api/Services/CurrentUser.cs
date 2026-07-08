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
            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded.Split(',')[0].Trim();
            }
            return context.Connection.RemoteIpAddress?.ToString();
        }
    }
}
