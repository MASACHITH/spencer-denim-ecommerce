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

        // "token" alone is not a credential: redacting every identifier that contains it would make the
        // log useless for the dispute it exists for.
        Assert.Equal("reference-not-a-credential", root.GetProperty("payment_token").GetString());
    }

    [Theory]
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
    public void Every_spelling_of_a_credential_name_is_recognised(string propertyName)
    {
        var payload = $"{{\"{propertyName}\":\"do-not-store\",\"keep\":\"yes\"}}";

        var prepared = PaymentEventPayload.Prepare(payload);

        Assert.True(prepared.Redacted);
        Assert.DoesNotContain("do-not-store", prepared.Json, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"yes\"", prepared.Json, StringComparison.Ordinal);
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
    [InlineData("", "e")]
    [InlineData("PayPal", " ")]
    public void A_key_part_cannot_be_blank(string provider, string eventId)
    {
        Assert.ThrowsAny<ArgumentException>(() => PaymentEventKey.ForEvent(provider, eventId));
    }
}
