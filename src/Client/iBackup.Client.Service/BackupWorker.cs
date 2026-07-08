using iBackup.Client.Core.Api;
using iBackup.Client.Core.Engine;
using iBackup.Client.Core.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace iBackup.Client.Service;

/// <summary>
/// Hosts the backup engine as a Windows Service. Resumes the DPAPI-persisted
/// session created by the desktop app; if none exists it waits and retries so
/// the service can start before the first sign-in.
/// </summary>
public sealed class BackupWorker : BackgroundService
{
    private readonly AuthService _auth;
    private readonly BackupEngine _engine;
    private readonly INotificationService _notifications;
    private readonly ILogger<BackupWorker> _logger;

    public BackupWorker(
        AuthService auth,
        BackupEngine engine,
        INotificationService notifications,
        ILogger<BackupWorker> logger)
    {
        _auth = auth;
        _engine = engine;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _notifications.NotificationRaised += n =>
            _logger.LogInformation("[{Type}] {Title}: {Message}", n.Type, n.Title, n.Message);

        // Wait until a persisted session exists (the user signs in via the app once).
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_auth.TryResumeSession() && _auth.PersistedCredentials is { DeviceId: not null } stored)
            {
                _engine.DeviceId = stored.DeviceId;
                break;
            }
            _logger.LogInformation("No persisted session yet; retrying in 60 seconds");
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _engine.StartAsync(stoppingToken);
            _logger.LogInformation("Backup engine running as a service");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            await _engine.StopAsync();
        }
    }
}
