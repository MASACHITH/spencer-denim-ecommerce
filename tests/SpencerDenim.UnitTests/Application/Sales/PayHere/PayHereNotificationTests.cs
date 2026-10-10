using System.Text.Json;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// Reading a posted PayHere notification, and the allow-list that is all of it that gets stored
/// (doc 11 P8b step 4).
/// </summary>
public sealed class PayHereNotificationTests
{
    [Fact]
    public void A_well_formed_form_is_read_field_by_field()
    {
        Assert.True(PayHereNotification.TryRead(PayHereSample.Form(), out var form));

        var notification = form!.Notification;

        Assert.Equal(PayHereSample.MerchantId, notification.MerchantId);
        Assert.Equal(PayHereSample.OrderId, notification.OrderId);
        Assert.Equal(PayHereSample.PayHerePaymentId, notification.PaymentId);
        Assert.Equal("1500.50", notification.PayhereAmount);
        Assert.Equal("LKR", notification.PayhereCurrency);
        Assert.Equal("2", notification.StatusCode);
        Assert.Equal("Successfully received the payment", notification.StatusMessage);
        Assert.Equal("VISA", notification.Method);
        Assert.Equal(32, form.Signature.Length);
    }

    [Theory]
    [InlineData("merchant_id")]
    [InlineData("order_id")]
    [InlineData("payment_id")]
    [InlineData("payhere_amount")]
    [InlineData("payhere_currency")]
    [InlineData("status_code")]
    [InlineData("md5sig")]
    public void A_form_missing_a_signed_field_is_not_a_notification(string missing)
    {
        var fields = PayHereSample.Form();
        fields.Remove(missing);

        Assert.False(PayHereNotification.TryRead(fields, out var form));
        Assert.Null(form);
    }

    [Theory]
    [InlineData("merchant_id", "")]
    [InlineData("merchant_id", "1211 149")]
    [InlineData("merchant_id", "1211149;DROP")]
    [InlineData("order_id", "SD 2026")]
    [InlineData("order_id", "SD-2026-000123-4567'--")]
    [InlineData("payment_id", "32 0032")]
    [InlineData("payment_id", "<script>")]
    [InlineData("payhere_amount", "1,500.50")]
    [InlineData("payhere_amount", "-1500.50")]
    [InlineData("payhere_amount", "1.5.0")]
    [InlineData("payhere_amount", ".50")]
    [InlineData("payhere_amount", "1500.")]
    [InlineData("payhere_amount", "1e3")]
    [InlineData("payhere_amount", "NaN")]
    [InlineData("payhere_currency", "LK")]
    [InlineData("payhere_currency", "LKRR")]
    [InlineData("payhere_currency", "L1R")]
    [InlineData("status_code", "two")]
    [InlineData("status_code", "--1")]
    [InlineData("status_code", "1234")]
    [InlineData("status_code", "+2")]
    [InlineData("md5sig", "ZZ9858CC8ADFDBE274AFABDD689EA94B")]
    [InlineData("md5sig", "299858CC8ADFDBE274AFABDD689EA94")]
    public void A_signed_field_with_the_wrong_shape_is_refused_before_anything_else_happens(string field, string value)
    {
        var fields = PayHereSample.Form();
        fields[field] = value;

        Assert.False(PayHereNotification.TryRead(fields, out _));
    }

    [Fact]
    public void A_signed_field_longer_than_any_real_one_is_refused()
    {
        var fields = PayHereSample.Form();
        fields["payment_id"] = new string('9', 65);

        Assert.False(PayHereNotification.TryRead(fields, out _));
    }

