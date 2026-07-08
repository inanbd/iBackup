using System.Collections.Concurrent;
using iBackup.Client.Core.Api;
using iBackup.Client.Core.Configuration;
using iBackup.Client.Core.Notifications;
using iBackup.Client.Core.State;
using iBackup.Client.Core.Util;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace iBackup.Client.Core.Engine;

public enum EngineState
{
    Stopped,
    Idle,
    Running,
    Paused
}

public sealed record EngineStatus(
    EngineState State,
    string? CurrentFolder,
    string? CurrentFile,
    int QueueLength,
    int FilesUploaded,
    int FilesSkipped,
    int FilesFailed,
    long BytesUploaded,
    DateTime? NextScheduledRun);

/// <summary>
/// Orchestrates the whole backup lifecycle on the client:
///  - loads folder configuration from the server,
///  - watches folders (FileSystemWatcher) and marks them dirty on changes,
///  - runs scheduled and consistency-scan backups,
///  - diffs the filesystem against the local index for incremental backups,
///  - drives the <see cref="UploadEngine"/> with bounded parallelism,
///  - reports deletions and renames, and closes each job with statistics.
/// Supports pause / resume / cancel and immediate runs.
/// </summary>
public sealed class BackupEngine : IAsyncDisposable
{
    private readonly BackupApiClient _api;
    private readonly FileScanner _scanner;
    private readonly ChangeMonitor _monitor;
    private readonly UploadEngine _uploads;
    private readonly LocalStateStore _state;
    private readonly INotificationService _notifications;
    private readonly ClientOptions _options;
    private readonly ILogger<BackupEngine> _logger;

    private readonly ConcurrentDictionary<Guid, bool> _dirtyFolders = new();
    private readonly ConcurrentQueue<(Guid FolderId, string OldPath, string NewPath)> _pendingRenames = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRuns = new();

    private volatile TaskCompletionSource _pauseGate = CreateCompletedGate();
    private CancellationTokenSource? _engineCts;
    private CancellationTokenSource? _currentRunCts;
    private Task? _loop;
    private IReadOnlyList<BackupFolderDto> _folders = [];
    private DateTime _lastConsistencyScan = DateTime.MinValue;

    private volatile EngineState _stateValue = EngineState.Stopped;
    private int _filesUploaded, _filesSkipped, _filesFailed;
    private long _bytesUploaded;
    private string? _currentFolder, _currentFile;

    public event Action<EngineStatus>? StatusChanged;

    public BackupEngine(
        BackupApiClient api,
        FileScanner scanner,
        ChangeMonitor monitor,
        UploadEngine uploads,
        LocalStateStore state,
        INotificationService notifications,
        IOptions<ClientOptions> options,
        ILogger<BackupEngine> logger)
    {
        _api = api;
        _scanner = scanner;
        _monitor = monitor;
        _uploads = uploads;
        _state = state;
        _notifications = notifications;
        _options = options.Value;
        _logger = logger;
    }

    public Guid? DeviceId { get; set; }

    public EngineState State => _stateValue;

    // ------------------------------------------------------------- lifecycle

