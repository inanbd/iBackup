using iBackup.Shared;

namespace iBackup.Server.Domain.Entities;

/// <summary>A resumable chunked upload of one encrypted file.</summary>
public sealed class UploadSession
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid FolderId { get; init; }
    public Guid? BackupJobId { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public long OriginalSize { get; init; }
    public long EncryptedSize { get; init; }
    public DateTime FileModifiedAtUtc { get; init; }
    public CompressionMethod CompressionMethod { get; init; }
    public EncryptionMethod EncryptionMethod { get; init; }
    public int ChunkSize { get; init; }
    public int TotalChunks { get; init; }
    public int ReceivedChunks { get; init; }
    public UploadSessionStatus Status { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime LastActivityAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
}
