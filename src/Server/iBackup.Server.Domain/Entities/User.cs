namespace iBackup.Server.Domain.Entities;

/// <summary>A registered user account.</summary>
public sealed class User
{
    public Guid Id { get; init; }
    public required string Email { get; init; }
    public required string PasswordHash { get; init; }
    public required string DisplayName { get; init; }
    public long QuotaBytes { get; init; }
    public long UsedBytes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}
