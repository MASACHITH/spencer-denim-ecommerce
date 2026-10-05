using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 08 §9's reference data for checkout: provinces, districts, delivery methods, zone rates and
/// payment providers.
/// <para>
/// <b>Every figure here was read from doc 08 §9 and only then checked against the database.</b> That is
/// the order that matters: copying the numbers out of the seeder would make this test assert that the
/// seed is what it is rather than what the document says — the trap W3-03's own index test fell into and
/// recorded.
/// </para>
/// <para>
/// <b>And every delivery figure is a placeholder the client has still to confirm (D8).</b> That is not an
/// excuse for them being untested; it is the reason they must be, because the one thing worse than a
/// placeholder fee is a fee nobody can trace to a decision.
/// </para>
/// </summary>
public sealed class SalesReferenceDataTests(SqlServerDatabaseFixture sqlServer)
    : SalesSchemaTestBase(sqlServer)
{
    [Fact]
    public async Task The_nine_provinces_of_doc_08_section_9_are_seeded_with_its_ids()
    {
        SqlServer.SkipIfUnavailable();

        var provinces = await DbContext.Provinces
            .AsNoTracking()
            .OrderBy(province => province.Id)
            .Select(province => $"{province.Id}|{province.Code}|{province.Name}")
            .ToListAsync(Token);

        Assert.Equal(
            [
                "1|LK-1|Western",
                "2|LK-2|Central",
                "3|LK-3|Southern",
                "4|LK-4|Northern",
                "5|LK-5|Eastern",
                "6|LK-6|North Western",
                "7|LK-7|North Central",
                "8|LK-8|Uva",
                "9|LK-9|Sabaragamuwa",
            ],
            provinces);
    }

    /// <summary>
    /// The twenty-five districts, with the <b>initial</b> zone doc 08 §9 assigns: Colombo is its own zone,
    /// Gampaha and Kalutara are Western, everything else is the most conservative Island-wide.
    /// </summary>
    [Fact]
    public async Task The_twenty_five_districts_of_doc_08_section_9_are_seeded_with_its_zones()
    {
        SqlServer.SkipIfUnavailable();

        var districts = await DbContext.Districts
            .AsNoTracking()
            .OrderBy(district => district.Id)
            .Select(district => $"{district.Id}|{district.Code}|{district.Name}|{(byte)district.DeliveryZone}")
            .ToListAsync(Token);

        Assert.Equal(
            [
                "1|LK-11|Colombo|1",
                "2|LK-12|Gampaha|2",
                "3|LK-13|Kalutara|2",
                "4|LK-21|Kandy|3",
                "5|LK-22|Matale|3",
                "6|LK-23|Nuwara Eliya|3",
                "7|LK-31|Galle|3",
                "8|LK-32|Matara|3",
                "9|LK-33|Hambantota|3",
                "10|LK-41|Jaffna|3",
                "11|LK-42|Kilinochchi|3",
                "12|LK-43|Mannar|3",
                "13|LK-44|Vavuniya|3",
                "14|LK-45|Mullaitivu|3",
                "15|LK-51|Batticaloa|3",
                "16|LK-52|Ampara|3",
                "17|LK-53|Trincomalee|3",
                "18|LK-61|Kurunegala|3",
                "19|LK-62|Puttalam|3",
                "20|LK-71|Anuradhapura|3",
                "21|LK-72|Polonnaruwa|3",
                "22|LK-81|Badulla|3",
                "23|LK-82|Monaragala|3",
                "24|LK-91|Ratnapura|3",
                "25|LK-92|Kegalle|3",
            ],
            districts);

        Assert.All(
            await DbContext.Districts.AsNoTracking().ToListAsync(Token),
            district => Assert.True(district.IsActive));
    }

    [Fact]
    public async Task Every_district_belongs_to_the_province_doc_08_section_9_puts_it_in()
    {
        SqlServer.SkipIfUnavailable();

        var counts = await DbContext.Districts
            .AsNoTracking()
            .GroupBy(district => district.ProvinceId)
            .Select(group => new { ProvinceId = group.Key, Count = group.Count() })
            .OrderBy(row => row.ProvinceId)
            .ToListAsync(Token);

        // Western 3, Central 3, Southern 3, Northern 5, Eastern 3, North Western 2, North Central 2,
        // Uva 2, Sabaragamuwa 2 — which is 25.
        Assert.Equal([3, 3, 3, 5, 3, 2, 2, 2, 2], counts.Select(row => row.Count));
        Assert.Equal(25, counts.Sum(row => row.Count));
    }

    /// <summary>
    /// Doc 08 §9: three methods, with PICKUP disabled. §9 also seeds <b>zero</b> pickup locations (client
    /// input, D8), so an enabled PICKUP would be an option that fails after the shopper chose it.
    /// </summary>
    /// <remarks>
    /// <b>Filtered to the three codes doc 08 §9 names, and it has to be.</b> This assembly shares one
    /// database and <c>SalesSchemaTestBase</c> inserts delivery methods of its own with generated codes,
    /// so reading the whole table would be reading which other suites had run first.
    /// <para>
    /// For the same reason there is <b>no assertion here that the shop has no pickup locations or
    /// couriers</b>, although §9 seeds none of either (client input, D8): those tables are exactly the
    /// ones the schema suites add rows to. The fact that the seeder writes none of them is visible in
    /// <c>SalesReferenceData</c>, which has no list for either — and the behaviour that depends on it is
    /// asserted where it belongs, in
    /// <c>CheckoutEndpointTests.A_delivery_method_that_is_switched_off_is_not_offered</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_three_delivery_methods_are_seeded_with_pickup_disabled()
    {
        SqlServer.SkipIfUnavailable();

        var seededCodes = new[]
        {
            DeliveryMethod.StandardCode,
            DeliveryMethod.ExpressCode,
            DeliveryMethod.PickupCode,
        };

        var methods = await DbContext.DeliveryMethods
            .AsNoTracking()
            .Where(method => seededCodes.Contains(method.Code))
            .OrderBy(method => method.SortOrder)
            .Select(method => $"{method.Code}|{method.IsEnabled}|{method.RequiresAddress}|{method.SortOrder}")
            .ToListAsync(Token);

        Assert.Equal(
            [
                "STANDARD|True|True|1",
                "EXPRESS|True|True|2",
                "PICKUP|False|False|3",
            ],
            methods);
    }

    /// <summary>
    /// Doc 08 §9's nine zone rates, verbatim. <b>Placeholders, client to confirm (D8).</b>
    /// </summary>
    [Fact]
    public async Task The_nine_zone_rates_of_doc_08_section_9_are_seeded_exactly()
    {
        SqlServer.SkipIfUnavailable();

        var seededCodes = new[]
        {
            DeliveryMethod.StandardCode,
            DeliveryMethod.ExpressCode,
            DeliveryMethod.PickupCode,
        };

        // Joined to the seeded methods only: the schema suites add methods of their own, and a rate row
        // for one of those is not doc 08 §9's.
        var rates = await DbContext.DeliveryMethodZoneRates
            .AsNoTracking()
            .Join(
                DbContext.DeliveryMethods.AsNoTracking().Where(method => seededCodes.Contains(method.Code)),
                rate => rate.DeliveryMethodId,
                method => method.Id,
                (rate, method) => new { method.Code, Rate = rate })
            .OrderBy(row => row.Rate.DeliveryZone)
            .ThenBy(row => row.Code)
            .Select(row =>
                $"{row.Code}|{(byte)row.Rate.DeliveryZone}|{row.Rate.IsAvailable}|{row.Rate.Fee}|"
                + $"{row.Rate.FreeAboveAmount}|{row.Rate.EstimatedDaysMin}-{row.Rate.EstimatedDaysMax}")
            .ToListAsync(Token);

        Assert.Equal(
            [
                // Colombo: Standard Rs. 400 free ≥ 10,000 in 1–2 days; Express Rs. 950 in 1 day;
                // Pickup Rs. 0 in 1–2 days.
                "EXPRESS|1|True|950.00||1-1",
                "PICKUP|1|True|0.00||1-2",
                "STANDARD|1|True|400.00|10000.00|1-2",

                // Western: Standard Rs. 450 free ≥ 10,000 in 2–3 days; Express Rs. 1,200 in 1–2 days.
                "EXPRESS|2|True|1200.00||1-2",
                "PICKUP|2|True|0.00||1-2",
                "STANDARD|2|True|450.00|10000.00|2-3",

                // IslandWide: Standard Rs. 600 free ≥ 15,000 in 3–5 days; **Express unavailable**.
                "EXPRESS|3|False|0.00||1-2",
                "PICKUP|3|True|0.00||1-2",
                "STANDARD|3|True|600.00|15000.00|3-5",
            ],
            rates);
    }

    /// <summary>Doc 08 §9: BankTransfer enabled, PayPal disabled until its credentials and FX rate exist.</summary>
    [Fact]
    public async Task The_two_payment_providers_are_seeded_with_PayPal_disabled()
    {
        SqlServer.SkipIfUnavailable();

        var seededCodes = new[] { PaymentProviderCodes.BankTransfer, PaymentProviderCodes.PayPal };

        // The two §9 names, not the whole table: `SalesSchemaTestBase` inserts an enabled provider of its
        // own, and reading every row would be reading which suites had run first.
        var providers = await DbContext.PaymentProviders
            .AsNoTracking()
            .Where(provider => seededCodes.Contains(provider.Code))
            .OrderBy(provider => provider.DisplayOrder)
            .Select(provider => $"{provider.Code}|{provider.IsEnabled}|{provider.DisplayOrder}")
            .ToListAsync(Token);

        Assert.Equal(["BankTransfer|True|1", "PayPal|False|2"], providers);
    }

    /// <summary>
    /// **No seeded checkout description promises anything that is not built.** Doc 11 P7's bank details,
    /// the reference to quote and the slip upload are W3-03's step 3, and a seeded sentence promising them
    /// on a page that has none would be the shop lying on its own checkout.
    /// </summary>
    [Fact]
    public async Task No_seeded_payment_description_promises_what_is_not_built()
    {
        SqlServer.SkipIfUnavailable();

        var seededCodes = new[] { PaymentProviderCodes.BankTransfer, PaymentProviderCodes.PayPal };

        var descriptions = await DbContext.PaymentProviders
            .AsNoTracking()
            .Where(provider => seededCodes.Contains(provider.Code))
            .Select(provider => provider.CheckoutDescription ?? string.Empty)
            .ToListAsync(Token);

        foreach (var description in descriptions)
        {
            Assert.DoesNotContain("account number", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sort code", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reference", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("slip", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("upload", description, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// **N13: the seeder inserts what is missing and never overwrites an admin edit.** This is the one
    /// property that matters most here, because the zone map and the fees are exactly what the client is
    /// expected to change first (D8) — and a seeder that re-applied its own figures would undo that
    /// silently on the next deployment.
    /// </summary>
    [Fact]
    public async Task Running_the_seeder_again_changes_nothing_it_has_already_written()
    {
        SqlServer.SkipIfUnavailable();

        // An admin reassigns a district's zone and raises a fee: the two edits D8 anticipates.
        var kandyId = await DbContext.Districts
            .AsNoTracking()
            .Where(district => district.Name == "Kandy")
            .Select(district => district.Id)
            .SingleAsync(Token);

        var standardId = await DbContext.DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == DeliveryMethod.StandardCode)
            .Select(method => method.Id)
            .SingleAsync(Token);

        await DbContext.Districts
            .Where(district => district.Id == kandyId)
            .ExecuteUpdateAsync(
                district => district.SetProperty(row => row.DeliveryZone, DeliveryZone.Western),
                Token);

        await DbContext.DeliveryMethodZoneRates
            .Where(rate => rate.DeliveryMethodId == standardId && rate.DeliveryZone == DeliveryZone.Colombo)
            .ExecuteUpdateAsync(rate => rate.SetProperty(row => row.Fee, 555m), Token);

        await DbContext.DeliveryMethods
            .Where(method => method.Id == standardId)
            .ExecuteUpdateAsync(method => method.SetProperty(row => row.IsEnabled, false), Token);

        try
        {
            using var scope = SqlServer.CreateScope();
            var counts = await scope.ServiceProvider
                .GetRequiredService<SalesReferenceDataSeeder>()
                .SeedAsync(Token);

            // Nothing was missing, so nothing was inserted.
            Assert.Equal(0, counts.Provinces);
            Assert.Equal(0, counts.Districts);
            Assert.Equal(0, counts.DeliveryMethods);
            Assert.Equal(0, counts.DeliveryMethodZoneRates);
            Assert.Equal(0, counts.PaymentProviders);

            // And every edit survived.
            Assert.Equal(
                DeliveryZone.Western,
                await DbContext.Districts
                    .AsNoTracking()
                    .Where(district => district.Id == kandyId)
                    .Select(district => district.DeliveryZone)
                    .SingleAsync(Token));

            Assert.Equal(
                555m,
                await DbContext.DeliveryMethodZoneRates
                    .AsNoTracking()
                    .Where(rate =>
                        rate.DeliveryMethodId == standardId && rate.DeliveryZone == DeliveryZone.Colombo)
                    .Select(rate => rate.Fee)
                    .SingleAsync(Token));

            Assert.False(
                await DbContext.DeliveryMethods
                    .AsNoTracking()
                    .Where(method => method.Id == standardId)
                    .Select(method => method.IsEnabled)
                    .SingleAsync(Token));
        }
        finally
        {
            // Put the seeded values back: the other suites in this run read them, and doc 14's rule about
            // leaving nothing behind applies to rows as much as to databases.
            await DbContext.Districts
                .Where(district => district.Id == kandyId)
                .ExecuteUpdateAsync(
                    district => district.SetProperty(row => row.DeliveryZone, DeliveryZone.IslandWide),
                    Token);

            await DbContext.DeliveryMethodZoneRates
                .Where(rate =>
                    rate.DeliveryMethodId == standardId && rate.DeliveryZone == DeliveryZone.Colombo)
                .ExecuteUpdateAsync(rate => rate.SetProperty(row => row.Fee, 400m), Token);

            await DbContext.DeliveryMethods
                .Where(method => method.Id == standardId)
                .ExecuteUpdateAsync(method => method.SetProperty(row => row.IsEnabled, true), Token);
        }
    }

    /// <summary>
    /// The seeder repairs a <b>missing</b> row, which is the other half of N13 and the case a deployment
    /// after a new zone is added will meet.
    /// </summary>
    [Fact]
    public async Task The_seeder_restores_a_row_that_has_been_deleted()
    {
        SqlServer.SkipIfUnavailable();

        var standardId = await DbContext.DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == DeliveryMethod.StandardCode)
            .Select(method => method.Id)
            .SingleAsync(Token);

        await DbContext.DeliveryMethodZoneRates
            .Where(rate => rate.DeliveryMethodId == standardId && rate.DeliveryZone == DeliveryZone.Western)
            .ExecuteDeleteAsync(Token);

        using var scope = SqlServer.CreateScope();
        var counts = await scope.ServiceProvider
            .GetRequiredService<SalesReferenceDataSeeder>()
            .SeedAsync(Token);

        Assert.Equal(1, counts.DeliveryMethodZoneRates);

        var restored = await DbContext.DeliveryMethodZoneRates
            .AsNoTracking()
            .SingleAsync(
                rate => rate.DeliveryMethodId == standardId && rate.DeliveryZone == DeliveryZone.Western,
                Token);

        Assert.Equal(450m, restored.Fee);
        Assert.Equal(10_000m, restored.FreeAboveAmount);
    }
}
