using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace iBackup.Client.Core.Security;

/// <summary>Credentials and tokens persisted on the client.</summary>
public sealed record StoredCredentials(
    string ServerUrl,
    string Email,
    string? RefreshToken,
    Guid? DeviceId,
    // Base64 of the derived 256-bit encryption key. Never leaves the machine unprotected.
    string? EncryptionKeyBase64);

/// <summary>
/// Persists credentials with the Windows Data Protection API (DPAPI, per-user scope).
/// The file is unreadable by other users and other machines.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _filePath;

    public CredentialStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "credentials.dat");
    }

    public void Save(StoredCredentials credentials)
    {
        EnsureWindows();
        var json = JsonSerializer.Serialize(credentials);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, protectedBytes);
    }

    public StoredCredentials? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_filePath);
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<StoredCredentials>(json);
        }
        catch (CryptographicException)
        {
            // Written by a different user/machine - treat as absent.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Credential storage uses the Windows Data Protection API (DPAPI).");
        }
    }
}
