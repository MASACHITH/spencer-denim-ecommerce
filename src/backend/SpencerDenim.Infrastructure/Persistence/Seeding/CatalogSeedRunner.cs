using Microsoft.Extensions.Logging;
using SpencerDenim.Infrastructure.Media;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Runs the catalogue seeders in dependency order. Both steps are idempotent (doc 08 §9, N13), so
/// this can be run against a database that is already seeded, partly seeded or edited by an admin.
/// It never applies migrations: migrations are a separate, deliberate step (doc 08 §10.4).
/// </summary>
public sealed partial class CatalogSeedRunner(
    CatalogReferenceDataSeeder referenceData,
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
        var counts = await referenceData.SeedAsync(cancellationToken);

        // After the reference data, because the department tiles of HOME-02 point at the three
        // seeded departments and cannot be written before they exist.
        var content = await homeContent.SeedAsync(cancellationToken);
        counts.HomeSections = content.Sections;
        counts.HomeSectionItems = content.SectionItems;
        counts.AnnouncementMessages = content.Announcements;

        // ops.Settings (ADM-22). Unconditional, like the reference data and unlike the demo catalogue: these
        // rows are the *shape* of the settings screen rather than sample data, their values are all null, and
        // a production database without them has a settings screen with no boxes on it. It runs after the
        // content seeder for no ordering reason — nothing here depends on anything above — but keeping the
        // one order makes the log line's counts read in a fixed order.
        var settingsCounts = await settings.SeedAsync(cancellationToken);
        counts.Settings = settingsCounts.Inserted;

        if (includeDemoCatalogue)
        {
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

        LogSeeded(logger, counts);
        return counts;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Catalogue seeding inserted {Counts}.")]
    private static partial void LogSeeded(ILogger logger, SeedCounts counts);
}
