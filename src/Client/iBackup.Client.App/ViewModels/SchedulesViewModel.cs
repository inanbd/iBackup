using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Api;
using iBackup.Client.Core.Engine;
using iBackup.Shared;
using iBackup.Shared.Contracts;

namespace iBackup.Client.App.ViewModels;

/// <summary>Editable schedule row for one backup folder.</summary>
public partial class ScheduleRow : ObservableObject
{
    public BackupFolderDto Folder { get; }

    [ObservableProperty] private ScheduleType _type;
    [ObservableProperty] private int _intervalMinutes = 60;
    [ObservableProperty] private string _timeOfDay = "02:00";
    [ObservableProperty] private DayOfWeek _dayOfWeek = DayOfWeek.Sunday;
    [ObservableProperty] private int _dayOfMonth = 1;

    public ScheduleRow(BackupFolderDto folder)
    {
        Folder = folder;
        Type = folder.Schedule.Type;
        IntervalMinutes = folder.Schedule.IntervalMinutes ?? 60;
        TimeOfDay = (folder.Schedule.TimeOfDay ?? TimeSpan.FromHours(2)).ToString(@"hh\:mm");
        DayOfWeek = folder.Schedule.DayOfWeek ?? DayOfWeek.Sunday;
        DayOfMonth = folder.Schedule.DayOfMonth ?? 1;
    }

    public string Path => Folder.Path;

    public ScheduleSettings ToSettings() => new(
        Type,
        Type == ScheduleType.EveryXMinutes ? Math.Max(1, IntervalMinutes) : null,
        TimeSpan.TryParse(TimeOfDay, out var t) ? t : TimeSpan.FromHours(2),
        Type == ScheduleType.Weekly ? DayOfWeek : null,
        Type == ScheduleType.Monthly ? Math.Clamp(DayOfMonth, 1, 31) : null);
}

public partial class SchedulesViewModel : ObservableObject, IRefreshable
{
    private readonly BackupApiClient _api;
    private readonly BackupEngine _engine;

    public ObservableCollection<ScheduleRow> Rows { get; } = [];
    public Array ScheduleTypes { get; } = Enum.GetValues<ScheduleType>();
    public Array DaysOfWeek { get; } = Enum.GetValues<DayOfWeek>();

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public SchedulesViewModel(BackupApiClient api, BackupEngine engine)
    {
        _api = api;
        _engine = engine;
    }

    public void RefreshCommandIfIdle()
    {
        if (!IsBusy)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private async Task Load() => await LoadAsync();

    private async Task LoadAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var folders = await _api.GetFoldersAsync(_engine.DeviceId, CancellationToken.None);
            Rows.Clear();
            foreach (var folder in folders)
            {
                Rows.Add(new ScheduleRow(folder));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Save(ScheduleRow? row)
    {
        if (row is null)
        {
            return;
        }
        Error = null;
        try
        {
            var f = row.Folder;
            await _api.UpdateFolderAsync(new UpdateFolderRequest(
                f.Id, f.Path, f.IsEnabled, f.Recursive, f.IncludeHidden, f.IncludeSystem,
                f.ExcludedFolders, f.ExcludedExtensions, f.ExcludedFileNames,
                f.MaxFileSizeBytes, f.Priority, row.ToSettings(), f.Retention, f.Compression),
                CancellationToken.None);
            await _engine.ReloadFoldersAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
