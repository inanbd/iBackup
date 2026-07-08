namespace iBackup.Shared;

/// <summary>Compression algorithm applied to file content before encryption.</summary>
public enum CompressionMethod : byte
{
    None = 0,
    Gzip = 1,
    /// <summary>Zstandard - preferred default.</summary>
    Zstd = 2
}

/// <summary>Encryption algorithm applied to file content on the client.</summary>
public enum EncryptionMethod : byte
{
    None = 0,
    Aes256Gcm = 1
}

/// <summary>Type of a backup run.</summary>
public enum BackupType : byte
{
    Full = 0,
    Incremental = 1,
    /// <summary>Reserved for future use.</summary>
    Differential = 2
}

/// <summary>Lifecycle state of a backup job / session.</summary>
public enum BackupJobStatus : byte
{
    Running = 0,
    Completed = 1,
    CompletedWithErrors = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>How a backup folder is scheduled.</summary>
public enum ScheduleType : byte
{
    Manual = 0,
    EveryXMinutes = 1,
    Hourly = 2,
    Daily = 3,
    Weekly = 4,
    Monthly = 5
}

/// <summary>Version retention policy for a backup folder.</summary>
public enum RetentionMode : byte
{
    KeepForever = 0,
    LastNVersions = 1,
    LastNDays = 2,
    LastNMonths = 3
}

/// <summary>State of a chunked upload session.</summary>
public enum UploadSessionStatus : byte
{
    Active = 0,
    Completed = 1,
    Aborted = 2
}

/// <summary>State of an individual file version on the server.</summary>
public enum FileVersionStatus : byte
{
    Uploading = 0,
    Stored = 1,
    Deleted = 2
}
