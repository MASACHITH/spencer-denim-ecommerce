namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Whether the API seeds the catalogue when it starts. Off by default: seeding is a deliberate act,
/// and production data belongs to the client. Set through environment variables
/// <c>Seed__RunOnStartup</c> and <c>Seed__IncludeDemoCatalogue</c>.
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Run the idempotent reference-data seeder on startup.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Also insert the sample catalogue (development and staging only, doc 08 §9).</summary>
    public bool IncludeDemoCatalogue { get; set; }
}
