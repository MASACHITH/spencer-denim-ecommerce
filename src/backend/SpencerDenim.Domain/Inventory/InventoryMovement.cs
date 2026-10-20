using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Domain.Inventory;

/// <summary>
/// Doc 08 §3.4's <c>MovementType</c>. The numbers are the stored values and the check constraint's range, so
/// they never move.
/// </summary>
public enum InventoryMovementType : byte
{
    /// <summary>A variant's first stock entry (doc 11 P1 "Stock effects"). Not written by ADM-12; see the journal.</summary>
    Initial = 1,

    /// <summary>Units in from a supplier or production (doc 11 P2 step 2).</summary>
    Receive = 2,

    /// <summary>A manual increase with a reason, e.g. "Found in count".</summary>
    AdjustIn = 3,

    /// <summary>A manual decrease with a reason, e.g. "Damaged in store".</summary>
    AdjustOut = 4,

    /// <summary>Held at order placement (doc 08 §5.4).</summary>
    Reserve = 5,

    /// <summary>Given back on expiry or cancel (doc 08 §5.6).</summary>
    Release = 6,

    /// <summary>Taken off the shelf when an order is paid (doc 08 §5.5, §5.7).</summary>
    Sale = 7,

    /// <summary>Back on the shelf after a refund with restock (doc 08 §5.7).</summary>
    Restock = 8,

    /// <summary>"Set count" after a stock-take, and the DBA runbook's correction (doc 08 §5.8, §5.9).</summary>
    CountCorrection = 9,
}

/// <summary>
/// <c>inventory.InventoryMovements</c> (doc 08 §3.4): the append-only stock ledger (ADM-12, ADM-19, #108).
/// <para>
/// <b>One row per change to a variant's <c>StockLevels</c> row</b>, carrying the change and the figures it
/// left behind. <see cref="OnHandAfter"/> and <see cref="ReservedAfter"/> are taken from the conditional
/// <c>UPDATE</c>'s own <c>OUTPUT</c>, inside the transaction that still holds the row's exclusive lock, so
/// they are the figures that change produced and not a later read.
/// </para>
/// <para>
/// <b>Never updated, never deleted</b> (doc 08 §1.6). A variant with a movement can therefore not be
/// hard-deleted either: <see cref="VariantReferences"/> declares it as blocking the delete.
/// </para>
/// </summary>
public sealed class InventoryMovement
{
    /// <summary>Doc 08 §3.4: <c>nvarchar(250)</c>.</summary>
    public const int ReasonMaxLength = 250;

    public long Id { get; init; }

    public int VariantId { get; init; }

    public ProductVariant? Variant { get; init; }

    public InventoryMovementType MovementType { get; init; }

    public int OnHandDelta { get; init; }

    public int ReservedDelta { get; init; }

    public int OnHandAfter { get; init; }

    public int ReservedAfter { get; init; }

    /// <summary>The order that caused it, for the automatic movements. Null for a manual change.</summary>
    public int? OrderId { get; init; }

    public Order? Order { get; init; }

    /// <summary>Shared by every row one bulk update applied (doc 08 §5.8). Null for a single change.</summary>
    public Guid? BatchId { get; init; }

    /// <summary>Required for Adjust and CountCorrection (doc 08 §3.4); ADM-12 requires it for every manual change.</summary>
    public string? Reason { get; init; }

    /// <summary>NULL = system. No foreign key, the convention doc 08 §1.5 sets for user columns.</summary>
    public int? CreatedByUserId { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}
