namespace iBackup.Shared.Contracts;

/// <summary>Starts a backup job for a device (optionally scoped to one folder).</summary>
public sealed record StartBackupRequest(Guid DeviceId, Guid? FolderId, BackupType Type);

public sealed record StartBackupResponse(Guid BackupJobId);

/// <summary>
/// Announces a file that is about to be uploaded. The server answers with either a
/// deduplication hit (no bytes needed) or an upload session to push chunks into.
/// Also used for resuming: chunk indexes already stored are returned.
/// </summary>
public sealed record BeginFileUploadRequest(
    Guid BackupJobId,
    Guid FolderId,
    string RelativePath,
    string FileName,
    string Sha256,
    long OriginalSize,
    long EncryptedSize,
    DateTime FileModifiedAtUtc,
    CompressionMethod Compression,
    EncryptionMethod Encryption,
    int ChunkSize,
    int TotalChunks);

public sealed record BeginFileUploadResponse(
    bool Deduplicated,
    Guid? FileVersionId,
    Guid? UploadSessionId,
    int ChunkSize,
    int TotalChunks,
    IReadOnlyList<int> ReceivedChunkIndexes);

/// <summary>Result of uploading one chunk.</summary>
public sealed record ChunkUploadResponse(
    Guid UploadSessionId,
    int ChunkIndex,
    bool Received,
    // True when this chunk completed the file and the server assembled it.
    bool FileCompleted,
    Guid? FileVersionId);

/// <summary>Records that a file was deleted locally (incremental backups keep the history).</summary>
public sealed record DeleteFileRequest(Guid BackupJobId, Guid FolderId, string RelativePath);

/// <summary>Records that a file was renamed/moved locally without content changes.</summary>
public sealed record RenameFileRequest(Guid BackupJobId, Guid FolderId, string OldRelativePath, string NewRelativePath, string NewFileName);

/// <summary>Marks a backup job finished and stores its statistics.</summary>
public sealed record FinishBackupRequest(
    Guid BackupJobId,
    BackupJobStatus Status,
    int UploadedFiles,
    int SkippedFiles,
    int FailedFiles,
    long TotalBytes,
    string? ErrorMessage);

public sealed record BackupHistoryItemDto(
    Guid BackupJobId,
    Guid DeviceId,
    string DeviceName,
    Guid? FolderId,
    string? FolderPath,
    BackupType Type,
    BackupJobStatus Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    double? DurationSeconds,
    int UploadedFiles,
    int SkippedFiles,
    int FailedFiles,
    long TotalBytes,
    string? ErrorMessage);
