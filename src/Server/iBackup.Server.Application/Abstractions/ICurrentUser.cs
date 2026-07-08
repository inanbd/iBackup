namespace iBackup.Server.Application.Abstractions;

/// <summary>Identity of the caller, resolved from the validated JWT by the API layer.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    /// <summary>Device the access token was issued to, when present in the token.</summary>
    Guid? DeviceId { get; }
    string? IpAddress { get; }
}
