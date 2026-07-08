using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Clients;

/// <summary>
/// POST /api/client/register — registers a new device for the authenticated user
/// (or updates the metadata of the device the token was issued to).
/// </summary>
public sealed record RegisterClientCommand(string DeviceName, string OperatingSystem, string ClientVersion)
    : IRequest<RegisterClientResponse>;

internal sealed class RegisterClientValidator : AbstractValidator<RegisterClientCommand>
{
    public RegisterClientValidator()
    {
        RuleFor(x => x.DeviceName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OperatingSystem).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ClientVersion).NotEmpty().MaximumLength(50);
    }
}

internal sealed class RegisterClientHandler : IRequestHandler<RegisterClientCommand, RegisterClientResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public RegisterClientHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task<RegisterClientResponse> Handle(RegisterClientCommand request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);

        if (_currentUser.DeviceId is { } deviceId && deviceId != Guid.Empty)
        {
            const string updateSql = """
                UPDATE dbo.Devices
                SET DeviceName = @Name, OperatingSystem = @Os, ClientVersion = @Version,
                    LastOnlineAtUtc = SYSUTCDATETIME(), IpAddress = @Ip
                WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;
                """;
            await using var update = Sql.Command(connection, updateSql)
                .With("@Id", deviceId)
                .With("@UserId", _currentUser.UserId)
                .With("@Name", request.DeviceName)
                .With("@Os", request.OperatingSystem)
                .With("@Version", request.ClientVersion)
                .With("@Ip", _currentUser.IpAddress);
            if (await update.ExecuteNonQueryAsync(ct) == 1)
            {
                return new RegisterClientResponse(deviceId);
            }
        }

        var newDeviceId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO dbo.Devices (Id, UserId, DeviceName, OperatingSystem, ClientVersion, LastOnlineAtUtc, IpAddress)
            VALUES (@Id, @UserId, @Name, @Os, @Version, SYSUTCDATETIME(), @Ip);
            """;
        await using var insert = Sql.Command(connection, insertSql)
            .With("@Id", newDeviceId)
            .With("@UserId", _currentUser.UserId)
            .With("@Name", request.DeviceName)
            .With("@Os", request.OperatingSystem)
            .With("@Version", request.ClientVersion)
            .With("@Ip", _currentUser.IpAddress);
        await insert.ExecuteNonQueryAsync(ct);

        await _audit.LogAsync(_currentUser.UserId, newDeviceId, "device.registered", request.DeviceName, _currentUser.IpAddress, ct);
        return new RegisterClientResponse(newDeviceId);
    }
}
