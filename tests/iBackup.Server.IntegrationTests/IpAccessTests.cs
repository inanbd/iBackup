using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using iBackup.Shared.Contracts;
using Microsoft.Data.SqlClient;
using Xunit;

namespace iBackup.Server.IntegrationTests;

/// <summary>
/// End-to-end IP access control: default-deny, whitelist, blacklist precedence,
/// auto-blacklist after repeated failures, and the login-attempts view — driven
/// through the real HTTP pipeline. A simulated client IP is supplied via
/// X-Forwarded-For (the fixture enables TrustForwardedFor); requests without it
/// resolve to the loopback test host and are always allowed.
/// </summary>
public class IpAccessTests : IClassFixture<ApiFixture>
{
    private static readonly Regex AntiforgeryToken =
        new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly ApiFixture _fixture;

    public IpAccessTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Ip_access_control_full_flow()
    {
        if (!ApiFixture.IsAvailable)
        {
            return;
        }

        await ResetIpRulesAsync();

        // --- seed an admin and sign into the dashboard (from the loopback test host)
        var adminEmail = $"ipadm-{Guid.NewGuid():N}@example.com";
        const string adminPassword = "ip-admin-password-1";
        await TestAccounts.CreateAsync(adminEmail, adminPassword, isAdmin: true);

        var admin = _fixture.CreateClient();
        await SignInAdminAsync(admin, adminEmail, adminPassword);

        // ============================ default deny ============================
        // A remote IP with no whitelist entry is blocked before auth (403, not 401).
        Assert.Equal(HttpStatusCode.Forbidden, await StatusFromIp("198.51.100.10", "api/dashboard"));
        Assert.Equal(HttpStatusCode.Forbidden, await LoginStatusFromIp("198.51.100.10", "someone@example.com", "whatever-123"));
        // The loopback test host (no X-Forwarded-For) passes the IP filter and is
        // only stopped by auth -> 401, proving localhost is allowed by default.
        var loopback = _fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await loopback.GetAsync("api/dashboard")).StatusCode);

        // ============================ whitelist ==============================
        await AddRuleAsync(admin, "AddWhitelist", "198.51.100.20");
        // Whitelisted IP now passes the filter and reaches login (bad creds -> 401).
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusFromIp("198.51.100.20", "nobody@example.com", "bad-password-1"));
        // A different, non-whitelisted IP is still denied.
        Assert.Equal(HttpStatusCode.Forbidden, await LoginStatusFromIp("198.51.100.21", "nobody@example.com", "bad-password-1"));

        // ===================== blacklist precedence ==========================
        await AddRuleAsync(admin, "AddWhitelist", "*");                 // open everything
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusFromIp("198.51.100.30", "nobody@example.com", "bad-password-1"));
        await AddRuleAsync(admin, "AddBlacklist", "198.51.100.30");     // explicit deny wins over '*'
        Assert.Equal(HttpStatusCode.Forbidden, await LoginStatusFromIp("198.51.100.30", "nobody@example.com", "bad-password-1"));
        // other IPs remain allowed by the wildcard
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusFromIp("198.51.100.31", "nobody@example.com", "bad-password-1"));

        // ===================== auto-blacklist after 5 fails ==================
        const string attacker = "203.0.113.55";
        for (var i = 0; i < 5; i++)
        {
            // allowed by '*', reaches login, invalid credentials -> 401
            Assert.Equal(HttpStatusCode.Unauthorized,
                await LoginStatusFromIp(attacker, "victim@example.com", "wrong-password-1"));
        }
        // The 6th request from that IP is now blocked by the auto-added blacklist.
        Assert.Equal(HttpStatusCode.Forbidden,
            await LoginStatusFromIp(attacker, "victim@example.com", "wrong-password-1"));

        // The auto rule is visible on the admin IP page, flagged "auto".
        var ipPage = await admin.GetStringAsync("/Admin/IpAccess");
        Assert.Contains(attacker, ipPage);
        Assert.Contains("auto", ipPage);

        // The failed attempts are visible on the login-attempts page.
        var attemptsPage = await admin.GetStringAsync($"/Admin/LoginAttempts?ip={attacker}&failuresOnly=true");
        Assert.Contains(attacker, attemptsPage);
        Assert.Contains("victim@example.com", attemptsPage);

        // Removing the auto blacklist rule restores access (allowed again by '*').
        await RemoveBlacklistRuleAsync(admin, attacker);
        Assert.Equal(HttpStatusCode.Unauthorized,
            await LoginStatusFromIp(attacker, "victim@example.com", "wrong-password-1"));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<HttpStatusCode> StatusFromIp(string ip, string path)
    {
        var client = _fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", ip);
        return (await client.SendAsync(request)).StatusCode;
    }

    private async Task<HttpStatusCode> LoginStatusFromIp(string ip, string email, string password)
    {
        var client = _fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(email, password))
        };
        request.Headers.Add("X-Forwarded-For", ip);
        return (await client.SendAsync(request)).StatusCode;
    }

    private static async Task SignInAdminAsync(HttpClient client, string email, string password)
    {
        var page = await client.GetStringAsync("/Admin/Login");
        var fields = new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = AntiforgeryToken.Match(page).Groups[1].Value
        };
        var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(fields));
        Assert.EndsWith("/Admin", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    private static async Task AddRuleAsync(HttpClient admin, string handler, string ip)
    {
        var page = await admin.GetStringAsync("/Admin/IpAccess");
        var fields = new Dictionary<string, string>
        {
            ["ipAddress"] = ip,
            ["__RequestVerificationToken"] = AntiforgeryToken.Match(page).Groups[1].Value
        };
        var response = await admin.PostAsync($"/Admin/IpAccess?handler={handler}", new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Deletes a blacklist rule for the given IP via the admin page's Delete handler.</summary>
    private static async Task RemoveBlacklistRuleAsync(HttpClient admin, string ip)
    {
        long ruleId;
        await using (var connection = new SqlConnection(ApiFixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM dbo.IpAccessRules WHERE IpAddress = @Ip AND Kind = 1;";
            command.Parameters.AddWithValue("@Ip", ip);
            ruleId = (long)(await command.ExecuteScalarAsync())!;
        }

        var page = await admin.GetStringAsync("/Admin/IpAccess");
        var fields = new Dictionary<string, string>
        {
            ["ruleId"] = ruleId.ToString(),
            ["__RequestVerificationToken"] = AntiforgeryToken.Match(page).Groups[1].Value
        };
        var response = await admin.PostAsync("/Admin/IpAccess?handler=Delete", new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode();
    }

    private static async Task ResetIpRulesAsync()
    {
        await using var connection = new SqlConnection(ApiFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.IpAccessRules;";
        await command.ExecuteNonQueryAsync();
    }
}
