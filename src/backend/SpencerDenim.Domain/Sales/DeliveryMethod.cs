using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.DeliveryMethods</c> (doc 08 §3.5, CHK-02/PDP-05/ADM-22) [A] [RV]: <i>how</i> an order
/// travels — standard, express or collection from a shop.
/// <para>
/// <b>No fee, threshold or estimate lives here.</b> Doc 08 §3.5 (N4) puts all three in
/// <see cref="DeliveryMethodZoneRate"/>, one row per zone, because a flat fee on the method could not
/// express "Express is not available island-wide" and would have to be a JSON blob — which, as §3.5
/// says, moves CK validation, row-level admin editing and audit, and the checkout join into application
/// code.
/// </para>
/// </summary>
public sealed class DeliveryMethod : IAuditable
{
    /// <summary>Doc 08 §3.5's code for collection from a shop. The order check constraints key on it.</summary>
    public const string PickupCode = "PICKUP";

    public const string StandardCode = "STANDARD";

    public const string ExpressCode = "EXPRESS";

    /// <summary>
    /// Overseas delivery by an international service such as Sri Lanka Post EMS or DHL (owner decision
    /// 2026-10-21). It has no zone rates: its fee comes from <c>sales.DeliveryRegions</c> and
    /// <c>ref.Countries</c>. It is offered for an address outside Sri Lanka only, and the domestic methods
    /// for an address inside it only — <c>CK_Orders_Destination</c> holds that on every order.
    /// </summary>
    public const string InternationalCode = "INTERNATIONAL";

    /// <summary>Doc 08 §3.5: <c>varchar(20)</c>, shared with the order's snapshot of the code.</summary>
    public const int MaxCodeLength = 20;

    public int Id { get; set; }

    /// <summary>
    /// <c>varchar(20)</c>, unique. An open-ended code (doc 08 §1.4) and <b>not</b> an enum: ADM-22 lets
    /// staff add a method. The three v1 values are the constants above, and <see cref="PickupCode"/> is
    /// the only one with behaviour attached in the schema.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Doc 08 §3.5: a method is offered only when this is true <i>and</i> its zone rate row exists with
    /// <c>IsAvailable = 1</c>. It defaults to false, so a method an admin has added is not live before
    /// its rates are entered.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// False for <see cref="PickupCode"/> (doc 08 §3.5). It decides whether checkout asks for a shipping
    /// address, and the order-level mirror of it is <c>CK_Orders_ShipAddress</c>.
    /// </summary>
    public bool RequiresAddress { get; set; } = true;

    public short SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<DeliveryMethodZoneRate> ZoneRates { get; } = [];
}
