using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Folders;

/// <summary>GET /api/folders — backup folders of the authenticated user (optionally one device's).</summary>
public sealed record GetFoldersQuery(Guid? DeviceId) : IRequest<IReadOnlyList<BackupFolderDto>>;

internal sealed class GetFoldersHandler : IRequestHandler<GetFoldersQuery, IReadOnlyList<BackupFolderDto>>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;

    public GetFoldersHandler(ISqlConnectionFactory connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<BackupFolderDto>> Handle(GetFoldersQuery request, CancellationToken ct)
    {
        var sql = $"""
            SELECT {FolderData.SelectColumns}
            FROM dbo.BackupFolders
            WHERE UserId = @UserId AND IsDeleted = 0
              AND (@DeviceId IS NULL OR DeviceId = @DeviceId)
            ORDER BY Priority DESC, FolderPath;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql)
            .With("@UserId", _currentUser.UserId)
            .With("@DeviceId", request.DeviceId);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var folders = new List<BackupFolderDto>();
        while (await reader.ReadAsync(ct))
        {
            folders.Add(FolderData.Map(reader));
        }
        return folders;
    }
}
