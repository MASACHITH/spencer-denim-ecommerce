using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// The dollar amount PayPal is charged (doc 08 §3.5 <c>ProviderAmount</c>, doc 11 P8 "Currency", D1):
/// <c>round(total ÷ LKR per USD × (1 + buffer%), 2)</c>. It is the figure the captured amount is later compared
/// against, to the cent (doc 08 §4.2 step 3.1), so the arithmetic is pinned here rather than trusted.
/// </summary>
public sealed class PayPalUsdAmountTests
{
    /// <summary>Doc 11 P8's own worked example.</summary>
    [Fact]
    public void Rs_15596_60_at_300_with_a_3_percent_buffer_is_USD_53_55()
    {
        Assert.Equal(53.55m, PayPalUsdAmount.Compute(15_596.60m, 300.00m, 3.00m));
    }

    [Theory]
    // 1,000 / 300 = 3.3333…; × 1.03 = 3.4333… -> 3.43
    [InlineData(1_000, 300, 3, 3.43)]
    // No buffer: 9,184 / 300 = 30.6133… -> 30.61
    [InlineData(9_184, 300, 0, 30.61)]
    // Exactly representable: 3,000 / 300 = 10.00; × 1.10 = 11.00
    [InlineData(3_000, 300, 10, 11.00)]
    // A rate with decimals: 15,596.60 / 301.25 = 51.7728…; × 1.03 = 53.3260… -> 53.33
    [InlineData(15_596.60, 301.25, 3, 53.33)]
    // A larger order: 250,000 / 312.50 = 800.00; × 1.03 = 824.00
    [InlineData(250_000, 312.50, 3, 824.00)]
    public void The_amount_is_worked_out_in_decimal_and_rounded_to_the_cent(
        double lkr,
        double rate,
        double buffer,
        double expected)
    {
        Assert.Equal(
            (decimal)expected,
            PayPalUsdAmount.Compute((decimal)lkr, (decimal)rate, (decimal)buffer));
    }

    /// <summary>
    /// Half a cent rounds up, the commercial convention - and not to the even cent, which is what
    /// <see cref="Math.Round(decimal, int)"/> does by default and would charge a different amount than the
    /// customer was shown on every second boundary.
    /// </summary>
    [Theory]
    // 1.50 / 300 = 0.005 exactly -> 0.01 (away from zero), not 0.00 (to even)
    [InlineData(1.50, 300, 0, 0.01)]
    // 7.50 / 300 = 0.025 -> 0.03 (to-even would give 0.02)
    [InlineData(7.50, 300, 0, 0.03)]
    // 12.50 / 100 = 0.125 -> 0.13 (to-even would give 0.12)
    [InlineData(12.50, 100, 0, 0.13)]
    public void Half_a_cent_rounds_up(double lkr, double rate, double buffer, double expected)
    {
        Assert.Equal(
            (decimal)expected,
            PayPalUsdAmount.Compute((decimal)lkr, (decimal)rate, (decimal)buffer));
    }

    /// <summary>
    /// A tiny order converted at a high rate would round to zero dollars, which PayPal refuses; one cent is the
    /// nearest chargeable amount.
    /// </summary>
    [Fact]
    public void An_amount_that_rounds_to_zero_is_raised_to_one_cent()
    {
        Assert.Equal(0.01m, PayPalUsdAmount.Compute(1.00m, 300m, 3m));
        Assert.Equal(PayPalUsdAmount.Minimum, PayPalUsdAmount.Compute(0.10m, 10_000m, 0m));
    }

    [Theory]
    [InlineData(0, 300, 3)]
    [InlineData(-5, 300, 3)]
    [InlineData(100, 0, 3)]
    [InlineData(100, -300, 3)]
    [InlineData(100, 300, -1)]
    public void Nonsense_inputs_are_refused_rather_than_charged(double lkr, double rate, double buffer)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PayPalUsdAmount.Compute((decimal)lkr, (decimal)rate, (decimal)buffer));
    }

    [Theory]
    [InlineData(53.55, "53.55")]
    [InlineData(10, "10.00")]
    [InlineData(0.5, "0.50")]
    [InlineData(1234.5, "1234.50")]
    public void The_amount_is_written_to_PayPal_with_two_places_and_no_grouping(double usd, string expected)
    {
        Assert.Equal(expected, PayPalUsdAmount.Format((decimal)usd));
    }

    [Fact]
    public void Formatting_ignores_the_culture_of_the_server()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal("53.55", PayPalUsdAmount.Format(53.55m));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void An_initiation_carries_the_frozen_rate_buffer_and_amount_and_is_due_after_the_window()
    {
        var settings = new PayPalSettings { LkrPerUsd = 300m, FxBufferPercent = 3m, ExpiryMinutes = 60 };
        var placed = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

        var initiation = PayPalInitiationPolicy.Build(settings, 15_596.60m, placed);

        Assert.Equal(PaymentStatus.Initiated, initiation.Status);
        Assert.Equal("USD", initiation.ProviderCurrency);
        Assert.Equal(53.55m, initiation.ProviderAmount);
        Assert.Equal(300m, initiation.ExchangeRate);
        Assert.Equal(3m, initiation.FxBufferPercent);
        Assert.Equal(placed.AddMinutes(60), initiation.PaymentDueAtUtc);
    }

    [Fact]
    public void An_initiation_without_a_rate_is_a_programming_error()
    {
        Assert.Throws<InvalidOperationException>(
            () => PayPalInitiationPolicy.Build(new PayPalSettings(), 1_000m, DateTime.UtcNow));
    }
}

/// <summary>
/// The idempotency keys sent to PayPal. They are pure functions of the payment id, which is what makes a
/// repeated call safe (doc 08 §4.2 step 2).
/// </summary>
public sealed class PayPalRequestIdTests
{
    [Fact]
    public void The_capture_request_id_is_capture_dash_the_payment_id()
    {
        Assert.Equal("capture-481", PayPalRequestIds.Capture(481));
    }

    [Fact]
    public void A_request_id_is_stable_across_calls_and_different_for_different_payments()
    {
        Assert.Equal(PayPalRequestIds.Capture(7), PayPalRequestIds.Capture(7));
        Assert.Equal(PayPalRequestIds.Create(7), PayPalRequestIds.Create(7));
        Assert.NotEqual(PayPalRequestIds.Capture(7), PayPalRequestIds.Capture(8));

        // A create and a capture on one payment must not share a key: PayPal would answer the second with
        // the first one's response.
        Assert.NotEqual(PayPalRequestIds.Create(7), PayPalRequestIds.Capture(7));
    }

    [Fact]
    public void The_request_id_does_not_depend_on_the_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");

            Assert.Equal("capture-1234567", PayPalRequestIds.Capture(1_234_567));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("pay-12", 12)]
    [InlineData("pay-1", 1)]
    [InlineData("pay-2147483647", 2147483647)]
    public void A_custom_id_we_wrote_gives_its_payment_back(string customId, int expected)
    {
        Assert.Equal(expected, PayPalRequestIds.PaymentIdFromCustomId(customId));
        Assert.Equal(customId, PayPalRequestIds.CustomId(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("pay-")]
    [InlineData("pay-0")]
    [InlineData("pay--3")]
    [InlineData("pay-12x")]
    [InlineData("PAY-12")]
    [InlineData("order-12")]
    [InlineData("pay-99999999999")]
    public void Anything_else_is_not_ours(string? customId)
    {
        Assert.Null(PayPalRequestIds.PaymentIdFromCustomId(customId));
    }
}
