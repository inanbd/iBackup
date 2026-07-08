using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Application.Features.Auth;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Devices;

/// <summary>
/// DELETE /api/devices/{id} — soft-deletes a device and revokes all of its sessions.
/// Backed-up data from the device stays available for restore.
/// </summary>
public sealed record DeleteDeviceCommand(Guid DeviceId) : IRequest;

internal sealed class DeleteDeviceValidator : AbstractValidator<DeleteDeviceCommand>
{
    public DeleteDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
    }
}

internal sealed class DeleteDeviceHandler : IRequestHandler<DeleteDeviceCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public DeleteDeviceHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task Handle(DeleteDeviceCommand request, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.Devices
            SET IsDeleted = 1, IsActive = 0
            WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql)
            .With("@Id", request.DeviceId)
            .With("@UserId", _currentUser.UserId);

        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw AppException.NotFound("Device not found.");
        }

        await RefreshTokenData.RevokeAllAsync(connection, _currentUser.UserId, request.DeviceId, ct);
        await _audit.LogAsync(_currentUser.UserId, request.DeviceId, "device.deleted", null, _currentUser.IpAddress, ct);
    }
}
