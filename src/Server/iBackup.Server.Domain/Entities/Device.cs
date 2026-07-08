namespace iBackup.Server.Domain.Entities;

/// <summary>A client device registered under a user account.</summary>
public sealed class Device
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string DeviceName { get; init; }
    public required string OperatingSystem { get; init; }
    public required string ClientVersion { get; init; }
    public DateTime? LastBackupAtUtc { get; init; }
    public DateTime? LastOnlineAtUtc { get; init; }
    public string? IpAddress { get; init; }
    public DateTime RegisteredAtUtc { get; init; }
    public bool IsActive { get; init; }
    public bool IsDeleted { get; init; }
}
