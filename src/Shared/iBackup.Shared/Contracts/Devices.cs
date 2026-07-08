namespace iBackup.Shared.Contracts;

public sealed record DeviceDto(
    Guid Id,
    string DeviceName,
    string OperatingSystem,
    string ClientVersion,
    DateTime? LastBackupAtUtc,
    DateTime? LastOnlineAtUtc,
    string? IpAddress,
    DateTime RegisteredAtUtc,
    bool IsActive,
    bool IsCurrentDevice);

/// <summary>Rename and/or enable/disable a device.</summary>
public sealed record UpdateDeviceRequest(Guid DeviceId, string? DeviceName, bool? IsActive);
