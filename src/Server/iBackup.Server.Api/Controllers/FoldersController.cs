using iBackup.Server.Application.Features.Folders;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/folders")]
[Authorize]
public sealed class FoldersController : ControllerBase
{
    private readonly ISender _sender;

    public FoldersController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Backup folders of the authenticated user, optionally filtered by device.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<BackupFolderDto>> Get([FromQuery] Guid? deviceId, CancellationToken ct)
        => await _sender.Send(new GetFoldersQuery(deviceId), ct);

    /// <summary>Adds a backup source folder.</summary>
    [HttpPost]
    public async Task<BackupFolderDto> Create(CreateFolderRequest request, CancellationToken ct)
        => await _sender.Send(new CreateFolderCommand(request), ct);

    /// <summary>Updates a backup folder's configuration.</summary>
    [HttpPut]
    public async Task<BackupFolderDto> Update(UpdateFolderRequest request, CancellationToken ct)
        => await _sender.Send(new UpdateFolderCommand(request), ct);

    /// <summary>Soft-deletes a backup folder configuration.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _sender.Send(new DeleteFolderCommand(id), ct);
        return NoContent();
    }
}
