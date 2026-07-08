using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using iBackup.Client.Core.Security;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace iBackup.Client.Core.Api;

/// <summary>Raised when the session cannot be re-established and the user must log in again.</summary>
public sealed class AuthenticationExpiredException : Exception
{
    public AuthenticationExpiredException() : base("Authentication expired; please sign in again.") { }
}

/// <summary>
/// Typed REST client for the iBackup server. Attaches the bearer token to every
/// call and transparently refreshes it (single-flight) when expired.
/// </summary>
public sealed class BackupApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TokenStore _tokens;
    private readonly CredentialStore _credentials;
    private readonly ILogger<BackupApiClient> _logger;

    public BackupApiClient(HttpClient http, TokenStore tokens, CredentialStore credentials, ILogger<BackupApiClient> logger)
    {
        _http = http;
        _tokens = tokens;
        _credentials = credentials;
        _logger = logger;
    }

    public void ConfigureServer(string serverUrl)
        => _http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);

    // ------------------------------------------------------------------ auth

    public async Task<RegisterUserResponse> RegisterAsync(RegisterUserRequest request, CancellationToken ct)
        => await PostAnonymousAsync<RegisterUserRequest, RegisterUserResponse>("api/auth/register", request, ct);

    public async Task<AuthTokensResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var tokens = await PostAnonymousAsync<LoginRequest, AuthTokensResponse>("api/auth/login", request, ct);
        _tokens.Set(tokens);
        PersistRefreshToken(tokens);
        return tokens;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var refreshToken = _tokens.Current?.RefreshToken;
        if (refreshToken is not null)
        {
            try
            {
                await PostAnonymousAsync<LogoutRequest, object?>("api/auth/logout", new LogoutRequest(refreshToken), ct, expectBody: false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Server-side logout failed; clearing local session anyway");
            }
        }
        _tokens.Clear();
    }

    // ---------------------------------------------------------------- client

    public Task<RegisterClientResponse> RegisterClientAsync(RegisterClientRequest request, CancellationToken ct)
        => SendAsync<RegisterClientRequest, RegisterClientResponse>(HttpMethod.Post, "api/client/register", request, ct);

    public Task<ProfileResponse> GetProfileAsync(CancellationToken ct)
        => SendAsync<object?, ProfileResponse>(HttpMethod.Get, "api/client/profile", null, ct);

    // --------------------------------------------------------------- folders

    public Task<IReadOnlyList<BackupFolderDto>> GetFoldersAsync(Guid? deviceId, CancellationToken ct)
        => SendAsync<object?, IReadOnlyList<BackupFolderDto>>(
            HttpMethod.Get, deviceId is null ? "api/folders" : $"api/folders?deviceId={deviceId}", null, ct);

    public Task<BackupFolderDto> CreateFolderAsync(CreateFolderRequest request, CancellationToken ct)
        => SendAsync<CreateFolderRequest, BackupFolderDto>(HttpMethod.Post, "api/folders", request, ct);

    public Task<BackupFolderDto> UpdateFolderAsync(UpdateFolderRequest request, CancellationToken ct)
        => SendAsync<UpdateFolderRequest, BackupFolderDto>(HttpMethod.Put, "api/folders", request, ct);

    public Task DeleteFolderAsync(Guid folderId, CancellationToken ct)
        => SendAsync<object?, object?>(HttpMethod.Delete, $"api/folders/{folderId}", null, ct, expectBody: false);

    // ---------------------------------------------------------------- backup

    public Task<StartBackupResponse> StartBackupAsync(StartBackupRequest request, CancellationToken ct)
        => SendAsync<StartBackupRequest, StartBackupResponse>(HttpMethod.Post, "api/backup/start", request, ct);

    public Task<BeginFileUploadResponse> BeginFileUploadAsync(BeginFileUploadRequest request, CancellationToken ct)
        => SendAsync<BeginFileUploadRequest, BeginFileUploadResponse>(HttpMethod.Post, "api/backup/upload", request, ct);

    /// <summary>Uploads one encrypted chunk from <paramref name="chunkStream"/>.</summary>
    public async Task<ChunkUploadResponse> UploadChunkAsync(
        Guid sessionId, int chunkIndex, string chunkSha256, Stream chunkStream, CancellationToken ct)
    {
        return await WithAuthRetryAsync(async token =>
        {
            if (chunkStream.CanSeek)
            {
                chunkStream.Position = 0;
            }
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"api/backup/chunk?sessionId={sessionId}&chunkIndex={chunkIndex}&sha256={chunkSha256}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StreamContent(chunkStream, 128 * 1024);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return await ReadResponseAsync<ChunkUploadResponse>(response, ct)
                ?? throw new HttpRequestException("Empty chunk upload response.");
        }, ct);
    }

    public Task DeleteFileAsync(DeleteFileRequest request, CancellationToken ct)
        => SendAsync<DeleteFileRequest, object?>(HttpMethod.Post, "api/backup/delete-file", request, ct, expectBody: false);

    public Task RenameFileAsync(RenameFileRequest request, CancellationToken ct)
        => SendAsync<RenameFileRequest, object?>(HttpMethod.Post, "api/backup/rename-file", request, ct, expectBody: false);

    public Task FinishBackupAsync(FinishBackupRequest request, CancellationToken ct)
        => SendAsync<FinishBackupRequest, object?>(HttpMethod.Post, "api/backup/finish", request, ct, expectBody: false);

    public Task<PagedResult<BackupHistoryItemDto>> GetBackupHistoryAsync(Guid? deviceId, int page, int pageSize, CancellationToken ct)
        => SendAsync<object?, PagedResult<BackupHistoryItemDto>>(
            HttpMethod.Get,
            $"api/backup/history?page={page}&pageSize={pageSize}" + (deviceId is null ? "" : $"&deviceId={deviceId}"),
            null, ct);

    // --------------------------------------------------------------- restore

    public Task<PagedResult<RestoreFileItemDto>> GetRestoreFilesAsync(
        Guid? folderId, string? pathPrefix, bool includeDeleted, int page, int pageSize, CancellationToken ct)
    {
        var url = $"api/restore/files?includeDeleted={includeDeleted}&page={page}&pageSize={pageSize}";
        if (folderId is not null)
        {
            url += $"&folderId={folderId}";
        }
        if (!string.IsNullOrEmpty(pathPrefix))
        {
            url += $"&pathPrefix={Uri.EscapeDataString(pathPrefix)}";
        }
        return SendAsync<object?, PagedResult<RestoreFileItemDto>>(HttpMethod.Get, url, null, ct);
    }

    public Task<CreateRestoreResponse> CreateRestoreAsync(CreateRestoreRequest request, CancellationToken ct)
        => SendAsync<CreateRestoreRequest, CreateRestoreResponse>(HttpMethod.Post, "api/restore", request, ct);

    /// <summary>Opens a stream over the encrypted content of a file version.</summary>
    public async Task<Stream> DownloadVersionAsync(Guid versionId, CancellationToken ct)
    {
        return await WithAuthRetryAsync(async token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"api/restore/download/{versionId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new UnauthorizedAccessException();
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }, ct);
    }

    // --------------------------------------------------------------- devices

    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct)
        => SendAsync<object?, IReadOnlyList<DeviceDto>>(HttpMethod.Get, "api/devices", null, ct);

    public Task UpdateDeviceAsync(UpdateDeviceRequest request, CancellationToken ct)
        => SendAsync<UpdateDeviceRequest, object?>(HttpMethod.Put, "api/devices", request, ct, expectBody: false);

    public Task DeleteDeviceAsync(Guid deviceId, CancellationToken ct)
        => SendAsync<object?, object?>(HttpMethod.Delete, $"api/devices/{deviceId}", null, ct, expectBody: false);

    public Task ForceLogoutDeviceAsync(Guid deviceId, CancellationToken ct)
        => SendAsync<object?, object?>(HttpMethod.Post, $"api/devices/{deviceId}/logout", null, ct, expectBody: false);

    // ------------------------------------------------------------- dashboard

    public Task<DashboardResponse> GetDashboardAsync(CancellationToken ct)
        => SendAsync<object?, DashboardResponse>(HttpMethod.Get, "api/dashboard", null, ct);

    // ------------------------------------------------------------- internals

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method, string url, TRequest? body, CancellationToken ct, bool expectBody = true)
    {
        return await WithAuthRetryAsync(async token =>
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            using var response = await _http.SendAsync(request, ct);
            if (!expectBody)
            {
                await EnsureSuccessAsync(response, ct);
                return default(TResponse)!;
            }
            return await ReadResponseAsync<TResponse>(response, ct)
                ?? throw new HttpRequestException($"Empty response from {url}.");
        }, ct);
    }

    /// <summary>
    /// Runs an authenticated call; on 401 (or a proactive expiry check) refreshes
    /// the token once and retries.
    /// </summary>
    private async Task<T> WithAuthRetryAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(ct);
        try
        {
            return await call(token);
        }
        catch (UnauthorizedAccessException)
        {
            token = await ForceRefreshAsync(ct);
            return await call(token);
        }
    }

    private async Task<string> GetValidAccessTokenAsync(CancellationToken ct)
    {
        if (!_tokens.HasTokens)
        {
            TryRestorePersistedSession();
        }

        if (_tokens.NeedsRefresh(TimeSpan.FromSeconds(30)))
        {
            return await ForceRefreshAsync(ct);
        }

        return _tokens.Current?.AccessToken ?? throw new AuthenticationExpiredException();
    }

    private async Task<string> ForceRefreshAsync(CancellationToken ct)
    {
        var refreshed = await _tokens.RefreshAsync(async refreshToken =>
        {
            try
            {
                var tokens = await PostAnonymousAsync<RefreshTokenRequest, AuthTokensResponse>(
                    "api/auth/refresh", new RefreshTokenRequest(refreshToken), ct);
                PersistRefreshToken(tokens);
                return tokens;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Refresh token rejected by the server");
                return null;
            }
        }, ct);

        if (refreshed is null)
        {
            _tokens.Clear();
            throw new AuthenticationExpiredException();
        }
        return refreshed.AccessToken;
    }

    private void TryRestorePersistedSession()
    {
        var stored = _credentials.Load();
        if (stored?.RefreshToken is null)
        {
            return;
        }
        // Seed the store with an already-expired access token; the first call refreshes it.
        _tokens.Set(new AuthTokensResponse(
            Guid.Empty, stored.Email, string.Empty, stored.DeviceId ?? Guid.Empty,
            AccessToken: string.Empty, AccessTokenExpiresAtUtc: DateTime.MinValue,
            RefreshToken: stored.RefreshToken, RefreshTokenExpiresAtUtc: DateTime.MaxValue));
    }

    private void PersistRefreshToken(AuthTokensResponse tokens)
    {
        try
        {
            var stored = _credentials.Load();
            if (stored is not null)
            {
                _credentials.Save(stored with { RefreshToken = tokens.RefreshToken, DeviceId = tokens.DeviceId });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist rotated refresh token");
        }
    }

    private async Task<TResponse> PostAnonymousAsync<TRequest, TResponse>(
        string url, TRequest body, CancellationToken ct, bool expectBody = true)
    {
        using var response = await _http.PostAsJsonAsync(url, body, Json, ct);
        if (!expectBody)
        {
            await EnsureSuccessAsync(response, ct);
            return default!;
        }
        return await ReadResponseAsync<TResponse>(response, ct)
            ?? throw new HttpRequestException($"Empty response from {url}.");
    }

    private static async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException();
        }
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiError>(Json, ct);
        }
        catch (JsonException)
        {
            // Non-JSON error body.
        }

        throw new HttpRequestException(
            error is null
                ? $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : $"{error.Code}: {error.Message}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
