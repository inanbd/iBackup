using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace iBackup.Client.Core.Engine;

/// <summary>A file discovered by a scan.</summary>
public sealed record ScannedFile(string FullPath, string RelativePath, long Size, DateTime ModifiedAtUtc);

/// <summary>
/// Enumerates a backup folder applying its filter rules. Streams results
/// (IAsyncEnumerable) so scanning millions of files never builds a giant list.
/// </summary>
public sealed class FileScanner
{
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(ILogger<FileScanner> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<ScannedFile> ScanAsync(
        BackupFolderDto folder,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(folder.Path))
        {
            _logger.LogWarning("Backup folder {Path} does not exist; skipping scan", folder.Path);
            yield break;
        }

        var root = Path.GetFullPath(folder.Path);
        var excludedFolders = new HashSet<string>(folder.ExcludedFolders, StringComparer.OrdinalIgnoreCase);
        var excludedExtensions = new HashSet<string>(
            folder.ExcludedExtensions.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);
        var excludedNames = new HashSet<string>(folder.ExcludedFileNames, StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = directories.Pop();

            IEnumerable<string> subdirectories = [];
            IEnumerable<string> files = [];
            try
            {
                if (folder.Recursive)
                {
                    subdirectories = Directory.EnumerateDirectories(directory, "*", options);
                }
                files = Directory.EnumerateFiles(directory, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                _logger.LogWarning(ex, "Cannot enumerate {Directory}", directory);
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (excludedFolders.Contains(name))
                {
                    continue;
                }
                if (!ShouldIncludeByAttributes(subdirectory, folder.IncludeHidden, folder.IncludeSystem))
                {
                    continue;
                }
                directories.Push(subdirectory);
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(file);
                if (excludedNames.Contains(fileName) || excludedExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }
                if (!ShouldIncludeByAttributes(file, folder.IncludeHidden, folder.IncludeSystem))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (folder.MaxFileSizeBytes is { } maxSize && info.Length > maxSize)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                yield return new ScannedFile(file, relativePath, info.Length, info.LastWriteTimeUtc);

                // Yield to the scheduler periodically on giant directories.
                await Task.Yield();
            }
        }
    }

    private static bool ShouldIncludeByAttributes(string path, bool includeHidden, bool includeSystem)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception)
        {
            return false;
        }

        if (!includeHidden && attributes.HasFlag(FileAttributes.Hidden))
        {
            return false;
        }
        if (!includeSystem && attributes.HasFlag(FileAttributes.System))
        {
            return false;
        }
        return true;
    }
}
