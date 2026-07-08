using System.IO.Compression;
using iBackup.Shared;
using ZstdSharp;

namespace iBackup.Client.Core.Compression;

/// <summary>Streaming compression applied before encryption.</summary>
public interface ICompressor
{
    CompressionMethod Method { get; }
    Task CompressAsync(Stream input, Stream output, CancellationToken ct);
    Task DecompressAsync(Stream input, Stream output, CancellationToken ct);
}

public sealed class NoCompressionCompressor : ICompressor
{
    public CompressionMethod Method => CompressionMethod.None;

    public Task CompressAsync(Stream input, Stream output, CancellationToken ct)
        => input.CopyToAsync(output, ct);

    public Task DecompressAsync(Stream input, Stream output, CancellationToken ct)
        => input.CopyToAsync(output, ct);
}

public sealed class GzipCompressor : ICompressor
{
    public CompressionMethod Method => CompressionMethod.Gzip;

    public async Task CompressAsync(Stream input, Stream output, CancellationToken ct)
    {
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await input.CopyToAsync(gzip, ct);
    }

    public async Task DecompressAsync(Stream input, Stream output, CancellationToken ct)
    {
        await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        await gzip.CopyToAsync(output, ct);
    }
}

/// <summary>Zstandard - the preferred default: much faster than GZip at similar ratios.</summary>
public sealed class ZstdCompressor : ICompressor
{
    public CompressionMethod Method => CompressionMethod.Zstd;

    public async Task CompressAsync(Stream input, Stream output, CancellationToken ct)
    {
        await using var zstd = new CompressionStream(output, level: 3, leaveOpen: true);
        await input.CopyToAsync(zstd, ct);
    }

    public async Task DecompressAsync(Stream input, Stream output, CancellationToken ct)
    {
        await using var zstd = new DecompressionStream(input, leaveOpen: true);
        await zstd.CopyToAsync(output, ct);
    }
}

/// <summary>Resolves the compressor for a configured method.</summary>
public static class CompressorFactory
{
    public static ICompressor Create(CompressionMethod method) => method switch
    {
        CompressionMethod.None => new NoCompressionCompressor(),
        CompressionMethod.Gzip => new GzipCompressor(),
        CompressionMethod.Zstd => new ZstdCompressor(),
        _ => throw new NotSupportedException($"Compression method {method} is not supported.")
    };
}
