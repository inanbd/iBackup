using System.Net;
using System.Net.Http.Json;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Server.IntegrationTests;

/// <summary>Register → login → refresh → logout against the real API + database.</summary>
public class AuthFlowTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public AuthFlowTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Full_auth_lifecycle()
    {
        if (!ApiFixture.IsAvailable)
        {
            return; // no test database configured; see ApiFixture docs
        }

        var client = _fixture.CreateClient();
        var email = $"it-{Guid.NewGuid():N}@example.com";
        const string password = "integration-password-1";

        // Register
        var register = await client.PostAsJsonAsync("api/auth/register",
            new RegisterUserRequest(email, password, "Integration User"));
        register.EnsureSuccessStatusCode();

        // Duplicate registration conflicts
        var duplicate = await client.PostAsJsonAsync("api/auth/register",
            new RegisterUserRequest(email, password, "Integration User"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Login
        var login = await client.PostAsJsonAsync("api/auth/login",
            new LoginRequest(email, password, new DeviceInfo("IT Device", "TestOS", "1.0.0")));
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<AuthTokensResponse>();
        Assert.NotNull(tokens);
        Assert.NotEqual(Guid.Empty, tokens!.DeviceId);

        // Wrong password rejected
        var badLogin = await client.PostAsJsonAsync("api/auth/login", new LoginRequest(email, "wrong-password-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        // Authenticated call works
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var profile = await client.GetFromJsonAsync<ProfileResponse>("api/client/profile");
        Assert.Equal(email, profile!.Email);

        // Refresh rotates the token
        var refresh = await client.PostAsJsonAsync("api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        refresh.EnsureSuccessStatusCode();
        var rotated = await refresh.Content.ReadFromJsonAsync<AuthTokensResponse>();
        Assert.NotEqual(tokens.RefreshToken, rotated!.RefreshToken);

        // Replaying the rotated-away token is rejected (reuse detection)
        var replay = await client.PostAsJsonAsync("api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Logout revokes
        var logout = await client.PostAsJsonAsync("api/auth/logout", new LogoutRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.PostAsJsonAsync("api/auth/refresh", new RefreshTokenRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_to_protected_endpoints_are_rejected()
    {
        if (!ApiFixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.CreateClient();
        var response = await client.GetAsync("api/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
