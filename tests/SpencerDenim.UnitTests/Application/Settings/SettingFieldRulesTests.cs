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

        // And the normalised form is bounded too: normalisation can lengthen a value (percent-encoding, or a
        // punycode label), so the cap is applied to what actually gets stored as well as to what arrived.
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl(atLimit, out var normalised));
        Assert.InRange(normalised.Length, 1, SettingFieldRules.UrlMaxLength);
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


    // ---- normalisation (round 1) -----------------------------------------------------------------

    /// <summary>
    /// <b>The homograph that made normalisation necessary.</b>
    /// <para>
    /// U+3002 is IDEOGRAPHIC FULL STOP. <c>Uri.TryCreate</c> accepts it, and both .NET's
    /// <see cref="System.Uri.IdnHost"/> and Chromium map it to an ordinary dot — so storing the raw text left a
    /// row that <i>read</i> <c>instagram.com</c> and <i>went</i> to <c>evil.example</c>, with nothing on any
    /// screen able to tell a staff user. The value is now rewritten, so the text and the destination agree.
    /// </para>
    /// </summary>
    [Fact]
    public void A_homograph_separator_is_normalised_to_the_host_it_resolves_to()
    {
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl(
            "https://instagram.com\u3002evil.example/shop", out var normalised));

        Assert.Equal("https://instagram.com.evil.example/shop", normalised);
        Assert.DoesNotContain("\u3002", normalised, StringComparison.Ordinal);
    }

    /// <summary>
    /// A homoglyph label is stored as its punycode, which is how a client can see that it is not the brand it
    /// imitates: Cyrillic es then "ompany" becomes <c>xn--ompany-hrf</c>.
    /// </summary>
    [Fact]
    public void A_homoglyph_label_is_normalised_to_punycode()
    {
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl("https://\u0441ompany.example/shop", out var normalised));

        Assert.Equal("https://xn--ompany-hrf.example/shop", normalised);
    }

    /// <summary>
    /// <b>The two rows that used to be stored and then never render.</b> U+FF20 (FULLWIDTH COMMERCIAL AT) and
    /// U+FF0F (FULLWIDTH SOLIDUS) are accepted by <c>Uri.TryCreate</c> and make <see cref="System.Uri.IdnHost"/>
    /// throw; the browser throws on them too. The old rule stored them, so a client got a 204, no error
    /// anywhere, and an icon that never appeared. Computing the ASCII host is what refuses them.
    /// </summary>
    [Theory]
    [InlineData("https://instagram.com\uFF20evil.example/")]
    [InlineData("https://instagram.com\uFF0Fevil.example/")]
    public void A_host_dotnet_cannot_map_to_idn_is_refused_rather_than_stored(string value)
    {
        Assert.False(SettingFieldRules.TryNormaliseProfileUrl(value, out var normalised));
        Assert.Equal(string.Empty, normalised);

        Assert.Single(Check(value));
    }

    /// <summary>
    /// Canonical form: lower-cased host, default port dropped, empty path filled. Two staff users typing the
    /// same profile two ways produce one row, and the screen shows them what the link will do.
    /// </summary>
    [Theory]
    [InlineData("https://WWW.Facebook.com/Shop", "https://www.facebook.com/Shop")]
    [InlineData("https://example.com:443/shop", "https://example.com/shop")]
    [InlineData("http://example.com:80/shop", "http://example.com/shop")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("  https://example.com/shop  ", "https://example.com/shop")]
    [InlineData("https://www.tiktok.com/@shop", "https://www.tiktok.com/@shop")]
    public void An_accepted_address_is_normalised(string value, string expected)
    {
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl(value, out var normalised));
        Assert.Equal(expected, normalised);
    }

    /// <summary>A non-default port survives, because it is part of where the link goes.</summary>
    [Fact]
    public void A_non_default_port_is_kept()
    {
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl("https://example.com:8443/shop", out var normalised));
        Assert.Equal("https://example.com:8443/shop", normalised);
    }

    /// <summary>
    /// Normalisation is idempotent — the stored value, fed back in, comes out unchanged. Without that, the read
    /// model's re-check would rewrite every value on every page render and the two sides would never settle.
    /// </summary>
    [Theory]
    [InlineData("https://www.instagram.com/colombowardrobe")]
    [InlineData("https://instagram.com\u3002evil.example/shop")]
    [InlineData("https://WWW.Facebook.com:443")]
    public void Normalisation_is_idempotent(string value)
    {
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl(value, out var once));
        Assert.True(SettingFieldRules.TryNormaliseProfileUrl(once, out var twice));

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// A host that is not a plausible hostname. A single label is refused because nothing a client pastes from a
    /// browser looks like that and <c>https://intranet/profile</c> is not a public profile; empty labels and a
    /// leading or trailing hyphen are refused because they are not hosts anybody meant to type.
    /// </summary>
    [Theory]
    [InlineData("https://intranet/profile")]
    [InlineData("https://example..com/shop")]
    [InlineData("https://-bad.example/shop")]
    [InlineData("https://bad-.example/shop")]
    [InlineData("https://.example.com/shop")]
    public void An_implausible_host_is_refused(string value)
    {
        Assert.False(SettingFieldRules.TryNormaliseProfileUrl(value, out _));
    }

    /// <summary>
    /// <b>The empty-host claim, corrected.</b> An earlier comment said .NET gives <c>http:///profile</c> an
    /// empty host. It does not: <c>Uri.TryCreate</c> returns false for it outright. The outcome — refused — was
    /// always right; the stated reason was not, which is the kind of thing the next person builds on.
    /// </summary>
    [Fact]
    public void An_authority_less_url_does_not_parse_in_dotnet_at_all()
    {
        Assert.False(Uri.TryCreate("http:///profile", UriKind.Absolute, out _));
        Assert.False(SettingFieldRules.TryNormaliseProfileUrl("http:///profile", out _));
    }

    /// <summary>A refused value leaves the out parameter empty rather than half-built.</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/women/jeans")]
    [InlineData(null)]
    [InlineData("")]
    public void A_refused_value_yields_no_normalised_form(string? value)
    {
        Assert.False(SettingFieldRules.TryNormaliseProfileUrl(value, out var normalised));
        Assert.Equal(string.Empty, normalised);
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
