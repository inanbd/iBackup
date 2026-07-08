using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace iBackup.Server.Infrastructure.BackgroundServices;

/// <summary>
/// Periodically aborts upload sessions with no activity beyond the cutoff and
/// removes their chunk files from disk.
/// </summary>
public sealed class AbandonedUploadCleanupService : BackgroundService
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IFileStorage _storage;
    private readonly ILogger<AbandonedUploadCleanupService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _idleCutoff;

    public AbandonedUploadCleanupService(
        ISqlConnectionFactory connections,
        IFileStorage storage,
        IConfiguration configuration,
        ILogger<AbandonedUploadCleanupService> logger)
    {
        _connections = connections;
        _storage = storage;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(configuration.GetValue("Maintenance:UploadCleanupIntervalMinutes", 30));
        _idleCutoff = TimeSpan.FromHours(configuration.GetValue("Maintenance:AbandonedUploadCutoffHours", 48));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Abandoned upload cleanup failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    internal async Task CleanupOnceAsync(CancellationToken ct)
    {
        var sessions = new List<(Guid SessionId, Guid UserId)>();

        await using (var connection = await _connections.OpenConnectionAsync(ct))
        await using (var command = Sql.StoredProcedure(connection, "dbo.usp_AbortAbandonedUploads")
            .With("@IdleCutoffUtc", DateTime.UtcNow - _idleCutoff))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                sessions.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        foreach (var (sessionId, userId) in sessions)
        {
            await _storage.DeleteChunksAsync(userId, sessionId, ct);
        }

        if (sessions.Count > 0)
        {
            _logger.LogInformation("Aborted {Count} abandoned upload sessions", sessions.Count);
        }
    }
}
