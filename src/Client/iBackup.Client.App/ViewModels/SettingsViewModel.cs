using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Configuration;
using iBackup.Shared;
using Microsoft.Extensions.Options;

namespace iBackup.Client.App.ViewModels;

/// <summary>
/// Settings page: engine tuning applied to the running instance.
/// (Values reset to defaults on restart unless changed in configuration.)
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ClientOptions _options;

    [ObservableProperty] private int _chunkSizeMb;
    [ObservableProperty] private int _parallelFileUploads;
    [ObservableProperty] private int _parallelChunkUploads;
    [ObservableProperty] private CompressionMethod _compression;
    [ObservableProperty] private int _maxRetryAttempts;
    [ObservableProperty] private string _dataDirectory = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    public Array CompressionMethods { get; } = Enum.GetValues<CompressionMethod>();

    public SettingsViewModel(IOptions<ClientOptions> options)
    {
        _options = options.Value;
        ChunkSizeMb = _options.ChunkSizeBytes / (1024 * 1024);
        ParallelFileUploads = _options.ParallelFileUploads;
        ParallelChunkUploads = _options.ParallelChunkUploads;
        Compression = _options.Compression;
        MaxRetryAttempts = _options.MaxRetryAttempts;
        DataDirectory = _options.DataDirectory;
    }

    [RelayCommand]
    private void Apply()
    {
        _options.ChunkSizeBytes = Math.Clamp(ChunkSizeMb, 1, 512) * 1024 * 1024;
        _options.ParallelFileUploads = Math.Clamp(ParallelFileUploads, 1, 16);
        _options.ParallelChunkUploads = Math.Clamp(ParallelChunkUploads, 1, 16);
        _options.Compression = Compression;
        _options.MaxRetryAttempts = Math.Clamp(MaxRetryAttempts, 1, 10);
        StatusText = "Settings applied to the running engine.";
    }
}
