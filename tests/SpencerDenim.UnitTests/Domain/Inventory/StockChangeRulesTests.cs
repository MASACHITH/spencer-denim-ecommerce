using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Domain.Inventory;

/// <summary>
/// The rules a manual stock change passes before it reaches the database (ADM-12, doc 11 P2), and the
/// per-variant status rule (doc 11 P2 step 9: out of stock is never low).
/// </summary>
public sealed class StockChangeRulesTests
{
    // ---- status ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, null, 5, StockStatus.OutOfStock)]   // nothing available is out, never low
    [InlineData(-1, null, 5, StockStatus.OutOfStock)]
    [InlineData(0, 0, 5, StockStatus.OutOfStock)]
    [InlineData(1, null, 5, StockStatus.LowStock)]     // the first unit is low
    [InlineData(5, null, 5, StockStatus.LowStock)]     // at the default is low
    [InlineData(6, null, 5, StockStatus.InStock)]      // one above is in stock
    [InlineData(8, 10, 5, StockStatus.LowStock)]       // its own threshold above the default wins
    [InlineData(4, 3, 5, StockStatus.InStock)]         // its own threshold below the default wins
    [InlineData(1, 0, 5, StockStatus.InStock)]         // a threshold of 0 never reports low
    public void A_variant_is_out_low_or_in_stock_by_its_own_threshold_or_the_default(
        int available, int? own, int fallback, StockStatus expected) =>
        Assert.Equal(expected, StockAvailability.ForVariant(available, own, fallback));

    // ---- shape and range --------------------------------------------------------------------------------

    [Theory]
    [InlineData(StockChangeKind.Receive, 1, null)]
    [InlineData(StockChangeKind.Receive, StockChangeRules.MaxQuantity, null)]
    [InlineData(StockChangeKind.Adjust, 1, null)]
    [InlineData(StockChangeKind.Adjust, -1, null)]
    [InlineData(StockChangeKind.Adjust, StockChangeRules.MaxQuantity, null)]
    [InlineData(StockChangeKind.Adjust, -StockChangeRules.MaxQuantity, null)]
    [InlineData(StockChangeKind.SetCount, 0, 0)]
    [InlineData(StockChangeKind.SetCount, StockChangeRules.MaxOnHand, 12)]
    public void A_change_at_the_edge_of_its_range_is_accepted(StockChangeKind kind, int quantity, int? expected) =>
        Assert.Empty(StockChangeRules.Validate(kind, quantity, expected));

    [Theory]
    [InlineData(StockChangeKind.Receive, 0, null, "quantity")]
    [InlineData(StockChangeKind.Receive, -1, null, "quantity")]
    [InlineData(StockChangeKind.Receive, StockChangeRules.MaxQuantity + 1, null, "quantity")]
    [InlineData(StockChangeKind.Adjust, 0, null, "quantity")]
    [InlineData(StockChangeKind.Adjust, StockChangeRules.MaxQuantity + 1, null, "quantity")]
    [InlineData(StockChangeKind.Adjust, -StockChangeRules.MaxQuantity - 1, null, "quantity")]
    [InlineData(StockChangeKind.Adjust, int.MinValue, null, "quantity")]
    [InlineData(StockChangeKind.SetCount, -1, 5, "quantity")]
    [InlineData(StockChangeKind.SetCount, StockChangeRules.MaxOnHand + 1, 5, "quantity")]
    [InlineData(StockChangeKind.SetCount, 5, null, "expectedOnHand")]
    [InlineData(StockChangeKind.SetCount, 5, -1, "expectedOnHand")]
    [InlineData(StockChangeKind.Receive, 5, 5, "expectedOnHand")]
    [InlineData(StockChangeKind.Adjust, 5, 0, "expectedOnHand")]
    public void A_change_outside_its_range_is_refused_naming_the_field(
        StockChangeKind kind, int quantity, int? expected, string field)
    {
        var problems = StockChangeRules.Validate(kind, quantity, expected, "rows[2].");

        var problem = Assert.Single(problems);
        Assert.Equal("rows[2]." + field, problem.Field);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    [Fact]
    public void An_undefined_kind_is_refused() =>
        Assert.Equal("kind", Assert.Single(StockChangeRules.Validate((StockChangeKind)99, 1, null)).Field);

    // ---- reason ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_reason_is_refused(string? reason) =>
        Assert.Equal("reason", StockChangeRules.ValidateReason(reason, out _)?.Field);

    [Fact]
    public void A_reason_is_trimmed_and_may_be_exactly_250_characters()
    {
        var longest = new string('r', InventoryMovement.ReasonMaxLength);

        Assert.Null(StockChangeRules.ValidateReason("  " + longest + "\t", out var trimmed));
        Assert.Equal(longest, trimmed);
        Assert.NotNull(StockChangeRules.ValidateReason(longest + "r", out _));
    }

    // ---- threshold -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(StockChangeRules.MaxThreshold, true)]
    [InlineData(-1, false)]
    [InlineData(StockChangeRules.MaxThreshold + 1, false)]
    public void A_threshold_is_blank_or_0_to_the_maximum(int? threshold, bool accepted) =>
        Assert.Equal(accepted, StockChangeRules.ValidateThreshold(threshold) is null);

    // ---- movement type ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(StockChangeKind.Receive, 4, InventoryMovementType.Receive)]
    [InlineData(StockChangeKind.Adjust, 4, InventoryMovementType.AdjustIn)]
    [InlineData(StockChangeKind.Adjust, -4, InventoryMovementType.AdjustOut)]
    [InlineData(StockChangeKind.SetCount, 4, InventoryMovementType.CountCorrection)]
    [InlineData(StockChangeKind.SetCount, -4, InventoryMovementType.CountCorrection)]
    [InlineData(StockChangeKind.SetCount, 0, InventoryMovementType.CountCorrection)]
    public void Each_change_writes_the_movement_type_doc_08_names(StockChangeKind kind, int delta, InventoryMovementType expected) =>
        Assert.Equal(expected, StockChangeRules.MovementTypeFor(kind, delta));

    /// <summary>The stored numbers are the check constraint's range and doc 08 §3.4's table; they never move.</summary>
    [Fact]
    public void The_movement_types_keep_doc_08_s_numbers()
    {
        Assert.Equal(
            Enumerable.Range(1, 9).Select(value => (byte)value),
            Enum.GetValues<InventoryMovementType>().Select(type => (byte)type));
        Assert.Equal(9, (byte)InventoryMovementType.CountCorrection);
        Assert.Equal(3, (byte)InventoryMovementType.AdjustIn);
        Assert.Equal(4, (byte)InventoryMovementType.AdjustOut);
    }
}
