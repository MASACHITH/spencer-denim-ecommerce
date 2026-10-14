-- Spencer Denim: application logins and database users (W0-15, doc 08 §10.4, docs/15 §5).
--
-- Run by scripts/db-provision.sh as sa, AFTER tools/db/provision-database.sql has created the database. Idempotent:
-- re-running it re-applies the grants and resets both passwords to the values in config/app.env, which is also how
-- a password is rotated (change app.env, run db-provision.sh, redeploy).
--
-- Variables (set by the script with :setvar lines on stdin, never on a command line): DbName, AppLogin,
-- AppPassword, MigratorLogin, MigratorPassword. The script refuses passwords containing ' " ] \ ; or $.
--
--   <AppLogin>       db_datareader + db_datawriter   the API
--   <MigratorLogin>  db_owner                        the one-shot migrations bundle only
--
-- NOT applied here yet: doc 08 §10.4's `DENY UPDATE ON identity.Roles(GrantsAllPermissions)` for the app login.
-- ASP.NET Identity's RoleManager.UpdateAsync writes every column of a role, so that DENY would make any future
-- role edit through RoleManager fail in production only (tests run as sa). It belongs with the Users & Roles work
-- (W1-09), together with a column-targeted update for roles and a test that runs as the app login.

SET NOCOUNT ON;
SET XACT_ABORT ON;

USE [master];

IF SUSER_ID(N'$(AppLogin)') IS NULL
BEGIN
    PRINT N'Creating login [$(AppLogin)]...';
    EXEC(N'CREATE LOGIN [$(AppLogin)] WITH PASSWORD = N''$(AppPassword)'', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DbName)];');
END
ELSE
BEGIN
    EXEC(N'ALTER LOGIN [$(AppLogin)] WITH PASSWORD = N''$(AppPassword)'';');
END

IF SUSER_ID(N'$(MigratorLogin)') IS NULL
BEGIN
    PRINT N'Creating login [$(MigratorLogin)]...';
    EXEC(N'CREATE LOGIN [$(MigratorLogin)] WITH PASSWORD = N''$(MigratorPassword)'', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DbName)];');
END
ELSE
BEGIN
    EXEC(N'ALTER LOGIN [$(MigratorLogin)] WITH PASSWORD = N''$(MigratorPassword)'';');
END
GO

USE [$(DbName)];

IF DATABASE_PRINCIPAL_ID(N'$(AppLogin)') IS NULL
    EXEC(N'CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)];');
IF DATABASE_PRINCIPAL_ID(N'$(MigratorLogin)') IS NULL
    EXEC(N'CREATE USER [$(MigratorLogin)] FOR LOGIN [$(MigratorLogin)];');

-- ALTER ROLE ... ADD MEMBER is a no-op when the user is already a member.
ALTER ROLE [db_datareader] ADD MEMBER [$(AppLogin)];
ALTER ROLE [db_datawriter] ADD MEMBER [$(AppLogin)];
ALTER ROLE [db_owner]      ADD MEMBER [$(MigratorLogin)];

PRINT N'Logins [$(AppLogin)] (read/write) and [$(MigratorLogin)] (db_owner) are in place on [$(DbName)].';
GO
