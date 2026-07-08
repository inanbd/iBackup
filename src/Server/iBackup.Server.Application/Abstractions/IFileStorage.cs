namespace iBackup.Server.Application.Abstractions;

public sealed record AssembledObject(string RelativeStoragePath, long StoredSize);

/// <summary>
/// Disk storage for encrypted chunks and assembled objects.
/// Layout: Storage/Users/{UserId}/Chunks/{SessionId}/{Index}.chunk and
/// Storage/Users/{UserId}/Files/{aa}/{bb}/{sha256}.obj
/// </summary>
public interface IFileStorage
{
    /// <summary>Streams one chunk to disk and returns its SHA-256 hex digest and size.</summary>
    Task<(string Sha256, long Size)> SaveChunkAsync(Guid userId, Guid sessionId, int chunkIndex, Stream content, CancellationToken ct);

    /// <summary>Chunk indexes currently on disk for a session.</summary>
    Task<IReadOnlyList<int>> GetStoredChunkIndexesAsync(Guid userId, Guid sessionId, CancellationToken ct);

    /// <summary>Concatenates all chunks (in index order) into the content-addressed object store.</summary>
    Task<AssembledObject> AssembleAsync(Guid userId, Guid sessionId, int totalChunks, string sha256, CancellationToken ct);

    /// <summary>Opens an assembled object for streaming download.</summary>
    Stream OpenObjectRead(Guid userId, string relativeStoragePath);

    Task DeleteChunksAsync(Guid userId, Guid sessionId, CancellationToken ct);

    Task DeleteObjectAsync(Guid userId, string relativeStoragePath, CancellationToken ct);
}
