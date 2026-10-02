using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The ADM-22 field rules (<see cref="SettingFieldRules"/>).
/// <para>
/// <b>Weighted towards the attack list rather than the happy path, on purpose.</b> W1-12's banner-link
/// validator was defeated three times after it had been written, reviewed and tested — by
/// <c>/\host</c>, by a tab-split value and by a newline-split one — and each time the test suite was
/// green. The rule here answers a different question (an absolute external address, never a site path),
/// so the inputs are not the same; what carries over is that the refusals are the test.
/// </para>
/// </summary>
public sealed class SettingFieldRulesTests
{
    // ---- profile URLs: what is accepted ----------------------------------------------------------

    [Theory]
    [InlineData("https://www.instagram.com/colombowardrobe")]
    [InlineData("https://instagram.com/colombowardrobe")]
    [InlineData("http://example.com/shop")]
    [InlineData("https://www.tiktok.com/@colombowardrobe")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path?utm_source=x#frag")]
    [InlineData("https://example.com:8443/shop")]
    [InlineData("https://xn--80ak6aa92e.com/shop")]
    public void An_absolute_http_or_https_address_is_accepted(string value)
    {
        Assert.True(SettingFieldRules.IsAcceptableProfileUrl(value));
        Assert.Empty(Check(value));
    }

    [Fact]
    public void A_blank_value_is_accepted_and_means_hide_the_icon()
    {
        // The most important "valid" case, because it is the one the client will use: "we have no TikTok".
        Assert.Empty(Check(null));
        Assert.Empty(Check(string.Empty));
        Assert.Empty(Check("   "));

        // …and it is *not* an acceptable URL, which is what makes the read model drop it rather than render
        // an icon with an empty href. The two answers differ deliberately.
        Assert.False(SettingFieldRules.IsAcceptableProfileUrl(null));
        Assert.False(SettingFieldRules.IsAcceptableProfileUrl("   "));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_rather_than_refused()
    {
        // A pasted URL very often carries a trailing space or newline from an email.
        Assert.Empty(Check("  https://www.facebook.com/shop  "));
    }

    /// <summary>
    /// <b>A control character at either <i>end</i> is trimmed away, and the value is then accepted.</b>
    /// <para>
    /// Written down as a test because the first version of this file asserted the opposite and was wrong, and
    /// because the distinction is the whole of W1-12's lesson. <see cref="SettingFieldRules.Clean"/> trims
    /// whitespace, and TAB, CR, LF and the vertical tab are all whitespace — so a <i>trailing</i> one never
    /// reaches the control-character check, and does not need to: what is left is a plain safe URL. The
    /// dangerous position is the <i>middle</i>, which <c>Trim</c> does not touch, and that is what the refusal
    /// list above covers. A NUL is refused wherever it sits, because it is a control character and not
    /// whitespace.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://www.facebook.com/shop\t")]
    [InlineData("https://www.facebook.com/shop\n")]
    [InlineData("\rhttps://www.facebook.com/shop")]
    [InlineData("https://www.facebook.com/shop\u000b")]
    public void A_control_character_at_the_end_is_trimmed_and_the_rest_accepted(string value)
    {
        Assert.True(SettingFieldRules.IsAcceptableProfileUrl(value));
        Assert.Empty(Check(value));
    }

    [Fact]
    public void A_nul_is_refused_wherever_it_sits_because_it_is_not_whitespace()
    {
        Assert.False(SettingFieldRules.IsAcceptableProfileUrl("https://www.facebook.com/shop\u0000"));
        Assert.False(SettingFieldRules.IsAcceptableProfileUrl("https://www.face\u0000book.com/shop"));
    }

    // ---- profile URLs: what is refused ----------------------------------------------------------

    [Theory]

    // The schemes. These are the point of the rule.
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("  javascript:alert(1)  ")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/shop")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+94771234567")]
    [InlineData("about:blank")]
    [InlineData("blob:https://example.com/abc")]

    // Not absolute at all. A social icon pointing at a listing page is not a social icon.
    [InlineData("/women/jeans")]
    [InlineData("women/jeans")]
    [InlineData("//evil.example/")]
    [InlineData("instagram.com/shop")]
    [InlineData("www.instagram.com/shop")]

    // A reverse solidus is a solidus to a browser parsing a special scheme.
    [InlineData(@"https:\\evil.example")]
    [InlineData(@"https://good.example\@evil.example")]
    [InlineData(@"\\evil.example\share")]
    [InlineData(@"/\evil.example")]

    // Control characters a browser deletes before it parses. Clean() only trims the ends, so one in the
    // middle survives to do its work — W1-12's second and third bypasses, in the shape this rule can meet.
    [InlineData("https://good.\texample.com")]
    [InlineData("https://good.\nexample.com")]
    [InlineData("https://good.\rexample.com")]
    [InlineData("https://good.example\u0000")]
    [InlineData("https://good.\u000bexample.com")]
    [InlineData("https://good.\u0085example.com")]
    [InlineData("https://good.\u2028example.com")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("https://good.example/ /evil")]

    // No authority. .NET gives this an empty host, and an href with an empty host resolves against the
    // current page — so the footer's "Instagram" icon would link back into the shop.
    [InlineData("http:///profile")]
    [InlineData("https:///profile")]

