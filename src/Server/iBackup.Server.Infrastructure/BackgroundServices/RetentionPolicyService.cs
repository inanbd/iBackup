using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace iBackup.Server.Infrastructure.BackgroundServices;

/// <summary>
/// Periodically applies every folder's retention policy: expires old versions,
/// dereferences their storage objects and deletes orphaned objects from disk
/// (returning the space to the user's quota).
/// </summary>
public sealed class RetentionPolicyService : BackgroundService
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IFileStorage _storage;
    private readonly ILogger<RetentionPolicyService> _logger;
    private readonly TimeSpan _interval;

    public RetentionPolicyService(
        ISqlConnectionFactory connections,
        IFileStorage storage,
        IConfiguration configuration,
        ILogger<RetentionPolicyService> logger)
    {
        _connections = connections;
        _storage = storage;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(configuration.GetValue("Maintenance:RetentionSweepIntervalMinutes", 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention sweep failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        var folders = new List<(Guid FolderId, byte Mode, int? Value)>();

        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string foldersSql = """
            SELECT Id, RetentionMode, RetentionValue
            FROM dbo.BackupFolders
            WHERE RetentionMode <> 0 AND RetentionValue IS NOT NULL;
            """;
        await using (var command = Sql.Command(connection, foldersSql))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                folders.Add((reader.GetGuid(0), reader.GetByte(1), reader.GetInt32OrNull(2)));
            }
        }

        foreach (var (folderId, mode, value) in folders)
        {
            ct.ThrowIfCancellationRequested();

            var orphans = new List<(Guid ObjectId, Guid UserId, string StoragePath)>();
            await using (var apply = Sql.StoredProcedure(connection, "dbo.usp_ApplyRetention")
                .With("@FolderId", folderId)
                .With("@RetentionMode", mode)
                .With("@RetentionValue", value))
            await using (var reader = await apply.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    orphans.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
                }
            }

            foreach (var (objectId, userId, storagePath) in orphans)
            {
                await _storage.DeleteObjectAsync(userId, storagePath, ct);

                const string deleteSql = """
                    DELETE FROM dbo.StorageObjects WHERE Id = @Id AND ReferenceCount <= 0;
                    UPDATE dbo.Users SET UsedBytes = CASE WHEN UsedBytes >= @Size THEN UsedBytes - @Size ELSE 0 END
                    WHERE Id = @UserId AND @@ROWCOUNT > 0;
                    """;
                const string sizeSql = "SELECT StoredSize FROM dbo.StorageObjects WHERE Id = @Id;";

                long storedSize = 0;
                await using (var size = Sql.Command(connection, sizeSql).With("@Id", objectId))
                {
                    if (await size.ExecuteScalarAsync(ct) is long s)
                    {
                        storedSize = s;
                    }
                }

                await using var delete = Sql.Command(connection, deleteSql)
                    .With("@Id", objectId)
                    .With("@UserId", userId)
                    .With("@Size", storedSize);
                await delete.ExecuteNonQueryAsync(ct);
            }

            if (orphans.Count > 0)
            {
                _logger.LogInformation("Retention removed {Count} storage objects for folder {FolderId}", orphans.Count, folderId);
            }
        }
    }
}
