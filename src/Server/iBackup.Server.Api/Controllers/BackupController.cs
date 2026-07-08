using iBackup.Server.Application.Features.Backups;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/backup")]
[Authorize]
public sealed class BackupController : ControllerBase
{
    private readonly ISender _sender;

    public BackupController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Opens a backup job session for a device.</summary>
    [HttpPost("start")]
    public async Task<StartBackupResponse> Start(StartBackupRequest request, CancellationToken ct)
        => await _sender.Send(new StartBackupCommand(request.DeviceId, request.FolderId, request.Type), ct);

    /// <summary>
    /// Announces a file upload. Returns a deduplication hit or an upload session
    /// (with the chunk indexes already stored, for resuming).
    /// </summary>
    [HttpPost("upload")]
    public async Task<BeginFileUploadResponse> Upload(BeginFileUploadRequest request, CancellationToken ct)
        => await _sender.Send(new BeginFileUploadCommand(request), ct);

    /// <summary>
    /// Uploads one encrypted chunk as a raw octet stream.
    /// The body is streamed straight to disk; it is never buffered in memory.
    /// </summary>
    [HttpPost("chunk")]
    [DisableRequestSizeLimit]
    [Consumes("application/octet-stream")]
    public async Task<ChunkUploadResponse> Chunk(
        [FromQuery] Guid sessionId,
        [FromQuery] int chunkIndex,
        [FromQuery] string sha256,
        CancellationToken ct)
        => await _sender.Send(new UploadChunkCommand(sessionId, chunkIndex, sha256, Request.Body), ct);

    /// <summary>Records a file deletion (incremental backups keep history).</summary>
    [HttpPost("delete-file")]
    public async Task<IActionResult> DeleteFile(DeleteFileRequest request, CancellationToken ct)
    {
        await _sender.Send(new DeleteFileCommand(request.BackupJobId, request.FolderId, request.RelativePath), ct);
        return NoContent();
    }

    /// <summary>Records a rename/move without re-uploading content.</summary>
    [HttpPost("rename-file")]
    public async Task<IActionResult> RenameFile(RenameFileRequest request, CancellationToken ct)
    {
        await _sender.Send(new RenameFileCommand(
            request.BackupJobId, request.FolderId, request.OldRelativePath, request.NewRelativePath, request.NewFileName), ct);
        return NoContent();
    }

    /// <summary>Closes a backup job and stores its statistics.</summary>
    [HttpPost("finish")]
    public async Task<IActionResult> Finish(FinishBackupRequest request, CancellationToken ct)
    {
        await _sender.Send(new FinishBackupCommand(request), ct);
        return NoContent();
    }

    /// <summary>Paged backup job history.</summary>
    [HttpGet("history")]
    public async Task<PagedResult<BackupHistoryItemDto>> History(
        [FromQuery] Guid? deviceId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => await _sender.Send(new GetBackupHistoryQuery(deviceId, page, pageSize), ct);
}
