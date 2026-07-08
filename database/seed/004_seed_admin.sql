/*
    iBackup - DEFAULT ADMIN SEED  (OPTIONAL, DEVELOPMENT / DEMO ONLY)
    ================================================================================
    This script creates a ready-to-use administrator account so you can open the
    /Admin dashboard without the register + bootstrap dance.

        Email:    admin@ibackup.local
        Password: Admin@iBackup2026

    SECURITY WARNING
    ----------------
    These are PUBLIC, well-known credentials committed to source control. They are
    a convenience for local development and demos only.

      * Do NOT run this against a production or internet-exposed database.
      * If you must, CHANGE THE PASSWORD immediately from the admin dashboard
        (Users page) or by re-hashing a new password with the app's hasher.

    This script is intentionally NOT run by the automatic schema initializer
    (which only picks up the top-level .sql files in the database folder, not
    this subfolder). You run it by hand:

        sqlcmd -S localhost -U sa -P '<pw>' -d iBackup -C -i database/seed/004_seed_admin.sql

    or with docker:

        docker exec -i mssql /opt/mssql-tools18/bin/sqlcmd \
            -C -S localhost -U sa -P '<pw>' -d iBackup -i /path/inside/container.sql

    Idempotent: running it again promotes the account and resets it to active,
    but does NOT reset the password (so a changed password is preserved).

    Note on encryption: the admin dashboard only needs the password (BCrypt) to
    sign in, so this seeded account works immediately. It does NOT carry the
    client-side AES key derivation, so do not use it as a backup *client* account
    without signing in through the desktop client at least once.
================================================================================ */

SET NOCOUNT ON;

DECLARE @Email        NVARCHAR(256) = N'admin@ibackup.local';
DECLARE @DisplayName  NVARCHAR(200) = N'Default Administrator';
-- BCrypt hash (work factor 12) of 'Admin@iBackup2026', produced by the app's
-- BcryptPasswordHasher and verified to round-trip through it.
DECLARE @PasswordHash NVARCHAR(200) = N'$2a$12$KNCJj6NbZOZ1uxc4J.5/suH5SXEOGG.yuoAI3DfMmpw7JPKVH6wxi';

IF EXISTS (SELECT 1 FROM dbo.Users WHERE Email = @Email)
BEGIN
    -- Already present: ensure it is an active admin, but keep any changed password.
    UPDATE dbo.Users
    SET IsAdmin = 1, IsActive = 1, UpdatedAtUtc = SYSUTCDATETIME()
    WHERE Email = @Email;

    PRINT 'Default admin already existed; promoted to active administrator (password unchanged).';
END
ELSE
BEGIN
    INSERT INTO dbo.Users (Id, Email, PasswordHash, DisplayName, IsActive, IsAdmin)
    VALUES (NEWID(), @Email, @PasswordHash, @DisplayName, 1, 1);

    PRINT 'Default admin created:  admin@ibackup.local  /  Admin@iBackup2026';
    PRINT 'CHANGE THIS PASSWORD before using outside local development.';
END;
