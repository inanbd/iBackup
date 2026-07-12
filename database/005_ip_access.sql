/*
    iBackup - IP access control + login attempt auditing.
    Idempotent: safe to run repeatedly.

    Access model is DEFAULT-DENY: an IP is allowed only if it is loopback
    (localhost) or matches a whitelist rule ('*' = all). Blacklist rules and the
    implicit default-deny block everything else. No rows are seeded, so a fresh
    install already denies every non-local IP.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

------------------------------------------------------------------------------
-- IpAccessRules: whitelist (Kind = 0) and blacklist (Kind = 1) entries.
--   IpAddress holds a normalized IP literal or '*' (match all).
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.IpAccessRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IpAccessRules
    (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_IpAccessRules PRIMARY KEY,
        IpAddress       NVARCHAR(64)     NOT NULL,
        Kind            TINYINT          NOT NULL,   -- 0 = Whitelist (allow), 1 = Blacklist (deny)
        Reason          NVARCHAR(256)    NULL,
        IsAuto          BIT              NOT NULL CONSTRAINT DF_IpAccessRules_Auto DEFAULT (0),
        CreatedByUserId UNIQUEIDENTIFIER NULL,
        CreatedAtUtc    DATETIME2(3)     NOT NULL CONSTRAINT DF_IpAccessRules_Created DEFAULT (SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_IpAccessRules_AddrKind ON dbo.IpAccessRules(IpAddress, Kind);
END;

------------------------------------------------------------------------------
-- LoginAttempts: every authentication attempt (API and admin dashboard),
-- successful or not, with the originating IP. Drives the "5 failed logins
-- auto-blacklist" rule and the admin login-attempts view.
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.LoginAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoginAttempts
    (
        Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoginAttempts PRIMARY KEY,
        Email        NVARCHAR(256)    NULL,
        IpAddress    NVARCHAR(64)     NULL,
        Success      BIT              NOT NULL,
        Reason       NVARCHAR(256)    NULL,
        UserId       UNIQUEIDENTIFIER NULL,
        Source       NVARCHAR(20)     NULL,   -- 'api' or 'admin'
        CreatedAtUtc DATETIME2(3)     NOT NULL CONSTRAINT DF_LoginAttempts_Created DEFAULT (SYSUTCDATETIME())
    );
    CREATE INDEX IX_LoginAttempts_IpTime ON dbo.LoginAttempts(IpAddress, Success, CreatedAtUtc DESC);
    CREATE INDEX IX_LoginAttempts_Time ON dbo.LoginAttempts(CreatedAtUtc DESC);
END;
