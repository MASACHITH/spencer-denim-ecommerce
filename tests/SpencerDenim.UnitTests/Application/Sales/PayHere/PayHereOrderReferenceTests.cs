using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// The <c>order_id</c> the shop gives PayHere and reads back (doc 11 P8b step 1): <c>{OrderNumber}-{PaymentId}</c>,
/// a strict inverse, within PayHere's limits.
/// </summary>
public sealed class PayHereOrderReferenceTests
{
    [Fact]
    public void The_reference_is_the_order_number_a_hyphen_and_the_payment_id()
    {
        Assert.Equal("SD-2026-000123-4567", PayHereOrderReference.Format("SD-2026-000123", 4567));
    }

    [Theory]
    [InlineData("SD-2026-000123", 1)]
    [InlineData("SD-2026-000123", 4567)]
    [InlineData("SD-2026-999999", int.MaxValue)]
    [InlineData("SD-2031-1234567", 90210)]
    public void A_reference_round_trips(string orderNumber, int paymentId)
    {
        var reference = PayHereOrderReference.Format(orderNumber, paymentId);

        Assert.True(PayHereOrderReference.TryParse(reference, out var parsedNumber, out var parsedId));
        Assert.Equal(orderNumber, parsedNumber);
        Assert.Equal(paymentId, parsedId);
    }

    [Fact]
    public void The_longest_reference_is_within_PayHeres_limits()
    {
        // A 20-character order number (the column's width) and the largest payment id: 31 characters.
        var reference = PayHereOrderReference.Format(new string('A', 20), int.MaxValue);

        Assert.Equal(31, reference.Length);
        Assert.Equal(PayHereOrderReference.MaxLength, reference.Length);
        Assert.True(reference.Length <= 50);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("4567")]
    [InlineData("SD-2026-000123")]
    [InlineData("SD-2026-000123-")]
    [InlineData("-4567")]
    [InlineData("SD-2026-000123-0")]
    [InlineData("SD-2026-000123-007")]
    [InlineData("SD-2026-000123-+7")]
    [InlineData("SD-2026-000123- 7")]
    [InlineData("SD-2026-000123-7 ")]
    [InlineData("SD-2026-000123--5")]
    [InlineData("SD-2026-000123-1.5")]
    [InlineData("SD-2026-000123-0x10")]
    [InlineData("SD-2026-000123-99999999999")]
    [InlineData("SD_2026_000123-4567")]
    [InlineData("SD-2026-000123;DROP-4567")]
    [InlineData("SD-2026-000123\n-4567")]
    [InlineData("SD-2026-0001٣-4567")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA-4567")]
    public void Anything_this_class_would_not_have_written_is_refused(string? reference)
    {
        Assert.False(PayHereOrderReference.TryParse(reference, out var orderNumber, out var paymentId));
        Assert.Equal(string.Empty, orderNumber);
        Assert.Equal(0, paymentId);
    }

    /// <summary>
    /// A reference with more hyphens than ours is read with the last one as the separator - the strict
    /// inverse - and so names an order number that no order has; the caller's lookup finds nothing.
    /// </summary>
    [Fact]
    public void The_last_hyphen_is_the_separator()
    {
        Assert.True(PayHereOrderReference.TryParse("SD-2026-000123-4567-1", out var orderNumber, out var paymentId));
        Assert.Equal("SD-2026-000123-4567", orderNumber);
        Assert.Equal(1, paymentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SD 2026")]
    [InlineData("SD-2026-0001;")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA")]
    public void An_order_number_that_is_not_one_cannot_be_formatted(string orderNumber)
    {
        Assert.Throws<ArgumentException>(() => PayHereOrderReference.Format(orderNumber, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_payment_id_must_be_positive(int paymentId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PayHereOrderReference.Format("SD-2026-000123", paymentId));
    }
}
