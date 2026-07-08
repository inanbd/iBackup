using System.Security.Cryptography;
using iBackup.Client.Core.Security;
using Xunit;

namespace iBackup.Client.UnitTests;

public class ClientCryptoTests
{
    private static readonly byte[] Key = ClientCrypto.DeriveKey("test-password", "user@example.com");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(64 * 1024)]          // exactly one chunk (with 64K chunk size)
    [InlineData(64 * 1024 + 1)]      // one full chunk + 1 byte
    [InlineData(3 * 64 * 1024)]      // exact multiple of the chunk size
    [InlineData(200 * 1024 + 17)]
    public async Task Encrypt_then_decrypt_roundtrips(int length)
    {
        const int chunkSize = 64 * 1024;
        var plaintext = new byte[length];
        RandomNumberGenerator.Fill(plaintext);

        using var encrypted = new MemoryStream();
        await ClientCrypto.EncryptAsync(new MemoryStream(plaintext), encrypted, Key, chunkSize, CancellationToken.None);

        Assert.Equal(ClientCrypto.GetEncryptedSize(length, chunkSize), encrypted.Length);

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        await ClientCrypto.DecryptAsync(encrypted, decrypted, Key, chunkSize, CancellationToken.None);

        Assert.Equal(plaintext, decrypted.ToArray());
    }

    [Fact]
    public async Task Tampered_ciphertext_fails_authentication()
    {
        const int chunkSize = 1024;
        var plaintext = new byte[5000];
        RandomNumberGenerator.Fill(plaintext);

        using var encrypted = new MemoryStream();
        await ClientCrypto.EncryptAsync(new MemoryStream(plaintext), encrypted, Key, chunkSize, CancellationToken.None);

        var bytes = encrypted.ToArray();
        bytes[bytes.Length / 2] ^= 0xFF; // flip one bit in the middle

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
        {
            using var output = new MemoryStream();
            await ClientCrypto.DecryptAsync(new MemoryStream(bytes), output, Key, chunkSize, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Reordered_chunks_fail_authentication()
    {
        const int chunkSize = 100;
        var plaintext = new byte[300]; // exactly 3 chunks
        RandomNumberGenerator.Fill(plaintext);

        using var encrypted = new MemoryStream();
        await ClientCrypto.EncryptAsync(new MemoryStream(plaintext), encrypted, Key, chunkSize, CancellationToken.None);

        var bytes = encrypted.ToArray();
        var encryptedChunk = chunkSize + ClientCrypto.ChunkOverhead;

        // Swap chunk 0 and chunk 1: the chunk index is authenticated data, so this must fail.
        var swapped = new byte[bytes.Length];
        Array.Copy(bytes, encryptedChunk, swapped, 0, encryptedChunk);
        Array.Copy(bytes, 0, swapped, encryptedChunk, encryptedChunk);
        Array.Copy(bytes, 2 * encryptedChunk, swapped, 2 * encryptedChunk, bytes.Length - 2 * encryptedChunk);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
        {
            using var output = new MemoryStream();
            await ClientCrypto.DecryptAsync(new MemoryStream(swapped), output, Key, chunkSize, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Wrong_key_fails()
    {
        var otherKey = ClientCrypto.DeriveKey("other-password", "user@example.com");
        using var encrypted = new MemoryStream();
        await ClientCrypto.EncryptAsync(new MemoryStream(new byte[100]), encrypted, Key, 1024, CancellationToken.None);
        encrypted.Position = 0;

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
        {
            using var output = new MemoryStream();
            await ClientCrypto.DecryptAsync(encrypted, output, otherKey, 1024, CancellationToken.None);
        });
    }

    [Fact]
    public void DeriveKey_is_deterministic_per_account_and_differs_between_accounts()
    {
        var again = ClientCrypto.DeriveKey("test-password", "USER@example.com "); // case/space-insensitive email
        Assert.Equal(Key, again);

        var different = ClientCrypto.DeriveKey("test-password", "someone-else@example.com");
        Assert.NotEqual(Key, different);
    }

    [Theory]
    [InlineData(0, 100, 1)]
    [InlineData(1, 100, 1)]
    [InlineData(100, 100, 1)]
    [InlineData(101, 100, 2)]
    [InlineData(1000, 100, 10)]
    public void GetChunkCount_matches_expectations(long length, int chunkSize, int expected)
    {
        Assert.Equal(expected, ClientCrypto.GetChunkCount(length, chunkSize));
    }
}
