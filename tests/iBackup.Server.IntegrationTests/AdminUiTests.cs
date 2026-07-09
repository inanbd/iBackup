using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using iBackup.Shared.Contracts;
using Microsoft.Data.SqlClient;
using Xunit;

namespace iBackup.Server.IntegrationTests;

/// <summary>
/// Razor Pages admin dashboard: cookie auth, role gating, antiforgery-protected
/// account management, and isolation from the JWT API surface.
/// </summary>
public class AdminUiTests : IClassFixture<ApiFixture>
{
    private static readonly Regex AntiforgeryToken =
        new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly ApiFixture _fixture;

    public AdminUiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Admin_dashboard_full_flow()
    {
        if (!ApiFixture.IsAvailable)
        {
            return; // no test database configured; see ApiFixture docs
        }

        var client = _fixture.CreateClient(); // handles cookies + redirects

        // --- create an admin (register via API, promote via SQL) and a plain user
        var adminEmail = $"adm-{Guid.NewGuid():N}@example.com";
        var plainEmail = $"usr-{Guid.NewGuid():N}@example.com";
        const string password = "admin-ui-password-1";
        await client.PostAsJsonAsync("api/auth/register", new RegisterUserRequest(adminEmail, password, "Admin"));
        var plainUserId = (await (await client.PostAsJsonAsync("api/auth/register",
            new RegisterUserRequest(plainEmail, password, "Plain"))).Content.ReadFromJsonAsync<RegisterUserResponse>())!.UserId;

        await using (var connection = new SqlConnection(ApiFixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var promote = connection.CreateCommand();
            promote.CommandText = "UPDATE dbo.Users SET IsAdmin = 1 WHERE Email = @Email;";
            promote.Parameters.AddWithValue("@Email", adminEmail);
            Assert.Equal(1, await promote.ExecuteNonQueryAsync());
        }

        // --- anonymous access is pushed to the login page
        var anonymous = await client.GetAsync("/Admin/Users");
        Assert.Contains("/Admin/Login", anonymous.RequestMessage!.RequestUri!.ToString());

        // --- non-admin credentials are refused
        var refused = await PostFormAsync(client, "/Admin/Login",
            new() { ["Email"] = plainEmail, ["Password"] = password });
        Assert.Contains("does not have administrator access", await refused.Content.ReadAsStringAsync());

        // --- admin signs in and sees the overview
        var overview = await PostFormAsync(client, "/Admin/Login",
            new() { ["Email"] = adminEmail, ["Password"] = password });
        var overviewHtml = await overview.Content.ReadAsStringAsync();
        Assert.EndsWith("/Admin", overview.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Storage used", overviewHtml);

        // --- users page lists both accounts
        var usersHtml = await client.GetStringAsync("/Admin/Users");
        Assert.Contains(adminEmail, usersHtml);
        Assert.Contains(plainEmail, usersHtml);

        // --- create a new user through the admin form
        var createdEmail = $"new-{Guid.NewGuid():N}@example.com";
        var created = await PostFormAsync(client, "/Admin/Users?handler=Create", new()
        {
            ["NewUser.Email"] = createdEmail,
            ["NewUser.DisplayName"] = "Created In Panel",
            ["NewUser.Password"] = "created-in-panel-1",
            ["NewUser.QuotaGb"] = "10",
            ["NewUser.IsAdmin"] = "false"
        }, tokenPagePath: "/Admin/Users");
        Assert.Contains($"User {createdEmail} created", await created.Content.ReadAsStringAsync());

        // the created account can authenticate against the API with the given quota
        var createdClient = _fixture.CreateClient();
        var createdLogin = await createdClient.PostAsJsonAsync("api/auth/login",
            new LoginRequest(createdEmail, "created-in-panel-1"));
        createdLogin.EnsureSuccessStatusCode();
        var createdTokens = (await createdLogin.Content.ReadFromJsonAsync<AuthTokensResponse>())!;
        createdClient.DefaultRequestHeaders.Authorization = new("Bearer", createdTokens.AccessToken);
        var profile = (await createdClient.GetFromJsonAsync<ProfileResponse>("api/client/profile"))!;
        Assert.Equal(10L * 1024 * 1024 * 1024, profile.QuotaBytes);

        // duplicate email is rejected
        var duplicate = await PostFormAsync(client, "/Admin/Users?handler=Create", new()
        {
            ["NewUser.Email"] = createdEmail,
            ["NewUser.Password"] = "another-password-1",
            ["NewUser.IsAdmin"] = "false"
        }, tokenPagePath: "/Admin/Users");
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());

        // --- change the plain user's quota through the form
        var quota = await PostFormAsync(client, "/Admin/Users?handler=Quota",
            new() { ["userId"] = plainUserId.ToString(), ["quotaGb"] = "42" }, tokenPagePath: "/Admin/Users");
        Assert.Contains("Quota set to 42 GB", await quota.Content.ReadAsStringAsync());

        // --- disable the plain user; their API login must now fail with 403
        var disable = await PostFormAsync(client, "/Admin/Users?handler=ToggleActive",
            new() { ["userId"] = plainUserId.ToString(), ["isActive"] = "true" }, tokenPagePath: "/Admin/Users");
        Assert.Contains("Account disabled", await disable.Content.ReadAsStringAsync());

        var apiClient = _fixture.CreateClient();
        var apiLogin = await apiClient.PostAsJsonAsync("api/auth/login", new LoginRequest(plainEmail, password));
        Assert.Equal(HttpStatusCode.Forbidden, apiLogin.StatusCode);

        // --- audit log recorded the admin actions
        var auditHtml = await client.GetStringAsync("/Admin/Audit?action=admin.");
        Assert.Contains("admin.login", auditHtml);
        Assert.Contains("admin.user_updated", auditHtml);

        // --- logout ends the cookie session
        await PostFormAsync(client, "/Admin/Logout", new(), tokenPagePath: "/Admin/Users");
        var afterLogout = await client.GetAsync("/Admin/Users");
        Assert.Contains("/Admin/Login", afterLogout.RequestMessage!.RequestUri!.ToString());
    }

    /// <summary>Fetches an antiforgery token from a page, then posts the form (redirects followed).</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields, string? tokenPagePath = null)
    {
        var tokenPage = await client.GetStringAsync(tokenPagePath ?? path.Split('?')[0]);
        var match = AntiforgeryToken.Match(tokenPage);
        Assert.True(match.Success, "antiforgery token not found on " + (tokenPagePath ?? path));
        fields["__RequestVerificationToken"] = match.Groups[1].Value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
}