    [Fact]
    public void The_stored_payload_is_an_allow_list_and_never_holds_card_data_or_the_signature()
    {
        var fields = PayHereSample.Form();
        fields["card_holder_name"] = "Kamal Perera";
        fields["card_no"] = "************1234";
        fields["card_expiry"] = "12/30";
        fields["custom_1"] = "anything";
        fields["custom_2"] = "else";
        fields["captured_amount"] = "1500.50";
        fields["recurring"] = "0";

        Assert.True(PayHereNotification.TryRead(fields, out var form));

        var json = form!.Notification.ToStoredJson();

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(
            [
                "merchant_id",
                "order_id",
                "payment_id",
                "payhere_amount",
                "payhere_currency",
                "status_code",
                "status_message",
                "method",
            ],
            names);

        // Said again as the property that matters, in case the list above is ever extended carelessly.
        foreach (var forbidden in new[] { "Kamal", "****", "12/30", "card", "md5sig", fields["md5sig"], "anything" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_stored_payload_reads_back_as_the_same_notification()
    {
        var original = PayHereSample.Notification(status: "-2", message: "Card declined");

        var read = PayHereNotification.FromStoredJson(original.ToStoredJson());

        Assert.Equal(original, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"truncated\":true,\"originalLength\":70000}")]
    public void A_stored_payload_that_is_not_a_notification_reads_as_nothing(string? json)
    {
        Assert.Null(PayHereNotification.FromStoredJson(json));
    }

    [Theory]
    [InlineData("2", PayHereStatus.Success, "payment.success")]
    [InlineData("0", PayHereStatus.Pending, "payment.pending")]
    [InlineData("-1", PayHereStatus.Cancelled, "payment.cancelled")]
    [InlineData("-2", PayHereStatus.Failed, "payment.failed")]
    [InlineData("-3", PayHereStatus.ChargedBack, "payment.chargeback")]
    public void The_five_documented_status_codes_are_recognised(string code, PayHereStatus expected, string eventType)
    {
        var notification = PayHereSample.Notification(status: code);

        Assert.Equal(expected, notification.Status);
        Assert.Equal(eventType, notification.EventType);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("-4")]
    [InlineData("99")]
    public void Any_other_status_code_is_unknown_and_has_its_own_event_type(string code)
    {
        var notification = PayHereSample.Notification(status: code);

        Assert.Null(notification.Status);
        Assert.Equal("payment.status_" + code, notification.EventType);
    }

    [Theory]
    [InlineData("1500.50", 1500.50)]
    [InlineData("1500.5", 1500.5)]
    [InlineData("1500", 1500)]
    [InlineData("0.01", 0.01)]
    [InlineData("1234567.89", 1234567.89)]
    public void The_amount_is_parsed_in_the_invariant_culture(string text, double expected)
    {
        Assert.Equal((decimal)expected, PayHereSample.Notification(amount: text).AmountValue);
    }

    [Fact]
    public void The_unsigned_text_fields_are_cleaned_and_clipped()
    {
        var fields = PayHereSample.Form();
        fields["status_message"] = "  Declined\r\n\tby   the\u0000 bank  " + new string('x', 400);
        fields["method"] = "VISA\u0007";

        Assert.True(PayHereNotification.TryRead(fields, out var form));

        var notification = form!.Notification;

        Assert.StartsWith("Declined by the bank xxx", notification.StatusMessage, StringComparison.Ordinal);
        Assert.True(notification.StatusMessage!.Length <= PayHereNotification.StatusMessageMaxLength);
        Assert.DoesNotContain(notification.StatusMessage, char.IsControl);
        Assert.Equal("VISA", notification.Method);
    }

    [Fact]
    public void Absent_or_blank_unsigned_fields_are_simply_absent()
    {
        var fields = PayHereSample.Form();
        fields.Remove("status_message");
        fields["method"] = "   ";

        Assert.True(PayHereNotification.TryRead(fields, out var form));
        Assert.Null(form!.Notification.StatusMessage);
        Assert.Null(form.Notification.Method);

        using var document = JsonDocument.Parse(form.Notification.ToStoredJson());
        Assert.False(document.RootElement.TryGetProperty("status_message", out _));
        Assert.False(document.RootElement.TryGetProperty("method", out _));
    }

    [Fact]
    public void The_failure_message_is_PayHeres_when_it_sent_one_and_ours_when_it_did_not()
    {
        Assert.Equal("Card declined", PayHereSample.Notification(message: "Card declined").FailureMessage("fallback"));
        Assert.Equal("fallback", PayHereSample.Notification(message: null).FailureMessage("fallback"));
    }
}
