namespace SpencerDenim.Domain.Sales;

/// <summary>
/// The ship-to address as it stood when the order was placed (doc 08 §3.5, <c>Orders.ShipAddress</c>).
/// Mapped as an EF complex type, so the columns live on <c>sales.Orders</c> with a <c>Ship_</c> prefix
/// and there is no second table and no second row identity.
/// <para>
/// <b>It is a snapshot, not a link.</b> Doc 08 §3.5 gives it no FK to
/// <c>identity.CustomerAddresses</c>: an order has to keep showing where it was actually sent even
/// after the customer edits or deletes that address book entry.
/// </para>
/// <para>
/// <b>Every column is nullable, and the order still refuses a bad one</b> (doc 08 §3.5, DB11). The
/// nullability is there because a PICKUP order has no ship-to address at all and because PDPA
/// anonymisation (N5) overwrites these columns; the rule that a delivered order must have a recipient,
/// phone, line 1 and city is enforced one level up, by <c>CK_Orders_ShipAddress</c>, which can see the
/// delivery method code and these columns at the same time. A per-column <c>NOT NULL</c> could not
/// express "unless this is a pickup".
/// </para>
/// </summary>
public sealed class AddressSnapshot
{
    public string? RecipientName { get; set; }

    public string? Phone { get; set; }

    public string? Line1 { get; set; }

    public string? Line2 { get; set; }

    public string? City { get; set; }

    /// <summary>
    /// State, province or region, for an overseas address (owner decision 2026-10-21). Null for a Sri Lankan
    /// address, which names its district on the order instead.
    /// </summary>
    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    /// <summary>A nearby landmark. Sri Lankan addresses often need one to be findable.</summary>
    public string? Landmark { get; set; }
}
