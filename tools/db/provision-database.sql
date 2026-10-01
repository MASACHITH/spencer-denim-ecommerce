-- Database provisioning (doc 08 §1.4 / §10.1, decision N12). The database keeps the name
-- SpencerDenim_Dev deliberately: it is an identifier in every connection string, not brand.
--
-- Creates the named database with the project's mandated collation. Run this ONCE per environment
-- (local dev DB, staging, production) against the target SQL Server instance, BEFORE the first
-- `dotnet ef database update` / `Database.Migrate()` against it.
--
-- Why this exists as a separate script rather than something EF Core does: the collation must be
-- set at CREATE DATABASE time and never altered afterwards (ALTER DATABASE COLLATE is disruptive
-- once there is data). EF Core migrations deliberately never call UseCollation for this reason —
-- see the remarks on SpencerDenim.Infrastructure.Persistence.AppDbContext. Ephemeral integration-test
-- databases are handled separately and automatically by SqlServerDatabaseFixture, which needs no
-- copy of this script.
--
-- Usage — DbName is REQUIRED. sqlcmd has no reliable way to give it a script-level default that a
-- -v override can actually win against (a :setvar in the script always wins over -v), so it must
-- always be passed on the command line:
--   sqlcmd -S <server>[,<port>] -U <user> -P "<password>" -v DbName="SpencerDenim_Dev" -i tools\db\provision-database.sql
--   (or the equivalent with Trusted_Connection / -E for Windows auth)

IF DB_ID(N'$(DbName)') IS NULL
BEGIN
    PRINT N'Creating database [$(DbName)] with collation Latin1_General_100_CI_AS_SC...';
    EXEC(N'CREATE DATABASE [$(DbName)] COLLATE Latin1_General_100_CI_AS_SC;');
END
ELSE
BEGIN
    DECLARE @ExistingCollation nvarchar(128) = CAST(DATABASEPROPERTYEX(N'$(DbName)', 'Collation') AS nvarchar(128));
    IF @ExistingCollation <> N'Latin1_General_100_CI_AS_SC'
        THROW 50000, 'Database exists with the wrong collation. This must be fixed deliberately, not silently — see doc 08 N12.', 1;

    PRINT N'Database [$(DbName)] already exists with the correct collation. Nothing to do.';
END
GO