    /// <summary>Starts the engine loop (schedule checks, change processing, consistency scans).</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return;
        }

        _engineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await ReloadFoldersAsync(_engineCts.Token);

        _monitor.Changed += OnFileChanged;
        SetState(EngineState.Idle);
        _loop = Task.Run(() => RunLoopAsync(_engineCts.Token), CancellationToken.None);
        _logger.LogInformation("Backup engine started with {Count} folders", _folders.Count);
    }

    public async Task StopAsync()
    {
        _monitor.Changed -= OnFileChanged;
        _engineCts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            _loop = null;
        }
        SetState(EngineState.Stopped);
    }

    public void Pause()
    {
        if (_pauseGate.Task.IsCompleted)
        {
            _pauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetState(EngineState.Paused);
            _notifications.Notify(NotificationType.BackupPaused, "Backups paused", "Backups will not run until resumed.");
        }
    }

    public void Resume()
    {
        _pauseGate.TrySetResult();
        SetState(_currentRunCts is null ? EngineState.Idle : EngineState.Running);
    }

    /// <summary>Cancels the backup currently in progress (the engine stays running).</summary>
    public void CancelCurrentBackup() => _currentRunCts?.Cancel();

    /// <summary>Marks folders dirty so the next loop iteration backs them up immediately.</summary>
    public void RunNow(Guid? folderId = null)
    {
        foreach (var folder in _folders)
        {
            if (folderId is null || folder.Id == folderId)
            {
                _dirtyFolders[folder.Id] = true;
            }
        }
    }

    public async Task ReloadFoldersAsync(CancellationToken ct)
    {
        _folders = await _api.GetFoldersAsync(DeviceId, ct);
        _monitor.Configure(_folders);
        PublishStatus();
    }

    // ------------------------------------------------------------- main loop

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await _pauseGate.Task.WaitAsync(ct);

                var now = DateTime.Now;
                var consistencyDue = DateTime.UtcNow - _lastConsistencyScan >= _options.ConsistencyScanInterval;

                foreach (var folder in _folders.Where(f => f.IsEnabled))
                {
                    var isDirty = _dirtyFolders.TryRemove(folder.Id, out _);
                    var lastRun = _lastRuns.TryGetValue(folder.Id, out var lr) ? lr : (DateTime?)null;
                    var nextRun = BackupScheduler.GetNextRun(folder.Schedule, lastRun ?? now.AddMinutes(-1), lastRun);
                    var scheduleDue = nextRun is not null && nextRun <= now;

                    if (isDirty || scheduleDue || consistencyDue)
                    {
                        await RunFolderBackupAsync(folder, BackupType.Incremental, ct);
                        _lastRuns[folder.Id] = DateTime.Now;
                    }
                }

                if (consistencyDue)
                {
                    _lastConsistencyScan = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AuthenticationExpiredException)
            {
                _notifications.Notify(NotificationType.AuthenticationExpired,
                    "Session expired", "Please sign in again to continue backups.");
                SetState(EngineState.Idle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup engine loop iteration failed");
            }
        }
    }

    private void OnFileChanged(FileChange change)
    {
        if (change.Type == FileChangeType.Renamed && change.OldRelativePath is not null)
        {
            _pendingRenames.Enqueue((change.FolderId, change.OldRelativePath, change.RelativePath));
        }
        _dirtyFolders[change.FolderId] = true;
    }

    // ---------------------------------------------------------- backup runs

    /// <summary>Runs one backup job for a folder. Full backups ignore the local index.</summary>
    public async Task RunFolderBackupAsync(BackupFolderDto folder, BackupType type, CancellationToken ct)
    {
        if (DeviceId is not { } deviceId)
        {
            throw new InvalidOperationException("DeviceId is not set; log in first.");
        }

        await _runLock.WaitAsync(ct);
        _currentRunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = _currentRunCts.Token;

        Interlocked.Exchange(ref _filesUploaded, 0);
        Interlocked.Exchange(ref _filesSkipped, 0);
        Interlocked.Exchange(ref _filesFailed, 0);
        Interlocked.Exchange(ref _bytesUploaded, 0);
        _currentFolder = folder.Path;
        SetState(EngineState.Running);

        Guid jobId = Guid.Empty;
        try
        {
            var start = await _api.StartBackupAsync(new StartBackupRequest(deviceId, folder.Id, type), runCt);
            jobId = start.BackupJobId;
            _logger.LogInformation("Backup job {JobId} started for {Path} ({Type})", jobId, folder.Path, type);

            await ProcessRenamesAsync(folder, jobId, runCt);

            var index = type == BackupType.Full
                ? new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase)
                : await _state.GetFolderIndexAsync(folder.Id, runCt);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var uploadGate = new SemaphoreSlim(Math.Max(1, _options.ParallelFileUploads));
            var uploadTasks = new List<Task>();

            await foreach (var file in _scanner.ScanAsync(folder, runCt))
            {
                await _pauseGate.Task.WaitAsync(runCt);
                seen.Add(file.RelativePath);

                // Unchanged since the last backup? (size + mtime fast path)
                if (index.TryGetValue(file.RelativePath, out var indexed) &&
                    indexed.Size == file.Size &&
                    indexed.ModifiedTicksUtc == file.ModifiedAtUtc.Ticks)
                {
                    Interlocked.Increment(ref _filesSkipped);
                    continue;
                }

                await uploadGate.WaitAsync(runCt);
                uploadTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await UploadOneAsync(folder, file, jobId, runCt);
                    }
                    finally
                    {
                        uploadGate.Release();
                    }
                }, runCt));
            }

            await Task.WhenAll(uploadTasks);

            // Files present in the index but no longer on disk were deleted locally.
            if (type == BackupType.Incremental)
            {
                var fullIndex = await _state.GetFolderIndexAsync(folder.Id, runCt);
                foreach (var missing in fullIndex.Keys.Where(p => !seen.Contains(p)))
                {
                    runCt.ThrowIfCancellationRequested();
                    try
                    {
                        await _api.DeleteFileAsync(new DeleteFileRequest(jobId, folder.Id, missing), runCt);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Never uploaded; just drop it from the index.
                    }
                    await _state.RemoveIndexedFileAsync(folder.Id, missing, runCt);
                    _logger.LogInformation("Recorded deletion of {Path}", missing);
                }
            }

            var status = Volatile.Read(ref _filesFailed) > 0 ? BackupJobStatus.CompletedWithErrors : BackupJobStatus.Completed;
            await FinishAsync(jobId, status, null, CancellationToken.None);

            _notifications.Notify(NotificationType.BackupCompleted, "Backup completed",
                $"{folder.Path}: {_filesUploaded} uploaded, {_filesSkipped} unchanged, {_filesFailed} failed.");
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await FinishAsync(jobId, BackupJobStatus.Cancelled, "Cancelled by user", CancellationToken.None);
            _notifications.Notify(NotificationType.Info, "Backup cancelled", folder.Path);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(jobId, BackupJobStatus.Cancelled, "Client shutting down", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup of {Path} failed", folder.Path);
            await FinishAsync(jobId, BackupJobStatus.Failed, ex.Message, CancellationToken.None);
            _notifications.Notify(NotificationType.BackupFailed, "Backup failed", $"{folder.Path}: {ex.Message}");
        }
        finally
        {
            _currentFolder = null;
            _currentFile = null;
            _currentRunCts.Dispose();
            _currentRunCts = null;
            SetState(_pauseGate.Task.IsCompleted ? EngineState.Idle : EngineState.Paused);
            _runLock.Release();
        }
    }

    private async Task UploadOneAsync(BackupFolderDto folder, ScannedFile file, Guid jobId, CancellationToken ct)
    {
        _currentFile = file.RelativePath;
        PublishStatus();

        var outcome = await _uploads.UploadFileAsync(folder, file, jobId, ct);
        if (outcome.Success)
        {
            Interlocked.Increment(ref _filesUploaded);
            Interlocked.Add(ref _bytesUploaded, outcome.Deduplicated ? 0 : outcome.EncryptedSize);
            await _state.UpsertIndexedFileAsync(
                new IndexedFile(folder.Id, file.RelativePath, file.Size, file.ModifiedAtUtc.Ticks, outcome.Sha256), ct);
        }
        else
        {
            Interlocked.Increment(ref _filesFailed);
        }
        PublishStatus();
    }

    private async Task ProcessRenamesAsync(BackupFolderDto folder, Guid jobId, CancellationToken ct)
    {
        var requeue = new List<(Guid, string, string)>();
        while (_pendingRenames.TryDequeue(out var rename))
        {
            if (rename.FolderId != folder.Id)
            {
                requeue.Add(rename);
                continue;
            }

            try
            {
                await _api.RenameFileAsync(new RenameFileRequest(
                    jobId, folder.Id, rename.OldPath, rename.NewPath, Path.GetFileName(rename.NewPath)), ct);
                await _state.RenameIndexedFileAsync(folder.Id, rename.OldPath, rename.NewPath, ct);
                _logger.LogInformation("Recorded rename {Old} -> {New}", rename.OldPath, rename.NewPath);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                // Unknown to the server (or target exists): the scan diff will reconcile it.
            }
        }
        foreach (var rename in requeue)
        {
            _pendingRenames.Enqueue(rename);
        }
    }

    private async Task FinishAsync(Guid jobId, BackupJobStatus status, string? error, CancellationToken ct)
    {
        if (jobId == Guid.Empty)
        {
            return;
        }
        try
        {
            await _api.FinishBackupAsync(new FinishBackupRequest(
                jobId, status,
                Volatile.Read(ref _filesUploaded), Volatile.Read(ref _filesSkipped), Volatile.Read(ref _filesFailed),
                Volatile.Read(ref _bytesUploaded), error), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report job {JobId} completion", jobId);
        }
    }

    // --------------------------------------------------------------- status

    private void SetState(EngineState state)
    {
        _stateValue = state;
        PublishStatus();
    }

    private void PublishStatus()
    {
        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        DateTime? next = null;
        var now = DateTime.Now;
        foreach (var folder in _folders.Where(f => f.IsEnabled))
        {
            var lastRun = _lastRuns.TryGetValue(folder.Id, out var lr) ? lr : (DateTime?)null;
            var candidate = BackupScheduler.GetNextRun(folder.Schedule, now, lastRun);
            if (candidate is not null && (next is null || candidate < next))
            {
                next = candidate;
            }
        }

        handler(new EngineStatus(
            _stateValue, _currentFolder, _currentFile,
            QueueLength: _dirtyFolders.Count,
            FilesUploaded: Volatile.Read(ref _filesUploaded),
            FilesSkipped: Volatile.Read(ref _filesSkipped),
            FilesFailed: Volatile.Read(ref _filesFailed),
            BytesUploaded: Volatile.Read(ref _bytesUploaded),
            NextScheduledRun: next));
    }

    private static TaskCompletionSource CreateCompletedGate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _monitor.Dispose();
        _runLock.Dispose();
        _engineCts?.Dispose();
    }
}
