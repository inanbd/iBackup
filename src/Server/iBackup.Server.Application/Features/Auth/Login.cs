using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Auth;

/// <summary>
/// POST /api/auth/login — verifies credentials, registers/refreshes the calling device
/// and issues an access + refresh token pair bound to that device.
/// </summary>
public sealed record LoginCommand(string Email, string Password, DeviceInfo? Device, Guid? DeviceId)
    : IRequest<AuthTokensResponse>;

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        When(x => x.Device is not null, () =>
        {
            RuleFor(x => x.Device!.DeviceName).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Device!.OperatingSystem).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Device!.ClientVersion).NotEmpty().MaximumLength(50);
        });
    }
}

internal sealed class LoginHandler : IRequestHandler<LoginCommand, AuthTokensResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _tokens;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IIpAccessControl _ipAccess;

    public LoginHandler(
        ISqlConnectionFactory connections,
        IPasswordHasher passwordHasher,
        IJwtTokenService tokens,
        IAuditLogger audit,
        ICurrentUser currentUser,
        IIpAccessControl ipAccess)
    {
        _connections = connections;
        _passwordHasher = passwordHasher;
        _tokens = tokens;
        _audit = audit;
        _currentUser = currentUser;
        _ipAccess = ipAccess;
    }

    public async Task<AuthTokensResponse> Handle(LoginCommand request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var ip = _currentUser.IpAddress;

        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string userSql = """
            SELECT Id, PasswordHash, DisplayName, IsActive
            FROM dbo.Users
            WHERE Email = @Email;
            """;

        Guid userId = Guid.Empty;
        string? passwordHash = null;
        string displayName = string.Empty;
        var isActive = false;

        await using (var command = Sql.Command(connection, userSql).With("@Email", email))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                userId = reader.GetGuid(0);
                passwordHash = reader.GetString(1);
                displayName = reader.GetString(2);
                isActive = reader.GetBoolean(3);
            }
        }

        if (passwordHash is null || !_passwordHasher.Verify(request.Password, passwordHash))
        {
            await _audit.LogAsync(passwordHash is null ? null : userId, null, "auth.login_failed", $"Failed login for {email}", ip, ct);
            await _ipAccess.RecordLoginAttemptAsync(new LoginAttemptRecord(
                email, ip, Success: false, "Invalid email or password", passwordHash is null ? null : userId, "api"), ct);
            throw AppException.Unauthorized("Invalid email or password.");
        }

        if (!isActive)
        {
            await _ipAccess.RecordLoginAttemptAsync(new LoginAttemptRecord(
                email, ip, Success: false, "Account disabled", userId, "api"), ct);
            throw AppException.Forbidden("This account is disabled.");
        }

        var deviceId = await ResolveDeviceAsync(connection, userId, request, ip, ct);

        var access = _tokens.CreateAccessToken(userId, email, deviceId);
        var refreshToken = _tokens.CreateRefreshToken();
        var refreshExpires = DateTime.UtcNow.Add(_tokens.RefreshTokenLifetime);
        await RefreshTokenData.InsertAsync(connection, userId, deviceId, _tokens.HashToken(refreshToken), refreshExpires, ip, ct);

        await _audit.LogAsync(userId, deviceId, "auth.login", $"Login from {ip ?? "unknown"}", ip, ct);
        await _ipAccess.RecordLoginAttemptAsync(new LoginAttemptRecord(
            email, ip, Success: true, "Login succeeded", userId, "api"), ct);

        return new AuthTokensResponse(
            userId, email, displayName, deviceId,
            access.Token, access.ExpiresAtUtc,
            refreshToken, refreshExpires);
    }

    private static async Task<Guid> ResolveDeviceAsync(
        Microsoft.Data.SqlClient.SqlConnection connection, Guid userId, LoginCommand request, string? ip, CancellationToken ct)
    {
        if (request.DeviceId is { } existingId)
        {
            const string touchSql = """
                UPDATE dbo.Devices
                SET LastOnlineAtUtc = SYSUTCDATETIME(),
                    IpAddress = @Ip,
                    ClientVersion = COALESCE(@ClientVersion, ClientVersion)
                WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0 AND IsActive = 1;
                """;
            await using var touch = Sql.Command(connection, touchSql)
                .With("@Id", existingId)
                .With("@UserId", userId)
                .With("@Ip", ip)
                .With("@ClientVersion", request.Device?.ClientVersion);
            if (await touch.ExecuteNonQueryAsync(ct) == 1)
            {
                return existingId;
            }
            throw AppException.Forbidden("This device is not registered or has been disabled.");
        }

        var device = request.Device ?? new DeviceInfo("Unknown device", "Unknown", "0.0.0");
        var deviceId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO dbo.Devices (Id, UserId, DeviceName, OperatingSystem, ClientVersion, LastOnlineAtUtc, IpAddress)
            VALUES (@Id, @UserId, @Name, @Os, @Version, SYSUTCDATETIME(), @Ip);
            """;
        await using var insert = Sql.Command(connection, insertSql)
            .With("@Id", deviceId)
            .With("@UserId", userId)
            .With("@Name", device.DeviceName)
            .With("@Os", device.OperatingSystem)
            .With("@Version", device.ClientVersion)
            .With("@Ip", ip);
        await insert.ExecuteNonQueryAsync(ct);
        return deviceId;
    }
}
