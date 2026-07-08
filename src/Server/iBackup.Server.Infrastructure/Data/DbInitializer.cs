using System.Text.RegularExpressions;
using iBackup.Server.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace iBackup.Server.Infrastructure.Data;

/// <summary>
/// Optionally runs the SQL scripts in the database/ directory on startup
/// (Database:InitializeOnStartup = true). Scripts are idempotent.
/// </summary>
public sealed class DbInitializer
{
    private static readonly Regex GoSeparator = new(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly ISqlConnectionFactory _connections;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(ISqlConnectionFactory connections, IConfiguration configuration, ILogger<DbInitializer> logger)
    {
        _connections = connections;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_configuration.GetValue("Database:InitializeOnStartup", false))
        {
            return;
        }

        var scriptsPath = _configuration.GetValue<string>("Database:ScriptsPath") ?? "database";
        if (!Directory.Exists(scriptsPath))
        {
            _logger.LogWarning("Database scripts path {Path} not found; skipping initialization", scriptsPath);
            return;
        }

        await using var connection = await _connections.OpenConnectionAsync(ct);
        foreach (var file in Directory.EnumerateFiles(scriptsPath, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            _logger.LogInformation("Executing database script {Script}", Path.GetFileName(file));
            var script = await File.ReadAllTextAsync(file, ct);

            // SqlCommand cannot execute batches containing GO; split on batch separators.
            foreach (var batch in GoSeparator.Split(script))
            {
                if (string.IsNullOrWhiteSpace(batch))
                {
                    continue;
                }
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 300;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
