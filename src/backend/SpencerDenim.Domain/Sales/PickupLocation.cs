using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.PickupLocations</c> (doc 08 §3.5, CHK-02) [A]: a shop a customer may collect an order from.
/// </summary>
public sealed class PickupLocation : IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string AddressLine { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    /// <summary>
    /// Doc 08 §3.5 gives this an FK to <c>ref.Districts</c>, and R7 says <b>PICKUP uses the pickup
    /// location's district zone</b> rather than the customer's.
    /// <para>
    /// <b>The column exists; the foreign key does not yet.</b> No migration has created the <c>ref</c>
    /// schema (GLB-06), so the constraint would have nothing to point at. The column is deliberately
    /// <i>not</i> omitted the way W2-09 omitted <c>Carts.DistrictId</c>, because doc 08 makes it
    /// <c>NOT NULL</c>: a pickup location with no district cannot be quoted a delivery estimate at all,
    /// so leaving it out would make the table unusable rather than merely incomplete. Its index is
    /// already here, so adding the FK later is one constraint and no index build (doc 08 §1.6, DB7).
    /// </para>
    /// </summary>
    public int DistrictId { get; set; }

    public string? Phone { get; set; }

    public string? OpeningHours { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }
}
