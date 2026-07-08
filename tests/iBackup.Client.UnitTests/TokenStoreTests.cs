using iBackup.Client.Core.Api;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Client.UnitTests;

public class TokenStoreTests
{
    private static AuthTokensResponse Tokens(string access, string refresh, DateTime expires) => new(
        Guid.NewGuid(), "user@example.com", "User", Guid.NewGuid(),
        access, expires, refresh, DateTime.UtcNow.AddDays(30));

    [Fact]
    public void NeedsRefresh_respects_margin()
    {
        var store = new TokenStore();
        store.Set(Tokens("a", "r", DateTime.UtcNow.AddMinutes(10)));
        Assert.False(store.NeedsRefresh(TimeSpan.FromMinutes(1)));
        Assert.True(store.NeedsRefresh(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task Concurrent_refreshes_are_single_flight()
    {
        var store = new TokenStore();
        store.Set(Tokens("expired", "refresh-1", DateTime.UtcNow.AddMinutes(-1)));

        var refreshCalls = 0;
        var gate = new TaskCompletionSource();

        async Task<AuthTokensResponse?> SlowRefresh(string refreshToken)
        {
            Interlocked.Increment(ref refreshCalls);
            await gate.Task;
            return Tokens("fresh", "refresh-2", DateTime.UtcNow.AddMinutes(15));
        }

        var first = store.RefreshAsync(SlowRefresh, CancellationToken.None);
        var second = store.RefreshAsync(SlowRefresh, CancellationToken.None);
        gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, refreshCalls);
        Assert.All(results, r => Assert.Equal("fresh", r!.AccessToken));
    }

    [Fact]
    public async Task Failed_refresh_returns_null_and_keeps_nothing_stale()
    {
        var store = new TokenStore();
        store.Set(Tokens("expired", "bad-refresh", DateTime.UtcNow.AddMinutes(-1)));

        var result = await store.RefreshAsync(_ => Task.FromResult<AuthTokensResponse?>(null), CancellationToken.None);
        Assert.Null(result);
    }
}
