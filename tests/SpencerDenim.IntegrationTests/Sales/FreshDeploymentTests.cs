using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
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

    /// <summary>Doc 08 §9's sample catalogue, which only the demo-catalogue test asks for.</summary>
    private const int DemoProducts = 45;

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

            // **And `catalog.SizeGuides`, which is the only one that matters.** Every other seeded
            // reference table has a unique index, so a duplicate there is an exception somebody sees.
            // This one has only `PK_SizeGuides` and a non-unique `IX_SizeGuides_ImageAssetId`, so two
            // instances both finding "Women's apparel" missing both insert it and *nothing errors* —
            // the next pass's `ToDictionaryAsync(guide => guide.Name, …)` throws, one startup later, on
            // data that is already wrong. It is the failure mode that made the lock necessary, and
            // asserting the counts above without this one would have passed straight through it.
            Assert.Equal(
                CatalogReferenceData.SizeGuides.Count,
                await db.SizeGuides.CountAsync(cancellationToken));
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
    /// **A host that cannot get the seed lock says it is waiting, not that the database is broken.**
    /// </summary>
    /// <remarks>
    /// <b>This is the contention fallback, and it was unreachable.</b> The acquire built a raw
    /// <c>DbCommand</c> with <c>@LockTimeout = 60000</c> and never set <c>CommandTimeout</c>, so it took
    /// ADO's 30 s default: the client gave up at 32 s with "Execution Timeout Expired", SQL Server never
    /// reached its own timeout, and the exception — not being a unique violation — landed in the broad
    /// catch. The host logged a 20-line stack trace saying "cannot take an order" and telling an operator
    /// to run <c>dotnet ef database update</c> on a database that was fine and fully migrated, and
    /// <c>LogLockBusy</c> never fired once. That is exactly the "Error on a healthy startup" the lock was
    /// introduced to remove.
    /// <para>
    /// Here the lock is held by a session this test owns, for longer than the host's patience, and the
    /// assertion is on what the host <i>says</i>: not ready, which is true, with a reason that does not
    /// blame the database. Then the lock is released and the host gets there by itself on the retry timer.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_host_that_cannot_get_the_seed_lock_says_it_is_waiting_and_then_recovers()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        // **A session of this test's own, holding the lock the seeder wants — and `Pooling=false`, which
        // is not a detail.** `sp_getapplock` with owner `Session` is released when the session ends, and
        // `SqlConnection.Close()` on a *pooled* connection does not end the session: it returns it to the
        // pool, and the reset that would release the lock is sent on the next use of that connection, not
        // on close. The first version of this test closed a pooled holder and then waited ninety seconds
        // for a lock that was still held by an idle pooled session. Worth knowing outside the test, too:
        // anything holding one of these locks must end its session, not merely close its connection.
        var holderConnectionString = new SqlConnectionStringBuilder(deployment.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        await using var holder = new SqlConnection(holderConnectionString);
        await holder.OpenAsync(cancellationToken);

        await using (var take = holder.CreateCommand())
        {
            take.CommandText =
                "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'SpencerDenim:ReferenceDataSeed', "
                + "@LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";

            Assert.True(
                Convert.ToInt32(await take.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 0,
                "this test could not take the lock it is about");
        }

        await using var factory = new ApiFactory(deployment.ConnectionString);
        using var client = factory.CreateClient();

        // **Three waits of five seconds is the host's patience**, so it has moved on to the retry timer by
        // now and readiness says what it is waiting for.
        var reason = await ReadinessReasonAsync(client, cancellationToken);

        Assert.NotNull(reason);
        Assert.Contains("still establishing the reference data", reason, StringComparison.Ordinal);

        // The two sentences that were wrong: it must not blame the database or name a migration.
        Assert.DoesNotContain("cannot take an order", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet ef database update", reason, StringComparison.Ordinal);

        // Nothing was seeded, because nothing could be: the lock did its job.
        using (var scope = deployment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.Districts.CountAsync(cancellationToken));
        }

        // And now the holder lets go, and the host gets there on its own.
        await holder.CloseAsync();

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

        using var seeded = deployment.Services.CreateScope();
        Assert.Equal(
            Districts,
            await seeded.ServiceProvider.GetRequiredService<AppDbContext>().Districts
                .CountAsync(cancellationToken));
    }

    /// <summary>
    /// **The seed lock is free while the demo catalogue is being seeded**, and this host does not claim to
    /// be ready while still holding it.
    /// </summary>
    /// <remarks>
    /// <b>Both of those were the other way round, and together they are why the contention path was
    /// reachable at all.</b> The demo catalogue ran <i>inside</i> the lock, before the release: 45
    /// products, 480 variants and 149 media assets, measured at about eleven seconds on a fresh database
    /// with the placeholder generator switched off — and with <c>Media__PublicRoot</c> configured, as
    /// development and staging have it, 149 images are drawn in there too. So a second instance on a
    /// developer's machine really would wait on it, which is a scenario the lock was never meant to cover:
    /// the lock exists because <c>catalog.SizeGuides</c> has no unique index on <c>Name</c>, and the demo
    /// catalogue is gated behind two flags no multi-instance deployment sets. <c>state.Established()</c>
    /// was also called before the demo phase, so the host announced itself ready while holding the lock.
    /// <para>
    /// The assertion is an ordering, taken from outside: once the reference data is in and the demo
    /// catalogue is still being written, the lock must be takeable. Before the fix it was held for that
    /// whole window.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_seed_lock_is_released_before_the_demo_catalogue_is_written()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await using var factory = new ApiFactory(
            deployment.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?>
            {
                ["Seed:RunOnStartup"] = "true",
                ["Seed:IncludeDemoCatalogue"] = "true",
            });

        // `CreateClient()` does not return until every hosted service's StartAsync has, and the demo
        // phase is inside it — so the host is started on a task and watched from here.
        var starting = Task.Run(() => factory.CreateClient(), cancellationToken);

        var probeConnectionString = new SqlConnectionStringBuilder(deployment.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        var sawTheWindow = false;
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (DateTime.UtcNow < deadline)
        {
            bool referenceDataComplete;
            int products;

            using (var scope = deployment.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // **A row of `ops.Settings`, not `ref.Districts`, and the difference is the whole test.**
                // The districts are committed early in `RunReferenceDataAsync` and the lock is correctly
                // still held for the steps after them — the first version of this watched for districts
                // and reported a held lock that was being held exactly as intended. So the marker has to
                // be whatever that phase writes *last*.
                //
                // **It used to be the settings count and that stopped being true.** The versioned
                // default-role permission step (#122) now runs after the settings seeder and commits the
                // `Seed.Version` row, so `Settings.Count() == SettingCatalog.All.Count` is true only for
                // the microseconds between the two saves and a 100 ms poll never sees it — this test
                // failed with "the demo catalogue never took long enough to observe", and that is what it
                // was telling us. The marker is now that last row itself, which is both later and exact.
                referenceDataComplete = await db.Settings
                    .AnyAsync(setting => setting.Key == SettingKeys.Seed.Version, cancellationToken);

                products = await db.Products.CountAsync(cancellationToken);
            }

            // The window: reference data complete, demo catalogue not finished.
            if (referenceDataComplete && products < DemoProducts && !starting.IsCompleted)
            {
                // Given a couple of seconds, because the release is the statement after the one that
                // committed the settings: a sub-millisecond gap, against a demo phase of about eleven
                // seconds. If it is still held after this, it is held for the demo.
                Assert.True(
                    await Eventually(() => LockIsFreeAsync(probeConnectionString, cancellationToken)),
                    "the seed lock was still held while the demo catalogue was being written");

                sawTheWindow = true;
                break;
            }

            if (starting.IsCompleted)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        using var client = await starting;

        Assert.True(sawTheWindow, "the demo catalogue never took long enough to observe; test needs rework");

        // It finished the job as well as ordering it correctly.
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using var seeded = deployment.Services.CreateScope();
        Assert.Equal(
            DemoProducts,
            await seeded.ServiceProvider.GetRequiredService<AppDbContext>().Products
                .CountAsync(cancellationToken));
    }

    /// <summary>Polls a condition for two seconds, so a statement-sized gap is not read as a held lock.</summary>
    private static async Task<bool> Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }

        return false;
    }

    /// <summary>
    /// Whether the seed lock can be taken right now, on a session of its own that ends immediately.
    /// </summary>
    /// <remarks>
    /// <c>@LockTimeout = 0</c>, so it answers rather than waits, and <c>Pooling=false</c> so closing the
    /// connection really ends the session and releases what it took — a pooled close would leave this
    /// probe holding the lock it just tested for.
    /// </remarks>
    private static async Task<bool> LockIsFreeAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'SpencerDenim:ReferenceDataSeed', "
            + "@LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";

        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        return result >= 0;
    }

    /// <summary>
    /// The <c>referencedata</c> check's description once readiness has settled on an answer, or null
    /// while it is still deciding.
    /// </summary>
    /// <remarks>
    /// Polled, because the host answers the first probe before the seeder has finished waiting and the
    /// reason at that moment is "not established yet" rather than the one under test. The deadline is
    /// generous: three waits of five seconds plus the jitter between them, plus a retry cycle.
    /// </remarks>
    private static async Task<string?> ReadinessReasonAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(
                new Uri("/health/ready", UriKind.Relative),
                cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

            var description = body.GetProperty("checks").EnumerateArray()
                .Where(check => check.GetProperty("name").GetString() == "referencedata")
                .Select(check => check.GetProperty("description").GetString())
                .FirstOrDefault();

            if (description is not null && description.Contains("waiting", StringComparison.Ordinal))
            {
                return description;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }
}
