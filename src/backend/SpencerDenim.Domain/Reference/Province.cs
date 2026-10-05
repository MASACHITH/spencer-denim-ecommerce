namespace SpencerDenim.Domain.Reference;

/// <summary>
/// <c>ref.Provinces</c> (doc 08 §3.1, GLB-06 / CHK-01). The nine provinces of Sri Lanka.
/// <para>
/// <b>Immutable reference data.</b> Doc 08 §9's rule for this table is that a released seed row is never
/// changed afterwards: nothing in the admin edits a province, and nothing in the storefront lets a
/// shopper pick one. It exists so that <see cref="District.ProvinceId"/> has something to point at and so
/// that a district list can be grouped the way a Sri Lankan address is read.
/// </para>
/// <para>
/// <b>No audit columns and no rowversion</b>, because doc 08 §3.1 gives it neither — it is not marked
/// <c>[A]</c> or <c>[RV]</c>. A table nobody can edit needs no record of who edited it.
/// </para>
/// </summary>
public sealed class Province
{
    /// <summary>The longest ISO 3166-2 code this table holds (<c>LK-1</c>), per doc 08 §3.1.</summary>
    public const int MaxCodeLength = 6;

    public const int MaxNameLength = 60;

    /// <summary>
    /// PK, seeded 1–9 with **no identity** (doc 08 §3.1). Explicit ids are what let
    /// <see cref="District.ProvinceId"/> be seeded as data rather than resolved by a round trip.
    /// </summary>
    public int Id { get; set; }

    /// <summary>Unique. ISO 3166-2, e.g. <c>LK-1</c> for Western.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ICollection<District> Districts { get; } = [];
}
