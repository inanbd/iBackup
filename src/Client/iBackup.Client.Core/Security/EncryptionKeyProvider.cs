namespace iBackup.Client.Core.Security;

/// <summary>
/// Holds the account file-encryption key for the current session.
/// Derived from the password at login (see <see cref="ClientCrypto.DeriveKey"/>)
/// and cached DPAPI-protected between sessions.
/// </summary>
public sealed class EncryptionKeyProvider
{
    private readonly CredentialStore _credentials;
    private byte[]? _key;

    public EncryptionKeyProvider(CredentialStore credentials)
    {
        _credentials = credentials;
    }

    public bool HasKey => GetKeyOrNull() is not null;

    public void SetKey(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Encryption key must be 256 bits.", nameof(key));
        }
        _key = key;
    }

    public byte[] GetKey()
        => GetKeyOrNull() ?? throw new InvalidOperationException("No encryption key available; log in first.");

    private byte[]? GetKeyOrNull()
    {
        if (_key is not null)
        {
            return _key;
        }
        var stored = _credentials.Load();
        if (stored?.EncryptionKeyBase64 is { } base64)
        {
            _key = Convert.FromBase64String(base64);
        }
        return _key;
    }
}
