-- Database provisioning (doc 08 §1.1 / §10.4, decision N12). The database keeps the name
-- SpencerDenim_Dev deliberately: it is an identifier in every connection string, not brand.
--
-- Creates the named database with the project's mandated collation and READ_COMMITTED_SNAPSHOT ON.
-- Run this ONCE per environment (local dev DB, staging, production) against the target SQL Server
-- instance, BEFORE the first `dotnet ef database update` / migrations bundle against it. Re-running it
-- is safe: an existing database is checked, never recreated.
--
-- Who runs it:
--   * the VPS: deploy/scripts/install.sh copies this file to /opt/server/database/<env>/scripts/ and
--     deploy/scripts/db-provision.sh pipes it to sqlcmd as sa (docs/15 §5, step 3);
--   * every SQL Server integration test database (tests/SpencerDenim.IntegrationTests/Infrastructure/
--     TestDatabaseProvisioner.cs embeds this file), so the suite runs on a database created exactly as
--     the live one is;
--   * a developer, by hand, for SpencerDenim_Dev (README, docs/13).
--
-- Why this is a script rather than something EF Core does:
--   * The collation must be set at CREATE DATABASE time and never altered afterwards (ALTER DATABASE
--     COLLATE is disruptive once there is data). EF Core migrations deliberately never call
--     UseCollation — see the remarks on SpencerDenim.Infrastructure.Persistence.AppDbContext.
--   * READ_COMMITTED_SNAPSHOT cannot be set inside a transaction, and EF Core runs each migration in
--     one. It also needs the database to itself: SQL Server refuses the change while any other
--     connection is open on the database, even an idle one.
--
-- READ_COMMITTED_SNAPSHOT (doc 08 §1.1, issue #163): a READ COMMITTED read takes the last committed
-- version of a row instead of waiting for a transaction that is changing it. Without it, a lookup that
-- scans the small users table of a new shop waits behind every registration that has not committed
-- yet, which leaked through W3-01's response floor which kind of address was asked about, and made
-- concurrent registrations deadlock. Statements that WRITE are unaffected: an UPDATE still locks the
-- rows it changes and re-checks its WHERE clause after waiting, which is what the conditional UPDATEs
-- this project uses for stock, orders and payments rely on.
--
-- Usage — DbName is REQUIRED. sqlcmd has no reliable way to give it a script-level default that a
-- -v override can actually win against (a :setvar in the script always wins over -v), so it must
-- always be passed on the command line:
--   sqlcmd -S <server>[,<port>] -U <user> -P "<password>" -v DbName="SpencerDenim_Dev" -i tools\db\provision-database.sql
--   (or the equivalent with Trusted_Connection / -E for Windows auth)
--
-- An EXISTING database without READ_COMMITTED_SNAPSHOT is switched on here only if nothing else is
-- connected to it; otherwise the script stops with the one-off command an operator runs at a quiet
-- moment (docs/15 §5, "Existing database without READ_COMMITTED_SNAPSHOT").

SET NOCOUNT ON;

IF DB_ID(N'$(DbName)') IS NULL
BEGIN
    PRINT N'Creating database [$(DbName)] with collation Latin1_General_100_CI_AS_SC...';
    EXEC(N'CREATE DATABASE [$(DbName)] COLLATE Latin1_General_100_CI_AS_SC;');

    -- Nothing can have anything of value in flight on a database created a moment ago, so a
    -- connection that raced in (an API container retrying its connection) is simply disconnected.
    PRINT N'Setting READ_COMMITTED_SNAPSHOT ON for [$(DbName)]...';
    EXEC(N'ALTER DATABASE [$(DbName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;');
END
ELSE
BEGIN
    DECLARE @ExistingCollation nvarchar(128) = CAST(DATABASEPROPERTYEX(N'$(DbName)', 'Collation') AS nvarchar(128));
    IF @ExistingCollation <> N'Latin1_General_100_CI_AS_SC'
        THROW 50000, 'Database exists with the wrong collation. This must be fixed deliberately, not silently — see doc 08 N12.', 1;

    PRINT N'Database [$(DbName)] already exists with the correct collation.';

    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(DbName)' AND is_read_committed_snapshot_on = 0)
    BEGIN
        -- NO_WAIT, never ROLLBACK IMMEDIATE, on a database that already exists: it may be a live shop
        -- with a customer's checkout in flight, and rolling that back is the operator's call to make.
        BEGIN TRY
            EXEC(N'ALTER DATABASE [$(DbName)] SET READ_COMMITTED_SNAPSHOT ON WITH NO_WAIT;');
            PRINT N'READ_COMMITTED_SNAPSHOT is now ON for [$(DbName)].';
        END TRY
        BEGIN CATCH
            DECLARE @Refusal nvarchar(2048) = CONCAT(
                N'[$(DbName)] has READ_COMMITTED_SNAPSHOT OFF and other connections are open on it (',
                ERROR_NUMBER(), N': ', ERROR_MESSAGE(), N'). Stop the API, or at a quiet moment run as sa: ',
                N'ALTER DATABASE [$(DbName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE; ',
                N'then run this script again. See docs/15 §5.');
            THROW 50001, @Refusal, 1;
        END CATCH
    END
    ELSE
    BEGIN
        PRINT N'READ_COMMITTED_SNAPSHOT is already ON for [$(DbName)]. Nothing to do.';
    END
END
GO
