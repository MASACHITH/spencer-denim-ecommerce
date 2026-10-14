using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// Reading a PayPal webhook body (doc 08 §3.5 DB13). The body is verified as PayPal's before it is parsed for
/// action, but a verified body can still be a type or a shape this build has not seen, and the right answer to
/// that is to record it and do nothing - never an exception that fails the event for ever.
/// </summary>
public sealed class PayPalWebhookEventParserTests
{
    internal static string Capture(
        string eventType,
        string eventId = "WH-1AB23456CD789012E-34567890FG123456H",
        string captureId = "8AA831015G517922L",
        string amount = "53.55",
        string currency = "USD",
        string? orderId = "5O190127TN364715T",
        string? customId = "pay-481",
        string? status = "COMPLETED") =>
        $$"""
        {
          "id": "{{eventId}}",
          "event_version": "1.0",
          "create_time": "2026-10-10T09:08:45Z",
          "resource_type": "capture",
          "event_type": "{{eventType}}",
          "summary": "Payment completed for $ {{amount}} USD",
          "resource": {
            "id": "{{captureId}}",
            "amount": { "currency_code": "{{currency}}", "value": "{{amount}}" },
            "final_capture": true,
            "status": "{{status}}",
            {{(customId is null ? string.Empty : $"\"custom_id\": \"{customId}\",")}}
            "supplementary_data": { "related_ids": { {{(orderId is null ? string.Empty : $"\"order_id\": \"{orderId}\"")}} } },
            "status_details": { "reason": "ECHECK" }
          },
          "links": []
        }
        """;

    [Theory]
    [InlineData("PAYMENT.CAPTURE.COMPLETED", PayPalCaptureStatus.Completed)]
    [InlineData("PAYMENT.CAPTURE.PENDING", PayPalCaptureStatus.Pending)]
    [InlineData("PAYMENT.CAPTURE.DENIED", PayPalCaptureStatus.Declined)]
    [InlineData("PAYMENT.CAPTURE.DECLINED", PayPalCaptureStatus.Declined)]
    [InlineData("PAYMENT.CAPTURE.REFUNDED", PayPalCaptureStatus.Refunded)]
    [InlineData("PAYMENT.CAPTURE.REVERSED", PayPalCaptureStatus.Reversed)]
    public void A_capture_event_maps_to_what_happened_to_the_capture(string type, PayPalCaptureStatus expected)
    {
        Assert.True(PayPalWebhookEventParser.TryParse(Capture(type), out var parsed));

        Assert.Equal(expected, parsed!.CaptureStatus);
        Assert.True(parsed.IsCaptureEvent);
    }

    [Theory]
    [InlineData("CHECKOUT.ORDER.APPROVED")]
    [InlineData("CHECKOUT.ORDER.COMPLETED")]
    [InlineData("PAYMENT.CAPTURE.SOMETHING.NEW")]
    [InlineData("BILLING.SUBSCRIPTION.CREATED")]
    public void An_event_type_this_build_does_not_act_on_parses_and_is_not_a_capture_event(string type)
    {
        Assert.True(PayPalWebhookEventParser.TryParse(Capture(type), out var parsed));

        Assert.Null(parsed!.CaptureStatus);
        Assert.False(parsed.IsCaptureEvent);
    }

    [Fact]
    public void Every_identifier_the_shop_attributes_an_event_by_is_read()
    {
        Assert.True(PayPalWebhookEventParser.TryParse(Capture("PAYMENT.CAPTURE.COMPLETED"), out var parsed));

        Assert.Equal("WH-1AB23456CD789012E-34567890FG123456H", parsed!.EventId);
        Assert.Equal("PAYMENT.CAPTURE.COMPLETED", parsed.EventType);
        Assert.Equal("8AA831015G517922L", parsed.ResourceId);
        Assert.Equal("5O190127TN364715T", parsed.RelatedOrderId);
        Assert.Equal("pay-481", parsed.CustomId);
        Assert.Equal(53.55m, parsed.Amount);
        Assert.Equal("USD", parsed.Currency);
        Assert.Equal("ECHECK", parsed.Reason);
    }

    [Fact]
    public void A_missing_optional_field_is_null_and_not_an_error()
    {
        Assert.True(PayPalWebhookEventParser.TryParse(
            Capture("PAYMENT.CAPTURE.COMPLETED", orderId: null, customId: null),
            out var parsed));

        Assert.Null(parsed!.RelatedOrderId);
        Assert.Null(parsed.CustomId);
        Assert.Equal("8AA831015G517922L", parsed.ResourceId);
    }

    [Fact]
    public void An_event_with_no_resource_still_parses()
    {
        Assert.True(PayPalWebhookEventParser.TryParse(
            """{ "id": "WH-1", "event_type": "PAYMENT.CAPTURE.COMPLETED" }""",
            out var parsed));

        Assert.Equal("WH-1", parsed!.EventId);
        Assert.Null(parsed.ResourceId);
        Assert.Null(parsed.Amount);
    }

    [Fact]
    public void An_amount_that_is_not_a_number_is_null_so_a_completed_one_is_treated_as_a_mismatch()
    {
        Assert.True(PayPalWebhookEventParser.TryParse(
            Capture("PAYMENT.CAPTURE.COMPLETED", amount: "fifty"),
            out var parsed));

        Assert.Null(parsed!.Amount);
    }

    [Fact]
    public void The_amount_is_read_in_the_invariant_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.True(PayPalWebhookEventParser.TryParse(Capture("PAYMENT.CAPTURE.COMPLETED"), out var parsed));
            Assert.Equal(53.55m, parsed!.Amount);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    [InlineData("""{ "id": "WH-1" }""")]
    [InlineData("""{ "event_type": "" }""")]
    [InlineData("""{ "event_type": 5 }""")]
    [InlineData("{ \"event_type\": \"PAYMENT.CAPTURE.COMPLETED\"")]
    public void A_body_that_cannot_be_an_event_is_refused(string body)
    {
        Assert.False(PayPalWebhookEventParser.TryParse(body, out var parsed));
        Assert.Null(parsed);
    }
}
