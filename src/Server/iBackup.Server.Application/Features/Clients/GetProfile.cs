using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Clients;

/// <summary>GET /api/client/profile — profile and storage usage of the authenticated user.</summary>
public sealed record GetProfileQuery : IRequest<ProfileResponse>;

internal sealed class GetProfileHandler : IRequestHandler<GetProfileQuery, ProfileResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public GetProfileHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<ProfileResponse> Handle(GetProfileQuery request, CancellationToken ct)
    {
        const string sql = """
            SELECT u.Id, u.Email, u.DisplayName, u.QuotaBytes, u.UsedBytes, u.CreatedAtUtc,
                   (SELECT COUNT(*) FROM dbo.Devices d WHERE d.UserId = u.Id AND d.IsDeleted = 0) AS DeviceCount
            FROM dbo.Users u
            WHERE u.Id = @UserId;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql).With("@UserId", _currentUser.UserId);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            throw AppException.NotFound("User not found.");
        }

        return new ProfileResponse(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(6),
            reader.GetDateTime(5));
    }
}
