using iBackup.Shared;

namespace iBackup.Server.Domain.Entities;

/// <summary>A deduplicated, content-addressed encrypted blob on disk.</summary>
public sealed class StorageObject
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string Sha256 { get; init; }
    public long OriginalSize { get; init; }
    public long StoredSize { get; init; }
    public CompressionMethod CompressionMethod { get; init; }
    public EncryptionMethod EncryptionMethod { get; init; }
    public int ChunkSize { get; init; }
    public required string StoragePath { get; init; }
    public int ReferenceCount { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
