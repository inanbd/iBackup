/*
    iBackup - stored procedures for hot paths.
    Idempotent: uses CREATE OR ALTER.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

------------------------------------------------------------------------------
-- Records a received chunk and bumps session activity, atomically.
-- Returns the new ReceivedChunks count and TotalChunks.
------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_RegisterUploadChunk
    @UploadSessionId UNIQUEIDENTIFIER,
    @ChunkIndex      INT,
    @SizeBytes       BIGINT,
    @Sha256          CHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- Idempotent: re-uploading an existing chunk overwrites its metadata.
    MERGE dbo.UploadChunks AS target
    USING (SELECT @UploadSessionId AS UploadSessionId, @ChunkIndex AS ChunkIndex) AS src
        ON target.UploadSessionId = src.UploadSessionId AND target.ChunkIndex = src.ChunkIndex
    WHEN MATCHED THEN
        UPDATE SET SizeBytes = @SizeBytes, Sha256 = @Sha256, UploadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (UploadSessionId, ChunkIndex, SizeBytes, Sha256)
        VALUES (@UploadSessionId, @ChunkIndex, @SizeBytes, @Sha256);

    UPDATE s
    SET ReceivedChunks = (SELECT COUNT(*) FROM dbo.UploadChunks c WHERE c.UploadSessionId = s.Id),
        LastActivityAtUtc = SYSUTCDATETIME()
    FROM dbo.UploadSessions s
    WHERE s.Id = @UploadSessionId;

    SELECT ReceivedChunks, TotalChunks
    FROM dbo.UploadSessions
    WHERE Id = @UploadSessionId;

    COMMIT TRANSACTION;
END;
GO

------------------------------------------------------------------------------
-- Creates the next FileVersion for a (user, device, folder, path), creating the
-- logical file row if needed, pointing at an existing storage object.
-- Used both for deduplicated uploads and for completed chunk uploads.
------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_CommitFileVersion
    @UserId          UNIQUEIDENTIFIER,
    @DeviceId        UNIQUEIDENTIFIER,
    @FolderId        UNIQUEIDENTIFIER,
    @RelativePath    NVARCHAR(1024),
    @FileName        NVARCHAR(255),
    @StorageObjectId UNIQUEIDENTIFIER,
    @BackupJobId     UNIQUEIDENTIFIER = NULL,
    @OriginalSize    BIGINT,
    @EncryptedSize   BIGINT,
    @Sha256          CHAR(64),
    @FileModifiedAtUtc DATETIME2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @FileId UNIQUEIDENTIFIER;
    DECLARE @VersionId UNIQUEIDENTIFIER = NEWID();
    DECLARE @VersionNumber INT;
    DECLARE @PathHash BINARY(32) = CONVERT(BINARY(32), HASHBYTES('SHA2_256', LOWER(@RelativePath)));

    BEGIN TRANSACTION;

    SELECT @FileId = Id
    FROM dbo.Files WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId AND DeviceId = @DeviceId AND FolderId = @FolderId
      AND RelativePathHash = @PathHash;

    IF @FileId IS NULL
    BEGIN
        SET @FileId = NEWID();
        INSERT INTO dbo.Files (Id, UserId, DeviceId, FolderId, RelativePath, FileName)
        VALUES (@FileId, @UserId, @DeviceId, @FolderId, @RelativePath, @FileName);
    END;

    SELECT @VersionNumber = ISNULL(MAX(VersionNumber), 0) + 1
    FROM dbo.FileVersions
    WHERE FileId = @FileId;

    INSERT INTO dbo.FileVersions
        (Id, FileId, StorageObjectId, BackupJobId, VersionNumber,
         OriginalSize, EncryptedSize, Sha256, FileModifiedAtUtc, Status)
    VALUES
        (@VersionId, @FileId, @StorageObjectId, @BackupJobId, @VersionNumber,
         @OriginalSize, @EncryptedSize, @Sha256, @FileModifiedAtUtc, 1);

    UPDATE dbo.Files
    SET CurrentVersionId = @VersionId,
        FileName = @FileName,
        IsDeleted = 0,
        DeletedAtUtc = NULL,
        UpdatedAtUtc = SYSUTCDATETIME()
    WHERE Id = @FileId;

    UPDATE dbo.StorageObjects
    SET ReferenceCount = ReferenceCount + 1
    WHERE Id = @StorageObjectId;

    COMMIT TRANSACTION;

    SELECT @VersionId AS FileVersionId, @FileId AS FileId, @VersionNumber AS VersionNumber;
END;
GO

------------------------------------------------------------------------------
-- Marks upload sessions abandoned when idle beyond the cutoff.
-- Returns the sessions so the caller can delete chunk files from disk.
------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_AbortAbandonedUploads
    @IdleCutoffUtc DATETIME2(3)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.UploadSessions
    SET Status = 2 -- Aborted
    OUTPUT inserted.Id, inserted.UserId
    WHERE Status = 0
      AND LastActivityAtUtc < @IdleCutoffUtc;
END;
GO

------------------------------------------------------------------------------
-- Applies a folder's retention policy: returns the versions that fell out of
-- retention (and decrements references) so the caller can remove orphaned
-- storage objects from disk. The current version of a live file is always kept.
------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ApplyRetention
    @FolderId      UNIQUEIDENTIFIER,
    @RetentionMode TINYINT,       -- 1 = last N versions, 2 = last N days, 3 = last N months
    @RetentionValue INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RetentionMode = 0 OR @RetentionValue IS NULL OR @RetentionValue <= 0
        RETURN;

    DECLARE @CutoffUtc DATETIME2(3) = NULL;
    IF @RetentionMode = 2 SET @CutoffUtc = DATEADD(DAY, -@RetentionValue, SYSUTCDATETIME());
    IF @RetentionMode = 3 SET @CutoffUtc = DATEADD(MONTH, -@RetentionValue, SYSUTCDATETIME());

    DECLARE @Expired TABLE (VersionId UNIQUEIDENTIFIER, StorageObjectId UNIQUEIDENTIFIER);

    ;WITH Ranked AS
    (
        SELECT v.Id, v.StorageObjectId, v.BackedUpAtUtc, f.CurrentVersionId,
               ROW_NUMBER() OVER (PARTITION BY v.FileId ORDER BY v.VersionNumber DESC) AS Rn
        FROM dbo.FileVersions v
        JOIN dbo.Files f ON f.Id = v.FileId
        WHERE f.FolderId = @FolderId AND v.Status = 1
    )
    INSERT INTO @Expired (VersionId, StorageObjectId)
    SELECT Id, StorageObjectId
    FROM Ranked
    WHERE (CurrentVersionId IS NULL OR Id <> CurrentVersionId)
      AND (
            (@RetentionMode = 1 AND Rn > @RetentionValue)
         OR (@RetentionMode IN (2, 3) AND BackedUpAtUtc < @CutoffUtc)
          );

    BEGIN TRANSACTION;

    UPDATE v SET v.Status = 2 -- Deleted
    FROM dbo.FileVersions v
    JOIN @Expired e ON e.VersionId = v.Id;

    UPDATE o SET o.ReferenceCount = o.ReferenceCount - refs.Cnt
    FROM dbo.StorageObjects o
    JOIN (SELECT StorageObjectId, COUNT(*) AS Cnt FROM @Expired GROUP BY StorageObjectId) refs
      ON refs.StorageObjectId = o.Id;

    COMMIT TRANSACTION;

    -- Storage objects that no longer have any references: caller deletes the files.
    SELECT o.Id, o.UserId, o.StoragePath
    FROM dbo.StorageObjects o
    WHERE o.ReferenceCount <= 0
      AND o.Id IN (SELECT DISTINCT StorageObjectId FROM @Expired);
END;
GO
