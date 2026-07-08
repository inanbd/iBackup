using iBackup.Client.Core.Api;
using iBackup.Client.Core.Compression;
using iBackup.Client.Core.Notifications;
using iBackup.Client.Core.Security;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace iBackup.Client.Core.Restore;

public sealed record RestoreProgress(int ItemsCompleted, int ItemsTotal, string CurrentFile);

/// <summary>
/// Restores file versions: downloads the encrypted object, decrypts and
/// decompresses it locally, and writes it to the original or a custom location.
/// </summary>
public sealed class RestoreService
{
    private readonly BackupApiClient _api;
    private readonly EncryptionKeyProvider _keys;
    private readonly INotificationService _notifications;
    private readonly ILogger<RestoreService> _logger;

    public event Action<RestoreProgress>? Progress;

    public RestoreService(
        BackupApiClient api,
        EncryptionKeyProvider keys,
        INotificationService notifications,
        ILogger<RestoreService> logger)
    {
        _api = api;
        _keys = keys;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Restores the given versions under <paramref name="targetRoot"/>, recreating
    /// each file's relative path. Pass the original backup folder path to restore in place.
    /// </summary>
    public async Task<int> RestoreAsync(IReadOnlyList<Guid> fileVersionIds, string targetRoot, CancellationToken ct)
    {
        var plan = await _api.CreateRestoreAsync(new CreateRestoreRequest(fileVersionIds), ct);
        var completed = 0;
        var failed = 0;

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke(new RestoreProgress(completed, plan.Items.Count, item.RelativePath));

            try
            {
                await RestoreItemAsync(item, targetRoot, ct);
                completed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Failed to restore {Path}", item.RelativePath);
            }
        }

        Progress?.Invoke(new RestoreProgress(completed, plan.Items.Count, string.Empty));
        _notifications.Notify(NotificationType.RestoreCompleted, "Restore completed",
            failed == 0 ? $"{completed} files restored." : $"{completed} restored, {failed} failed.");
        return completed;
    }

    private async Task RestoreItemAsync(RestoreItemDto item, string targetRoot, CancellationToken ct)
    {
        var targetPath = Path.GetFullPath(Path.Combine(targetRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        // The server is not trusted with paths: never let a stored path escape the target root.
        var rootFull = Path.GetFullPath(targetRoot);
        if (!targetPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetPath, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Restore path escapes the target directory: {item.RelativePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var tempEncrypted = targetPath + ".ibackup-download";
        var tempPlain = targetPath + ".ibackup-restore";
        try
        {
            // 1. Download ciphertext to disk (bounded memory for arbitrarily large files).
            await using (var download = await _api.DownloadVersionAsync(item.FileVersionId, ct))
            await using (var file = new FileStream(tempEncrypted, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                await download.CopyToAsync(file, ct);
            }

            // 2. Decrypt chunk-by-chunk.
            await using (var encrypted = new FileStream(tempEncrypted, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var compressed = new FileStream(tempPlain, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                if (item.Encryption == EncryptionMethod.Aes256Gcm)
                {
                    await ClientCrypto.DecryptAsync(encrypted, compressed, _keys.GetKey(), item.ChunkSize, ct);
                }
                else
                {
                    await encrypted.CopyToAsync(compressed, ct);
                }
            }
            File.Delete(tempEncrypted);

            // 3. Decompress into the final file.
            var compressor = CompressorFactory.Create(item.Compression);
            await using (var compressed = new FileStream(tempPlain, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                await compressor.DecompressAsync(compressed, output, ct);
            }

            _logger.LogInformation("Restored {Path} ({Size} bytes)", targetPath, item.OriginalSize);
        }
        finally
        {
            TryDelete(tempEncrypted);
            TryDelete(tempPlain);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
