/*
    iBackup - SQL Server schema
    ---------------------------
    Idempotent: safe to run repeatedly. Creates all tables and indexes.
    Run against an empty database (e.g. CREATE DATABASE iBackup) before starting the API,
    or let the API's DbInitializer execute this script on startup (Database:InitializeOnStartup=true).
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

------------------------------------------------------------------------------
-- Users
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users
    (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Users PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        Email           NVARCHAR(256)    NOT NULL,
        PasswordHash    NVARCHAR(200)    NOT NULL,
        DisplayName     NVARCHAR(200)    NOT NULL,
        QuotaBytes      BIGINT           NOT NULL CONSTRAINT DF_Users_Quota DEFAULT (107374182400), -- 100 GB
        UsedBytes       BIGINT           NOT NULL CONSTRAINT DF_Users_Used DEFAULT (0),
        IsActive        BIT              NOT NULL CONSTRAINT DF_Users_Active DEFAULT (1),
        IsAdmin         BIT              NOT NULL CONSTRAINT DF_Users_Admin DEFAULT (0),
        CreatedAtUtc    DATETIME2(3)     NOT NULL CONSTRAINT DF_Users_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc    DATETIME2(3)     NOT NULL CONSTRAINT DF_Users_Updated DEFAULT (SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_Users_Email ON dbo.Users(Email);
END;

-- Upgrade path for databases created before the admin UI existed.
IF COL_LENGTH(N'dbo.Users', N'IsAdmin') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD IsAdmin BIT NOT NULL CONSTRAINT DF_Users_Admin DEFAULT (0);
END;

------------------------------------------------------------------------------
-- Devices
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Devices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Devices
    (
        Id               UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Devices PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_Devices_Users REFERENCES dbo.Users(Id),
        DeviceName       NVARCHAR(200)    NOT NULL,
        OperatingSystem  NVARCHAR(200)    NOT NULL,
        ClientVersion    NVARCHAR(50)     NOT NULL,
        LastBackupAtUtc  DATETIME2(3)     NULL,
        LastOnlineAtUtc  DATETIME2(3)     NULL,
        IpAddress        NVARCHAR(45)     NULL,
        RegisteredAtUtc  DATETIME2(3)     NOT NULL CONSTRAINT DF_Devices_Registered DEFAULT (SYSUTCDATETIME()),
        IsActive         BIT              NOT NULL CONSTRAINT DF_Devices_Active DEFAULT (1),
        IsDeleted        BIT              NOT NULL CONSTRAINT DF_Devices_Deleted DEFAULT (0)
    );
    CREATE INDEX IX_Devices_UserId ON dbo.Devices(UserId) INCLUDE (IsDeleted, IsActive);
END;

------------------------------------------------------------------------------
-- RefreshTokens (stores SHA-256 hash of the token, never the token itself)
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.RefreshTokens', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RefreshTokens
    (
        Id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
        UserId              UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_RefreshTokens_Users REFERENCES dbo.Users(Id),
        DeviceId            UNIQUEIDENTIFIER NULL CONSTRAINT FK_RefreshTokens_Devices REFERENCES dbo.Devices(Id),
        TokenHash           CHAR(64)         NOT NULL,
        ExpiresAtUtc        DATETIME2(3)     NOT NULL,
        CreatedAtUtc        DATETIME2(3)     NOT NULL CONSTRAINT DF_RefreshTokens_Created DEFAULT (SYSUTCDATETIME()),
        CreatedByIp         NVARCHAR(45)     NULL,
        RevokedAtUtc        DATETIME2(3)     NULL,
        ReplacedByTokenHash CHAR(64)         NULL
    );
    CREATE UNIQUE INDEX UX_RefreshTokens_TokenHash ON dbo.RefreshTokens(TokenHash);
    CREATE INDEX IX_RefreshTokens_UserId ON dbo.RefreshTokens(UserId);
END;

------------------------------------------------------------------------------
-- BackupFolders
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.BackupFolders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BackupFolders
    (
        Id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BackupFolders PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId              UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_BackupFolders_Users REFERENCES dbo.Users(Id),
        DeviceId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_BackupFolders_Devices REFERENCES dbo.Devices(Id),
        FolderPath          NVARCHAR(1024)   NOT NULL,
        IsEnabled           BIT              NOT NULL CONSTRAINT DF_BackupFolders_Enabled DEFAULT (1),
        Recursive           BIT              NOT NULL CONSTRAINT DF_BackupFolders_Recursive DEFAULT (1),
        IncludeHidden       BIT              NOT NULL CONSTRAINT DF_BackupFolders_Hidden DEFAULT (0),
        IncludeSystem       BIT              NOT NULL CONSTRAINT DF_BackupFolders_System DEFAULT (0),
        ExcludedFolders     NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_BackupFolders_ExFolders DEFAULT (N'[]'),   -- JSON string array
        ExcludedExtensions  NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_BackupFolders_ExExt DEFAULT (N'[]'),        -- JSON string array
        ExcludedFileNames   NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_BackupFolders_ExNames DEFAULT (N'[]'),      -- JSON string array
        MaxFileSizeBytes    BIGINT           NULL,
        Priority            INT              NOT NULL CONSTRAINT DF_BackupFolders_Priority DEFAULT (0),
        ScheduleType        TINYINT          NOT NULL CONSTRAINT DF_BackupFolders_SchedType DEFAULT (0),
        ScheduleIntervalMinutes INT          NULL,
        ScheduleTimeOfDayMinutes INT         NULL,   -- minutes after midnight, local device time
        ScheduleDayOfWeek   TINYINT          NULL,   -- 0 = Sunday
        ScheduleDayOfMonth  TINYINT          NULL,
        RetentionMode       TINYINT          NOT NULL CONSTRAINT DF_BackupFolders_RetMode DEFAULT (0),
        RetentionValue      INT              NULL,
        CompressionMethod   TINYINT          NOT NULL CONSTRAINT DF_BackupFolders_Compression DEFAULT (2), -- Zstd
        IsDeleted           BIT              NOT NULL CONSTRAINT DF_BackupFolders_Deleted DEFAULT (0),
        CreatedAtUtc        DATETIME2(3)     NOT NULL CONSTRAINT DF_BackupFolders_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc        DATETIME2(3)     NOT NULL CONSTRAINT DF_BackupFolders_Updated DEFAULT (SYSUTCDATETIME())
    );
    CREATE INDEX IX_BackupFolders_UserDevice ON dbo.BackupFolders(UserId, DeviceId) INCLUDE (IsDeleted, IsEnabled);
END;

------------------------------------------------------------------------------
-- BackupJobs
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.BackupJobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BackupJobs
    (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BackupJobs PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_BackupJobs_Users REFERENCES dbo.Users(Id),
        DeviceId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_BackupJobs_Devices REFERENCES dbo.Devices(Id),
        FolderId        UNIQUEIDENTIFIER NULL CONSTRAINT FK_BackupJobs_Folders REFERENCES dbo.BackupFolders(Id),
        BackupType      TINYINT          NOT NULL,
        Status          TINYINT          NOT NULL CONSTRAINT DF_BackupJobs_Status DEFAULT (0),
        StartedAtUtc    DATETIME2(3)     NOT NULL CONSTRAINT DF_BackupJobs_Started DEFAULT (SYSUTCDATETIME()),
        CompletedAtUtc  DATETIME2(3)     NULL,
        UploadedFiles   INT              NOT NULL CONSTRAINT DF_BackupJobs_Up DEFAULT (0),
        SkippedFiles    INT              NOT NULL CONSTRAINT DF_BackupJobs_Skip DEFAULT (0),
        FailedFiles     INT              NOT NULL CONSTRAINT DF_BackupJobs_Fail DEFAULT (0),
        TotalBytes      BIGINT           NOT NULL CONSTRAINT DF_BackupJobs_Bytes DEFAULT (0),
        ErrorMessage    NVARCHAR(2000)   NULL
    );
    CREATE INDEX IX_BackupJobs_UserStarted ON dbo.BackupJobs(UserId, StartedAtUtc DESC);
    CREATE INDEX IX_BackupJobs_DeviceStatus ON dbo.BackupJobs(DeviceId, Status);
END;

------------------------------------------------------------------------------
-- StorageObjects (content-addressed, deduplicated per user)
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.StorageObjects', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StorageObjects
    (
        Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_StorageObjects PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_StorageObjects_Users REFERENCES dbo.Users(Id),
        Sha256            CHAR(64)         NOT NULL,   -- hash of the original plaintext content
        OriginalSize      BIGINT           NOT NULL,
        StoredSize        BIGINT           NOT NULL,   -- bytes on disk after compression + encryption
        CompressionMethod TINYINT          NOT NULL,
        EncryptionMethod  TINYINT          NOT NULL,
        ChunkSize         INT              NOT NULL,
        StoragePath       NVARCHAR(1024)   NOT NULL,   -- relative to the storage root
        ReferenceCount    INT              NOT NULL CONSTRAINT DF_StorageObjects_Refs DEFAULT (0),
        CreatedAtUtc      DATETIME2(3)     NOT NULL CONSTRAINT DF_StorageObjects_Created DEFAULT (SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_StorageObjects_UserHash ON dbo.StorageObjects(UserId, Sha256);
END;

------------------------------------------------------------------------------
-- Files (logical files per user/device/folder)
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Files', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Files
    (
        Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Files PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId         UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_Files_Users REFERENCES dbo.Users(Id),
        DeviceId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_Files_Devices REFERENCES dbo.Devices(Id),
        FolderId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_Files_Folders REFERENCES dbo.BackupFolders(Id),
        RelativePath   NVARCHAR(1024)   NOT NULL,
        FileName       NVARCHAR(255)    NOT NULL,
        RelativePathHash AS CONVERT(BINARY(32), HASHBYTES('SHA2_256', LOWER(RelativePath))) PERSISTED,
        IsDeleted      BIT              NOT NULL CONSTRAINT DF_Files_Deleted DEFAULT (0),
        DeletedAtUtc   DATETIME2(3)     NULL,
        CurrentVersionId UNIQUEIDENTIFIER NULL,
        CreatedAtUtc   DATETIME2(3)     NOT NULL CONSTRAINT DF_Files_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc   DATETIME2(3)     NOT NULL CONSTRAINT DF_Files_Updated DEFAULT (SYSUTCDATETIME())
    );
    -- Path lookups use the fixed-size hash column so the index stays small even
    -- with millions of rows and 1024-char paths.
    CREATE UNIQUE INDEX UX_Files_Identity ON dbo.Files(UserId, DeviceId, FolderId, RelativePathHash);
    CREATE INDEX IX_Files_Folder ON dbo.Files(FolderId) INCLUDE (IsDeleted);
END;

------------------------------------------------------------------------------
-- FileVersions
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.FileVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileVersions
    (
        Id               UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FileVersions PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        FileId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_FileVersions_Files REFERENCES dbo.Files(Id),
        StorageObjectId  UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_FileVersions_Objects REFERENCES dbo.StorageObjects(Id),
        BackupJobId      UNIQUEIDENTIFIER NULL CONSTRAINT FK_FileVersions_Jobs REFERENCES dbo.BackupJobs(Id),
        VersionNumber    INT              NOT NULL,
        OriginalSize     BIGINT           NOT NULL,
        EncryptedSize    BIGINT           NOT NULL,
        Sha256           CHAR(64)         NOT NULL,
        FileModifiedAtUtc DATETIME2(3)    NOT NULL,
        BackedUpAtUtc    DATETIME2(3)     NOT NULL CONSTRAINT DF_FileVersions_Backed DEFAULT (SYSUTCDATETIME()),
        Status           TINYINT          NOT NULL CONSTRAINT DF_FileVersions_Status DEFAULT (1)
    );
    CREATE UNIQUE INDEX UX_FileVersions_FileVersion ON dbo.FileVersions(FileId, VersionNumber);
    CREATE INDEX IX_FileVersions_StorageObject ON dbo.FileVersions(StorageObjectId);
    CREATE INDEX IX_FileVersions_BackedUp ON dbo.FileVersions(FileId, BackedUpAtUtc DESC);
END;

------------------------------------------------------------------------------
-- UploadSessions + UploadChunks (resumable chunked uploads)
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.UploadSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UploadSessions
    (
        Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_UploadSessions PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_UploadSessions_Users REFERENCES dbo.Users(Id),
        DeviceId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_UploadSessions_Devices REFERENCES dbo.Devices(Id),
        FolderId          UNIQUEIDENTIFIER NOT NULL,
        BackupJobId       UNIQUEIDENTIFIER NULL,
        RelativePath      NVARCHAR(1024)   NOT NULL,
        FileName          NVARCHAR(255)    NOT NULL,
        Sha256            CHAR(64)         NOT NULL,
        OriginalSize      BIGINT           NOT NULL,
        EncryptedSize     BIGINT           NOT NULL,
        FileModifiedAtUtc DATETIME2(3)     NOT NULL,
        CompressionMethod TINYINT          NOT NULL,
        EncryptionMethod  TINYINT          NOT NULL,
        ChunkSize         INT              NOT NULL,
        TotalChunks       INT              NOT NULL,
        ReceivedChunks    INT              NOT NULL CONSTRAINT DF_UploadSessions_Recv DEFAULT (0),
        Status            TINYINT          NOT NULL CONSTRAINT DF_UploadSessions_Status DEFAULT (0),
        CreatedAtUtc      DATETIME2(3)     NOT NULL CONSTRAINT DF_UploadSessions_Created DEFAULT (SYSUTCDATETIME()),
        LastActivityAtUtc DATETIME2(3)     NOT NULL CONSTRAINT DF_UploadSessions_Activity DEFAULT (SYSUTCDATETIME()),
        CompletedAtUtc    DATETIME2(3)     NULL
    );
    CREATE INDEX IX_UploadSessions_User ON dbo.UploadSessions(UserId, Status);
    CREATE INDEX IX_UploadSessions_Activity ON dbo.UploadSessions(Status, LastActivityAtUtc);
END;

IF OBJECT_ID(N'dbo.UploadChunks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UploadChunks
    (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UploadChunks PRIMARY KEY,
        UploadSessionId UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_UploadChunks_Sessions REFERENCES dbo.UploadSessions(Id) ON DELETE CASCADE,
        ChunkIndex      INT              NOT NULL,
        SizeBytes       BIGINT           NOT NULL,
        Sha256          CHAR(64)         NOT NULL,
        UploadedAtUtc   DATETIME2(3)     NOT NULL CONSTRAINT DF_UploadChunks_Uploaded DEFAULT (SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_UploadChunks_SessionIndex ON dbo.UploadChunks(UploadSessionId, ChunkIndex);
END;

------------------------------------------------------------------------------
-- AuditLogs
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLogs
    (
        Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY,
        UserId       UNIQUEIDENTIFIER NULL,
        DeviceId     UNIQUEIDENTIFIER NULL,
        Action       NVARCHAR(100)    NOT NULL,
        Details      NVARCHAR(2000)   NULL,
        IpAddress    NVARCHAR(45)     NULL,
        CreatedAtUtc DATETIME2(3)     NOT NULL CONSTRAINT DF_AuditLogs_Created DEFAULT (SYSUTCDATETIME())
    );
    CREATE INDEX IX_AuditLogs_UserCreated ON dbo.AuditLogs(UserId, CreatedAtUtc DESC);
END;

------------------------------------------------------------------------------
-- RestoreRequests
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.RestoreRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RestoreRequests
    (
        Id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RestoreRequests PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_RestoreRequests_Users REFERENCES dbo.Users(Id),
        DeviceId     UNIQUEIDENTIFIER NULL,
        ItemCount    INT              NOT NULL,
        TotalBytes   BIGINT           NOT NULL CONSTRAINT DF_RestoreRequests_Bytes DEFAULT (0),
        CreatedAtUtc DATETIME2(3)     NOT NULL CONSTRAINT DF_RestoreRequests_Created DEFAULT (SYSUTCDATETIME())
    );
    CREATE INDEX IX_RestoreRequests_User ON dbo.RestoreRequests(UserId, CreatedAtUtc DESC);
END;

------------------------------------------------------------------------------
-- ApplicationSettings
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ApplicationSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ApplicationSettings
    (
        SettingKey   NVARCHAR(100)  NOT NULL CONSTRAINT PK_ApplicationSettings PRIMARY KEY,
        SettingValue NVARCHAR(MAX)  NOT NULL,
        UpdatedAtUtc DATETIME2(3)   NOT NULL CONSTRAINT DF_ApplicationSettings_Updated DEFAULT (SYSUTCDATETIME())
    );
END;
