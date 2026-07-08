using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Devices;

/// <summary>GET /api/devices — all registered devices of the authenticated user.</summary>
public sealed record GetDevicesQuery : IRequest<IReadOnlyList<DeviceDto>>;

internal sealed class GetDevicesHandler : IRequestHandler<GetDevicesQuery, IReadOnlyList<DeviceDto>>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public GetDevicesHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<DeviceDto>> Handle(GetDevicesQuery request, CancellationToken ct)
    {
        const string sql = """
            SELECT Id, DeviceName, OperatingSystem, ClientVersion, LastBackupAtUtc,
                   LastOnlineAtUtc, IpAddress, RegisteredAtUtc, IsActive
            FROM dbo.Devices
            WHERE UserId = @UserId AND IsDeleted = 0
            ORDER BY RegisteredAtUtc;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql).With("@UserId", _currentUser.UserId);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var devices = new List<DeviceDto>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            devices.Add(new DeviceDto(
                Id: id,
                DeviceName: reader.GetString(1),
                OperatingSystem: reader.GetString(2),
                ClientVersion: reader.GetString(3),
                LastBackupAtUtc: reader.GetDateTimeOrNull(4),
                LastOnlineAtUtc: reader.GetDateTimeOrNull(5),
                IpAddress: reader.GetStringOrNull(6),
                RegisteredAtUtc: reader.GetDateTime(7),
                IsActive: reader.GetBoolean(8),
                IsCurrentDevice: _currentUser.DeviceId == id));
        }
        return devices;
    }
}
