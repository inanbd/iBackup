using iBackup.Shared.Contracts;

namespace iBackup.Client.Core.Api;

/// <summary>
/// In-memory holder for the current token pair, with a single-flight refresh gate
/// so concurrent 401s trigger only one refresh call.
/// </summary>
public sealed class TokenStore
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile AuthTokensResponse? _tokens;

    public AuthTokensResponse? Current => _tokens;

    public bool HasTokens => _tokens is not null;

    public Guid? DeviceId => _tokens?.DeviceId;

    public void Set(AuthTokensResponse tokens) => _tokens = tokens;

    public void Clear() => _tokens = null;

    /// <summary>True when the access token expires within the safety margin.</summary>
    public bool NeedsRefresh(TimeSpan margin)
        => _tokens is { } t && DateTime.UtcNow >= t.AccessTokenExpiresAtUtc - margin;

    /// <summary>
    /// Ensures only one caller refreshes at a time. The delegate receives the
    /// refresh token and returns the new pair (or null when refresh failed).
    /// </summary>
    public async Task<AuthTokensResponse?> RefreshAsync(
        Func<string, Task<AuthTokensResponse?>> refresh, CancellationToken ct)
    {
        var before = _tokens;
        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited.
            if (!ReferenceEquals(_tokens, before) && _tokens is not null)
            {
                return _tokens;
            }

            var refreshToken = _tokens?.RefreshToken;
            if (refreshToken is null)
            {
                return null;
            }

            var fresh = await refresh(refreshToken);
            if (fresh is not null)
            {
                _tokens = fresh;
            }
            return fresh;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
