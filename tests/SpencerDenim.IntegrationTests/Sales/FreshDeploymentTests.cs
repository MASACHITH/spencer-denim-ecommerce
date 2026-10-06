using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

        // And it says so where an orchestrator looks, not only where a shopper does.
        using var ready = await factory.CreateClient()
            .GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    /// <summary>
    /// **Readiness tells the truth about a database that is connectable but unmigrated**, which is the
    /// state the first version of this missed entirely.
    /// </summary>
    /// <remarks>
    /// The argument for letting the seeder fail without taking the host down was that readiness would be
    /// the loud signal. It was not: the only <c>ready</c>-tagged check was <c>DatabaseHealthCheck</c>,
    /// which is <c>CanConnectAsync()</c>, so a host that could not take a single order answered
    /// <c>/health/ready</c> with <c>200 Healthy</c> and an orchestrator sent it live traffic. One log line
    /// was the entire signal. Found by the round-2 tester.
    /// <para>
    /// The other half of the same trade is asserted here too: liveness and the OpenAPI document keep
    /// answering, because they are what tells an operator <i>why</i>, and taking them down with the
    /// seeder would have been the worse failure.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_connectable_database_with_no_migrations_applied_is_not_ready()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken,
            migrate: false);

        await using var factory = new ApiFactory(deployment.ConnectionString);
        using var client = factory.CreateClient();

        // **Unhealthy, and it names the reason.** `sqlserver` is Healthy here — the server answers — so a
        // readiness check built on connectivity alone is exactly what passes this state.
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());

        var checks = body.GetProperty("checks").EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("name").GetString()!,
                check => check,
                StringComparer.Ordinal);

        Assert.Equal("Healthy", checks["sqlserver"].GetProperty("status").GetString());
        Assert.Equal("Unhealthy", checks["referencedata"].GetProperty("status").GetString());
        Assert.Contains(
            "cannot take an order",
            checks["referencedata"].GetProperty("description").GetString(),
            StringComparison.Ordinal);

        // And the endpoints that explain the problem still answer, which is why the host is allowed to
        // start at all.
        using var live = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    /// <summary>
    /// **Readiness goes green on its own once the migrations are applied**, so a host that came up before
    /// its database was ready is not an outage somebody has to notice and restart.
    /// </summary>
    /// <remarks>
    /// This is the other half of refusing to throw out of <c>StartAsync</c>. Without the retry, a SQL
    /// Server thirty seconds late — or a deployment that applies migrations after starting the API — left
    /// the host permanently unready, which is a correctly-signalled outage and still an outage. The wait
    /// below is generous because the retry interval is 10 s and CI's SQL Server is slow.
    /// </remarks>
    [Fact]
    public async Task Readiness_recovers_by_itself_once_the_migrations_are_applied()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken,
            migrate: false);

        await using var factory = new ApiFactory(deployment.ConnectionString);
        using var client = factory.CreateClient();

        using (var before = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);
        }

        // The operator does what the log line told them to do, with the host still up.
        await TestDatabaseMigrator.MigrateAsync(deployment.ConnectionString, cancellationToken);

        // Polled rather than slept through: the retry interval is 10 s and CI's SQL Server is slower
        // than this machine, so a fixed wait would either be flaky or be a minute of nothing.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        HttpStatusCode status;

        while (true)
        {
            using (var after = await client.GetAsync(
                new Uri("/health/ready", UriKind.Relative),
                cancellationToken))
            {
                status = after.StatusCode;
            }

            if (status == HttpStatusCode.OK || DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// **Three instances starting at once against one fresh database all come up ready**, and none of them
    /// reports that the shop cannot take an order.
    /// </summary>
    /// <remarks>
    /// <b>This became the normal case the moment the seeder stopped being behind a flag.</b> The losers of
    /// the race hit <c>UX_Departments_Name</c> on the very first seeder, logged
    /// "Could not establish the reference data … cannot take an order", and never reached the sales
    /// reference data at all — so every multi-replica deployment, rolling restart and overlapping
    /// container restart printed the one Error this whole design depends on, on a perfectly healthy
    /// startup. A unique violation on a row whose job is to exist means somebody else put it there.
    /// </remarks>
    [Fact]
    public async Task Three_instances_starting_at_once_all_come_up_ready()
    {
        sqlServer.SkipIfUnavailable();

        const int instances = 3;

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        var factories = Enumerable.Range(0, instances)
            .Select(_ => new ApiFactory(deployment.ConnectionString))
            .ToList();

        try
        {
            // Released together, so the race is real rather than three warm starts. Touching
            // `CreateClient()` is what starts the host and runs the hosted services.
            using var gate = new SemaphoreSlim(0, instances);

            var starting = factories.Select(async factory =>
            {
                await gate.WaitAsync(cancellationToken);
                return factory.CreateClient();
            }).ToList();

            gate.Release(instances);

            var clients = await Task.WhenAll(starting);

            try
            {
                foreach (var client in clients)
                {
                    using var ready = await client.GetAsync(
                        new Uri("/health/ready", UriKind.Relative),
                        cancellationToken);

                    // The body in the message, because "Expected OK, Actual ServiceUnavailable" does not
                    // say which seed row the loser gave up on.
                    Assert.True(
                        ready.StatusCode == HttpStatusCode.OK,
                        $"An instance came up unready: {await ready.Content.ReadAsStringAsync(cancellationToken)}");
                }
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }

            // **And the database is complete, not just complete-because-the-winner-finished.** Every
            // loser has to have carried on past the row it lost and seeded the rest.
            using var scope = deployment.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(Provinces, await db.Provinces.CountAsync(cancellationToken));
            Assert.Equal(Districts, await db.Districts.CountAsync(cancellationToken));
            Assert.True(await db.DeliveryMethods.AnyAsync(method => method.IsEnabled, cancellationToken));
            Assert.True(await db.DeliveryMethodZoneRates.AnyAsync(rate => rate.IsAvailable, cancellationToken));
            Assert.True(await db.PaymentProviders.AnyAsync(provider => provider.IsEnabled, cancellationToken));

            // Nothing was inserted twice: the losers inserted what was missing, not a second copy.
            Assert.Equal(3, await db.Departments.CountAsync(cancellationToken));
        }
        finally
        {
            foreach (var factory in factories)
            {
                await factory.DisposeAsync();
            }
        }
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

        /// <param name="migrate">
        /// False to leave the database empty of tables, which is the "connectable but no migrations
        /// applied" state that `CanConnectAsync()` cannot see.
        /// </param>
        public static async Task<FreshDeployment> CreateAsync(
            string fixtureConnectionString,
            CancellationToken cancellationToken,
            bool migrate = true)
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

            if (migrate)
            {
                await TestDatabaseMigrator.MigrateAsync(connectionString, cancellationToken);
            }

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
