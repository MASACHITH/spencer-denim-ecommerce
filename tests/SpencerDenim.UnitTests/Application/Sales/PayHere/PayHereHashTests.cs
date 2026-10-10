using System.Globalization;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// PayHere's two signatures against <b>known answers computed outside this code base</b> (doc 11 P8b).
/// <para>
/// Every expected value below was produced by Python's <c>hashlib.md5</c> over the concatenation PayHere's
/// documentation specifies, upper-cased — never by the class under test — so these tests fail if the
/// implementation drifts from the protocol, instead of agreeing with itself. The inputs are fixed; the
/// expected strings are what an independent implementation printed for them.
/// </para>
/// </summary>
public sealed class PayHereHashTests
{
    private const string Merchant = "1211149";
    private const string Order = "SD-2026-000123-4567";

    // UPPER(MD5("TestSecret")) and UPPER(MD5("abcdef")), the inner digest both signatures embed.
    private const string TestSecretDigest = "21FCA8107C3AA5DFFF63F34F526C6BBE";
    private const string LowerSecretDigest = "E80B5017098950FC58AAD83C8C14978E";

    [Fact]
    public void The_secret_digest_is_the_upper_case_md5_of_the_secret()
    {
        Assert.Equal(TestSecretDigest, PayHereHash.SecretDigest("TestSecret"));
        Assert.Equal(LowerSecretDigest, PayHereHash.SecretDigest("abcdef"));
    }

    [Theory]
    [InlineData("1500.50", "TestSecret", "B0454AFEFC38780441C1C85D1B4A1912")]
    [InlineData("1234567.89", "TestSecret", "C46C92726773901380E2436865524296")]
    [InlineData("2500.00", "TestSecret", "C7A5E7C91C739F5C0F02356F4A71053B")]
    [InlineData("0.50", "TestSecret", "2C146ACB13F42D109D4AEEAEF4769ADD")]
    [InlineData("1500.50", "abcdef", "A763E5CCCDD1511A6ED21E1CE4D74704")]
    public void The_checkout_hash_matches_an_independent_implementation(
        string formattedAmount,
        string secret,
        string expected)
    {
        Assert.Equal(expected, PayHereHash.CheckoutHash(Merchant, Order, formattedAmount, "LKR", secret));
    }

    /// <summary>
    /// The decimal overload formats the amount first: 1500.5 is signed as <c>1500.50</c>, 2500 as
    /// <c>2500.00</c>, and a large amount carries no thousands separator.
    /// </summary>
    [Theory]
    [InlineData(1500.5, "B0454AFEFC38780441C1C85D1B4A1912")]
    [InlineData(1234567.89, "C46C92726773901380E2436865524296")]
    [InlineData(2500, "C7A5E7C91C739F5C0F02356F4A71053B")]
    [InlineData(0.5, "2C146ACB13F42D109D4AEEAEF4769ADD")]
    public void The_checkout_hash_formats_the_amount_to_two_decimals_first(double amount, string expected)
    {
        Assert.Equal(expected, PayHereHash.CheckoutHash(Merchant, Order, (decimal)amount, "LKR", "TestSecret"));
    }

    [Theory]
    [InlineData(1500.5, "1500.50")]
    [InlineData(2500, "2500.00")]
    [InlineData(0.5, "0.50")]
    [InlineData(1234567.89, "1234567.89")]
    [InlineData(9184, "9184.00")]
    public void An_amount_is_written_with_two_decimals_a_full_stop_and_no_grouping(double amount, string expected)
    {
        Assert.Equal(expected, PayHereHash.FormatAmount((decimal)amount));
    }

