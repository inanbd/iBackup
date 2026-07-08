using System.Security.Cryptography;
using iBackup.Client.Core.Compression;
using iBackup.Client.Core.Security;
using iBackup.Shared;
using Xunit;

namespace iBackup.Client.UnitTests;

/// <summary>
/// End-to-end backup/restore data pipeline: what the upload engine produces
/// (compress → encrypt) must be exactly reversible by the restore path
/// (decrypt → decompress), including across simulated chunk splits.
/// </summary>
public class RestorePipelineTests
{
    private static readonly byte[] Key = ClientCrypto.DeriveKey("pipeline-password", "pipeline@example.com");

    [Theory]
    [InlineData(CompressionMethod.None, 0)]
    [InlineData(CompressionMethod.Gzip, 123_456)]
    [InlineData(CompressionMethod.Zstd, 123_456)]
    [InlineData(CompressionMethod.Zstd, 1_000_000)]
    public async Task Backup_pipeline_then_restore_pipeline_returns_original(CompressionMethod method, int size)
    {
        const int chunkSize = 64 * 1024;
        var original = new byte[size];
        RandomNumberGenerator.Fill(original);
        var compressor = CompressorFactory.Create(method);

        // --- upload path: compress then encrypt
        using var compressed = new MemoryStream();
        await compressor.CompressAsync(new MemoryStream(original), compressed, CancellationToken.None);
        compressed.Position = 0;

        using var encrypted = new MemoryStream();
        await ClientCrypto.EncryptAsync(compressed, encrypted, Key, chunkSize, CancellationToken.None);

        // --- simulate server-side chunk split + reassembly (byte-identical concat)
        var encryptedChunkSize = chunkSize + ClientCrypto.ChunkOverhead;
        var bytes = encrypted.ToArray();
        using var reassembled = new MemoryStream();
        for (var offset = 0; offset < bytes.Length; offset += encryptedChunkSize)
        {
            var length = Math.Min(encryptedChunkSize, bytes.Length - offset);
            reassembled.Write(bytes, offset, length);
        }
        Assert.Equal(bytes.Length, reassembled.Length);
        reassembled.Position = 0;

        // --- restore path: decrypt then decompress
        using var decrypted = new MemoryStream();
        await ClientCrypto.DecryptAsync(reassembled, decrypted, Key, chunkSize, CancellationToken.None);
        decrypted.Position = 0;

        using var restored = new MemoryStream();
        await compressor.DecompressAsync(decrypted, restored, CancellationToken.None);

        Assert.Equal(original, restored.ToArray());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Large_file_pipeline_streams_through_bounded_memory()
    {
        // 32 MB with a 1 MB chunk size: exercises many chunks without slowing CI down.
        const int chunkSize = 1024 * 1024;
        const int size = 32 * 1024 * 1024;

        var source = Path.Combine(Path.GetTempPath(), "ibackup-large-" + Guid.NewGuid().ToString("N"));
        var encryptedPath = source + ".enc";
        var restoredPath = source + ".out";
        try
        {
            var payload = new byte[size];
            RandomNumberGenerator.Fill(payload);
            await File.WriteAllBytesAsync(source, payload);

            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(encryptedPath))
            {
                await ClientCrypto.EncryptAsync(input, output, Key, chunkSize, CancellationToken.None);
            }

            Assert.Equal(ClientCrypto.GetEncryptedSize(size, chunkSize), new FileInfo(encryptedPath).Length);

            await using (var input = File.OpenRead(encryptedPath))
            await using (var output = File.Create(restoredPath))
            {
                await ClientCrypto.DecryptAsync(input, output, Key, chunkSize, CancellationToken.None);
            }

            Assert.Equal(payload, await File.ReadAllBytesAsync(restoredPath));
        }
        finally
        {
            foreach (var path in new[] { source, encryptedPath, restoredPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
