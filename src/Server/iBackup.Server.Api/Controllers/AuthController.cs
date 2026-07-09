using iBackup.Server.Application.Features.Auth;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ISender _sender;

    public AuthController(ISender sender)
    {
        _sender = sender;
    }

    // Account creation is admin-only: there is no public self-registration.
    // Administrators create users via the admin dashboard (/Admin), and the
    // first admin is provisioned by the bootstrap setting or the seed script.

    /// <summary>Verifies credentials and issues an access + refresh token pair.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokensResponse>(StatusCodes.Status200OK)]
    public async Task<AuthTokensResponse> Login(LoginRequest request, CancellationToken ct)
        => await _sender.Send(new LoginCommand(request.Email, request.Password, request.Device, request.DeviceId), ct);

    /// <summary>Rotates a refresh token and issues a new access token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokensResponse>(StatusCodes.Status200OK)]
    public async Task<AuthTokensResponse> Refresh(RefreshTokenRequest request, CancellationToken ct)
        => await _sender.Send(new RefreshCommand(request.RefreshToken), ct);

    /// <summary>Revokes the presented refresh token.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await _sender.Send(new LogoutCommand(request.RefreshToken), ct);
        return NoContent();
    }
}
