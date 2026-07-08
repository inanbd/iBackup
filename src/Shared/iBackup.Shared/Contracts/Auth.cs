namespace iBackup.Shared.Contracts;

/// <summary>Basic information a client reports about the device it runs on.</summary>
public sealed record DeviceInfo(string DeviceName, string OperatingSystem, string ClientVersion);

/// <summary>Request to create a new user account.</summary>
public sealed record RegisterUserRequest(string Email, string Password, string DisplayName);

public sealed record RegisterUserResponse(Guid UserId, string Email);

/// <summary>
/// Login request. When <paramref name="Device"/> is supplied the server registers
/// (or re-activates) the device and binds the issued refresh token to it.
/// </summary>
public sealed record LoginRequest(string Email, string Password, DeviceInfo? Device = null, Guid? DeviceId = null);

public sealed record AuthTokensResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid DeviceId,
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record RefreshTokenRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);
