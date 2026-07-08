using System.Security.Cryptography;
using System.Text;
using iBackup.Client.Core.Compression;
using iBackup.Shared;
using Xunit;

namespace iBackup.Client.UnitTests;

public class CompressionTests
{
    [Theory]
    [InlineData(CompressionMethod.None)]
    [InlineData(CompressionMethod.Gzip)]
    [InlineData(CompressionMethod.Zstd)]
    public async Task Compress_then_decompress_roundtrips(CompressionMethod method)
    {
        var compressor = CompressorFactory.Create(method);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox. ", 5000)));

        using var compressed = new MemoryStream();
        await compressor.CompressAsync(new MemoryStream(payload), compressed, CancellationToken.None);

        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        await compressor.DecompressAsync(compressed, decompressed, CancellationToken.None);

        Assert.Equal(payload, decompressed.ToArray());
    }

    [Theory]
    [InlineData(CompressionMethod.Gzip)]
    [InlineData(CompressionMethod.Zstd)]
    public async Task Repetitive_content_actually_shrinks(CompressionMethod method)
    {
        var compressor = CompressorFactory.Create(method);
        var payload = Encoding.UTF8.GetBytes(new string('a', 100_000));

        using var compressed = new MemoryStream();
        await compressor.CompressAsync(new MemoryStream(payload), compressed, CancellationToken.None);

        Assert.True(compressed.Length < payload.Length / 10);
    }

    [Fact]
    public async Task Random_binary_roundtrips_through_zstd()
    {
        var compressor = CompressorFactory.Create(CompressionMethod.Zstd);
        var payload = new byte[512 * 1024];
        RandomNumberGenerator.Fill(payload);

        using var compressed = new MemoryStream();
        await compressor.CompressAsync(new MemoryStream(payload), compressed, CancellationToken.None);
        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        await compressor.DecompressAsync(compressed, decompressed, CancellationToken.None);

        Assert.Equal(payload, decompressed.ToArray());
    }
}
