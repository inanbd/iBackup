using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Features.Auth;

/// <summary>ADO.NET access for the RefreshTokens table, shared by the auth slices.</summary>
internal static class RefreshTokenData
{
    public static async Task InsertAsync(
        SqlConnection connection, Guid userId, Guid? deviceId, string tokenHash,
        DateTime expiresAtUtc, string? createdByIp, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.RefreshTokens (UserId, DeviceId, TokenHash, ExpiresAtUtc, CreatedByIp)
            VALUES (@UserId, @DeviceId, @TokenHash, @ExpiresAtUtc, @CreatedByIp);
            """;

        await using var command = Sql.Command(connection, sql)
            .With("@UserId", userId)
            .With("@DeviceId", deviceId)
            .With("@TokenHash", tokenHash)
            .With("@ExpiresAtUtc", expiresAtUtc)
            .With("@CreatedByIp", createdByIp);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<RefreshToken?> GetByHashAsync(SqlConnection connection, string tokenHash, CancellationToken ct)
    {
        const string sql = """
            SELECT Id, UserId, DeviceId, TokenHash, ExpiresAtUtc, CreatedAtUtc, CreatedByIp, RevokedAtUtc, ReplacedByTokenHash
            FROM dbo.RefreshTokens
            WHERE TokenHash = @TokenHash;
            """;

        await using var command = Sql.Command(connection, sql).With("@TokenHash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RefreshToken
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetGuid(1),
            DeviceId = reader.GetGuidOrNull(2),
            TokenHash = reader.GetString(3),
            ExpiresAtUtc = reader.GetDateTime(4),
            CreatedAtUtc = reader.GetDateTime(5),
            CreatedByIp = reader.GetStringOrNull(6),
            RevokedAtUtc = reader.GetDateTimeOrNull(7),
            ReplacedByTokenHash = reader.GetStringOrNull(8)
        };
    }

    public static async Task RevokeAsync(SqlConnection connection, string tokenHash, string? replacedByTokenHash, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.RefreshTokens
            SET RevokedAtUtc = SYSUTCDATETIME(), ReplacedByTokenHash = @ReplacedBy
            WHERE TokenHash = @TokenHash AND RevokedAtUtc IS NULL;
            """;

        await using var command = Sql.Command(connection, sql)
            .With("@TokenHash", tokenHash)
            .With("@ReplacedBy", replacedByTokenHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Revokes every live refresh token of a user, optionally scoped to one device.</summary>
    public static async Task RevokeAllAsync(SqlConnection connection, Guid userId, Guid? deviceId, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.RefreshTokens
            SET RevokedAtUtc = SYSUTCDATETIME()
            WHERE UserId = @UserId
              AND (@DeviceId IS NULL OR DeviceId = @DeviceId)
              AND RevokedAtUtc IS NULL;
            """;

        await using var command = Sql.Command(connection, sql)
            .With("@UserId", userId)
            .With("@DeviceId", deviceId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
