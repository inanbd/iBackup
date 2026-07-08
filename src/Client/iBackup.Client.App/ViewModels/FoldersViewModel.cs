using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Api;
using iBackup.Client.Core.Engine;
using iBackup.Shared;
using iBackup.Shared.Contracts;

namespace iBackup.Client.App.ViewModels;

/// <summary>Backup Sources page: add, edit, enable/disable and remove backup folders.</summary>
public partial class FoldersViewModel : ObservableObject, IRefreshable
{
    private readonly BackupApiClient _api;
    private readonly BackupEngine _engine;

    public ObservableCollection<BackupFolderDto> Folders { get; } = [];

    [ObservableProperty] private BackupFolderDto? _selectedFolder;
    [ObservableProperty] private string _newFolderPath = string.Empty;
    [ObservableProperty] private string _excludedExtensionsText = string.Empty;
    [ObservableProperty] private string _excludedFoldersText = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public FoldersViewModel(BackupApiClient api, BackupEngine engine)
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
            Folders.Clear();
            foreach (var folder in folders)
            {
                Folders.Add(folder);
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
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder to back up" };
        if (dialog.ShowDialog() == true)
        {
            NewFolderPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task Add()
    {
        if (string.IsNullOrWhiteSpace(NewFolderPath) || _engine.DeviceId is not { } deviceId)
        {
            return;
        }

        Error = null;
        try
        {
            var request = new CreateFolderRequest(
                DeviceId: deviceId,
                Path: NewFolderPath.Trim(),
                IsEnabled: true,
                Recursive: true,
                IncludeHidden: false,
                IncludeSystem: false,
                ExcludedFolders: SplitList(ExcludedFoldersText),
                ExcludedExtensions: SplitList(ExcludedExtensionsText),
                ExcludedFileNames: [],
                MaxFileSizeBytes: null,
                Priority: 0,
                Schedule: new ScheduleSettings(ScheduleType.Daily, TimeOfDay: TimeSpan.FromHours(2)),
                Retention: new RetentionSettings(RetentionMode.KeepForever),
                Compression: CompressionMethod.Zstd);

            var created = await _api.CreateFolderAsync(request, CancellationToken.None);
            Folders.Add(created);
            NewFolderPath = string.Empty;
            await _engine.ReloadFoldersAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleEnabled(BackupFolderDto? folder)
    {
        if (folder is null)
        {
            return;
        }
        await UpdateAsync(folder, isEnabled: !folder.IsEnabled);
    }

    [RelayCommand]
    private async Task Remove(BackupFolderDto? folder)
    {
        if (folder is null)
        {
            return;
        }
        try
        {
            await _api.DeleteFolderAsync(folder.Id, CancellationToken.None);
            Folders.Remove(folder);
            await _engine.ReloadFoldersAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void BackupNow(BackupFolderDto? folder)
    {
        if (folder is not null)
        {
            _engine.RunNow(folder.Id);
        }
    }

    private async Task UpdateAsync(BackupFolderDto folder, bool isEnabled)
    {
        try
        {
            var updated = await _api.UpdateFolderAsync(new UpdateFolderRequest(
                folder.Id, folder.Path, isEnabled, folder.Recursive, folder.IncludeHidden, folder.IncludeSystem,
                folder.ExcludedFolders, folder.ExcludedExtensions, folder.ExcludedFileNames,
                folder.MaxFileSizeBytes, folder.Priority, folder.Schedule, folder.Retention, folder.Compression),
                CancellationToken.None);

            var index = Folders.IndexOf(folder);
            if (index >= 0)
            {
                Folders[index] = updated;
            }
            await _engine.ReloadFoldersAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private static IReadOnlyList<string> SplitList(string text)
        => text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
