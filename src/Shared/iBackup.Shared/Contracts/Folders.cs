namespace iBackup.Shared.Contracts;

/// <summary>Schedule configuration for a backup folder.</summary>
public sealed record ScheduleSettings(
    ScheduleType Type,
    int? IntervalMinutes = null,
    TimeSpan? TimeOfDay = null,
    DayOfWeek? DayOfWeek = null,
    int? DayOfMonth = null);

/// <summary>Version retention configuration for a backup folder.</summary>
public sealed record RetentionSettings(RetentionMode Mode, int? Value = null);

/// <summary>A configured backup source folder.</summary>
public sealed record BackupFolderDto(
    Guid Id,
    Guid DeviceId,
    string Path,
    bool IsEnabled,
    bool Recursive,
    bool IncludeHidden,
    bool IncludeSystem,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string> ExcludedExtensions,
    IReadOnlyList<string> ExcludedFileNames,
    long? MaxFileSizeBytes,
    int Priority,
    ScheduleSettings Schedule,
    RetentionSettings Retention,
    CompressionMethod Compression,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CreateFolderRequest(
    Guid DeviceId,
    string Path,
    bool IsEnabled,
    bool Recursive,
    bool IncludeHidden,
    bool IncludeSystem,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string> ExcludedExtensions,
    IReadOnlyList<string> ExcludedFileNames,
    long? MaxFileSizeBytes,
    int Priority,
    ScheduleSettings Schedule,
    RetentionSettings Retention,
    CompressionMethod Compression);

public sealed record UpdateFolderRequest(
    Guid Id,
    string Path,
    bool IsEnabled,
    bool Recursive,
    bool IncludeHidden,
    bool IncludeSystem,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string> ExcludedExtensions,
    IReadOnlyList<string> ExcludedFileNames,
    long? MaxFileSizeBytes,
    int Priority,
    ScheduleSettings Schedule,
    RetentionSettings Retention,
    CompressionMethod Compression);
