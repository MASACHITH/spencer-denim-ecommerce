using System.Text.Json;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The <c>PaymentException</c> alert (doc 08 §4.2 step 3.4, doc 11 P8): what staff are told when money has
/// arrived for an order that cannot take it. Pure, so the whole payload is asserted — including what it
/// must not contain.
/// </summary>
public sealed class PaymentExceptionAlertTests
{
    private static readonly Guid OrderPublicId = Guid.Parse("3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f");
    private static readonly DateTime Now = new(2026, 10, 10, 6, 30, 0, DateTimeKind.Utc);

    private static PaymentExceptionFacts Facts(
        string reason = PaymentExceptionReasons.OrderExpired,
        decimal? confirmedAmount = 53.55m,
        string? confirmedCurrency = "USD") =>
        new(
            PaymentId: 481,
            ProviderCode: "PayPal",
            OrderPublicId,
            OrderNumber: "CW-2026-000123",
            reason,
            ExpectedAmount: 53.55m,
            ExpectedCurrency: "USD",
            confirmedAmount,
            confirmedCurrency);

    [Fact]
    public void It_is_a_warning_PaymentException_about_the_order()
    {
        var alert = PaymentExceptionAlert.For(Facts(), Now);

        // Doc 11 §4.3: PaymentException is a Warning. The type is from the shared vocabulary so the
        // visibility rule for Orders.Approve and Orders.Refund holders (R10) applies to it.
        Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
        Assert.Equal(Now, alert.CreatedAtUtc);

        // What a staff member opens: the order, by the token the admin route takes.
        Assert.Equal(AdminAlert.OrderEntityType, alert.RelatedEntityType);
        Assert.Equal(OrderPublicId.ToString("D"), alert.RelatedEntityId);
        Assert.Null(alert.AuditLogId);
        Assert.Null(alert.AcknowledgedAtUtc);
    }

    [Fact]
    public void The_title_states_the_facts_and_names_the_order_and_the_payment()
    {
        var alert = PaymentExceptionAlert.For(Facts(), Now);

        Assert.Equal(
            "Payment received but not applied to order CW-2026-000123: the order had expired (payment 481)",
            alert.Title);
        Assert.True(alert.Title.Length <= AdminAlert.TitleMaxLength);
    }

    [Theory]
    [InlineData(PaymentExceptionReasons.OrderExpired, "the order had expired")]
    [InlineData(PaymentExceptionReasons.OrderCancelled, "the order had been cancelled")]
    [InlineData(PaymentExceptionReasons.OrderAlreadyPaid, "the order was already paid")]
    [InlineData(PaymentExceptionReasons.AmountMismatch, "the amount or currency did not match")]
    [InlineData(PaymentExceptionReasons.OrderNotPayable, "the order could not take payment")]
    [InlineData(PaymentExceptionReasons.ProviderDataInvalid, "the provider's reference could not be stored")]
    public void Every_reason_has_its_own_wording(string reason, string expected)
    {
        var alert = PaymentExceptionAlert.For(Facts(reason), Now);

        Assert.Contains(expected, alert.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_details_are_keys_ids_and_amounts_in_a_fixed_order()
    {
        var alert = PaymentExceptionAlert.For(
            Facts(PaymentExceptionReasons.AmountMismatch, confirmedAmount: 40.00m, confirmedCurrency: "USD"),
            Now);

        Assert.Equal(
            "{\"paymentId\":481,\"provider\":\"PayPal\",\"reason\":\"amount_mismatch\","
            + "\"orderNumber\":\"CW-2026-000123\",\"orderPublicId\":\"3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f\","
            + "\"expectedAmount\":53.55,\"expectedCurrency\":\"USD\","
            + "\"confirmedAmount\":40.00,\"confirmedCurrency\":\"USD\"}",
            alert.DetailsJson);

        // CK_AdminAlerts_DetailsJson (ISJSON).
        using var document = JsonDocument.Parse(alert.DetailsJson!);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void An_amount_the_caller_did_not_know_is_absent_not_null()
    {
        var alert = PaymentExceptionAlert.For(Facts(confirmedAmount: null, confirmedCurrency: null), Now);

        using var document = JsonDocument.Parse(alert.DetailsJson!);

        // A null would read as "the provider took nothing", which is not what "unknown" means.
        Assert.False(document.RootElement.TryGetProperty("confirmedAmount", out _));
        Assert.False(document.RootElement.TryGetProperty("confirmedCurrency", out _));
        Assert.True(document.RootElement.TryGetProperty("expectedAmount", out _));
    }

    [Fact]
    public void Nothing_about_the_customer_or_the_provider_s_transaction_is_in_the_alert()
    {
        // Doc 08 §3.1 step 8 searches ops.AdminAlerts for a customer's name, email and telephone number,
        // and nothing anonymises these rows. The facts do not even carry those fields, so this pins the
        // shape: if somebody adds one to the record, this list of keys is what they have to explain.
        var alert = PaymentExceptionAlert.For(Facts(), Now);

        using var document = JsonDocument.Parse(alert.DetailsJson!);

        Assert.Equal(
            [
                "paymentId",
                "provider",
                "reason",
                "orderNumber",
                "orderPublicId",
                "expectedAmount",
                "expectedCurrency",
                "confirmedAmount",
                "confirmedCurrency",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void A_currency_that_is_not_a_currency_code_is_clipped_in_the_details()
    {
        // What a provider sends in place of "USD" is not something to copy into a table every Super Admin's
        // banner reads.
        var alert = PaymentExceptionAlert.For(Facts(confirmedCurrency: new string('C', 500)), Now);

        using var document = JsonDocument.Parse(alert.DetailsJson!);

        Assert.Equal(
            new string('C', PaymentExceptionAlert.ConfirmedCurrencyMaxLength),
            document.RootElement.GetProperty("confirmedCurrency").GetString());
    }

    [Fact]
    public void A_title_that_would_not_fit_the_column_is_clipped_not_dropped()
    {
        // An alert withheld because its title was long would restore the silence it exists to end.
        var facts = Facts() with { OrderNumber = new string('9', 400) };

        var alert = PaymentExceptionAlert.For(facts, Now);

        Assert.Equal(AdminAlert.TitleMaxLength, alert.Title.Length);
    }

    [Fact]
    public void Every_reason_the_port_names_is_distinct()
    {
        Assert.Equal(
            PaymentExceptionReasons.All.Count,
            PaymentExceptionReasons.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("AMOUNT_MISMATCH", PaymentFailureCodes.AmountMismatch);
    }
}
