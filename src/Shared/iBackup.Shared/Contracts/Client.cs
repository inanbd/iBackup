namespace iBackup.Shared.Contracts;

/// <summary>Registers (or updates) the calling device for the authenticated user.</summary>
public sealed record RegisterClientRequest(string DeviceName, string OperatingSystem, string ClientVersion);

public sealed record RegisterClientResponse(Guid DeviceId);

/// <summary>Profile of the authenticated user.</summary>
public sealed record ProfileResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    long QuotaBytes,
    long UsedBytes,
    int DeviceCount,
    DateTime CreatedAtUtc);
