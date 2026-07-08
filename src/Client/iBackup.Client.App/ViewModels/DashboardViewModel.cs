using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.App.Views;
using iBackup.Client.Core.Api;
using iBackup.Client.Core.Engine;
using iBackup.Shared.Contracts;

namespace iBackup.Client.App.ViewModels;

public partial class DashboardViewModel : ObservableObject, IRefreshable
{
    private readonly BackupApiClient _api;
    private readonly BackupEngine _engine;
    private readonly UploadEngine _uploads;

    [ObservableProperty] private string _storageUsedText = "-";
    [ObservableProperty] private string _quotaText = "-";
    [ObservableProperty] private double _storagePercent;
    [ObservableProperty] private string _lastBackupText = "Never";
    [ObservableProperty] private string _currentStatusText = "Idle";
    [ObservableProperty] private string _nextRunText = "-";
    [ObservableProperty] private string _uploadSpeedText = "0 B/s";
    [ObservableProperty] private int _queueLength;
    [ObservableProperty] private int _filesUploadedToday;
    [ObservableProperty] private int _registeredDevices;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<ActivityItemDto> RecentActivity { get; } = [];
    public ObservableCollection<BackupHistoryItemDto> RecentJobs { get; } = [];

    private long _speedWindowBytes;
    private DateTime _speedWindowStart = DateTime.UtcNow;

    public DashboardViewModel(BackupApiClient api, BackupEngine engine, UploadEngine uploads)
    {
        _api = api;
        _engine = engine;
        _uploads = uploads;
        _engine.StatusChanged += OnEngineStatus;
        _uploads.Progress += OnUploadProgress;
    }

    public void RefreshCommandIfIdle()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    [RelayCommand]
    private void RunBackupNow() => _engine.RunNow();

    [RelayCommand]
    private void PauseBackups() => _engine.Pause();

    [RelayCommand]
    private void ResumeBackups() => _engine.Resume();

    [RelayCommand]
    private void CancelBackup() => _engine.CancelCurrentBackup();

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var dashboard = await _api.GetDashboardAsync(CancellationToken.None);
            StorageUsedText = Converters.BytesToText.Convert(dashboard.StorageUsedBytes, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!.ToString()!;
            QuotaText = Converters.BytesToText.Convert(dashboard.QuotaBytes, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!.ToString()!;
            StoragePercent = dashboard.QuotaBytes > 0 ? dashboard.StorageUsedBytes * 100.0 / dashboard.QuotaBytes : 0;
            LastBackupText = dashboard.LastBackupAtUtc?.ToLocalTime().ToString("g") ?? "Never";
            FilesUploadedToday = dashboard.FilesUploadedToday;
            RegisteredDevices = dashboard.RegisteredDevices;

            RecentActivity.Clear();
            foreach (var item in dashboard.RecentActivity)
            {
                RecentActivity.Add(item);
            }
            RecentJobs.Clear();
            foreach (var job in dashboard.RecentJobs)
            {
                RecentJobs.Add(job);
            }
        }
        catch (Exception ex)
        {
            CurrentStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnEngineStatus(EngineStatus status)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentStatusText = status.State switch
            {
                EngineState.Running => $"Backing up {status.CurrentFile ?? status.CurrentFolder ?? "..."}",
                EngineState.Paused => "Paused",
                EngineState.Idle => "Idle",
                _ => "Stopped"
            };
            QueueLength = status.QueueLength;
            NextRunText = status.NextScheduledRun?.ToString("g") ?? "-";
        });
    }

    private void OnUploadProgress(UploadProgress progress)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _speedWindowStart;
        _speedWindowBytes = progress.BytesUploaded;
        if (elapsed > TimeSpan.FromSeconds(2))
        {
            var speed = _speedWindowBytes / Math.Max(1, elapsed.TotalSeconds);
            _speedWindowStart = now;
            App.Current.Dispatcher.BeginInvoke(() =>
                UploadSpeedText = Converters.BytesToText.Convert((long)speed, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) + "/s");
        }
    }
}
