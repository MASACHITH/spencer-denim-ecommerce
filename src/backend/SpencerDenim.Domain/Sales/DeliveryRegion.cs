using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.DeliveryRegions</c> (owner decision 2026-10-21) [A] [RV]: one overseas delivery band — Asia,
/// Middle East, Europe, North America, Oceania, rest of world — with the fee and the estimate every country
/// in it is charged unless the country overrides them.
/// <para>
/// <b>Overseas only.</b> Sri Lankan delivery keeps <see cref="DeliveryMethodZoneRate"/>; nothing here applies
/// to an address in Sri Lanka. A parcel abroad goes by an international service (Sri Lanka Post EMS, DHL),
/// through the single <see cref="DeliveryMethod.InternationalCode"/> method.
/// </para>
/// <para>
/// <b>The seeded fees are placeholders for the owner to replace.</b> A null <see cref="Fee"/> means "not
/// priced", and checkout refuses an unpriced destination rather than charging nothing for it.
/// </para>
/// </summary>
public sealed class DeliveryRegion : IAuditable
{
    public const int MaxCodeLength = 20;

    public const int MaxNameLength = 60;

    public const string Asia = "ASIA";

    public const string MiddleEast = "MIDDLE_EAST";

    public const string Europe = "EUROPE";

    public const string NorthAmerica = "NORTH_AMERICA";

    public const string Oceania = "OCEANIA";

    public const string RestOfWorld = "REST_OF_WORLD";

    /// <summary>PK, an upper-case code such as <see cref="Europe"/>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>LKR, CK ≥ 0. Null = not priced, so not offered.</summary>
    public decimal? Fee { get; set; }

    /// <summary>CK <c>Min ≤ Max</c>.</summary>
    public byte EstimatedDaysMin { get; set; }

    public byte EstimatedDaysMax { get; set; }

    /// <summary>LKR express fee. Null = express not offered for this region. CK ≥ 0.</summary>
    public decimal? ExpressFee { get; set; }

    /// <summary>CK <c>Min ≤ Max</c>. Defaults seeded at 3–7 working days.</summary>
    public byte ExpressDaysMin { get; set; } = 3;

    public byte ExpressDaysMax { get; set; } = 7;

    /// <summary>Off switches off every country in the region at once.</summary>
    public bool IsEnabled { get; set; } = true;

    public short SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }
}
