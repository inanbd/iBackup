using iBackup.Server.Application.Features.Restore;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/restore")]
[Authorize]
public sealed class RestoreController : ControllerBase
{
    private readonly ISender _sender;

    public RestoreController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Browse backed-up files and their version history.</summary>
    [HttpGet("files")]
    public async Task<PagedResult<RestoreFileItemDto>> Files(
        [FromQuery] Guid? folderId,
        [FromQuery] string? pathPrefix,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 200,
        CancellationToken ct = default)
        => await _sender.Send(new GetRestoreFilesQuery(folderId, pathPrefix, includeDeleted, page, pageSize), ct);

    /// <summary>Creates a restore request and returns download tickets for each version.</summary>
    [HttpPost]
    public async Task<CreateRestoreResponse> Create(CreateRestoreRequest request, CancellationToken ct)
        => await _sender.Send(new CreateRestoreCommand(request.FileVersionIds), ct);

    /// <summary>Streams the stored (encrypted) content of a file version.</summary>
    [HttpGet("download/{versionId:guid}")]
    public async Task<IActionResult> Download(Guid versionId, CancellationToken ct)
    {
        var download = await _sender.Send(new DownloadVersionQuery(versionId), ct);
        return File(download.Content, "application/octet-stream", download.FileName + ".enc", enableRangeProcessing: false);
    }
}
