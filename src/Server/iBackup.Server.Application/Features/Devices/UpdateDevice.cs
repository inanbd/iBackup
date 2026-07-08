using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Server.Application.Features.Auth;
using MediatR;

namespace iBackup.Server.Application.Features.Devices;

/// <summary>PUT /api/devices — rename and/or enable/disable a device. Disabling revokes its sessions.</summary>
public sealed record UpdateDeviceCommand(Guid DeviceId, string? DeviceName, bool? IsActive) : IRequest;

internal sealed class UpdateDeviceValidator : AbstractValidator<UpdateDeviceCommand>
{
    public UpdateDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.DeviceName).MaximumLength(200);
        RuleFor(x => x)
            .Must(x => x.DeviceName is not null || x.IsActive is not null)
            .WithMessage("Nothing to update.");
    }
}

internal sealed class UpdateDeviceHandler : IRequestHandler<UpdateDeviceCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public UpdateDeviceHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task Handle(UpdateDeviceCommand request, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.Devices
            SET DeviceName = COALESCE(@Name, DeviceName),
                IsActive = COALESCE(@IsActive, IsActive)
            WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql)
            .With("@Id", request.DeviceId)
            .With("@UserId", _currentUser.UserId)
            .With("@Name", request.DeviceName)
            .With("@IsActive", request.IsActive);

        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw AppException.NotFound("Device not found.");
        }

        if (request.IsActive == false)
        {
            await RefreshTokenData.RevokeAllAsync(connection, _currentUser.UserId, request.DeviceId, ct);
        }

        await _audit.LogAsync(_currentUser.UserId, request.DeviceId, "device.updated",
            $"Name={request.DeviceName ?? "(unchanged)"}, Active={request.IsActive?.ToString() ?? "(unchanged)"}",
            _currentUser.IpAddress, ct);
    }
}
