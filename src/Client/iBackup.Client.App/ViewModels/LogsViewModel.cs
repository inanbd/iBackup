using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Configuration;
using iBackup.Client.Core.Notifications;
using Microsoft.Extensions.Options;

namespace iBackup.Client.App.ViewModels;

/// <summary>Logs page: recent notifications plus the tail of the rolling client log file.</summary>
public partial class LogsViewModel : ObservableObject, IRefreshable
{
    private readonly INotificationService _notifications;
    private readonly ClientOptions _options;

    public ObservableCollection<Notification> Notifications { get; } = [];

    [ObservableProperty] private string _logTail = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public LogsViewModel(INotificationService notifications, IOptions<ClientOptions> options)
    {
        _notifications = notifications;
        _options = options.Value;
        _notifications.NotificationRaised += n =>
            App.Current.Dispatcher.BeginInvoke(() => Notifications.Insert(0, n));
    }

    public void RefreshCommandIfIdle() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        Notifications.Clear();
        foreach (var notification in _notifications.Recent)
        {
            Notifications.Add(notification);
        }

        try
        {
            var logDirectory = _options.LogDirectory;
            if (!Directory.Exists(logDirectory))
            {
                LogTail = "(no log files yet)";
                return;
            }
            var latest = Directory.EnumerateFiles(logDirectory, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                LogTail = "(no log files yet)";
                return;
            }

            using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new LinkedList<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.AddLast(line);
                if (lines.Count > 500)
                {
                    lines.RemoveFirst();
                }
            }
            LogTail = string.Join(Environment.NewLine, lines);
        }
        catch (IOException ex)
        {
            LogTail = $"(failed to read log: {ex.Message})";
        }
    }
}
