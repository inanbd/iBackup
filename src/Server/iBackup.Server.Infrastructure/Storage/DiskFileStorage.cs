using System.Security.Cryptography;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace iBackup.Server.Infrastructure.Storage;

/// <summary>
/// Content-addressed disk storage.
/// Layout:
///   {Root}/Users/{UserId}/Chunks/{SessionId}/{Index:D6}.chunk   - in-flight chunks
///   {Root}/Users/{UserId}/Files/{aa}/{bb}/{sha256}.obj          - assembled encrypted objects
/// Only server-generated names ever touch the filesystem, so client input cannot
/// influence paths.
/// </summary>
public sealed class DiskFileStorage : IFileStorage
{
    private readonly StorageOptions _options;
    private readonly ILogger<DiskFileStorage> _logger;

    public DiskFileStorage(IOptions<StorageOptions> options, ILogger<DiskFileStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string Sha256, long Size)> SaveChunkAsync(Guid userId, Guid sessionId, int chunkIndex, Stream content, CancellationToken ct)
    {
        var directory = ChunkDirectory(userId, sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ChunkFileName(chunkIndex));
        var tempPath = path + ".tmp";

        using var sha = SHA256.Create();
        long size = 0;

        await using (var file = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            _options.StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[_options.StreamBufferSize];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                size += read;
            }
            await file.FlushAsync(ct);
        }

        sha.TransformFinalBlock([], 0, 0);
        var digest = Convert.ToHexStringLower(sha.Hash!);

        File.Move(tempPath, path, overwrite: true);
        return (digest, size);
    }

    public Task<IReadOnlyList<int>> GetStoredChunkIndexesAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var directory = ChunkDirectory(userId, sessionId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        var indexes = Directory.EnumerateFiles(directory, "*.chunk")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var i) ? i : -1)
            .Where(i => i >= 0)
            .OrderBy(i => i)
            .ToList();
        return Task.FromResult<IReadOnlyList<int>>(indexes);
    }

    public async Task<AssembledObject> AssembleAsync(Guid userId, Guid sessionId, int totalChunks, string sha256, CancellationToken ct)
    {
        var chunkDirectory = ChunkDirectory(userId, sessionId);
        var relativePath = ObjectRelativePath(sha256);
        var finalPath = Path.Combine(UserRoot(userId), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var tempPath = finalPath + "." + sessionId.ToString("N") + ".tmp";
        long totalSize = 0;

        await using (var output = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            _options.StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            for (var index = 0; index < totalChunks; index++)
            {
                var chunkPath = Path.Combine(chunkDirectory, ChunkFileName(index));
                if (!File.Exists(chunkPath))
                {
                    await output.DisposeAsync();
                    File.Delete(tempPath);
                    throw AppException.Conflict($"Chunk {index} is missing; upload cannot be assembled.");
                }

                await using var input = new FileStream(
                    chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    _options.StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, _options.StreamBufferSize, ct);
                totalSize += input.Length;
            }
            await output.FlushAsync(ct);
        }

        File.Move(tempPath, finalPath, overwrite: true);
        _logger.LogInformation("Assembled object {Sha256} ({Size} bytes) for user {UserId}", sha256, totalSize, userId);
        return new AssembledObject(relativePath, totalSize);
    }

    public Stream OpenObjectRead(Guid userId, string relativeStoragePath)
    {
        var path = ResolveUserPath(userId, relativeStoragePath);
        if (!File.Exists(path))
        {
            throw AppException.NotFound("Stored object not found on disk.");
        }
        return new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            _options.StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public Task DeleteChunksAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var directory = ChunkDirectory(userId, sessionId);
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete chunk directory {Directory}", directory);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteObjectAsync(Guid userId, string relativeStoragePath, CancellationToken ct)
    {
        var path = ResolveUserPath(userId, relativeStoragePath);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete object {Path}", path);
            }
        }
        return Task.CompletedTask;
    }

    private string UserRoot(Guid userId)
        => Path.Combine(Path.GetFullPath(_options.RootPath), "Users", userId.ToString("D"));

    private string ChunkDirectory(Guid userId, Guid sessionId)
        => Path.Combine(UserRoot(userId), "Chunks", sessionId.ToString("D"));

    private static string ChunkFileName(int index) => index.ToString("D6") + ".chunk";

    private static string ObjectRelativePath(string sha256)
    {
        var normalized = sha256.ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw AppException.BadRequest("Invalid content hash.");
        }
        return Path.Combine("Files", normalized[..2], normalized[2..4], normalized + ".obj");
    }

    /// <summary>Defense in depth: a stored relative path must stay inside the user's root.</summary>
    private string ResolveUserPath(Guid userId, string relativePath)
    {
        var root = UserRoot(userId);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw AppException.Forbidden("Invalid storage path.");
        }
        return full;
    }
}
