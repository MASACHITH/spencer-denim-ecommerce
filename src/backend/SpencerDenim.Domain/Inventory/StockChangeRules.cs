namespace SpencerDenim.Domain.Inventory;

/// <summary>The three manual stock actions of doc 11 P2 step 2.</summary>
public enum StockChangeKind : byte
{
    /// <summary>Add units that arrived. <c>Quantity</c> is the number received, 1 or more.</summary>
    Receive = 1,

    /// <summary>Add or remove units. <c>Quantity</c> is the signed change, never 0.</summary>
    Adjust = 2,

    /// <summary>
    /// Replace the count after a stock-take. <c>Quantity</c> is the new on-hand figure, and the change is
    /// applied only while on hand still equals the figure the screen was showing (doc 08 §5.8).
    /// </summary>
    SetCount = 3,
}

/// <summary>One refusal of a requested stock change, keyed by the field it is about.</summary>
public sealed record StockChangeProblem(string Field, string Message);

/// <summary>
/// The rules a manual stock change must pass <b>before</b> it reaches the database (ADM-12, doc 11 P2).
/// <para>
/// <b>What is not here, on purpose: the reserved floor.</b> "On hand may never fall below reserved" (doc 11
/// P2 step 3) depends on <c>Reserved</c>, which an order placed a millisecond ago may have moved. Checking it
/// against a figure read earlier would be the read-then-write doc 08 §5.2 forbids, so it lives only in the
/// conditional <c>UPDATE</c>'s <c>WHERE</c> clause, evaluated under the row's own lock, with
/// <c>CK_StockLevels_ReservedLeOnHand</c> as the backstop. What this class checks is everything that does
/// not depend on the row: the shape, the range and the reason.
/// </para>
/// </summary>
public static class StockChangeRules
{
    /// <summary>
    /// The largest single receive or adjustment. Ten times anything a fashion shop takes in at once, and far
    /// enough below <see cref="int.MaxValue"/> that <c>OnHand + delta</c> cannot overflow.
    /// </summary>
    public const int MaxQuantity = 100_000;

    /// <summary>The largest on-hand figure a manual change may leave a variant at.</summary>
    public const int MaxOnHand = 1_000_000;

    /// <summary>The largest low-stock threshold a variant may carry.</summary>
    public const int MaxThreshold = 10_000;

    /// <summary>The most rows one bulk update may carry: doc 08 §5.8's 500 per transaction.</summary>
    public const int MaxBulkRows = 500;

    /// <summary>
    /// The shape and range of one requested change. <paramref name="prefix"/> is prepended to each field
    /// name, so a bulk row's refusal names <c>rows[3].quantity</c> rather than a bare <c>quantity</c>.
    /// </summary>
    public static IReadOnlyList<StockChangeProblem> Validate(
        StockChangeKind kind,
        int quantity,
        int? expectedOnHand,
        string prefix = "")
    {
        var problems = new List<StockChangeProblem>();
        var quantityField = prefix + "quantity";
        var expectedField = prefix + "expectedOnHand";

        switch (kind)
        {
            case StockChangeKind.Receive:
                if (quantity < 1 || quantity > MaxQuantity)
                {
                    problems.Add(new(quantityField, "Receive between 1 and 100,000 units."));
                }

                break;

            case StockChangeKind.Adjust:
                if (quantity == 0)
                {
                    problems.Add(new(quantityField, "Enter how many units to add or remove; 0 changes nothing."));
                }
                else if (quantity < -MaxQuantity || quantity > MaxQuantity)
                {
                    problems.Add(new(quantityField, "Add or remove at most 100,000 units at a time."));
                }

                break;

            case StockChangeKind.SetCount:
                if (quantity < 0 || quantity > MaxOnHand)
                {
                    problems.Add(new(quantityField, "Set a count between 0 and 1,000,000."));
                }

                if (expectedOnHand is null)
                {
                    problems.Add(new(
                        expectedField,
                        "Send the on-hand figure the screen was showing, so a count that changed meanwhile is caught."));
                }
                else if (expectedOnHand < 0)
                {
                    problems.Add(new(expectedField, "The on-hand figure cannot be negative."));
                }

                break;

            default:
                problems.Add(new(prefix + "kind", "Use kind Receive, Adjust or SetCount."));
                break;
        }

        // A count is a fact about the shelf, not a request to move it, so only SetCount carries one.
        if (kind is StockChangeKind.Receive or StockChangeKind.Adjust && expectedOnHand is not null)
        {
            problems.Add(new(expectedField, "Only a SetCount carries the on-hand figure; leave it out."));
        }

        return problems;
    }

    /// <summary>
    /// The trimmed reason, or a refusal. Doc 08 §3.4 requires a reason for Adjust and CountCorrection;
    /// ADM-12 requires one for <b>every</b> manual change, so a receipt is explained too.
    /// </summary>
    public static StockChangeProblem? ValidateReason(string? reason, out string trimmed)
    {
        trimmed = reason?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return new("reason", "Give a reason, for example \"Delivery from the factory\" or \"Damaged in store\".");
        }

        if (trimmed.Length > InventoryMovement.ReasonMaxLength)
        {
            return new("reason", $"Keep the reason to {InventoryMovement.ReasonMaxLength} characters.");
        }

        return null;
    }

    /// <summary>A threshold is blank (the shop default) or a whole number from 0 to <see cref="MaxThreshold"/>.</summary>
    public static StockChangeProblem? ValidateThreshold(int? threshold) =>
        threshold is < 0 or > MaxThreshold
            ? new("lowStockThreshold", "Use a threshold between 0 and 10,000, or leave it blank for the shop default.")
            : null;

    /// <summary>
    /// Which ledger row a manual change writes (doc 08 §3.4). A receipt is <c>Receive</c>; an adjustment is
    /// <c>AdjustIn</c> or <c>AdjustOut</c> by its sign; a set count is <c>CountCorrection</c> whatever its
    /// direction, because what it records is that the shelf was counted.
    /// </summary>
    public static InventoryMovementType MovementTypeFor(StockChangeKind kind, int onHandDelta) => kind switch
    {
        StockChangeKind.Receive => InventoryMovementType.Receive,
        StockChangeKind.Adjust => onHandDelta > 0 ? InventoryMovementType.AdjustIn : InventoryMovementType.AdjustOut,
        StockChangeKind.SetCount => InventoryMovementType.CountCorrection,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a manual stock change."),
    };
}
