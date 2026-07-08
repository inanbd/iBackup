using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Application.Features.Auth;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Devices;

/// <summary>POST /api/devices/{id}/logout — revokes every refresh token issued to a device.</summary>
public sealed record ForceLogoutDeviceCommand(Guid DeviceId) : IRequest;

internal sealed class ForceLogoutDeviceValidator : AbstractValidator<ForceLogoutDeviceCommand>
{
    public ForceLogoutDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
    }
}

internal sealed class ForceLogoutDeviceHandler : IRequestHandler<ForceLogoutDeviceCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;

    public ForceLogoutDeviceHandler(ISqlConnectionFactory connections, ICurrentUser currentUser, IAuditLogger audit)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task Handle(ForceLogoutDeviceCommand request, CancellationToken ct)
    {
        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string sql = "SELECT 1 FROM dbo.Devices WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;";
        await using (var check = Sql.Command(connection, sql)
            .With("@Id", request.DeviceId)
            .With("@UserId", _currentUser.UserId))
        {
            if (await check.ExecuteScalarAsync(ct) is null)
            {
                throw AppException.NotFound("Device not found.");
            }
        }

        await RefreshTokenData.RevokeAllAsync(connection, _currentUser.UserId, request.DeviceId, ct);
        await _audit.LogAsync(_currentUser.UserId, request.DeviceId, "device.force_logout", null, _currentUser.IpAddress, ct);
    }
}
