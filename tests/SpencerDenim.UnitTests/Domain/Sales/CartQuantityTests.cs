using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Sales;

/// <summary>
/// Doc 08 §3.5's two quantity rules, which are not the same rule (CART-01).
/// </summary>
public sealed class CartQuantityTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void What_a_caller_may_ask_for_is_one_to_ten(int quantity, bool requestable) =>
        Assert.Equal(requestable, CartQuantity.IsRequestable(quantity));

    /// <summary>
    /// The add's arithmetic, which clamps instead of refusing: doc 08 §3.5's
    /// <c>IIF(Quantity + @q &gt; 10, 10, Quantity + @q)</c>. This mirrors the SQL, so a failure here and a
    /// failure in the integration suite's concurrency rounds mean different things — this one means the two
    /// have drifted apart.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(6, 6, 10)]
    [InlineData(9, 1, 10)]
    [InlineData(10, 1, 10)]
    [InlineData(10, 10, 10)]
    [InlineData(1, 9, 10)]
    public void An_add_sums_and_caps_at_ten(int existing, int add, short expected) =>
        Assert.Equal(expected, CartQuantity.ClampTotal(existing, add));

    /// <summary>
    /// An add large enough to overflow <see cref="short"/>, or even <see cref="int"/>, still lands on 10.
    /// The sum is computed in <see cref="long"/> for exactly this reason: <c>(short)(10000 + 30000)</c>
    /// wraps negative, and a negative quantity would violate the check constraint rather than being clamped.
    /// </summary>
    [Theory]
    [InlineData(10, int.MaxValue)]
    [InlineData(1, int.MaxValue)]
    [InlineData(30_000, 30_000)]
    public void An_add_that_would_overflow_still_lands_on_ten(int existing, int add) =>
        Assert.Equal(CartQuantity.Max, CartQuantity.ClampTotal(existing, add));

    /// <summary>The floor is 1: a clamp never produces a quantity the check constraint would refuse.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -5)]
    [InlineData(0, -1)]
    public void A_clamp_never_produces_a_quantity_below_one(int existing, int add) =>
        Assert.Equal(CartQuantity.Min, CartQuantity.ClampTotal(existing, add));

    /// <summary>
    /// The three constants are one number in three places, and a drift between them would be a check
    /// constraint that disagrees with the code that is trying to satisfy it.
    /// </summary>
    [Fact]
    public void The_ceiling_is_the_same_number_everywhere()
    {
        Assert.Equal(CartItem.MaxQuantity, CartQuantity.Max);
        Assert.Equal(CartItem.MinQuantity, CartQuantity.Min);
        Assert.Equal<short>(10, CartQuantity.Max);
    }
}
