using System.Security.Cryptography;

namespace iBackup.Client.Core.Util;

/// <summary>Async SHA-256 hashing helpers.</summary>
public static class FileHasher
{
    /// <summary>SHA-256 (lower-case hex) of a file's content, streamed with a small buffer.</summary>
    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await Sha256OfStreamAsync(stream, ct);
    }

    public static async Task<string> Sha256OfStreamAsync(Stream stream, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha256OfBytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
