using System.Text.Json;
using SpencerDenim.Application.Sales.Payments;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// What may be written into <c>sales.PaymentEvents.PayloadJson</c> (doc 08 §3.5, DB13/DB15): valid JSON
/// object or array, secrets overwritten, size capped, and otherwise <b>verbatim</b>.
/// </summary>
public sealed class PaymentEventPayloadTests
{
    [Fact]
    public void A_payload_with_nothing_to_hide_is_stored_exactly_as_it_arrived()
    {
        // Whitespace and number formatting are the provider's; a dispute needs them back unchanged, so a
        // payload nothing touched must not be re-serialised.
        const string original = "{ \"id\" : \"WH-1\",\n  \"amount\": 1500.00, \"nested\": { \"a\": [1, 2.50] } }";

        var prepared = PaymentEventPayload.Prepare(original);

        Assert.Equal(original, prepared.Json);
        Assert.False(prepared.Redacted);
        Assert.False(prepared.Truncated);
        Assert.Equal(original.Length, prepared.OriginalLength);
    }

    [Fact]
    public void An_array_payload_is_accepted()
    {
        var prepared = PaymentEventPayload.Prepare("[{\"id\":1},{\"id\":2}]");

        Assert.Equal("[{\"id\":1},{\"id\":2}]", prepared.Json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_payload_is_refused(string? payload)
    {
        // NOT NULL in the database: an event with no payload could never be reprocessed or disputed.
        Assert.Throws<ArgumentException>(() => PaymentEventPayload.Prepare(payload));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("{} trailing")]
    [InlineData("merchant_id=1&order_id=2")]
    public void A_payload_that_is_not_json_is_refused_before_the_database_is_asked(string payload)
    {
        // A form post is the realistic case: the caller converts it to an object first. Reported here with
        // a message, not as CK_PaymentEvents_PayloadJson (ISJSON) violation number 547.
        var thrown = Assert.Throws<ArgumentException>(() => PaymentEventPayload.Prepare(payload));

        Assert.Contains("JSON", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void A_bare_scalar_is_refused_because_ISJSON_does_not_agree_across_versions(string payload)
    {
        Assert.Throws<ArgumentException>(() => PaymentEventPayload.Prepare(payload));
    }

    [Fact]
    public void A_payload_nested_deeper_than_any_real_event_is_refused()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 100)) + string.Concat(Enumerable.Repeat("]", 100));

        Assert.Throws<ArgumentException>(() => PaymentEventPayload.Prepare(deep));
    }

    [Fact]
    public void A_secret_named_property_is_overwritten_at_any_depth_and_everything_else_is_kept()
    {
        const string payload = """
            {
              "id": "EVT-1",
              "client_secret": "s3cr3t-value",
              "resource": {
                "amount": "1500.00",
                "merchantSecret": "another-secret",
                "items": [ { "Authorization": "Bearer abc.def", "sku": "SDJ-001" } ]
              },
              "payment_token": "reference-not-a-credential"
            }
            """;

        var prepared = PaymentEventPayload.Prepare(payload);

        Assert.True(prepared.Redacted);
        Assert.DoesNotContain("s3cr3t-value", prepared.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", prepared.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", prepared.Json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(prepared.Json);
        var root = document.RootElement;

        Assert.Equal("EVT-1", root.GetProperty("id").GetString());
        Assert.Equal(PaymentEventPayload.RedactedValue, root.GetProperty("client_secret").GetString());
        Assert.Equal("1500.00", root.GetProperty("resource").GetProperty("amount").GetString());
        Assert.Equal(
            PaymentEventPayload.RedactedValue,
            root.GetProperty("resource").GetProperty("merchantSecret").GetString());
        Assert.Equal("SDJ-001", root.GetProperty("resource").GetProperty("items")[0].GetProperty("sku").GetString());
        Assert.Equal(
            PaymentEventPayload.RedactedValue,
            root.GetProperty("resource").GetProperty("items")[0].GetProperty("Authorization").GetString());

        // "token" is redacted whole (see SecretFragments): the log is safe first.
        Assert.Equal(PaymentEventPayload.RedactedValue, root.GetProperty("payment_token").GetString());
    }

    [Theory]
    // A PAN in the spellings a card response can carry it. The project chose an allow-list for the far
    // less sensitive redirect-URL problem because a block-list "is always one scheme short"; the same
    // argument applies with more force to a card number stored verbatim for dispute reading.
    [InlineData("card_number")]
    [InlineData("cardNumber")]
    [InlineData("CARD-NUMBER")]
    [InlineData("funding_card_number")]
    [InlineData("pan")]
    [InlineData("PAN")]
    [InlineData("client_secret")]
    [InlineData("clientSecret")]
    [InlineData("merchant-secret")]
    [InlineData("PASSWORD")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("private-key")]
    [InlineData("cvv")]
    [InlineData("token")]
    [InlineData("payment_token")]
    [InlineData("X-Auth-Token")]
    [InlineData("id_token")]
    [InlineData("credential")]
    [InlineData("credentials")]
    [InlineData("user.credentials")]
    [InlineData("pwd")]
    [InlineData("Pwd")]
    [InlineData("passphrase")]
    [InlineData("pass_phrase")]
    [InlineData("auth")]
    [InlineData("Authorization")]
    [InlineData("proxy-authorization")]
    [InlineData("AUTH")]
    [InlineData("auth_key")]
    [InlineData("card_cvv")]
    [InlineData("CVC")]
    [InlineData("cvv2")]
    [InlineData("card.cvc2")]
    [InlineData("tokenization_token")]
    public void Every_spelling_of_a_credential_name_is_recognised(string propertyName)
    {
        var payload = $"{{\"{propertyName}\":\"do-not-store\",\"keep\":\"yes\"}}";

        var prepared = PaymentEventPayload.Prepare(payload);

        Assert.True(prepared.Redacted);
        Assert.DoesNotContain("do-not-store", prepared.Json, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"yes\"", prepared.Json, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>What the rule must leave alone is as much its contract as what it takes.</b> These are the fields a
    /// payment dispute reads — PayPal's capture webhook and PayHere's notification, plus the ones that merely
    /// share a stem with a credential word (<c>authorization_id</c> is not an authorization header,
    /// <c>author</c> is not <c>auth</c>). A signature (<c>md5sig</c>) is not redacted by the store: the provider
    /// decides, and the recommendation is to drop it once verified.
    /// </summary>
    [Theory]
    [InlineData("payment_id")]
    [InlineData("order_id")]
    [InlineData("merchant_id")]
    [InlineData("payer_id")]
    [InlineData("id")]
    [InlineData("event_type")]
    [InlineData("status_code")]
    [InlineData("status_message")]
    [InlineData("payhere_amount")]
    [InlineData("payhere_currency")]
    [InlineData("captured_amount")]
    [InlineData("custom_id")]
    [InlineData("invoice_id")]
    [InlineData("authorization_id")]
    [InlineData("authorizations")]
    [InlineData("authorised_amount")]
    [InlineData("author")]
    [InlineData("card_holder_name")]
    [InlineData("card_no")]
    [InlineData("method")]
    [InlineData("md5sig")]
    [InlineData("amount")]
    [InlineData("currency_code")]
    [InlineData("related_ids")]
    [InlineData("cvv_code")]
    [InlineData("cvc_check")]
    [InlineData("tokenization")]
    [InlineData("tokenized")]
    [InlineData("card_cvv_result")]
    // Every one of these contains "pan" once normalised, which is why "pan" is an exact name and not a
    // fragment. PayPal sends company_name on a business account and an address line can say Japan, so a
    // fragment rule here would redact the payer's own identity out of a dispute record.
    [InlineData("company_name")]
    [InlineData("company_id")]
    [InlineData("expand")]
    [InlineData("panel")]
    [InlineData("japan")]
    public void A_field_a_payment_dispute_reads_is_never_redacted(string propertyName)
    {
        Assert.False(PaymentEventPayload.LooksSecret(propertyName), propertyName);

        var payload = $"{{\"{propertyName}\":\"kept\"}}";
        var prepared = PaymentEventPayload.Prepare(payload);

        Assert.False(prepared.Redacted);
        Assert.Equal(payload, prepared.Json);
    }

    /// <summary>
    /// The flags and result letters of a card response are not secrets: <c>cvv_code</c> is "M" for a match,
    /// <c>cvc_check</c> is "pass". The three digits on the back of a card (<c>cvv</c>) still are.
    /// </summary>
    [Fact]
    public void A_cards_verification_results_are_kept_and_its_verification_value_is_not()
    {
        const string payload = """
            {
              "processor_response": { "avs_code": "A", "cvv_code": "M", "response_code": "0000" },
              "checks": { "cvc_check": "pass", "tokenization": "applied" },
              "card": { "cvv": "123", "last_digits": "1234" }
            }
            """;

        var prepared = PaymentEventPayload.Prepare(payload);

        Assert.True(prepared.Redacted);

        using var document = JsonDocument.Parse(prepared.Json);
        var root = document.RootElement;

        Assert.Equal("M", root.GetProperty("processor_response").GetProperty("cvv_code").GetString());
        Assert.Equal("pass", root.GetProperty("checks").GetProperty("cvc_check").GetString());
        Assert.Equal("applied", root.GetProperty("checks").GetProperty("tokenization").GetString());
        Assert.Equal("1234", root.GetProperty("card").GetProperty("last_digits").GetString());
        Assert.Equal(PaymentEventPayload.RedactedValue, root.GetProperty("card").GetProperty("cvv").GetString());
        Assert.DoesNotContain("\"123\"", prepared.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_realistic_PayPal_capture_and_a_realistic_PayHere_notification_come_through_untouched()
    {
        const string payPal = """
            {
              "id": "WH-2WR32451HC0233532-67976317FL4543714",
              "event_type": "PAYMENT.CAPTURE.COMPLETED",
              "resource": {
                "id": "3C679366HH908993F",
                "status": "COMPLETED",
                "amount": { "currency_code": "USD", "value": "31.25" },
                "custom_id": "4711",
                "invoice_id": "CW-2026-000123",
                "supplementary_data": { "related_ids": { "order_id": "5O190127TN364715T", "authorization_id": "0AF1" } },
                "links": [ { "href": "https://api.example.test/v2/payments/captures/3C679366HH908993F", "rel": "self" } ]
              }
            }
            """;

        const string payHere = """
            {
              "merchant_id": "121XXXX", "order_id": "CW-2026-000123-4711", "payment_id": "320032400018",
              "captured_amount": "9375.00", "payhere_amount": "9375.00", "payhere_currency": "LKR",
              "status_code": "2", "md5sig": "ABCDEF0123456789", "custom_1": "", "custom_2": "",
              "method": "VISA", "status_message": "Successfully received payment",
              "card_holder_name": "N Perera", "card_no": "************1234", "card_expiry": "0527"
            }
            """;

        Assert.False(PaymentEventPayload.Prepare(payPal).Redacted);
        Assert.Equal(payPal, PaymentEventPayload.Prepare(payPal).Json);
        Assert.False(PaymentEventPayload.Prepare(payHere).Redacted);
        Assert.Equal(payHere, PaymentEventPayload.Prepare(payHere).Json);
    }

    [Fact]
    public void A_payload_at_the_cap_is_stored_and_one_character_over_is_replaced_by_a_marker()
    {
        const string prefix = "{\"data\":\"";
        const string suffix = "\"}";

        var atCap = prefix + new string('x', PaymentEventPayload.MaxLength - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(PaymentEventPayload.MaxLength, atCap.Length);

        var stored = PaymentEventPayload.Prepare(atCap);
        Assert.False(stored.Truncated);
        Assert.Equal(atCap, stored.Json);

        var overCap = atCap.Replace("xx", "xxx", StringComparison.Ordinal);
        Assert.True(overCap.Length > PaymentEventPayload.MaxLength);

        var dropped = PaymentEventPayload.Prepare(overCap);

        Assert.True(dropped.Truncated);
        Assert.Equal(overCap.Length, dropped.OriginalLength);

        // A prefix of a JSON document is not a JSON document, so what is stored is a marker that is.
        using var document = JsonDocument.Parse(dropped.Json);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(overCap.Length, document.RootElement.GetProperty("originalLength").GetInt32());
        Assert.True(dropped.Json.Length < 100);
    }

    [Fact]
    public void The_idempotency_keys_have_the_shapes_doc_08_names()
    {
        Assert.Equal("PayPal:WH-1AB", PaymentEventKey.ForEvent("PayPal", "WH-1AB"));
        Assert.Equal(
            "Gateway:PAYMENT.NOTIFY:482910",
            PaymentEventKey.ForResource("Gateway", "PAYMENT.NOTIFY", "482910"));
    }

    [Fact]
    public void A_key_that_would_not_fit_its_column_is_refused_rather_than_truncated()
    {
        // A truncated key could collide with another event's, and a collision is a real event ignored as
        // a duplicate.
        Assert.Throws<ArgumentException>(
            () => PaymentEventKey.ForEvent("PayPal", new string('a', PaymentEventKey.MaxLength)));
    }

    [Theory]
    [InlineData("PayPal", "WH-\u0DC3\u0DD2\u0D82\u0DC4\u0DBD")]
    [InlineData("PayPal", "\u0434\u0430\u043D\u043D\u044B\u0435")]
    [InlineData("PayPal", "caf\u00E9")]
    [InlineData("PayPal", "WH 1")]
    [InlineData("PayPal", "WH-1\u0007")]
    [InlineData("P\u0430yPal", "WH-1")] // Cyrillic a
    public void A_key_the_varchar_column_would_fold_is_refused(string provider, string eventId)
    {
        // Folded to '?', two different ids become one and the unique index reports a violation instead of
        // a redelivery. See PaymentEventKey.IsStorable.
        Assert.Throws<ArgumentException>(() => PaymentEventKey.ForEvent(provider, eventId));
        Assert.Throws<ArgumentException>(() => PaymentEventKey.ForResource(provider, "PAYMENT.NOTIFY", eventId));
    }

    [Theory]
    [InlineData("PayPal:WH-1AB", true)]
    [InlineData("a", true)]
    [InlineData("~!@#$%^&*()_+-=[]{};:,./<>?|`", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(" ", false)]
    [InlineData("a b", false)]
    [InlineData("a\tb", false)]
    [InlineData("a\u007Fb", false)]
    [InlineData("a\u0080b", false)]
    [InlineData("\u00E9", false)]
    [InlineData("\u0434", false)]
    public void Only_printable_ASCII_with_no_space_is_storable_as_a_key(string? value, bool expected)
    {
        Assert.Equal(expected, PaymentEventKey.IsStorable(value));
    }

    [Theory]
    [InlineData("", "e")]
    [InlineData("PayPal", " ")]
    public void A_key_part_cannot_be_blank(string provider, string eventId)
    {
        Assert.ThrowsAny<ArgumentException>(() => PaymentEventKey.ForEvent(provider, eventId));
    }
}
