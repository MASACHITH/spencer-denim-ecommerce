using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Issue #33's seeding against a real SQL Server: the couriers, the Shipping and Returns copy, and the two
/// product-page texts. Idempotent, never over an admin's value, and — by the version markers — never bringing
/// back a courier an admin deleted or rewriting a page an admin emptied.
/// <para>
/// <b>Each test owns a throwaway database</b> (<see cref="FreshDeployment"/>), because the marker rows and rows
/// going missing are exactly what is under test.
/// </para>
/// </summary>
public sealed class DeliveryReturnsSeedTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- couriers -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_database_gets_the_couriers_once_and_a_deleted_one_stays_deleted()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await new SalesReferenceDataSeeder(db).SeedAsync(Token);
            Assert.Equal(SalesReferenceData.Couriers.Count, counts.Couriers);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var couriers = await db.Couriers.AsNoTracking().OrderBy(courier => courier.SortOrder).ToListAsync(Token);

            Assert.Equal(SalesReferenceData.Couriers.Select(seed => seed.Name), couriers.Select(courier => courier.Name));
            foreach (var seed in SalesReferenceData.Couriers)
            {
                var row = couriers.Single(courier => courier.Name == seed.Name);
                Assert.Equal((seed.Phone, seed.TrackingUrlTemplate, seed.IsActive, seed.SortOrder), (row.Phone, row.TrackingUrlTemplate, row.IsActive, row.SortOrder));
            }

            Assert.Equal("1", await MarkerAsync(db, SettingKeys.Seed.CouriersVersion));

            // Again: nothing.
            Assert.Equal(0, (await new SalesReferenceDataSeeder(db).SeedAsync(Token)).Couriers);

            // An admin deletes Domex: the next deployment does not bring it back.
            Assert.Equal(1, await db.Couriers.Where(courier => courier.Name == "Domex").ExecuteDeleteAsync(Token));
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, (await new SalesReferenceDataSeeder(db).SeedAsync(Token)).Couriers);
            Assert.False(await db.Couriers.AnyAsync(courier => courier.Name == "Domex", Token));
            Assert.Equal(SalesReferenceData.Couriers.Count - 1, await db.Couriers.CountAsync(Token));
        }
    }

    [Fact]
    public async Task An_existing_database_keeps_its_own_couriers_untouched_and_gains_only_the_missing_ones()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        // The pre-#33 state: an admin already added Citypak with their own account's details, and "domex" in
        // lower case. No marker.
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Couriers.AddRange(
                new Courier { Name = "Citypak", Phone = "0771234567", TrackingUrlTemplate = null, IsActive = false, SortOrder = 9 },
                new Courier { Name = "domex", Phone = null, TrackingUrlTemplate = "https://example.test/t/{trackingNumber}", IsActive = true, SortOrder = 0 });
            await db.SaveChangesAsync(Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await new SalesReferenceDataSeeder(db).SeedAsync(Token);
            Assert.Equal(SalesReferenceData.Couriers.Count - 2, counts.Couriers);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var citypak = await db.Couriers.AsNoTracking().SingleAsync(courier => courier.Name == "Citypak", Token);
            Assert.Equal(("0771234567", (string?)null, false, (short)9), (citypak.Phone, citypak.TrackingUrlTemplate, citypak.IsActive, citypak.SortOrder));

            var domex = await db.Couriers.AsNoTracking().SingleAsync(courier => courier.Name == "domex", Token);
            Assert.Equal("https://example.test/t/{trackingNumber}", domex.TrackingUrlTemplate);

            Assert.Equal(SalesReferenceData.Couriers.Count, await db.Couriers.CountAsync(Token));
        }
    }

    // ---- the Shipping and Returns pages ----------------------------------------------------------------

    [Fact]
    public async Task A_fresh_database_gets_shipping_and_returns_written_and_published_and_nothing_else()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await Seeder(db).SeedAsync(Token);
            Assert.Equal(2, counts.PolicyPages);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pages = await db.ContentPages.AsNoTracking().Where(page => page.SystemKey != null).ToDictionaryAsync(page => page.SystemKey!, Token);

            Assert.Equal(PolicyPageContent.ShippingBody, pages[ContentPageKeys.Shipping].BodyHtml);
            Assert.Equal(PolicyPageContent.ReturnsBody, pages[ContentPageKeys.Returns].BodyHtml);
            Assert.True(pages[ContentPageKeys.Shipping].IsPublished);
            Assert.True(pages[ContentPageKeys.Returns].IsPublished);
            Assert.NotNull(pages[ContentPageKeys.Returns].PublishedAtUtc);

            // About, Privacy and Terms wait for the client and a PDPA review.
            foreach (var key in new[] { ContentPageKeys.About, ContentPageKeys.Privacy, ContentPageKeys.Terms })
            {
                Assert.Equal(string.Empty, pages[key].BodyHtml);
                Assert.False(pages[key].IsPublished);
            }

            Assert.Equal("1", await MarkerAsync(db, SettingKeys.Seed.PolicyPagesVersion));

            // An admin empties Returns and takes it off the shop: the next deployment leaves it that way.
            await db.ContentPages
                .Where(page => page.SystemKey == ContentPageKeys.Returns)
                .ExecuteUpdateAsync(update => update.SetProperty(page => page.BodyHtml, string.Empty).SetProperty(page => page.IsPublished, false), Token);

            Assert.Equal(0, (await Seeder(db).SeedAsync(Token)).PolicyPages);

            var returns = await db.ContentPages.AsNoTracking().SingleAsync(page => page.SystemKey == ContentPageKeys.Returns, Token);
            Assert.Equal(string.Empty, returns.BodyHtml);
            Assert.False(returns.IsPublished);
        }
    }

    [Fact]
    public async Task On_an_existing_database_a_page_an_admin_wrote_is_never_overwritten_or_published()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        // The pre-#33 state: shells seeded, no marker, and an admin's draft of Shipping (unpublished).
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Seeder(db).SeedAsync(Token);

            await db.Settings.Where(setting => setting.Key == SettingKeys.Seed.PolicyPagesVersion).ExecuteDeleteAsync(Token);
            await db.ContentPages
                .Where(page => page.SystemKey == ContentPageKeys.Shipping || page.SystemKey == ContentPageKeys.Returns)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(page => page.BodyHtml, string.Empty)
                        .SetProperty(page => page.IsPublished, false)
                        .SetProperty(page => page.PublishedAtUtc, (DateTime?)null),
                    Token);
            await db.ContentPages
                .Where(page => page.SystemKey == ContentPageKeys.Shipping)
                .ExecuteUpdateAsync(update => update.SetProperty(page => page.BodyHtml, "<p>Our own draft.</p>"), Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, (await Seeder(db).SeedAsync(Token)).PolicyPages);

            var shipping = await db.ContentPages.AsNoTracking().SingleAsync(page => page.SystemKey == ContentPageKeys.Shipping, Token);
            Assert.Equal("<p>Our own draft.</p>", shipping.BodyHtml);
            Assert.False(shipping.IsPublished);

            var returns = await db.ContentPages.AsNoTracking().SingleAsync(page => page.SystemKey == ContentPageKeys.Returns, Token);
            Assert.Equal(PolicyPageContent.ReturnsBody, returns.BodyHtml);
            Assert.True(returns.IsPublished);
        }
    }

    [Fact]
    public async Task A_soft_deleted_policy_page_is_left_alone()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Seeder(db).SeedAsync(Token);
            await db.Settings.Where(setting => setting.Key == SettingKeys.Seed.PolicyPagesVersion).ExecuteDeleteAsync(Token);

            var returns = await db.ContentPages.SingleAsync(page => page.SystemKey == ContentPageKeys.Returns, Token);
            returns.BodyHtml = string.Empty;
            returns.IsPublished = false;
            returns.SoftDelete(DateTime.UtcNow, null);
            await db.SaveChangesAsync(Token);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Seeder(db).SeedAsync(Token);

            var returns = await db.ContentPages.IgnoreQueryFilters().AsNoTracking().SingleAsync(page => page.SystemKey == ContentPageKeys.Returns, Token);
            Assert.Equal(string.Empty, returns.BodyHtml);
            Assert.False(returns.IsPublished);
        }
    }

    // ---- the product-page texts ------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_are_seeded_into_an_existing_database_once_and_a_saved_value_survives()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new SettingsSeeder(db).SeedAsync(Token);

            // A database from before #33: no Content rows.
            await db.Settings.Where(setting => setting.GroupKey == SettingKeys.Content.Group).ExecuteDeleteAsync(Token);
            db.ChangeTracker.Clear();
            Assert.Equal(new SettingsSeedCounts(2, 0), await new SettingsSeeder(db).SeedAsync(Token));
            db.ChangeTracker.Clear();

            var rows = await db.Settings.AsNoTracking().Where(setting => setting.GroupKey == SettingKeys.Content.Group).ToDictionaryAsync(row => row.Key, Token);
            Assert.Equal(ContentSettingDefinitions.DeliveryInfoSeed, rows[SettingKeys.Content.PdpDeliveryInfoHtml].Value);
            Assert.Equal(ContentSettingDefinitions.ReturnsInfoSeed, rows[SettingKeys.Content.PdpReturnsInfoHtml].Value);
            Assert.All(rows.Values, row => Assert.Equal(SettingValueType.TextHtml, row.ValueType));

            // The owner rewrites one and empties the other; the next deployment keeps both.
            await db.Settings.Where(setting => setting.Key == SettingKeys.Content.PdpDeliveryInfoHtml)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "<p>Owner's words.</p>"), Token);
            await db.Settings.Where(setting => setting.Key == SettingKeys.Content.PdpReturnsInfoHtml)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, (string?)null), Token);

            db.ChangeTracker.Clear();
            Assert.Equal(new SettingsSeedCounts(0, 0), await new SettingsSeeder(db).SeedAsync(Token));

            var after = await db.Settings.AsNoTracking().Where(setting => setting.GroupKey == SettingKeys.Content.Group).ToDictionaryAsync(row => row.Key, row => row.Value, Token);
            Assert.Equal("<p>Owner's words.</p>", after[SettingKeys.Content.PdpDeliveryInfoHtml]);
            Assert.Null(after[SettingKeys.Content.PdpReturnsInfoHtml]);
        }
    }

    private static HomeContentSeeder Seeder(AppDbContext dbContext) => new(dbContext, new SystemTestClock());

    private static Task<string?> MarkerAsync(AppDbContext dbContext, string key) =>
        dbContext.Settings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(Token);

    private sealed class SystemTestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
