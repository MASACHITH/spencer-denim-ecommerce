using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Domain.Reference;

/// <summary>
/// <c>ref.Countries</c> (owner decision 2026-10-21: Sri Lanka plus worldwide delivery; GLB-06, CHK-01) [A] [RV]:
/// every ISO 3166-1 alpha-2 country with its English short name, and the few facts checkout needs to take an
/// address there and charge for it.
/// <para>
/// <b>Sri Lanka is a row like any other, and the only one with no region.</b> An order or address in
/// <see cref="SriLankaCode"/> keeps the district, the zone and the domestic delivery methods exactly as they
/// were before this table existed; every other country is charged through its
/// <see cref="DeliveryRegion"/> or its own override. <c>CK_Countries_Region</c> holds the line: LK has no
/// region and every other country has one.
/// </para>
/// <para>
/// <b>Reference data with admin-editable delivery fields</b>, the same reading <see cref="District"/> gets for
/// its zone: the code and the name are the world's, not the shop's, so nothing edits them; whether the shop
/// sends parcels there, and for how much, is the shop's, edited under <c>Settings.Edit</c> and audited.
/// </para>
/// </summary>
public sealed class Country : IAuditable
{
    /// <summary>The one domestic country. Its orders keep the district and zone shape.</summary>
    public const string SriLankaCode = "LK";

    /// <summary>ISO 3166-1 alpha-2 is two letters: <c>char(2)</c>.</summary>
    public const int CodeLength = 2;

    public const int MaxNameLength = 80;

    /// <summary>PK. Upper-case ISO 3166-1 alpha-2, e.g. <c>GB</c>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>English short name, unique.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// FK <c>sales.DeliveryRegions</c>. Null for <see cref="SriLankaCode"/> only (<c>CK_Countries_Region</c>).
    /// </summary>
    public string? RegionCode { get; set; }

    public DeliveryRegion? Region { get; set; }

    /// <summary>
    /// Whether an address here must carry a postcode. Set for the countries where every address has one, and
    /// nowhere else: refusing a shopper who does not know a code their country does not use would cost a
    /// sale and buy nothing.
    /// </summary>
    public bool PostalCodeRequired { get; set; }

    /// <summary>Whether an address here must name a state, province or region (e.g. the United States).</summary>
    public bool RegionRequired { get; set; }

    /// <summary>
    /// The shop's switch for this destination. Off means checkout refuses it with a clear message. Ignored for
    /// <see cref="SriLankaCode"/>, which is always served (<c>CK_Countries_SriLankaEnabled</c>).
    /// </summary>
    public bool IsShippingEnabled { get; set; } = true;

    /// <summary>LKR. When set, replaces the region's fee for this country only. CK ≥ 0.</summary>
    public decimal? DeliveryFeeOverride { get; set; }

    /// <summary>When set (with <see cref="EstimatedDaysMaxOverride"/>), replaces the region's estimate.</summary>
    public byte? EstimatedDaysMinOverride { get; set; }

    public byte? EstimatedDaysMaxOverride { get; set; }

    /// <summary>LKR express fee override. When set, replaces the region's express fee for this country only. CK ≥ 0.</summary>
    public decimal? ExpressFeeOverride { get; set; }

    /// <summary>When set (with <see cref="ExpressDaysMaxOverride"/>), replaces the region's express estimate.</summary>
    public byte? ExpressDaysMinOverride { get; set; }

    public byte? ExpressDaysMaxOverride { get; set; }

    public short SortOrder { get; set; }

    public bool IsDomestic => string.Equals(Code, SriLankaCode, StringComparison.Ordinal);

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }
}
