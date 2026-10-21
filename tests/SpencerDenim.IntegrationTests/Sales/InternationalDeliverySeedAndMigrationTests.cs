using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Owner decision 2026-10-21, on databases of their own (never the shared one):
/// <list type="bullet">
/// <item><description>
/// <b>The bank-transfer seed rule.</b> A fresh database inserts bank transfer switched off. An existing one is
/// switched off once — unless an admin has saved the row — and never again, so a Super Admin who turns it back
/// on keeps it on. Recorded by <c>Seed.PaymentProvidersVersion</c>.
/// </description></item>
/// <item><description>
/// <b>The country seed:</b> 249 countries, six regions with placeholder fees, International delivery — inserted
/// once and never overwritten.
/// </description></item>
/// <item><description>
/// <b>The migration backfill:</b> an order and a saved address that existed before the migration come out of it
/// as Sri Lanka, and the country columns are NOT NULL afterwards.
/// </description></item>
/// </list>
/// </summary>
public sealed class InternationalDeliverySeedAndMigrationTests(SqlServerDatabaseFixture sqlServer)
{
    private const string MigrationBefore = "20261020181219_MegaMenuPromoCards";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_database_seeds_bank_transfer_off_every_country_and_International_delivery()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        var counts = await SeedAsync(deployment);

        // 248: the migration itself inserts Sri Lanka, so the backfill has a key; the seeder adds the rest.
        Assert.Equal(248, counts.Countries);
        Assert.Equal(249, await CountriesAsync(deployment));
        Assert.Equal(6, counts.DeliveryRegions);
        Assert.False(counts.BankTransferSwitchedOff);

        using var scope = deployment.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(await EnabledAsync(db, PaymentProviderCodes.BankTransfer));
        Assert.False(await EnabledAsync(db, PaymentProviderCodes.PayPal));
        Assert.Equal("1", await MarkerAsync(db));

        var international = await db.DeliveryMethods.AsNoTracking().SingleAsync(method => method.Code == DeliveryMethod.InternationalCode, Token);
        Assert.True(international.IsEnabled);
        Assert.True(international.RequiresAddress);
        Assert.Empty(await db.DeliveryMethodZoneRates.AsNoTracking().Where(rate => rate.DeliveryMethodId == international.Id).ToListAsync(Token));

        var lk = await db.Countries.AsNoTracking().SingleAsync(country => country.Code == Country.SriLankaCode, Token);
        Assert.Null(lk.RegionCode);
        Assert.Equal(0, lk.SortOrder);

