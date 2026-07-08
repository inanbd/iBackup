using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace iBackup.Server.IntegrationTests;

/// <summary>
/// Boots the real API against a disposable database and temp storage directory.
///
/// Requires a reachable SQL Server; set:
///   IBACKUP_TEST_DB = Server=localhost;Database=iBackup_Test;User Id=sa;Password=...;TrustServerCertificate=True
/// Tests that need the fixture no-op (pass) when the variable is absent, so the
/// suite is safe to run anywhere and exercises the full stack when a DB exists
/// (e.g. via docker-compose up sqlserver).
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("IBACKUP_TEST_DB");

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public string StorageRoot { get; } =
        Path.Combine(Path.GetTempPath(), "ibackup-int-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Jwt:SigningKey"] = "integration-test-signing-key-32-bytes-min!",
                ["Storage:RootPath"] = StorageRoot,
                ["Database:InitializeOnStartup"] = "true",
                ["Database:ScriptsPath"] = FindScriptsPath(),
                ["RateLimiting:AuthPermitPerMinute"] = "1000",
                ["RateLimiting:GlobalPermitPerMinute"] = "100000"
            });
        });
    }

    private static string FindScriptsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "database");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return "database";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            try
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
