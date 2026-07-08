using iBackup.Client.Core.Security;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace iBackup.Client.Core.Api;

/// <summary>
/// High-level sign-in flow: authenticates, registers the device, derives the
/// client-side encryption key from the password (the server never sees it),
/// and persists everything DPAPI-protected for unattended runs.
/// </summary>
public sealed class AuthService
{
    private readonly BackupApiClient _api;
    private readonly CredentialStore _credentials;
    private readonly EncryptionKeyProvider _keys;
    private readonly TokenStore _tokens;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        BackupApiClient api,
        CredentialStore credentials,
        EncryptionKeyProvider keys,
        TokenStore tokens,
        ILogger<AuthService> logger)
    {
        _api = api;
        _credentials = credentials;
        _keys = keys;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>True when a persisted session exists that can be resumed without a password.</summary>
    public bool HasPersistedSession =>
        _credentials.Load() is { RefreshToken: not null, EncryptionKeyBase64: not null };

    public StoredCredentials? PersistedCredentials => _credentials.Load();

    public async Task<AuthTokensResponse> RegisterAndLoginAsync(
        string serverUrl, string email, string password, string displayName, CancellationToken ct)
    {
        _api.ConfigureServer(serverUrl);
        await _api.RegisterAsync(new RegisterUserRequest(email, password, displayName), ct);
        return await LoginAsync(serverUrl, email, password, ct);
    }

    public async Task<AuthTokensResponse> LoginAsync(string serverUrl, string email, string password, CancellationToken ct)
    {
        _api.ConfigureServer(serverUrl);

        var existing = _credentials.Load();
        var device = new DeviceInfo(
            Environment.MachineName,
            OperatingSystemDescription(),
            typeof(AuthService).Assembly.GetName().Version?.ToString() ?? "1.0.0");

        var tokens = await _api.LoginAsync(new LoginRequest(email, password, device,
            existing?.Email.Equals(email, StringComparison.OrdinalIgnoreCase) == true ? existing.DeviceId : null), ct);

        // Derive the AES-256 key locally; store it DPAPI-protected so scheduled
        // backups run without prompting for the password.
        var key = ClientCrypto.DeriveKey(password, email);
        _keys.SetKey(key);

        _credentials.Save(new StoredCredentials(
            serverUrl, email, tokens.RefreshToken, tokens.DeviceId, Convert.ToBase64String(key)));

        _logger.LogInformation("Signed in as {Email}, device {DeviceId}", email, tokens.DeviceId);
        return tokens;
    }

    /// <summary>Resumes a persisted session (used by the background service and app autostart).</summary>
    public bool TryResumeSession()
    {
        var stored = _credentials.Load();
        if (stored?.RefreshToken is null || stored.EncryptionKeyBase64 is null)
        {
            return false;
        }
        _api.ConfigureServer(stored.ServerUrl);
        _keys.SetKey(Convert.FromBase64String(stored.EncryptionKeyBase64));
        // Token store is seeded lazily by the API client from the credential store.
        return true;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        await _api.LogoutAsync(ct);
        _credentials.Clear();
        _tokens.Clear();
    }

    private static string OperatingSystemDescription()
        => $"{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";
}
