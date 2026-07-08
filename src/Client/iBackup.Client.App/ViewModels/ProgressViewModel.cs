using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Engine;

namespace iBackup.Client.App.ViewModels;

public sealed record ProgressRow(string File, string Progress, string Chunks);

/// <summary>Live backup progress: engine state, current file, per-file chunk progress.</summary>
public partial class ProgressViewModel : ObservableObject
{
    private readonly BackupEngine _engine;

    [ObservableProperty] private string _stateText = "Idle";
    [ObservableProperty] private string _currentFolder = "-";
    [ObservableProperty] private string _currentFile = "-";
    [ObservableProperty] private int _filesUploaded;
    [ObservableProperty] private int _filesSkipped;
    [ObservableProperty] private int _filesFailed;
    [ObservableProperty] private string _bytesUploadedText = "0 B";

    public ObservableCollection<ProgressRow> ActiveUploads { get; } = [];

    public ProgressViewModel(BackupEngine engine, UploadEngine uploads)
    {
        _engine = engine;
        engine.StatusChanged += OnStatus;
        uploads.Progress += OnUploadProgress;
    }

    [RelayCommand] private void Pause() => _engine.Pause();
    [RelayCommand] private void Resume() => _engine.Resume();
    [RelayCommand] private void Cancel() => _engine.CancelCurrentBackup();
    [RelayCommand] private void RunNow() => _engine.RunNow();

    private void OnStatus(EngineStatus status)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            StateText = status.State.ToString();
            CurrentFolder = status.CurrentFolder ?? "-";
            CurrentFile = status.CurrentFile ?? "-";
            FilesUploaded = status.FilesUploaded;
            FilesSkipped = status.FilesSkipped;
            FilesFailed = status.FilesFailed;
            BytesUploadedText = Views.Converters.BytesToText
                .Convert(status.BytesUploaded, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!
                .ToString()!;
        });
    }

    private void OnUploadProgress(UploadProgress progress)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            var row = new ProgressRow(
                progress.RelativePath,
                $"{(progress.TotalBytes > 0 ? progress.BytesUploaded * 100 / progress.TotalBytes : 0)}%",
                $"{progress.ChunksCompleted}/{progress.TotalChunks}");

            var existing = ActiveUploads.FirstOrDefault(r => r.File == progress.RelativePath);
            if (existing is not null)
            {
                ActiveUploads[ActiveUploads.IndexOf(existing)] = row;
            }
            else
            {
                ActiveUploads.Insert(0, row);
                while (ActiveUploads.Count > 20)
                {
                    ActiveUploads.RemoveAt(ActiveUploads.Count - 1);
                }
            }
        });
    }
}
