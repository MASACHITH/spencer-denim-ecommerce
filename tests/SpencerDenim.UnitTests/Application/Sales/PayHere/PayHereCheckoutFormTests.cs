using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// The form the customer's browser posts to PayHere (doc 11 P8b step 1): which fields, in which order, with
/// which values, signed with which hash - and what must never be in it.
/// </summary>
public sealed class PayHereCheckoutFormTests
{
    private const string Origin = "https://shop.example.test";

    private static PayHereCheckoutContext Context(
        string orderNumber = "SD-2026-000123",
        int paymentId = 4567,
        decimal amount = 1500.5m,
        string firstName = "Nimal",
        string? lastName = "Perera",
        string email = "nimal@example.test",
        string? phone = "0771234567",
        string? address = "12 Galle Road, Colombo 3",
        string? city = "Colombo") =>
        new(
            paymentId,
            SpencerDenim.Domain.Sales.PaymentStatus.Initiated,
            orderNumber,
            PayHereSample.OrderPublicId,
            amount,
            firstName,
            lastName,
            email,
            phone,
            address,
            city);

    private static Dictionary<string, string> Fields(PaymentRedirectForm form) =>
        form.Fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);

    [Fact]
    public void The_fields_are_in_PayHeres_documented_order()
    {
        var form = PayHereCheckoutForm.Build(PayHereSample.Settings(), Context(), Origin);

        Assert.Equal(
            [
                "merchant_id",
                "return_url",
                "cancel_url",
                "notify_url",
                "order_id",
                "items",
                "currency",
                "amount",
                "first_name",
                "last_name",
                "email",
                "phone",
                "address",
                "city",
                "country",
                "hash",
            ],
            form.Fields.Select(field => field.Name));
    }

    [Fact]
    public void Every_value_comes_from_the_server_s_own_order()
    {
        var form = PayHereCheckoutForm.Build(PayHereSample.Settings(), Context(), Origin);
        var fields = Fields(form);

        Assert.Equal("1211149", fields["merchant_id"]);
        Assert.Equal("SD-2026-000123-4567", fields["order_id"]);
        Assert.Equal("Order SD-2026-000123", fields["items"]);
        Assert.Equal("LKR", fields["currency"]);
        Assert.Equal("1500.50", fields["amount"]);
        Assert.Equal("Nimal", fields["first_name"]);
        Assert.Equal("Perera", fields["last_name"]);
        Assert.Equal("nimal@example.test", fields["email"]);
        Assert.Equal("0771234567", fields["phone"]);
        Assert.Equal("12 Galle Road, Colombo 3", fields["address"]);
        Assert.Equal("Colombo", fields["city"]);
        Assert.Equal("Sri Lanka", fields["country"]);
    }

    [Fact]
    public void The_three_addresses_are_built_from_the_public_origin()
    {
        var fields = Fields(PayHereCheckoutForm.Build(PayHereSample.Settings(), Context(), Origin + "/"));

        Assert.Equal(
            "https://shop.example.test/order/0f8fad5b-d9cb-469f-a165-70867728950e?payment=return",
            fields["return_url"]);
        Assert.Equal(
            "https://shop.example.test/order/0f8fad5b-d9cb-469f-a165-70867728950e?payment=cancelled",
            fields["cancel_url"]);
        Assert.Equal("https://shop.example.test/api/payments/payhere/notify", fields["notify_url"]);
    }

    /// <summary>
    /// The hash is the signature PayHere checks. The expected value was computed outside this code base
    /// (see <see cref="PayHereHashTests"/>): UPPER(MD5(merchant_id + order_id + "1500.50" + "LKR" +
    /// UPPER(MD5("TestSecret")))).
    /// </summary>
    [Fact]
    public void The_hash_is_the_known_answer_for_this_order()
    {
        var fields = Fields(PayHereCheckoutForm.Build(PayHereSample.Settings(), Context(), Origin));

        Assert.Equal("B0454AFEFC38780441C1C85D1B4A1912", fields["hash"]);
    }

    [Fact]
    public void A_larger_amount_is_signed_without_a_thousands_separator()
    {
        var fields = Fields(PayHereCheckoutForm.Build(
            PayHereSample.Settings(),
            Context(amount: 1234567.89m),
            Origin));

        Assert.Equal("1234567.89", fields["amount"]);
        Assert.Equal("C46C92726773901380E2436865524296", fields["hash"]);
    }

    [Theory]
    [InlineData("sandbox", "https://sandbox.payhere.lk/pay/checkout")]
    [InlineData("live", "https://www.payhere.lk/pay/checkout")]
    public void The_form_posts_to_the_host_for_the_mode(string mode, string action)
    {
        var form = PayHereCheckoutForm.Build(PayHereSample.Settings(mode: mode), Context(), Origin);

        Assert.Equal("POST", form.Method);
        Assert.Equal(action, form.Action);
    }

    /// <summary>
    /// "Nothing secret goes in": the secret is an input to the hash and never a field value, and neither is
    /// its digest.
    /// </summary>
    [Fact]
    public void Neither_the_secret_nor_its_digest_is_in_any_field()
    {
        var form = PayHereCheckoutForm.Build(PayHereSample.Settings(), Context(), Origin);

        foreach (var field in form.Fields)
        {
            Assert.DoesNotContain(PayHereSample.Secret, field.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                PayHereHash.SecretDigest(PayHereSample.Secret),
                field.Value,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_customer_with_no_last_name_phone_or_address_still_gets_a_complete_form()
    {
        var form = PayHereCheckoutForm.Build(
            PayHereSample.Settings(),
            Context(firstName: "   ", lastName: null, phone: null, address: null, city: null),
            Origin);

        var fields = Fields(form);

        Assert.Equal(PayHereCheckoutForm.FallbackFirstName, fields["first_name"]);
        Assert.Equal(PayHereCheckoutForm.FallbackLastName, fields["last_name"]);
        Assert.Equal(PayHereCheckoutForm.FallbackPhone, fields["phone"]);
        Assert.Equal(PayHereCheckoutForm.FallbackAddress, fields["address"]);
        Assert.Equal(PayHereCheckoutForm.FallbackCity, fields["city"]);
        Assert.All(form.Fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Value), field.Name));
    }

    [Fact]
    public void Long_or_untidy_customer_text_is_cleaned_and_clipped_to_what_PayHere_takes()
    {
        var fields = Fields(PayHereCheckoutForm.Build(
            PayHereSample.Settings(),
            Context(
                firstName: new string('N', 120),
                lastName: "Per\r\nera\t ",
                phone: "077 123 4567 ext. 99 and some more",
                address: "Line one\nLine two " + new string('a', 200),
                city: new string('C', 80)),
            Origin));

        Assert.Equal(50, fields["first_name"].Length);
        Assert.Equal("Per era", fields["last_name"]);
        Assert.True(fields["phone"].Length <= 20);
        Assert.True(fields["address"].Length <= 100);
        Assert.StartsWith("Line one Line two aaa", fields["address"], StringComparison.Ordinal);
        Assert.Equal(50, fields["city"].Length);
        Assert.All(fields.Values, value => Assert.DoesNotContain(value, char.IsControl));
    }

    [Fact]
    public void The_form_is_one_the_order_page_will_follow()
    {
        var form = PayHereCheckoutForm.Build(PayHereSample.Settings(), Context(), Origin);

        Assert.Equal(PaymentRedirectForm.PostMethod, form.Method);
        Assert.True(PaymentRedirectForm.IsSafeAbsoluteUrl(form.Action));
        Assert.All(form.Fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Name)));
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("", "secret")]
    [InlineData("1211149", null)]
    [InlineData("1211149", "")]
    public void A_form_is_not_built_for_an_unconfigured_account(string? merchantId, string? secret)
    {
        Assert.Throws<InvalidOperationException>(() => PayHereCheckoutForm.Build(
            PayHereSample.Settings(merchantId: merchantId, secret: secret),
            Context(),
            Origin));
    }

    [Fact]
    public void A_form_is_not_built_for_an_unknown_mode()
    {
        Assert.Throws<InvalidOperationException>(() => PayHereCheckoutForm.Build(
            PayHereSample.Settings(mode: null),
            Context(),
            Origin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shop.example.test")]
    [InlineData("/relative")]
    [InlineData("ftp://shop.example.test")]
    [InlineData("https://user:pass@shop.example.test")]
    public void A_form_is_not_built_without_a_usable_public_origin(string? origin)
    {
        Assert.Throws<InvalidOperationException>(() => PayHereCheckoutForm.Build(
            PayHereSample.Settings(),
            Context(),
            origin));
    }

    [Fact]
    public void A_live_form_is_not_built_for_a_plain_http_address_and_a_sandbox_one_is()
    {
        Assert.Throws<InvalidOperationException>(() => PayHereCheckoutForm.Build(
            PayHereSample.Settings(mode: "live"),
            Context(),
            "http://shop.example.test"));

        var form = PayHereCheckoutForm.Build(
            PayHereSample.Settings(mode: "sandbox"),
            Context(),
            "http://shop.example.test");

        Assert.StartsWith("http://shop.example.test/order/", Fields(form)["return_url"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_is_not_built_for_an_origin_with_a_query_string()
    {
        Assert.Throws<InvalidOperationException>(() => PayHereCheckoutForm.Build(
            PayHereSample.Settings(),
            Context(),
            "https://shop.example.test?x=1"));
    }

    [Fact]
    public void The_text_the_form_is_clipped_with_never_splits_a_character()
    {
        // 49 letters then an emoji (a surrogate pair): clipping at 50 would leave half of it.
        var clipped = PayHereText.Clip(new string('a', 49) + "\U0001F600\U0001F600", 50);

        Assert.NotNull(clipped);
        Assert.False(char.IsHighSurrogate(clipped[^1]));
        Assert.True(clipped.Length <= 50);
    }
}
