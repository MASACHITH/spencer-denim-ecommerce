using Microsoft.Extensions.Logging;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Runs the catalogue seeders in dependency order. Both steps are idempotent (doc 08 §9, N13), so
/// this can be run against a database that is already seeded, partly seeded or edited by an admin.
/// It never applies migrations: migrations are a separate, deliberate step (doc 08 §10.4).
/// </summary>
public sealed partial class CatalogSeedRunner(
    CatalogReferenceDataSeeder referenceData,
    HomeContentSeeder homeContent,
    DemoCatalogueSeeder demoCatalogue,
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

        if (includeDemoCatalogue)
        {
            var demo = await demoCatalogue.SeedAsync(cancellationToken);
            counts.Products = demo.Products;
            counts.Variants = demo.Variants;
            counts.MediaAssets = demo.MediaAssets;
        }

        LogSeeded(logger, counts);
        return counts;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Catalogue seeding inserted {Counts}.")]
    private static partial void LogSeeded(ILogger logger, SeedCounts counts);
}