    /// <summary>
    /// The server's culture must not leak into a signed string: Sinhala, German and French all write a
    /// decimal comma or a grouping character that PayHere would not recognise.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("si-LK")]
    public void The_amount_is_formatted_in_the_invariant_culture_whatever_the_server_culture(string culture)
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("1234567.89", PayHereHash.FormatAmount(1234567.89m));
            Assert.Equal(
                "C46C92726773901380E2436865524296",
                PayHereHash.CheckoutHash(Merchant, Order, 1234567.89m, "LKR", "TestSecret"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("1500.50", "LKR", "2", "TestSecret", "299858CC8ADFDBE274AFABDD689EA94B")]
    [InlineData("1500.50", "LKR", "0", "TestSecret", "362ADCAE60120459FA1F62F59EEE8C49")]
    [InlineData("1500.50", "LKR", "-1", "TestSecret", "702FB288BA643494F1E32926CBEAEF1F")]
    [InlineData("1500.50", "LKR", "2", "abcdef", "FA353183705936E980C69DD9B3AA2493")]
    [InlineData("1500.50", "USD", "2", "TestSecret", "784786E7555CC3660D5D8DF0CB4D3ED7")]
    public void The_notification_signature_matches_an_independent_implementation(
        string amount,
        string currency,
        string status,
        string secret,
        string expected)
    {
        Assert.Equal(
            expected,
            PayHereHash.NotificationSignature(Merchant, Order, amount, currency, status, secret));
    }

    /// <summary>
    /// <c>payhere_amount</c> is signed <b>as PayHere wrote it</b>. <c>1500.5</c> and <c>1500.50</c> are the
    /// same number and different messages, and verifying the wrong one would reject a genuine notification
    /// (or, worse, accept a forged one that happens to normalise to the same text).
    /// </summary>
    [Fact]
    public void The_signature_covers_the_amount_text_exactly_as_posted()
    {
        Assert.Equal(
            "4EF845D89CD5C4A53955BAD4A155BCA6",
            PayHereHash.NotificationSignature(Merchant, Order, "1500.5", "LKR", "2", "TestSecret"));

        Assert.NotEqual(
            PayHereHash.NotificationSignature(Merchant, Order, "1500.50", "LKR", "2", "TestSecret"),
            PayHereHash.NotificationSignature(Merchant, Order, "1500.5", "LKR", "2", "TestSecret"));
    }

    [Fact]
    public void A_genuine_signature_verifies_in_either_case()
    {
        const string signature = "299858CC8ADFDBE274AFABDD689EA94B";

        Assert.True(Verify(signature));
        Assert.True(Verify(signature.ToLowerInvariant()));
        Assert.True(Verify("  " + signature + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("299858CC8ADFDBE274AFABDD689EA94C")]
    [InlineData("299858CC8ADFDBE274AFABDD689EA94")]
    [InlineData("299858CC8ADFDBE274AFABDD689EA94B0")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("é99858CC8ADFDBE274AFABDD689EA94B")]
    public void Anything_else_does_not_verify(string? signature)
    {
        Assert.False(Verify(signature));
    }

    /// <summary>
    /// Changing any one signed value breaks the signature - the property that makes a forged amount, another
    /// order's id or a different status fail.
    /// </summary>
    [Fact]
    public void Changing_any_signed_value_breaks_the_signature()
    {
        const string signature = "299858CC8ADFDBE274AFABDD689EA94B";

        Assert.False(PayHereHash.IsValidNotificationSignature("1211148", Order, "1500.50", "LKR", "2", "TestSecret", signature));
        Assert.False(PayHereHash.IsValidNotificationSignature(Merchant, "SD-2026-000124-4567", "1500.50", "LKR", "2", "TestSecret", signature));
        Assert.False(PayHereHash.IsValidNotificationSignature(Merchant, Order, "1500.51", "LKR", "2", "TestSecret", signature));
        Assert.False(PayHereHash.IsValidNotificationSignature(Merchant, Order, "1500.50", "USD", "2", "TestSecret", signature));
        Assert.False(PayHereHash.IsValidNotificationSignature(Merchant, Order, "1500.50", "LKR", "0", "TestSecret", signature));
        Assert.False(PayHereHash.IsValidNotificationSignature(Merchant, Order, "1500.50", "LKR", "2", "TestSecreT", signature));
    }

    [Fact]
    public void Every_digest_is_upper_case_hex()
    {
        var hash = PayHereHash.CheckoutHash(Merchant, Order, 1500.5m, "LKR", "abcdef");
        var signature = PayHereHash.NotificationSignature(Merchant, Order, "1500.50", "LKR", "2", "abcdef");

        foreach (var digest in new[] { hash, signature, PayHereHash.SecretDigest("abcdef") })
        {
            Assert.Equal(32, digest.Length);
            Assert.Equal(digest.ToUpperInvariant(), digest);
            Assert.All(digest, character => Assert.True(char.IsAsciiHexDigit(character)));
        }
    }

    [Fact]
    public void An_empty_input_is_refused_rather_than_hashed()
    {
        Assert.Throws<ArgumentException>(() => PayHereHash.SecretDigest(string.Empty));
        Assert.Throws<ArgumentException>(() => PayHereHash.CheckoutHash(string.Empty, Order, "1.00", "LKR", "s"));
        Assert.Throws<ArgumentException>(() => PayHereHash.NotificationSignature(Merchant, Order, "1.00", "LKR", string.Empty, "s"));
    }

    private static bool Verify(string? signature) =>
        PayHereHash.IsValidNotificationSignature(Merchant, Order, "1500.50", "LKR", "2", "TestSecret", signature);
}
