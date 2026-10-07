using Microsoft.Extensions.Logging;
using SpencerDenim.Infrastructure.Media;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Runs the catalogue seeders in dependency order. Every step is idempotent (doc 08 §9, N13), so
/// this can be run against a database that is already seeded, partly seeded or edited by an admin.
/// It never applies migrations: migrations are a separate, deliberate step (doc 08 §10.4).
/// <para>
/// <b>There are two phases and the difference between them is not a preference.</b>
/// <see cref="RunReferenceDataAsync"/> writes doc 08 §9's reference data — the districts an address
/// names, the delivery methods and their zone rates, the payment providers, the departments and
/// categories the navigation is built from, the homepage's structure and the shape of the settings
/// screen. None of it is sample data and a shop without it cannot function at all, so it runs on every
/// startup of every environment (<see cref="CatalogSeedHostedService"/>).
/// <see cref="RunDemoCatalogueAsync"/> writes the forty-five sample products, which are development and
/// staging only and stay behind <c>Seed__RunOnStartup</c> / <c>Seed__IncludeDemoCatalogue</c>.
/// </para>
/// <para>
/// <b>Why they were one phase, and what that cost.</b> Both used to sit behind
/// <c>Seed__RunOnStartup</c>, which defaults to <c>false</c> and which nothing in the repository sets.
/// An empty database with doc 08 §1.1's collation plus <c>dotnet ef database update</c> therefore came
/// up with <c>ref.Districts</c>, <c>sales.DeliveryMethods</c>, <c>sales.DeliveryMethodZoneRates</c> and
/// <c>sales.PaymentProviders</c> all empty: an empty district dropdown, no delivery options, a
/// <c>NoPaymentMethod</c> blocker, and <c>Orders.DeliveryDistrictId NOT NULL</c> with nothing to put in
/// it. A fresh deploy on the documented defaults could not take a single order, ever — found in testing
/// W3-02. The flag's own name says what it is for ("seeding is a deliberate act, and production data
/// belongs to the client"), and a district is not production data.
/// </para>
/// </summary>
public sealed partial class CatalogSeedRunner(
    CatalogReferenceDataSeeder referenceData,
    SalesReferenceDataSeeder salesReferenceData,
    HomeContentSeeder homeContent,
    SettingsSeeder settings,
    DemoCatalogueSeeder demoCatalogue,
    DemoMediaPlaceholderGenerator demoMedia,
    ILogger<CatalogSeedRunner> logger)
{
    /// <param name="includeDemoCatalogue">
    /// True on development and staging only. Production seeds reference data and starts with an empty
    /// catalogue that the client fills in the admin.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SeedCounts> RunAsync(bool includeDemoCatalogue, CancellationToken cancellationToken)
    {
        var counts = await RunReferenceDataAsync(cancellationToken);

        if (includeDemoCatalogue)
        {
            await RunDemoCatalogueAsync(counts, cancellationToken);
        }

        LogSeeded(logger, counts);
        return counts;
    }

    /// <summary>
    /// Doc 08 §9's reference data, which every environment needs and none of which is sample data. Safe to
    /// call on every startup: each seeder inserts only what is missing and never overwrites an admin edit
    /// (N13), so a second run over a seeded database inserts nothing.
    /// </summary>
    /// <remarks>
    /// <b>The mechanism differs from doc 08 §9 and the gating is the reason it matters.</b> §9 asks for EF
    /// <c>HasData</c> with explicit ids; <see cref="CatalogReferenceDataSeeder"/> and
    /// <see cref="SalesReferenceDataSeeder"/> both record why they are runtime seeders instead — EF
    /// re-applies a <c>HasData</c> row whenever the model's value changes, and the columns most likely to
    /// change are exactly the admin-editable ones (<c>Districts.DeliveryZone</c>, a department's name). The
    /// consequence §9 was protecting against is that the rows simply are not there: a <c>HasData</c> seed
    /// cannot be forgotten, because it travels inside the migration. A runtime seeder can be, and was —
    /// it sat behind a flag that defaults off. Calling it unconditionally here restores that guarantee:
    /// applying the migrations and starting the API is now enough, which is what §9 promised, by a
    /// different route. On an empty database the rows land with the exact ids §9 fixes, because the ids are
    /// in the seed data and the keys are <c>ValueGeneratedNever</c>.
    /// </remarks>
    public async Task<SeedCounts> RunReferenceDataAsync(CancellationToken cancellationToken)
    {
        var counts = await referenceData.SeedAsync(cancellationToken);

        // Doc 08 §9's sales reference data: the districts an address names, the delivery methods and their
        // zone rates, and the payment providers checkout lists. It runs on every environment including
        // production, because none of it is sample data — a shop with no districts cannot take an order at
        // all (`sales.Orders.DeliveryDistrictId` is NOT NULL) and a shop with no enabled payment provider
        // has nothing for a shopper to choose.
        counts.Sales = await salesReferenceData.SeedAsync(cancellationToken);

        // After the reference data, because the department tiles of HOME-02 point at the three
        // seeded departments and cannot be written before they exist.
        var content = await homeContent.SeedAsync(cancellationToken);
        counts.HomeSections = content.Sections;
        counts.HomeSectionItems = content.SectionItems;
        counts.AnnouncementMessages = content.Announcements;
        counts.ContentPages = content.ContentPages;

        // ops.Settings (ADM-22). These rows are the *shape* of the settings screen rather than sample data,
        // their values are all null, and a production database without them has a settings screen with no
        // boxes on it. It runs after the content seeder for no ordering reason — nothing here depends on
        // anything above — but keeping the one order makes the log line's counts read in a fixed order.
        var settingsCounts = await settings.SeedAsync(cancellationToken);
        counts.Settings = settingsCounts.Inserted;

        return counts;
    }

    /// <summary>
    /// The forty-five sample products of doc 08 §9 and the placeholder images behind them. Development and
    /// staging only: production starts with an empty catalogue that the client fills in the admin, so this
    /// stays behind <c>Seed__RunOnStartup</c> and <c>Seed__IncludeDemoCatalogue</c>.
    /// </summary>
    public async Task RunDemoCatalogueAsync(SeedCounts counts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var demo = await demoCatalogue.SeedAsync(cancellationToken);
        counts.Products = demo.Products;
        counts.Variants = demo.Variants;
        counts.MediaAssets = demo.MediaAssets;

        // The rows above point at public/originals/demo/…; this draws the files behind them, so a
        // development or staging storefront shows labelled placeholders rather than broken images
        // (W1-04). It is a no-op when no media volume is configured.
        var placeholders = await demoMedia.RunAsync(cancellationToken);
        counts.MediaPlaceholders = placeholders.Generated;
    }

    /// <summary>Writes the "Catalogue seeding inserted …" line, for a caller that ran a phase by hand.</summary>
    public void LogCounts(SeedCounts counts) => LogSeeded(logger, counts);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Catalogue seeding inserted {Counts}.")]
    private static partial void LogSeeded(ILogger logger, SeedCounts counts);
}
