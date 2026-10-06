namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Whether the API seeds the <b>optional</b> catalogue data when it starts. Off by default: seeding sample
/// data is a deliberate act, and production data belongs to the client. Set through environment variables
/// <c>Seed__RunOnStartup</c> and <c>Seed__IncludeDemoCatalogue</c>.
/// <para>
/// <b>Neither flag gates doc 08 §9's reference data any more.</b> The districts, delivery methods, zone
/// rates, payment providers, departments, categories, homepage structure and settings keys are established
/// on every startup of every environment — see <see cref="CatalogSeedHostedService"/> for why, and for
/// what a flag-gated reference seeder cost.
/// </para>
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>
    /// Whether this host may seed optional data at all. Today that is the demo catalogue and nothing else,
    /// and it is kept as a separate switch from <see cref="IncludeDemoCatalogue"/> so that an existing
    /// deployment's two variables keep doing exactly what they did before.
    /// </summary>
    public bool RunOnStartup { get; set; }

    /// <summary>
    /// Insert the sample catalogue — development and staging only (doc 08 §9). Needs
    /// <see cref="RunOnStartup"/> as well.
    /// </summary>
    public bool IncludeDemoCatalogue { get; set; }
}