    // Credentials: what a shopper reads is not where the link goes.
    [InlineData("https://instagram.com@evil.example/")]
    [InlineData("https://user:password@evil.example/")]

    // Not a URL at all.
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("https:")]
    public void A_value_that_is_not_an_absolute_external_address_is_refused(string value)
    {
        Assert.False(SettingFieldRules.IsAcceptableProfileUrl(value));

        var error = Assert.Single(Check(value));
        Assert.Equal("instagramUrl", error.Field);

        // The message has to tell the client what to do, including how to clear the box — a validation
        // message that only says "invalid" sends them to support.
        Assert.Contains("https://", error.Message, StringComparison.Ordinal);
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The length cap, and its own message. <c>Value</c> is <c>nvarchar(max)</c>, so without this the only
    /// bound on a field reachable from a form is how much a client can paste.
    /// </summary>
    [Fact]
    public void An_over_long_address_is_refused_for_its_length()
    {
        var tooLong = "https://example.com/" + new string('a', SettingFieldRules.UrlMaxLength);

        var error = Assert.Single(Check(tooLong));
        Assert.Contains(
            SettingFieldRules.UrlMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_at_exactly_the_limit_is_accepted()
    {
        var prefix = "https://example.com/";
        var atLimit = prefix + new string('a', SettingFieldRules.UrlMaxLength - prefix.Length);

        Assert.Equal(SettingFieldRules.UrlMaxLength, atLimit.Length);
        Assert.Empty(Check(atLimit));
    }

    /// <summary>
    /// The label reaches the message, so three boxes on one form do not all say "the address".
    /// </summary>
    [Theory]
    [InlineData("Facebook")]
    [InlineData("Instagram")]
    [InlineData("TikTok")]
    public void The_message_names_the_network(string label)
    {
        var errors = new List<ValidationError>();
        SettingFieldRules.CheckProfileUrl(errors, "x", "javascript:alert(1)", label);

        Assert.Contains(label, Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    // ---- WhatsApp numbers ------------------------------------------------------------------------

    [Theory]
    [InlineData("94771234567")]
    [InlineData("1234567")]
    [InlineData("123456789012345")]
    [InlineData("  94771234567  ")]
    public void A_number_in_international_form_is_accepted(string value)
    {
        Assert.Empty(WhatsApp(value));
    }

    [Fact]
    public void An_empty_whats_app_number_is_accepted_and_clears_the_setting()
    {
        Assert.Empty(WhatsApp(null));
        Assert.Empty(WhatsApp("   "));
    }

    [Theory]
    [InlineData("0771234567")]      // a national number; wa.me/0771234567 is a link to nobody
    [InlineData("+94771234567")]    // wa.me silently fails on a plus
    [InlineData("94 77 123 4567")]
    [InlineData("94-77-123-4567")]
    [InlineData("(077) 1234567")]
    [InlineData("941234")]          // below WhatsAppMinDigits
    [InlineData("1234567890123456")] // above E.164's fifteen
    [InlineData("94771234567x")]
    [InlineData("whatsapp")]
    [InlineData("٩٤٧٧١٢٣٤٥٦٧")]    // Arabic-Indic digits: char.IsDigit says yes, wa.me does not
    [InlineData("９４７７１２３４５６７")]  // fullwidth digits, same trap
    [InlineData("947712\t34567")]
    public void A_number_that_is_not_digits_in_international_form_is_refused(string value)
    {
        Assert.False(SettingFieldRules.IsAcceptableWhatsAppNumber(value));

        var error = Assert.Single(WhatsApp(value));
        Assert.Equal("whatsApp", error.Field);
        Assert.Contains("94771234567", error.Message, StringComparison.Ordinal);
    }

    // ---- row versions ----------------------------------------------------------------------------

    [Fact]
    public void A_base64_row_version_round_trips()
    {
        var token = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, SettingFieldRules.ReadRowVersion(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    [InlineData("=")]
    public void A_missing_or_malformed_row_version_reads_as_nothing_rather_than_throwing(string? token)
    {
        Assert.Null(SettingFieldRules.ReadRowVersion(token));
    }

    /// <summary>
    /// A token longer than the fixed buffer reads as nothing rather than overflowing or throwing. Reachable
    /// only from a client sending something it did not get from the read model.
    /// </summary>
    [Fact]
    public void An_over_long_row_version_reads_as_nothing()
    {
        Assert.Null(SettingFieldRules.ReadRowVersion(Convert.ToBase64String(new byte[128])));
    }

    // ---- Clean -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t\n", null)]
    [InlineData(" value ", "value")]
    public void Clean_turns_blank_into_null_so_a_cleared_box_is_not_stored_as_empty(string? input, string? expected)
    {
        Assert.Equal(expected, SettingFieldRules.Clean(input));
    }

    private static List<ValidationError> Check(string? value)
    {
        var errors = new List<ValidationError>();
        SettingFieldRules.CheckProfileUrl(errors, "instagramUrl", value, "Instagram");

        return errors;
    }

    private static List<ValidationError> WhatsApp(string? value)
    {
        var errors = new List<ValidationError>();
        SettingFieldRules.CheckWhatsAppNumber(errors, "whatsApp", value);

        return errors;
    }
}
