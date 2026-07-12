using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace iBackup.Server.Infrastructure.Security;

/// <summary>
/// ADO.NET-backed IP access control with an in-memory rule cache.
///
/// The security decision itself lives in <see cref="IpRuleEvaluator"/>; this
/// class loads the whitelist/blacklist sets, caches them, records login attempts
/// and applies the "N failed logins → auto-blacklist" rule. Registered as a
/// singleton so the cache is shared across requests.
/// </summary>
public sealed class IpAccessControl : IIpAccessControl
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IAuditLogger _audit;
    private readonly IpAccessOptions _options;
    private readonly ILogger<IpAccessControl> _logger;

    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile Snapshot? _snapshot;

    public IpAccessControl(
        ISqlConnectionFactory connections,
        IAuditLogger audit,
        IOptions<IpAccessOptions> options,
        ILogger<IpAccessControl> logger)
    {
        _connections = connections;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public void Invalidate() => _snapshot = null;

    public async Task<bool> IsAllowedAsync(string? ip, CancellationToken ct = default)
    {
        // Fast path: loopback/unknown never needs the rule sets.
        if (IpRuleEvaluator.IsLoopbackOrUnknown(ip))
        {
            return true;
        }

        var snapshot = await GetSnapshotAsync(ct);
        return IpRuleEvaluator.IsAllowed(ip, snapshot.Whitelist, snapshot.Blacklist);
    }

    public async Task RecordLoginAttemptAsync(LoginAttemptRecord attempt, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(ct);

            const string insertSql = """
                INSERT INTO dbo.LoginAttempts (Email, IpAddress, Success, Reason, UserId, Source)
                VALUES (@Email, @Ip, @Success, @Reason, @UserId, @Source);
                """;
            await using (var insert = Sql.Command(connection, insertSql)
                .With("@Email", Trim(attempt.Email, 256))
                .With("@Ip", attempt.IpAddress)
                .With("@Success", attempt.Success)
                .With("@Reason", Trim(attempt.Reason, 256))
                .With("@UserId", attempt.UserId)
                .With("@Source", attempt.Source))
            {
                await insert.ExecuteNonQueryAsync(ct);
            }

            if (!attempt.Success)
            {
                await MaybeAutoBlacklistAsync(connection, attempt.IpAddress, ct);
            }
        }
        catch (Exception ex)
        {
            // Recording/auth-hardening must never break the login flow itself.
            _logger.LogError(ex, "Failed to record login attempt for {Ip}", attempt.IpAddress);
        }
    }

    private async Task MaybeAutoBlacklistAsync(SqlConnection connection, string? ip, CancellationToken ct)
    {
        // Never auto-blacklist the local host, unknown, or wildcard.
        if (IpRuleEvaluator.IsLoopbackOrUnknown(ip))
        {
            return;
        }
        var normalized = IpRuleEvaluator.Normalize(ip);
        if (normalized is null)
        {
            return;
        }

        var since = DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.FailedAttemptWindowMinutes));

        const string countSql = """
            SELECT COUNT(*) FROM dbo.LoginAttempts
            WHERE IpAddress = @Ip AND Success = 0 AND CreatedAtUtc >= @Since;
            """;
        int failures;
        await using (var count = Sql.Command(connection, countSql)
            .With("@Ip", normalized)
            .With("@Since", since))
        {
            failures = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        if (failures < Math.Max(1, _options.MaxFailedAttempts))
        {
            return;
        }

        // Insert a blacklist rule if one is not already present (idempotent).
        const string insertRuleSql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.IpAccessRules WHERE IpAddress = @Ip AND Kind = 1)
                INSERT INTO dbo.IpAccessRules (IpAddress, Kind, Reason, IsAuto)
                VALUES (@Ip, 1, @Reason, 1);
            """;
        int inserted;
        await using (var insert = Sql.Command(connection, insertRuleSql)
            .With("@Ip", normalized)
            .With("@Reason", $"Auto-blocked after {failures} failed login attempts"))
        {
            inserted = await insert.ExecuteNonQueryAsync(ct);
        }

        if (inserted > 0)
        {
            Invalidate();
            _logger.LogWarning("Auto-blacklisted {Ip} after {Failures} failed logins", normalized, failures);
            await _audit.LogAsync(null, null, "ip.auto_blacklisted",
                $"{normalized} blocked after {failures} failed logins", normalized, ct);
        }
    }

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && !current.IsStale(_options.CacheSeconds))
        {
            return current;
        }

        await _reloadGate.WaitAsync(ct);
        try
        {
            current = _snapshot;
            if (current is not null && !current.IsStale(_options.CacheSeconds))
            {
                return current;
            }

            var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var connection = await _connections.OpenConnectionAsync(ct);
            const string sql = "SELECT IpAddress, Kind FROM dbo.IpAccessRules;";
            await using (var command = Sql.Command(connection, sql))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var value = reader.GetString(0);
                    if (reader.GetByte(1) == 0)
                    {
                        whitelist.Add(value);
                    }
                    else
                    {
                        blacklist.Add(value);
                    }
                }
            }

            var snapshot = new Snapshot(whitelist, blacklist, DateTime.UtcNow);
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private static string? Trim(string? value, int max)
        => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private sealed record Snapshot(HashSet<string> Whitelist, HashSet<string> Blacklist, DateTime LoadedAtUtc)
    {
        public bool IsStale(int cacheSeconds)
            => cacheSeconds <= 0 || DateTime.UtcNow - LoadedAtUtc > TimeSpan.FromSeconds(cacheSeconds);
    }
}
