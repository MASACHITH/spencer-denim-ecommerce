using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests;

/// <summary>
/// Issue #163 and doc 08 §1.1: every database runs with <c>READ_COMMITTED_SNAPSHOT ON</c>, set by
/// <c>tools/db/provision-database.sql</c>, and the customer lookups no longer wait for somebody else's
/// uncommitted registration.
/// </summary>
/// <remarks>
/// <b>Three kinds of test, because "the option is on" is not the same claim as "the defect is gone".</b>
/// The first checks the option on the databases the suite runs on, which is what makes every other SQL
/// test represent the live database. The second reproduces the tester's probe from #163 against the real
/// <see cref="CustomerUserLookup"/>: one registration held open and uncommitted, then lookups for an
/// unknown and a staff address, which used to wait for it. The rest drive the script's handling of a
/// database that already exists, since <c>SpencerDenim_Dev</c> and any environment provisioned before
/// #163 are exactly that.
/// <para>
/// <b>The probe runs on a fresh database, never the shared fixture's.</b> The wait only happens while the
/// users table is small enough that SQL Server scans it; once the suite has registered a few hundred
/// customers into the fixture's database the plan seeks, and the probe would pass with the option off.
/// </para>
/// </remarks>
public sealed class ReadCommittedSnapshotTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>
    /// How long a lookup may take while the registration is held open. The tester's probe waited the full
    /// 3 s timeout before #163; an unblocked lookup on an empty database takes milliseconds.
    /// </summary>
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_shared_test_database_and_a_fresh_deployment_both_have_read_committed_snapshot_on()
    {
        sqlServer.SkipIfUnavailable();

        Assert.True(
            await IsReadCommittedSnapshotOnAsync(sqlServer.ConnectionString, sqlServer.DatabaseName),
            $"{sqlServer.DatabaseName} has READ_COMMITTED_SNAPSHOT OFF; the suite no longer represents the live database (doc 08 §1.1).");

        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token, migrate: false);
        var name = new SqlConnectionStringBuilder(deployment.ConnectionString).InitialCatalog;

        Assert.True(
            await IsReadCommittedSnapshotOnAsync(sqlServer.ConnectionString, name),
            $"{name} has READ_COMMITTED_SNAPSHOT OFF.");
    }

    [Fact]
    public async Task A_customer_lookup_does_not_wait_for_a_registration_that_has_not_committed()
    {
        sqlServer.SkipIfUnavailable();

        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        await using var factory = new ApiFactory(deployment.ConnectionString);

        var staff = $"rcsi-staff-{Guid.NewGuid():N}@example.com";
        await CreateUserAsync(factory, staff, UserType.Staff);

        // The registration that has inserted its row and not committed: exactly what a concurrent
        // POST /api/account/register holds open between UserManager.CreateAsync and its commit.
        // FreshDeployment's context has no retrying execution strategy, which is what lets it hold a
        // transaction open across statements the way the registration store's own unit of work does.
        await using var holder = deployment.Services.CreateAsyncScope();
        var heldContext = holder.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var held = await heldContext.Database.BeginTransactionAsync(Token);
        var pending = $"rcsi-pending-{Guid.NewGuid():N}@example.com";
        heldContext.Users.Add(new AppUser
        {
            UserName = pending,
            NormalizedUserName = pending.ToUpperInvariant(),
            Email = pending,
            NormalizedEmail = pending.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            UserType = UserType.Customer,
            FirstName = "Probe",
            LastName = "Pending",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await heldContext.SaveChangesAsync(Token);

        var unknown = $"rcsi-nobody-{Guid.NewGuid():N}@example.com";

        await using var scope = factory.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<CustomerUserLookup>();

        Assert.Null(await WithinBudgetAsync("FindCustomerByEmailAsync(unknown)", ct => lookup.FindCustomerByEmailAsync(unknown, ct)));
        Assert.Null(await WithinBudgetAsync("FindCustomerByEmailAsync(staff)", ct => lookup.FindCustomerByEmailAsync(staff, ct)));
        Assert.Equal(
            CustomerUserLookup.EmailOwner.Nobody,
            await WithinBudgetAsync("ClassifyAsync(unknown)", ct => lookup.ClassifyAsync(unknown, ct)));
        Assert.Equal(
            CustomerUserLookup.EmailOwner.Staff,
            await WithinBudgetAsync("ClassifyAsync(staff)", ct => lookup.ClassifyAsync(staff, ct)));

        await held.RollbackAsync(Token);
    }

    [Fact]
    public async Task The_script_turns_the_option_on_for_an_existing_database_nobody_is_connected_to()
    {
        sqlServer.SkipIfUnavailable();

        await using var database = await PreSnapshotDatabase.CreateAsync(sqlServer.ConnectionString);

        await TestDatabaseProvisioner.ProvisionAsync(sqlServer.ConnectionString, database.Name, Token);

        Assert.True(await IsReadCommittedSnapshotOnAsync(sqlServer.ConnectionString, database.Name));

        // And a second run is a no-op rather than an error.
        await TestDatabaseProvisioner.ProvisionAsync(sqlServer.ConnectionString, database.Name, Token);
        Assert.True(await IsReadCommittedSnapshotOnAsync(sqlServer.ConnectionString, database.Name));
    }

    [Fact]
    public async Task The_script_refuses_rather_than_disconnects_when_an_existing_database_is_in_use_and_says_what_to_run()
    {
        sqlServer.SkipIfUnavailable();

        await using var database = await PreSnapshotDatabase.CreateAsync(sqlServer.ConnectionString);

        // An idle connection is enough: SQL Server refuses the change while any other is open.
        await using var inUse = new SqlConnection(database.ConnectionString);
        await inUse.OpenAsync(Token);

        var refusal = await Assert.ThrowsAsync<SqlException>(
            () => TestDatabaseProvisioner.ProvisionAsync(sqlServer.ConnectionString, database.Name, Token));

        Assert.Equal(50001, refusal.Number);
        Assert.Contains(
            $"ALTER DATABASE [{database.Name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE",
            refusal.Message,
            StringComparison.Ordinal);

        // The open connection survived, and the option was left alone.
        Assert.Equal(System.Data.ConnectionState.Open, inUse.State);
        await using (var ping = inUse.CreateCommand())
        {
            ping.CommandText = "SELECT 1";
            Assert.Equal(1, await ping.ExecuteScalarAsync(Token));
        }

        Assert.False(await IsReadCommittedSnapshotOnAsync(sqlServer.ConnectionString, database.Name));
    }

    private static async Task<T> WithinBudgetAsync<T>(string what, Func<CancellationToken, Task<T>> lookup)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Token);
        budget.CancelAfter(LookupBudget);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            return await lookup(budget.Token);
        }
        catch (Exception exception) when (budget.IsCancellationRequested && !Token.IsCancellationRequested)
        {
            Assert.Fail(
                $"{what} was still waiting after {stopwatch.Elapsed.TotalSeconds:F1} s for a registration that had not committed "
                + $"({exception.GetType().Name}). Is READ_COMMITTED_SNAPSHOT on (#163)?");
            throw;
        }
    }

    private static async Task CreateUserAsync(ApiFactory factory, string email, UserType type)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var result = await userManager.CreateAsync(new AppUser
        {
            UserName = email,
            Email = email,
            UserType = type,
            FirstName = "Probe",
            LastName = "User",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        });

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    private static async Task<bool> IsReadCommittedSnapshotOnAsync(string anyConnectionString, string databaseName)
    {
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(anyConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("@name", databaseName);

        return await command.ExecuteScalarAsync(Token) is bool on
            ? on
            : throw new InvalidOperationException($"No database named {databaseName}.");
    }

    /// <summary>
    /// A database created the way every database was before #163 — collation, and nothing else — so the
    /// script's handling of an existing one can be driven. It is this test's own, and it drops it.
    /// </summary>
    private sealed class PreSnapshotDatabase : IAsyncDisposable
    {
        private readonly string _server;

        private PreSnapshotDatabase(string server, string name)
        {
            _server = server;
            Name = name;
            ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name, Pooling = false }.ConnectionString;
        }

        public string Name { get; }

        public string ConnectionString { get; }

        public static async Task<PreSnapshotDatabase> CreateAsync(string fixtureConnectionString)
        {
            var server = new SqlConnectionStringBuilder(fixtureConnectionString) { InitialCatalog = "master", Pooling = false }
                .ConnectionString;
            var database = new PreSnapshotDatabase(server, "SpencerDenim_Rcsi_" + Guid.NewGuid().ToString("N"));

            await database.ExecuteAsync(
                $"CREATE DATABASE [{database.Name}] COLLATE {SqlServerDatabaseFixture.Collation};",
                Token);

            // LocalDB and SQL Server ship with it off; a model database someone switched on would make
            // these tests prove nothing, so say so instead.
            Assert.False(
                await IsReadCommittedSnapshotOnAsync(server, database.Name),
                "This server creates databases with READ_COMMITTED_SNAPSHOT already on (model database?); these tests need it off to begin with.");

            return database;
        }

        public async ValueTask DisposeAsync() =>
            await ExecuteAsync(
                $"""
                IF DB_ID(N'{Name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{Name}];
                END
                """,
                CancellationToken.None);

        private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_server);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The only dynamic part is a database name generated from a GUID in this class.
            command.CommandText = sql;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
