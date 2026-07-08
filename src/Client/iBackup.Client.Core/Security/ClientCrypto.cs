using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace iBackup.Client.Core.Security;

/// <summary>
/// Client-side AES-256-GCM encryption. Files are encrypted chunk-by-chunk so
/// arbitrarily large files stream through fixed-size buffers:
///
///   encrypted chunk = 12-byte nonce || ciphertext || 16-byte tag
///
/// Every plaintext chunk is exactly <c>plainChunkSize</c> bytes except the last.
/// Chunk boundaries in the encrypted stream are therefore computable from the
/// chunk size alone, which lets the restore path decrypt without extra metadata.
/// The chunk index is mixed into the nonce and authenticated as associated data,
/// preventing chunk reordering attacks.
///
/// The server only ever sees ciphertext.
/// </summary>
public static class ClientCrypto
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int ChunkOverhead = NonceSize + TagSize;

    private const int KeyDerivationIterations = 210_000;

    /// <summary>
    /// Derives the 256-bit file encryption key from the account password.
    /// The (lower-cased) email acts as the salt, so every device of the same
    /// account derives the same key and can restore each other's files.
    /// The server never receives this key or anything derived from it.
    /// </summary>
    public static byte[] DeriveKey(string password, string email)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("iBackup:" + email.Trim().ToLowerInvariant()));
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, KeyDerivationIterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>Size of the encrypted stream for a given plaintext length.</summary>
    public static long GetEncryptedSize(long plainLength, int plainChunkSize)
    {
        if (plainLength == 0)
        {
            return ChunkOverhead; // one empty authenticated chunk
        }
        var fullChunks = plainLength / plainChunkSize;
        var remainder = plainLength % plainChunkSize;
        return fullChunks * (plainChunkSize + ChunkOverhead) + (remainder > 0 ? remainder + ChunkOverhead : 0);
    }

    /// <summary>Number of encrypted chunks for a given plaintext length.</summary>
    public static int GetChunkCount(long plainLength, int plainChunkSize)
        => plainLength == 0 ? 1 : (int)((plainLength + plainChunkSize - 1) / plainChunkSize);

    /// <summary>Encrypts <paramref name="input"/> to <paramref name="output"/> chunk by chunk.</summary>
    public static async Task EncryptAsync(Stream input, Stream output, byte[] key, int plainChunkSize, CancellationToken ct)
    {
        using var aes = new AesGcm(key, TagSize);
        var plain = new byte[plainChunkSize];
        var cipher = new byte[plainChunkSize];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var associatedData = new byte[8];
        long chunkIndex = 0;
        var any = false;

        while (true)
        {
            var read = await ReadFullAsync(input, plain, ct);
            if (read == 0 && any)
            {
                break;
            }

            RandomNumberGenerator.Fill(nonce.AsSpan(0, NonceSize - 4));
            BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(NonceSize - 4), (int)(chunkIndex & 0xFFFFFFFF));
            BinaryPrimitives.WriteInt64LittleEndian(associatedData, chunkIndex);

            aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, associatedData);

            await output.WriteAsync(nonce.AsMemory(0, NonceSize), ct);
            await output.WriteAsync(cipher.AsMemory(0, read), ct);
            await output.WriteAsync(tag.AsMemory(0, TagSize), ct);

            chunkIndex++;
            any = true;
            if (read < plainChunkSize)
            {
                break;
            }
        }
    }

    /// <summary>Decrypts a stream produced by <see cref="EncryptAsync"/>.</summary>
    public static async Task DecryptAsync(Stream input, Stream output, byte[] key, int plainChunkSize, CancellationToken ct)
    {
        using var aes = new AesGcm(key, TagSize);
        var encryptedChunkSize = plainChunkSize + ChunkOverhead;
        var buffer = new byte[encryptedChunkSize];
        var plain = new byte[plainChunkSize];
        var associatedData = new byte[8];
        long chunkIndex = 0;

        while (true)
        {
            var read = await ReadFullAsync(input, buffer, ct);
            if (read == 0)
            {
                break;
            }
            if (read < ChunkOverhead)
            {
                throw new CryptographicException("Encrypted stream is truncated.");
            }

            var cipherLength = read - ChunkOverhead;
            BinaryPrimitives.WriteInt64LittleEndian(associatedData, chunkIndex);

            aes.Decrypt(
                buffer.AsSpan(0, NonceSize),
                buffer.AsSpan(NonceSize, cipherLength),
                buffer.AsSpan(NonceSize + cipherLength, TagSize),
                plain.AsSpan(0, cipherLength),
                associatedData);

            await output.WriteAsync(plain.AsMemory(0, cipherLength), ct);
            chunkIndex++;

            if (read < encryptedChunkSize)
            {
                break;
            }
        }
    }

    /// <summary>Reads until the buffer is full or the stream ends. Returns bytes read.</summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
