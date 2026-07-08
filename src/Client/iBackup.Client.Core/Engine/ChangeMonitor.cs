using System.Collections.Concurrent;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace iBackup.Client.Core.Engine;

public enum FileChangeType
{
    CreatedOrModified,
    Deleted,
    Renamed
}

public sealed record FileChange(Guid FolderId, FileChangeType Type, string FullPath, string RelativePath, string? OldRelativePath = null);

/// <summary>
/// Watches configured backup folders with FileSystemWatcher and emits debounced
/// change events. A scheduled consistency scan (run by the engine) catches
/// anything the watcher missed - the two mechanisms together guarantee consistency.
/// </summary>
public sealed class ChangeMonitor : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    private readonly ILogger<ChangeMonitor> _logger;
    private readonly ConcurrentDictionary<Guid, FolderWatch> _watchers = new();
    private readonly ConcurrentDictionary<(Guid FolderId, string Path), DateTime> _pending = new();
    private readonly Timer _flushTimer;

    /// <summary>Raised (on a thread-pool thread) for each debounced change.</summary>
    public event Action<FileChange>? Changed;

    public ChangeMonitor(ILogger<ChangeMonitor> logger)
    {
        _logger = logger;
        _flushTimer = new Timer(_ => FlushDebounced(), null, DebounceWindow, DebounceWindow);
    }

    /// <summary>Reconciles the set of watched folders with the given configuration.</summary>
    public void Configure(IEnumerable<BackupFolderDto> folders)
    {
        var wanted = folders.Where(f => f.IsEnabled).ToDictionary(f => f.Id);

        foreach (var stale in _watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            if (_watchers.TryRemove(stale, out var watch))
            {
                watch.Dispose();
            }
        }

        foreach (var folder in wanted.Values)
        {
            _watchers.AddOrUpdate(
                folder.Id,
                _ => CreateWatch(folder),
                (_, existing) =>
                {
                    if (existing.Root.Equals(folder.Path, StringComparison.OrdinalIgnoreCase) &&
                        existing.Recursive == folder.Recursive)
                    {
                        return existing;
                    }
                    existing.Dispose();
                    return CreateWatch(folder);
                });
        }
    }

    private FolderWatch CreateWatch(BackupFolderDto folder)
    {
        var watch = new FolderWatch(folder.Id, folder.Path, folder.Recursive);
        try
        {
            watch.Watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                         NotifyFilters.LastWrite | NotifyFilters.Size;
            watch.Watcher.InternalBufferSize = 64 * 1024;
            watch.Watcher.Created += (_, e) => OnChange(folder.Id, folder.Path, e.FullPath);
            watch.Watcher.Changed += (_, e) => OnChange(folder.Id, folder.Path, e.FullPath);
            watch.Watcher.Deleted += (_, e) => OnDeleted(folder.Id, folder.Path, e.FullPath);
            watch.Watcher.Renamed += (_, e) => OnRenamed(folder.Id, folder.Path, e.OldFullPath, e.FullPath);
            watch.Watcher.Error += (_, e) =>
                _logger.LogWarning(e.GetException(), "FileSystemWatcher error for {Path}; consistency scan will reconcile", folder.Path);
            watch.Watcher.EnableRaisingEvents = true;
            _logger.LogInformation("Watching {Path} (recursive: {Recursive})", folder.Path, folder.Recursive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to watch {Path}", folder.Path);
        }
        return watch;
    }

    private void OnChange(Guid folderId, string root, string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return; // directory-level events are reconciled by the scan
        }
        _pending[(folderId, fullPath)] = DateTime.UtcNow;
    }

    private void OnDeleted(Guid folderId, string root, string fullPath)
    {
        _pending.TryRemove((folderId, fullPath), out _);
        Emit(new FileChange(folderId, FileChangeType.Deleted, fullPath, Relative(root, fullPath)));
    }

    private void OnRenamed(Guid folderId, string root, string oldFullPath, string newFullPath)
    {
        _pending.TryRemove((folderId, oldFullPath), out _);
        Emit(new FileChange(folderId, FileChangeType.Renamed, newFullPath,
            Relative(root, newFullPath), Relative(root, oldFullPath)));
    }

    private void FlushDebounced()
    {
        var cutoff = DateTime.UtcNow - DebounceWindow;
        foreach (var entry in _pending)
        {
            if (entry.Value > cutoff)
            {
                continue;
            }
            if (!_pending.TryRemove(entry.Key, out _))
            {
                continue;
            }

            var (folderId, fullPath) = entry.Key;
            if (!_watchers.TryGetValue(folderId, out var watch))
            {
                continue;
            }
            Emit(new FileChange(folderId, FileChangeType.CreatedOrModified, fullPath, Relative(watch.Root, fullPath)));
        }
    }

    private void Emit(FileChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change handler failed for {Path}", change.FullPath);
        }
    }

    private static string Relative(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public void Dispose()
    {
        _flushTimer.Dispose();
        foreach (var watch in _watchers.Values)
        {
            watch.Dispose();
        }
        _watchers.Clear();
    }

    private sealed class FolderWatch : IDisposable
    {
        public Guid FolderId { get; }
        public string Root { get; }
        public bool Recursive { get; }
        public FileSystemWatcher Watcher { get; }

        public FolderWatch(Guid folderId, string root, bool recursive)
        {
            FolderId = folderId;
            Root = root;
            Recursive = recursive;
            Watcher = new FileSystemWatcher(root) { IncludeSubdirectories = recursive };
        }

        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }
}
