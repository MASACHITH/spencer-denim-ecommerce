using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.DeliveryMethodZoneRates</c> (doc 08 §3.5, N4) [A] [RV]: what one method costs in one zone.
/// <para>
/// <b>A missing row means "unavailable"</b> (doc 08 §3.5), which is why the primary key is the pair
/// <c>(DeliveryMethodId, DeliveryZone)</c> rather than a surrogate: the absence of a row carries
/// meaning, so there must be no way to hold two rows for the same pair and have them disagree. Doc 08
/// notes an admin check warns about gaps; that screen is ADM-22, not this task.
/// </para>
/// <para>
/// Doc 08 §1.6 names this table in the cascade allow-list: a rate has no meaning apart from its method.
/// </para>
/// <para>
/// Rate values are <b>placeholders, client to confirm (D8)</b> — and this task seeds none at all.
/// </para>
/// </summary>
public sealed class DeliveryMethodZoneRate : IAuditable
{
    public int DeliveryMethodId { get; set; }

    public DeliveryMethod? DeliveryMethod { get; set; }

    /// <summary>
    /// The second half of the key. Doc 08 §3.5: the same CK as <c>ref.Districts.DeliveryZone</c>, which
    /// does not exist yet — see <see cref="Sales.DeliveryZone"/>.
    /// </summary>
    public DeliveryZone DeliveryZone { get; set; }

    /// <summary>
    /// Doc 08 §3.5's example is Express being unavailable island-wide. It is a separate column from "no
    /// row" so an admin can switch a zone off without losing the fee they had entered for it.
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>LKR, CK ≥ 0. Zero is a legitimate fee: free delivery for that zone.</summary>
    public decimal Fee { get; set; }

    /// <summary>Order subtotal at or above which the fee is waived. <see langword="null"/> = never free.</summary>
    public decimal? FreeAboveAmount { get; set; }

    /// <summary>CK <c>Min ≤ Max</c>, so a rate cannot promise "3 to 1 days".</summary>
    public byte EstimatedDaysMin { get; set; }

    public byte EstimatedDaysMax { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }
}
