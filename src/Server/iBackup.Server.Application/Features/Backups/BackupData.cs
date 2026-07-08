using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Features.Backups;

/// <summary>ADO.NET helpers shared by the backup slices.</summary>
internal static class BackupData
{
    /// <summary>Ensures a backup job exists, belongs to the user, and is still running.</summary>
    public static async Task EnsureRunningJobAsync(SqlConnection connection, Guid jobId, Guid userId, CancellationToken ct)
    {
        const string sql = "SELECT Status FROM dbo.BackupJobs WHERE Id = @Id AND UserId = @UserId;";
        await using var command = Sql.Command(connection, sql)
            .With("@Id", jobId)
            .With("@UserId", userId);
        var status = await command.ExecuteScalarAsync(ct);
        if (status is null)
        {
            throw AppException.NotFound("Backup job not found.");
        }
        if ((byte)status != 0)
        {
            throw AppException.Conflict("Backup job is no longer running.");
        }
    }

    /// <summary>Ensures a folder exists and belongs to the user. Returns its device id.</summary>
    public static async Task<Guid> EnsureFolderAsync(SqlConnection connection, Guid folderId, Guid userId, CancellationToken ct)
    {
        const string sql = "SELECT DeviceId FROM dbo.BackupFolders WHERE Id = @Id AND UserId = @UserId AND IsDeleted = 0;";
        await using var command = Sql.Command(connection, sql)
            .With("@Id", folderId)
            .With("@UserId", userId);
        var deviceId = await command.ExecuteScalarAsync(ct);
        if (deviceId is null)
        {
            throw AppException.NotFound("Folder not found.");
        }
        return (Guid)deviceId;
    }

    /// <summary>Commits a new file version pointing at an existing storage object. Returns the version id.</summary>
    public static async Task<Guid> CommitFileVersionAsync(
        SqlConnection connection,
        Guid userId, Guid deviceId, Guid folderId,
        string relativePath, string fileName,
        Guid storageObjectId, Guid? backupJobId,
        long originalSize, long encryptedSize, string sha256, DateTime fileModifiedAtUtc,
        CancellationToken ct)
    {
        await using var command = Sql.StoredProcedure(connection, "dbo.usp_CommitFileVersion")
            .With("@UserId", userId)
            .With("@DeviceId", deviceId)
            .With("@FolderId", folderId)
            .With("@RelativePath", relativePath)
            .With("@FileName", fileName)
            .With("@StorageObjectId", storageObjectId)
            .With("@BackupJobId", backupJobId)
            .With("@OriginalSize", originalSize)
            .With("@EncryptedSize", encryptedSize)
            .With("@Sha256", sha256)
            .With("@FileModifiedAtUtc", fileModifiedAtUtc);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw AppException.Conflict("Failed to commit file version.");
        }
        return reader.GetGuid(0);
    }
}
