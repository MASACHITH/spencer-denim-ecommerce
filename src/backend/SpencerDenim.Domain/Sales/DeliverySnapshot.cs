namespace SpencerDenim.Domain.Sales;

/// <summary>
/// How the order was being delivered when it was placed (doc 08 §3.5, <c>Orders.Delivery</c>). An EF
/// complex type, so the columns live on <c>sales.Orders</c>.
/// <para>
/// <b>Doc 08 §3.5: "No FKs inside."</b> The order already carries <c>DeliveryMethodId</c> and
/// <c>PickupLocationId</c> as real foreign keys; this is the text that must keep reading the same on an
/// invoice years later, after an admin has renamed the method or closed the shop.
/// </para>
/// <para>
/// <b><see cref="DeliveryMethodCode"/> is the one snapshot column with teeth.</b>
/// <c>CK_Orders_ShipAddress</c> and <c>CK_Orders_Pickup</c> both branch on it, and §4.1 (G-18) reads it
/// so that the auto-deliver job never marks an uncollected PICKUP order Delivered. A check constraint
/// cannot join to <c>DeliveryMethods</c>, so the code has to be on the order row for those rules to be
/// enforceable in the database at all.
/// </para>
/// </summary>
public sealed class DeliverySnapshot
{
    /// <summary>
    /// <c>varchar(20)</c>, a copy of <c>DeliveryMethods.Code</c> — <c>STANDARD</c>, <c>EXPRESS</c> or
    /// <see cref="DeliveryMethod.PickupCode"/>.
    /// </summary>
    public string DeliveryMethodCode { get; set; } = string.Empty;

    public string DeliveryMethodName { get; set; } = string.Empty;

    /// <summary><see langword="null"/> unless this is a pickup order.</summary>
    public string? PickupLocationName { get; set; }

    /// <summary>
    /// The estimate quoted to the customer, copied from the zone rate at placement. It is kept so a
    /// later rate change cannot retroactively alter what the order promised.
    /// </summary>
    public byte EstimatedDaysMin { get; set; }

    public byte EstimatedDaysMax { get; set; }
}
