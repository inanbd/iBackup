using iBackup.Server.Application.Features.Clients;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/client")]
[Authorize]
public sealed class ClientController : ControllerBase
{
    private readonly ISender _sender;

    public ClientController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Registers the calling device (or updates its metadata).</summary>
    [HttpPost("register")]
    public async Task<RegisterClientResponse> Register(RegisterClientRequest request, CancellationToken ct)
        => await _sender.Send(new RegisterClientCommand(request.DeviceName, request.OperatingSystem, request.ClientVersion), ct);

    /// <summary>Profile and storage usage of the authenticated user.</summary>
    [HttpGet("profile")]
    public async Task<ProfileResponse> Profile(CancellationToken ct)
        => await _sender.Send(new GetProfileQuery(), ct);
}
