namespace iBackup.Client.Core.Notifications;

public enum NotificationType
{
    BackupCompleted,
    BackupFailed,
    BackupPaused,
    AuthenticationExpired,
    QuotaExceeded,
    RestoreCompleted,
    Info
}

public sealed record Notification(NotificationType Type, string Title, string Message, DateTime TimestampUtc);

/// <summary>Publishes user-facing notifications; the UI (or service log) subscribes.</summary>
public interface INotificationService
{
    event Action<Notification>? NotificationRaised;
    void Notify(NotificationType type, string title, string message);
    IReadOnlyList<Notification> Recent { get; }
}

public sealed class NotificationService : INotificationService
{
    private const int MaxRecent = 100;
    private readonly Lock _lock = new();
    private readonly List<Notification> _recent = [];

    public event Action<Notification>? NotificationRaised;

    public IReadOnlyList<Notification> Recent
    {
        get
        {
            lock (_lock)
            {
                return _recent.ToList();
            }
        }
    }

    public void Notify(NotificationType type, string title, string message)
    {
        var notification = new Notification(type, title, message, DateTime.UtcNow);
        lock (_lock)
        {
            _recent.Insert(0, notification);
            if (_recent.Count > MaxRecent)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }
        NotificationRaised?.Invoke(notification);
    }
}
