namespace iBackup.Shared.Contracts;

public sealed record CurrentBackupDto(
    Guid BackupJobId,
    Guid DeviceId,
    string DeviceName,
    BackupType Type,
    DateTime StartedAtUtc);

public sealed record ActivityItemDto(
    DateTime TimestampUtc,
    string Action,
    string? Details,
    string? DeviceName);

public sealed record DashboardResponse(
    long StorageUsedBytes,
    long QuotaBytes,
    long RemainingQuotaBytes,
    DateTime? LastBackupAtUtc,
    CurrentBackupDto? CurrentBackup,
    int FilesUploadedToday,
    long BytesUploadedToday,
    int RegisteredDevices,
    int TotalFiles,
    IReadOnlyList<ActivityItemDto> RecentActivity,
    IReadOnlyList<BackupHistoryItemDto> RecentJobs);
