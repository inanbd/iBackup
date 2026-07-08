using iBackup.Server.Application.Features.Devices;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/devices")]
[Authorize]
public sealed class DevicesController : ControllerBase
{
    private readonly ISender _sender;

    public DevicesController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>All registered devices of the authenticated user.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DeviceDto>> Get(CancellationToken ct)
        => await _sender.Send(new GetDevicesQuery(), ct);

    /// <summary>Renames and/or enables/disables a device.</summary>
    [HttpPut]
    public async Task<IActionResult> Update(UpdateDeviceRequest request, CancellationToken ct)
    {
        await _sender.Send(new UpdateDeviceCommand(request.DeviceId, request.DeviceName, request.IsActive), ct);
        return NoContent();
    }

    /// <summary>Removes a device; its backed-up data stays restorable.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _sender.Send(new DeleteDeviceCommand(id), ct);
        return NoContent();
    }

    /// <summary>Revokes every session of a device (force logout).</summary>
    [HttpPost("{id:guid}/logout")]
    public async Task<IActionResult> ForceLogout(Guid id, CancellationToken ct)
    {
        await _sender.Send(new ForceLogoutDeviceCommand(id), ct);
        return NoContent();
    }
}
