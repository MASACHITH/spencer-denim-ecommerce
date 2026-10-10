using SpencerDenim.Application.Sales.Payments.Contracts;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The redirect part of <see cref="PaymentInstructions"/> (PAY-03): a form the browser posts to a
/// provider's hosted page, or a link to it. The storefront applies the same address rule in its own
/// language, so a provider that breaks it finds out here and not on a customer's screen.
/// </summary>
public sealed class PaymentRedirectFormTests
{
    [Fact]
    public void A_post_form_keeps_its_fields_in_the_order_given()
    {
        var form = PaymentRedirectForm.Post(
            "https://pay.example.test/checkout",
            [
                new PaymentRedirectFormField("merchant_id", "121XXXX"),
                new PaymentRedirectFormField("order_id", "CW-2026-000123-4567"),
                new PaymentRedirectFormField("hash", "ABCDEF0123"),
            ]);

        Assert.Equal("POST", form.Method);
        Assert.Equal(PaymentRedirectForm.PostMethod, form.Method);
        Assert.Equal("https://pay.example.test/checkout", form.Action);
        Assert.Equal(["merchant_id", "order_id", "hash"], form.Fields.Select(field => field.Name).ToArray());
        Assert.Equal("ABCDEF0123", form.Fields[2].Value);
    }

    /// <summary>
    /// <b>One list, run on both sides.</b> The storefront's <c>safeRedirectUrl</c> and this class's
    /// <see cref="PaymentRedirectForm.IsSafeAbsoluteUrl"/> are the same rule in two languages, and a rule
    /// written twice drifts: the first version of this one accepted <c>https://good.example@evil.example/</c>,
    /// <c>http://[::1]</c> and <c>http://127.0.0.2</c>, which the storefront refuses, while a comment said
    /// they matched. These two lists are copied verbatim into
    /// <c>src/frontend/projects/storefront/src/app/core/checkout/payment-redirect.spec.ts</c> (search for
    /// "SHARED URL LIST"); a test below fails if an entry here is missing there. Change a list in both places.
    /// <para>
    /// <b>Two addresses are deliberately not on either list</b>, because the two parsers disagree about them and
    /// a shared list needs one answer: <c>https:evil.example</c> and <c>https:\\evil.example\x</c> (no slashes,
    /// or backslashes) are accepted by the browser's WHATWG parser the storefront uses and refused by
    /// <see cref="Uri"/>. The backend is the stricter of the two there, which is the safe direction; do not
    /// "fix" it by loosening this side.
    /// </para>
    /// </summary>
    public static readonly string[] SafeUrls =
    [
        "https://pay.example.test/checkout",
        "https://pay.example.test/checkout?session=abc&x=1",
        "  https://pay.example.test/checkout  ",
        "HTTPS://PAY.EXAMPLE.TEST/checkout",
        "https://pay.example.test:8443/checkout",
        "https://203.0.113.7/pay",
        "http://localhost:5100/hosted",
        "http://LOCALHOST:5100/hosted",
        "http://127.0.0.1:5100/hosted",
    ];

    /// <summary>See <see cref="SafeUrls"/>.</summary>
    public static readonly string[] UnsafeUrls =
    [
        "",
        "   ",
        "not a url",
        "https:",
        "javascript:alert(1)",
        "JaVaScRiPt:alert(1)",
        "data:text/html,<script>alert(1)</script>",
        "vbscript:msgbox(1)",
        "file:///etc/passwd",
        "ftp://pay.example.test/checkout",
        "/pay/checkout",
        "pay/checkout",
        "//pay.example.test/checkout",
        "http://pay.example.test/checkout",
        "http://localhost.evil.example/hosted",
        "http://127.0.0.1.evil.example/hosted",
        "http://127.0.0.2/hosted",
        "http://[::1]/hosted",
        "http://[::1]:5100/hosted",
        "https://trusted.example@evil.example/pay",
        "https://good.example@evil.example/",
        "https://user:secret@pay.example.test/pay",
        "http://localhost@evil.example/hosted",
    ];

    public static TheoryData<string> SafeUrlData() => [.. SafeUrls];

    public static TheoryData<string> UnsafeUrlData() => [.. UnsafeUrls];

    [Theory]
    [MemberData(nameof(SafeUrlData))]
    public void The_shared_list_of_safe_addresses_is_accepted(string url)
    {
        Assert.True(PaymentRedirectForm.IsSafeAbsoluteUrl(url), url);
        Assert.Equal(url.Trim(), PaymentRedirectForm.Post(url, []).Action);
    }

    [Theory]
    [MemberData(nameof(UnsafeUrlData))]
    public void The_shared_list_of_unsafe_addresses_is_refused(string url)
    {
        Assert.False(PaymentRedirectForm.IsSafeAbsoluteUrl(url), url);

        // A form cannot be built for one either, so a provider that tries finds out in its own unit test.
        Assert.Throws<ArgumentException>(
            () => PaymentRedirectForm.Post(url, [new PaymentRedirectFormField("a", "b")]));
    }

    [Fact]
    public void A_null_address_is_refused()
    {
        Assert.False(PaymentRedirectForm.IsSafeAbsoluteUrl(null));
    }

    /// <summary>
    /// The drift guard: every address in both lists is also in the storefront's spec, verbatim, so the two
    /// suites cannot quietly run different lists. A source read is unusual in a unit test; it is confined to
    /// this question, as <c>PaymentProviderContractTests</c> confines its own.
    /// </summary>
    [Fact]
    public void The_storefront_spec_runs_the_same_two_lists()
    {
        var spec = File.ReadAllText(StorefrontSpecPath());

        Assert.Contains("SHARED URL LIST", spec, StringComparison.Ordinal);

        foreach (var url in SafeUrls.Concat(UnsafeUrls))
        {
            Assert.True(
                spec.Contains($"'{url}'", StringComparison.Ordinal),
                $"payment-redirect.spec.ts does not list '{url}'. Both lists must be identical.");
        }
    }

    private static string StorefrontSpecPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "frontend",
                "projects",
                "storefront",
                "src",
                "app",
                "core",
                "checkout",
                "payment-redirect.spec.ts");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find payment-redirect.spec.ts by walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void A_field_without_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => PaymentRedirectForm.Post(
                "https://pay.example.test/checkout",
                [new PaymentRedirectFormField(" ", "value")]));
    }

    [Fact]
    public void A_request_for_instructions_defaults_to_no_attempt_so_existing_callers_are_unchanged()
    {
        var request = new PaymentInstructionsRequest(
            "CW-2026-000123",
            1500m,
            new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            OrderUrl: null);

        Assert.Null(request.PaymentId);
        Assert.Null(request.ProviderReference);
    }

    [Fact]
    public void Instructions_default_to_no_redirect_form_so_the_bank_transfer_is_unchanged()
    {
        var instructions = new PaymentInstructions(
            "BankTransfer",
            "CW-2026-000123",
            1500m,
            new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            [],
            null,
            RedirectUrl: null,
            AcceptsProof: true);

        Assert.Null(instructions.RedirectForm);
    }
}
