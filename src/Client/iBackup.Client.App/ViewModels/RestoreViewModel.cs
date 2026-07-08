using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Api;
using iBackup.Client.Core.Restore;
using iBackup.Shared.Contracts;

namespace iBackup.Client.App.ViewModels;

public partial class RestoreFileRow : ObservableObject
{
    public RestoreFileItemDto File { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private FileVersionDto? _selectedVersion;

    public RestoreFileRow(RestoreFileItemDto file)
    {
        File = file;
        SelectedVersion = file.Versions.FirstOrDefault();
    }

    public string Path => File.RelativePath;
    public string Status => File.IsDeleted ? "Deleted" : "Available";
    public IReadOnlyList<FileVersionDto> Versions => File.Versions;
}

/// <summary>Restore page: browse versions, pick files, restore to original or custom location.</summary>
public partial class RestoreViewModel : ObservableObject, IRefreshable
{
    private readonly BackupApiClient _api;
    private readonly RestoreService _restore;

    public ObservableCollection<RestoreFileRow> Files { get; } = [];

    [ObservableProperty] private string _searchPrefix = string.Empty;
    [ObservableProperty] private bool _includeDeleted = true;
    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public RestoreViewModel(BackupApiClient api, RestoreService restore)
    {
        _api = api;
        _restore = restore;
        _restore.Progress += p => App.Current.Dispatcher.BeginInvoke(() =>
            StatusText = p.ItemsTotal == 0 ? string.Empty : $"Restoring {p.ItemsCompleted}/{p.ItemsTotal}: {p.CurrentFile}");
    }

    public void RefreshCommandIfIdle()
    {
        if (!IsBusy)
        {
            _ = SearchAsync();
        }
    }

    [RelayCommand]
    private async Task Search() => await SearchAsync();

    private async Task SearchAsync()
    {
        IsBusy = true;
        StatusText = string.Empty;
        try
        {
            var page = await _api.GetRestoreFilesAsync(
                null, string.IsNullOrWhiteSpace(SearchPrefix) ? null : SearchPrefix,
                IncludeDeleted, page: 1, pageSize: 500, CancellationToken.None);

            Files.Clear();
            foreach (var file in page.Items)
            {
                Files.Add(new RestoreFileRow(file));
            }
            StatusText = $"{page.TotalCount} files found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Restore to folder" };
        if (dialog.ShowDialog() == true)
        {
            TargetDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Files)
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        var versionIds = Files
            .Where(f => f.IsSelected && f.SelectedVersion is not null)
            .Select(f => f.SelectedVersion!.VersionId)
            .ToList();

        if (versionIds.Count == 0)
        {
            StatusText = "Select at least one file to restore.";
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            StatusText = "Choose a target folder first.";
            return;
        }

        IsBusy = true;
        try
        {
            var restored = await _restore.RestoreAsync(versionIds, TargetDirectory, CancellationToken.None);
            StatusText = $"Restored {restored} of {versionIds.Count} files to {TargetDirectory}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
