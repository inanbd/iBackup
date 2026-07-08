using System.Security.Claims;
using iBackup.Server.Application.Features.Admin;

namespace iBackup.Server.Api.Services;

/// <summary>Constants and principal factory for the cookie-based admin dashboard.</summary>
public static class AdminAuth
{
    public const string Scheme = "AdminCookies";
    public const string Policy = "AdminUI";
    public const string Role = "Admin";

    /// <summary>
    /// Builds the cookie principal. The "sub" claim keeps <c>ICurrentUser</c>
    /// working identically for admin page requests and JWT API requests.
    /// </summary>
    public static ClaimsPrincipal CreatePrincipal(AdminIdentity admin)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", admin.UserId.ToString()),
            new Claim(ClaimTypes.Name, admin.Email),
            new Claim("display_name", admin.DisplayName),
            new Claim(ClaimTypes.Role, Role)
        ], Scheme);

        return new ClaimsPrincipal(identity);
    }
}
