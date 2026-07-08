namespace iBackup.Server.Application.Abstractions;

/// <summary>Writes audit trail entries to the AuditLogs table.</summary>
public interface IAuditLogger
{
    Task LogAsync(Guid? userId, Guid? deviceId, string action, string? details, string? ipAddress, CancellationToken ct = default);
}
