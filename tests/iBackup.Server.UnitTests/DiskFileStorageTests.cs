using System.Security.Cryptography;
using System.Text;
using iBackup.Server.Domain.Exceptions;
using iBackup.Server.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace iBackup.Server.UnitTests;

/// <summary>Chunk save / verify / assemble / cleanup against a temp directory.</summary>
public class DiskFileStorageTests : IDisposable
{
    private readonly string _root;
    private readonly DiskFileStorage _storage;
    private readonly Guid _userId = Guid.NewGuid();

    public DiskFileStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ibackup-tests-" + Guid.NewGuid().ToString("N"));
        _storage = new DiskFileStorage(
            Options.Create(new StorageOptions { RootPath = _root }),
            NullLogger<DiskFileStorage>.Instance);
    }

    [Fact]
    public async Task SaveChunk_returns_correct_hash_and_size()
    {
        var payload = Encoding.UTF8.GetBytes("hello chunked world");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));

        var (sha256, size) = await _storage.SaveChunkAsync(_userId, Guid.NewGuid(), 0, new MemoryStream(payload), CancellationToken.None);

        Assert.Equal(expectedHash, sha256);
        Assert.Equal(payload.Length, size);
    }

    [Fact]
    public async Task Assemble_concatenates_chunks_in_order_and_supports_read_back()
    {
        var sessionId = Guid.NewGuid();
        byte[] c0 = [1, 2, 3], c1 = [4, 5], c2 = [6, 7, 8, 9];
        await _storage.SaveChunkAsync(_userId, sessionId, 0, new MemoryStream(c0), CancellationToken.None);
        await _storage.SaveChunkAsync(_userId, sessionId, 1, new MemoryStream(c1), CancellationToken.None);
        await _storage.SaveChunkAsync(_userId, sessionId, 2, new MemoryStream(c2), CancellationToken.None);

        var contentHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        var assembled = await _storage.AssembleAsync(_userId, sessionId, 3, contentHash, CancellationToken.None);

        Assert.Equal(9, assembled.StoredSize);

        await using var read = _storage.OpenObjectRead(_userId, assembled.RelativeStoragePath);
        var buffer = new byte[16];
        var total = await read.ReadAsync(buffer);
        Assert.Equal(9, total);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, buffer[..9]);
    }

    [Fact]
    public async Task Assemble_fails_when_a_chunk_is_missing()
    {
        var sessionId = Guid.NewGuid();
        await _storage.SaveChunkAsync(_userId, sessionId, 0, new MemoryStream([1]), CancellationToken.None);
        // chunk 1 missing
        var hash = new string('a', 64);
        await Assert.ThrowsAsync<AppException>(
            () => _storage.AssembleAsync(_userId, sessionId, 2, hash, CancellationToken.None));
    }

    [Fact]
    public async Task Resume_reports_stored_chunk_indexes()
    {
        var sessionId = Guid.NewGuid();
        await _storage.SaveChunkAsync(_userId, sessionId, 0, new MemoryStream([1]), CancellationToken.None);
        await _storage.SaveChunkAsync(_userId, sessionId, 4, new MemoryStream([2]), CancellationToken.None);

        var indexes = await _storage.GetStoredChunkIndexesAsync(_userId, sessionId, CancellationToken.None);
        Assert.Equal([0, 4], indexes);
    }

    [Fact]
    public async Task DeleteChunks_removes_session_directory()
    {
        var sessionId = Guid.NewGuid();
        await _storage.SaveChunkAsync(_userId, sessionId, 0, new MemoryStream([1]), CancellationToken.None);
        await _storage.DeleteChunksAsync(_userId, sessionId, CancellationToken.None);

        var indexes = await _storage.GetStoredChunkIndexesAsync(_userId, sessionId, CancellationToken.None);
        Assert.Empty(indexes);
    }

    [Fact]
    public void OpenObjectRead_rejects_path_traversal()
    {
        Assert.Throws<AppException>(() => _storage.OpenObjectRead(_userId, "../../other-user/secret.obj"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