        // Every overseas country has a region; only the seven with no street addresses are off.
        Assert.Equal(248, await db.Countries.CountAsync(country => country.RegionCode != null, Token));
        Assert.Equal(
            ["AQ", "BV", "GS", "HM", "IO", "TF", "UM"],
            await db.Countries.Where(country => !country.IsShippingEnabled).OrderBy(country => country.Code).Select(country => country.Code).ToListAsync(Token));
        Assert.True(await db.DeliveryRegions.AllAsync(region => region.Fee > 0 && region.IsEnabled, Token));
    }

    [Fact]
    public async Task An_existing_database_has_bank_transfer_switched_off_once_and_never_again()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        await SeedAsync(deployment);

        // As a database seeded before 2026-10-21 looks: bank transfer on, never touched by an admin, no marker.
        await SetBankAsync(deployment, enabled: true, updatedByUserId: null);
        await DeleteMarkerAsync(deployment);

        var first = await SeedAsync(deployment);
        Assert.True(first.BankTransferSwitchedOff);
        Assert.False(await BankEnabledAsync(deployment));
        Assert.Equal("1", await MarkerAsync(deployment));

        // A Super Admin switches it back on: the next deployment leaves it on.
        await SetBankAsync(deployment, enabled: true, updatedByUserId: 42);
        var second = await SeedAsync(deployment);
        Assert.False(second.BankTransferSwitchedOff);
        Assert.True(await BankEnabledAsync(deployment));

        // Even if the row somehow lost its stamp, the marker alone stops a second switch-off.
        await SetBankAsync(deployment, enabled: true, updatedByUserId: null);
        var third = await SeedAsync(deployment);
        Assert.False(third.BankTransferSwitchedOff);
        Assert.True(await BankEnabledAsync(deployment));
    }

    [Fact]
    public async Task A_row_an_admin_saved_before_the_marker_existed_is_left_as_the_admin_set_it()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        await SeedAsync(deployment);

        await SetBankAsync(deployment, enabled: true, updatedByUserId: 7);
        await DeleteMarkerAsync(deployment);

        var counts = await SeedAsync(deployment);

        Assert.False(counts.BankTransferSwitchedOff);
        Assert.True(await BankEnabledAsync(deployment));
        Assert.Equal("1", await MarkerAsync(deployment));
    }

    [Fact]
    public async Task An_unreadable_marker_counts_as_none()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        await SeedAsync(deployment);

        await SetBankAsync(deployment, enabled: true, updatedByUserId: null);
        using (var scope = deployment.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
                .Where(setting => setting.Key == SettingKeys.Seed.PaymentProvidersVersion)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "one"), Token);
        }

        Assert.True((await SeedAsync(deployment)).BankTransferSwitchedOff);
        Assert.Equal("1", await MarkerAsync(deployment));
    }

    [Fact]
    public async Task The_seeder_never_overwrites_an_owners_fees_or_switches()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);
        await SeedAsync(deployment);

        using (var scope = deployment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.DeliveryRegions.Where(region => region.Code == DeliveryRegion.Europe)
                .ExecuteUpdateAsync(update => update.SetProperty(region => region.Fee, 12_345m).SetProperty(region => region.IsEnabled, false), Token);
            await db.Countries.Where(country => country.Code == "GB")
                .ExecuteUpdateAsync(update => update.SetProperty(country => country.IsShippingEnabled, false).SetProperty(country => country.DeliveryFeeOverride, 1m), Token);
            await db.DeliveryMethods.Where(method => method.Code == DeliveryMethod.InternationalCode)
                .ExecuteUpdateAsync(update => update.SetProperty(method => method.IsEnabled, false), Token);
        }

        var again = await SeedAsync(deployment);
        Assert.Equal(0, again.Countries);
        Assert.Equal(0, again.DeliveryRegions);

        using var check = deployment.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var europe = await after.DeliveryRegions.AsNoTracking().SingleAsync(region => region.Code == DeliveryRegion.Europe, Token);
        var gb = await after.Countries.AsNoTracking().SingleAsync(country => country.Code == "GB", Token);

        Assert.Equal((12_345m, false), (europe.Fee!.Value, europe.IsEnabled));
        Assert.Equal((false, 1m), (gb.IsShippingEnabled, gb.DeliveryFeeOverride!.Value));
        Assert.False(await after.DeliveryMethods.Where(method => method.Code == DeliveryMethod.InternationalCode).Select(method => method.IsEnabled).SingleAsync(Token));
    }

    [Fact]
    public async Task The_migration_backfills_every_existing_order_and_address_as_Sri_Lanka_and_makes_the_country_required()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token, migrate: false);

        using (var scope = deployment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore, Token);

            await InsertPreMigrationRowsAsync(db);
        }

        await TestDatabaseMigrator.MigrateAsync(deployment.ConnectionString, Token);

        using var after = deployment.Services.CreateScope();
        var migrated = after.ServiceProvider.GetRequiredService<AppDbContext>();

        var order = await migrated.Orders.AsNoTracking().SingleAsync(Token);
        Assert.Equal(("LK", "Sri Lanka"), (order.DeliveryCountryCode, order.DeliveryCountryName));
        Assert.Equal(1, order.DeliveryDistrictId);
        Assert.Equal("Colombo", order.DeliveryDistrictName);
        Assert.Equal(DeliveryZone.Colombo, order.DeliveryZone);
        Assert.Null(order.ShipAddress.Region);

        var address = await migrated.CustomerAddresses.AsNoTracking().SingleAsync(Token);
        Assert.Equal("LK", address.CountryCode);
        Assert.Equal(1, address.DistrictId);
        Assert.Null(address.Region);

        var lk = await migrated.Countries.AsNoTracking().SingleAsync(Token);
        Assert.Equal(("LK", "Sri Lanka"), (lk.Code, lk.Name));

        Assert.Equal("NO", await IsNullableAsync(migrated, "sales", "Orders", "DeliveryCountryCode"));
        Assert.Equal("NO", await IsNullableAsync(migrated, "sales", "Orders", "DeliveryCountryName"));
        Assert.Equal("NO", await IsNullableAsync(migrated, "identity", "CustomerAddresses", "CountryCode"));
        Assert.Equal("YES", await IsNullableAsync(migrated, "sales", "Orders", "DeliveryDistrictId"));
        Assert.Equal("YES", await IsNullableAsync(migrated, "identity", "CustomerAddresses", "DistrictId"));

        // The constraints the district columns had are back, and the new ones hold.
        var constraints = await NamesAsync(
            migrated,
            "SELECT name FROM sys.objects WHERE name IN ('CK_Orders_DeliveryZone', 'CK_Orders_Destination', "
            + "'CK_CustomerAddresses_Destination', 'FK_Orders_Districts_DeliveryDistrictId', "
            + "'FK_CustomerAddresses_Districts_DistrictId', 'FK_Orders_Countries_DeliveryCountryCode', "
            + "'FK_CustomerAddresses_Countries_CountryCode')");
        Assert.Equal(7, constraints.Count);

        var indexes = await NamesAsync(
            migrated,
            "SELECT name FROM sys.indexes WHERE name IN ('IX_Orders_Paid', 'IX_Orders_DeliveryDistrictId', "
            + "'IX_Orders_DeliveryCountryCode', 'IX_CustomerAddresses_District', 'IX_CustomerAddresses_CountryCode', "
            + "'UX_Countries_Name', 'IX_Countries_RegionCode', 'UX_DeliveryRegions_Name')");
        Assert.Equal(8, indexes.Count);

        // An overseas order with a district is refused by the database itself.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => migrated.Database.ExecuteSqlRawAsync(
            "UPDATE sales.Orders SET DeliveryCountryCode = 'LK', DeliveryDistrictId = NULL", Token));
        Assert.Contains("CK_Orders_Destination", ex.Message, StringComparison.Ordinal);

        // The seeder then completes the country list around the migrated LK row without touching it.
        var counts = await new SalesReferenceDataSeeder(migrated).SeedAsync(Token);
        Assert.Equal(248, counts.Countries);
    }

    // ------------------------------------------------------------------------------------------- helpers

    private static async Task<SalesSeedCounts> SeedAsync(FreshDeployment deployment)
    {
        using var scope = deployment.Services.CreateScope();
        return await new SalesReferenceDataSeeder(scope.ServiceProvider.GetRequiredService<AppDbContext>()).SeedAsync(Token);
    }

    private static async Task<int> CountriesAsync(FreshDeployment deployment)
    {
        using var scope = deployment.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Countries.CountAsync(Token);
    }

    private static Task<bool> EnabledAsync(AppDbContext db, string code) =>
        db.PaymentProviders.AsNoTracking().Where(provider => provider.Code == code).Select(provider => provider.IsEnabled).SingleAsync(Token);

    private static async Task<bool> BankEnabledAsync(FreshDeployment deployment)
    {
        using var scope = deployment.Services.CreateScope();
        return await EnabledAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), PaymentProviderCodes.BankTransfer);
    }

    private static Task<string?> MarkerAsync(AppDbContext db) =>
        db.Settings.AsNoTracking().Where(setting => setting.Key == SettingKeys.Seed.PaymentProvidersVersion).Select(setting => setting.Value).SingleAsync(Token);

    private static async Task<string?> MarkerAsync(FreshDeployment deployment)
    {
        using var scope = deployment.Services.CreateScope();
        return await MarkerAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task SetBankAsync(FreshDeployment deployment, bool enabled, int? updatedByUserId)
    {
        using var scope = deployment.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentProviders
            .Where(provider => provider.Code == PaymentProviderCodes.BankTransfer)
            .ExecuteUpdateAsync(
                update => update.SetProperty(provider => provider.IsEnabled, enabled).SetProperty(provider => provider.UpdatedByUserId, updatedByUserId),
                Token);
    }

    private static async Task DeleteMarkerAsync(FreshDeployment deployment)
    {
        using var scope = deployment.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .Where(setting => setting.Key == SettingKeys.Seed.PaymentProvidersVersion)
            .ExecuteDeleteAsync(Token);
    }

    /// <summary>
    /// One Sri Lankan order and one saved address, written with the schema as it stood before the migration —
    /// raw SQL, because the model is the new one.
    /// </summary>
    private static async Task InsertPreMigrationRowsAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ref.Provinces (Id, Code, Name) VALUES (1, 'LK-1', N'Western');
            INSERT INTO ref.Districts (Id, ProvinceId, Code, Name, DeliveryZone, SortOrder, IsActive) VALUES (1, 1, 'LK-11', N'Colombo', 1, 1, 1);
            INSERT INTO sales.PaymentProviders (Code, DisplayName, IsEnabled, DisplayOrder) VALUES ('BankTransfer', N'Bank transfer', 1, 1);
            """,
            Token);

        var writer = new SalesRowWriter(db);
        var methodId = await writer.InsertAsync("sales.DeliveryMethods", SalesRowWriter.DeliveryMethodRow(), Token);

        var order = SalesRowWriter.OrderRow(PaymentProviderCodes.BankTransfer, methodId, DeliveryMethod.StandardCode);
        order.Remove("DeliveryCountryCode");
        order.Remove("DeliveryCountryName");
        await writer.InsertAsync("sales.Orders", order, Token);

        var userId = await InsertCustomerAsync(db, writer);

        await writer.InsertAsync(
            "[identity].CustomerAddresses",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["UserId"] = userId,
                ["RecipientName"] = "Nimal Perera",
                ["Phone"] = "+94771234567",
                ["Line1"] = "221 Galle Road",
                ["City"] = "Colombo",
                ["DistrictId"] = 1,
                ["IsDefault"] = true,
                ["CreatedAtUtc"] = DateTime.UtcNow,
            },
            Token);
    }

    /// <summary>
    /// A customer row filled from the table's own column list: every NOT NULL column without a default gets a
    /// value of its type, so the test does not have to track every Identity column the schema has grown.
    /// </summary>
    private static async Task<int> InsertCustomerAsync(AppDbContext db, SalesRowWriter writer)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Token);
        }

        var required = new List<(string Name, string Type)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT c.name, t.name
                  FROM sys.columns c
                  JOIN sys.types t ON t.user_type_id = c.user_type_id
                 WHERE c.object_id = OBJECT_ID('[identity].[Users]')
                   AND c.is_nullable = 0 AND c.is_identity = 0 AND c.is_computed = 0
                   AND c.default_object_id = 0 AND t.name NOT IN ('timestamp', 'rowversion');
                """;

            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                required.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, type) in required)
        {
            row[name] = type switch
            {
                "bit" => false,
                "tinyint" => (byte)1,
                "smallint" => (short)1,
                "int" => 1,
                "bigint" => 1L,
                "uniqueidentifier" => Guid.NewGuid(),
                "datetime2" or "datetime" or "datetimeoffset" => DateTime.UtcNow,
                "decimal" => 0m,
                _ => "migration-test",
            };
        }

        row["UserType"] = (byte)UserType.Customer;
        row["FirstName"] = "Nimal";
        row["Email"] = "nimal.migration@example.com";
        row["NormalizedEmail"] = "NIMAL.MIGRATION@EXAMPLE.COM";
        row["UserName"] = "nimal.migration@example.com";
        row["NormalizedUserName"] = "NIMAL.MIGRATION@EXAMPLE.COM";
        row["SecurityStamp"] = Guid.NewGuid().ToString("N");
        row["ConcurrencyStamp"] = Guid.NewGuid().ToString("N");

        foreach (var optional in row.Keys.Where(key => !required.Any(column => column.Name == key)).ToList())
        {
            // Only the columns that exist; an Identity column this list names but the table lacks is skipped.
            if (!await ColumnExistsAsync(db, optional))
            {
                row.Remove(optional);
            }
        }

        return await writer.InsertAsync("[identity].Users", row, Token);
    }

    private static async Task<bool> ColumnExistsAsync(AppDbContext db, string column) =>
        (await NamesAsync(db, string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('[identity].[Users]') AND name = '{column}'"))).Count == 1;

    private static async Task<string> IsNullableAsync(AppDbContext db, string schema, string table, string column) =>
        (await NamesAsync(db, string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")))
        .Single();

    private static async Task<List<string>> NamesAsync(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Token);
        }

        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Literal SQL written in this file; no input reaches it.
        command.CommandText = sql;
#pragma warning restore CA2100

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
