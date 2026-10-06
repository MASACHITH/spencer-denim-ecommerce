using System.Globalization;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <b>A fresh deployment, on the documented defaults, can take an order.</b>
/// <para>
/// This is the one test in the suite that does not use the shared fixture's database, and the reason is
/// the defect it exists to catch: the fixture seeds, so every other test is looking at a shop whose
/// reference data someone put there. Doc 08 §10's deployment is three steps — create the database with
/// §1.1's collation, apply the migrations, start the API — and for W3-02 all three of them ran and left
/// <c>ref.Districts</c>, <c>sales.DeliveryMethods</c>, <c>sales.DeliveryMethodZoneRates</c> and
/// <c>sales.PaymentProviders</c> empty, because the seeder that writes them sat behind
/// <c>Seed__RunOnStartup</c>, which defaults to <c>false</c> and which nothing in the repository sets.
/// The result was an empty district dropdown, no delivery options, a <c>NoPaymentMethod</c> blocker and
/// an <c>Orders.DeliveryDistrictId NOT NULL</c> that could not be satisfied: zero orders, ever.
/// </para>
/// <para>
/// So this test builds that deployment and asserts the shop works at the end of it. It deliberately
/// leaves <c>Seed:RunOnStartup</c> at the <c>false</c> that <see cref="ApiFactory"/> forces, because a
/// test that set it would be testing the flag rather than the default.
/// </para>
/// </summary>
public sealed class FreshDeploymentTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>Doc 08 §9's nine provinces and twenty-five districts, asserted as a count.</summary>
    private const int Provinces = 9;

    private const int Districts = 25;

    [Fact]
    public async Task Migrating_an_empty_database_and_starting_the_api_leaves_a_shop_that_can_take_an_order()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        // **Step 2 of the deployment, and the premise of the whole test.** The migrations create the
        // tables and put nothing in them; if this ever stops being true — because someone moved the
        // reference data to EF `HasData`, which is what doc 08 §9 actually asks for — this assertion is
        // the one that says so, and the fix is to delete this test rather than to work around it.
        using (var beforeScope = deployment.Services.CreateScope())
        {
            var before = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(0, await before.Provinces.CountAsync(cancellationToken));
            Assert.Equal(0, await before.Districts.CountAsync(cancellationToken));
            Assert.Equal(0, await before.DeliveryMethods.CountAsync(cancellationToken));
            Assert.Equal(0, await before.DeliveryMethodZoneRates.CountAsync(cancellationToken));
            Assert.Equal(0, await before.PaymentProviders.CountAsync(cancellationToken));
        }

        // **Step 3: start the API.** Nothing else — no flag, no extra command, no init container.
        await using var factory = new ApiFactory(deployment.ConnectionString);
        _ = factory.Services; // Touching Services starts the host, which runs the hosted services.

        int colomboId;
        using (var afterScope = deployment.Services.CreateScope())
        {
            var after = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(Provinces, await after.Provinces.CountAsync(cancellationToken));
            Assert.Equal(Districts, await after.Districts.CountAsync(cancellationToken));

            // Counts rather than figures: `SalesReferenceDataTests` owns what the rows say, against doc 08
            // §9. What this test is about is whether they are there at all.
            Assert.True(await after.DeliveryMethods.AnyAsync(method => method.IsEnabled, cancellationToken));
            Assert.True(
                await after.DeliveryMethodZoneRates.AnyAsync(rate => rate.IsAvailable, cancellationToken));
            Assert.True(
                await after.PaymentProviders.AnyAsync(provider => provider.IsEnabled, cancellationToken));

            // The navigation and the settings screen are in the same phase and were empty for the same
            // reason, so they are asserted here rather than left to be found later.
            Assert.True(await after.Departments.AnyAsync(cancellationToken));
            Assert.True(await after.Settings.AnyAsync(cancellationToken));

            colomboId = await after.Districts
                .Where(district => district.Name == "Colombo")
                .Select(district => district.Id)
                .SingleAsync(cancellationToken);
        }

        // **And the shop answers.** Checkout lists the districts, prices an enabled delivery option for a
        // real one and offers a payment method, so the preconditions of doc 11 P6 that this data provides
        // are met. The bag is empty — the catalogue is, too, because the demo catalogue is correctly still
        // behind its flag — so `EmptyBag` is the only blocker left, and it is the shopper's to fix.
        //
        // The district is passed because the options are priced per zone (doc 08 §6 step 10) and an
        // unasked quote has no zone to price for; that is the page's own first move too.
        var checkout = new CheckoutApiClient(factory.CreateDefaultClient(new Uri("https://localhost")));
        var (response, session) = await checkout.GetAsync(colomboId, null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Districts, session!.Districts.Count);
        Assert.Contains(session.DeliveryOptions, option => option.IsAvailable);
        Assert.NotEmpty(session.PaymentMethods);
        Assert.DoesNotContain(
            Application.Sales.Checkout.Contracts.CheckoutBlocker.NoPaymentMethod,
            session.Blockers);
        Assert.DoesNotContain(
            Application.Sales.Checkout.Contracts.CheckoutBlocker.NoDeliveryMethod,
            session.Blockers);
    }

    /// <summary>
    /// A throwaway database created exactly as doc 08 §10.1 says to create a real one, migrated and
    /// nothing else, with a service provider over it and a drop at the end.
    /// </summary>
    private sealed class FreshDeployment : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;
        private ServiceProvider? _services;

        private FreshDeployment(
            string serverConnectionString,
            string databaseName,
            string connectionString,
            ServiceProvider services)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
            _services = services;
        }

        public string ConnectionString { get; }

        public IServiceProvider Services =>
            _services ?? throw new ObjectDisposedException(nameof(FreshDeployment));

        public static async Task<FreshDeployment> CreateAsync(
            string fixtureConnectionString,
            CancellationToken cancellationToken)
        {
            var server = new SqlConnectionStringBuilder(fixtureConnectionString) { InitialCatalog = "master" }
                .ConnectionString;

            var databaseName = "SpencerDenim_Deploy_" + Guid.NewGuid().ToString("N");
            var connectionString = new SqlConnectionStringBuilder(fixtureConnectionString)
            {
                InitialCatalog = databaseName,
            }.ConnectionString;

            // Doc 08 §1.1: the collation is fixed at CREATE DATABASE and the migrations cannot set it.
            await ExecuteAsync(
                server,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"CREATE DATABASE [{databaseName}] COLLATE {SqlServerDatabaseFixture.Collation};"),
                cancellationToken);

            await TestDatabaseMigrator.MigrateAsync(connectionString, cancellationToken);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(
                    connectionString,
                    sql => sql.MigrationsHistoryTable(
                        AppDbContext.MigrationsHistoryTable,
                        AppDbContext.MigrationsHistorySchema))
                .Options;

            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddScoped<AppDbContext>();

            return new FreshDeployment(server, databaseName, connectionString, services.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
                _services = null;
            }

            // The API under test pooled connections to this database; they would keep it in use.
            SqlConnection.ClearAllPools();

            await ExecuteAsync(
                _serverConnectionString,
                $"""
                IF DB_ID(N'{_databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_databaseName}];
                END
                """,
                CancellationToken.None);
        }

        private static async Task ExecuteAsync(
            string connectionString,
            string sql,
            CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
