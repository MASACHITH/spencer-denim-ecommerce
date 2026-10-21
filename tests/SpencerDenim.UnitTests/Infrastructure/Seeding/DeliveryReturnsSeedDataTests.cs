using System.Text.RegularExpressions;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Content;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure.Seeding;

/// <summary>
/// Issue #33's seeded words and couriers, checked against the rules their tables and readers enforce: the copy is
/// already in the sanitiser's output form and quotes no delivery fee, and every courier passes the constraints
/// and the admin validator a staff member's own courier would.
/// </summary>
public sealed partial class DeliveryReturnsSeedDataTests
{
    public static TheoryData<string, string> SeededHtml => new()
    {
        { "PdpDeliveryInfoHtml", ContentSettingDefinitions.DeliveryInfoSeed },
        { "PdpReturnsInfoHtml", ContentSettingDefinitions.ReturnsInfoSeed },
        { "shipping", PolicyPageContent.ShippingBody },
        { "returns", PolicyPageContent.ReturnsBody },
    };

    [Theory]
    [MemberData(nameof(SeededHtml))]
    public void Seeded_copy_is_exactly_what_the_sanitiser_would_store(string name, string html)
    {
        // The product-page texts are cleaned with the narrower snippet profile; the pages with the page profile.
        var cleaned = name.StartsWith("Pdp", StringComparison.Ordinal)
            ? new HtmlSanitiser().SanitiseSnippet(html)
            : new HtmlSanitiser().Sanitise(html);

        Assert.False(cleaned.WasRejected, name);
        Assert.Empty(cleaned.Removed);
        Assert.Equal(html, cleaned.Html);
    }

    /// <summary>
    /// Fees live in <c>DeliveryMethodZoneRates</c>. A rupee figure in the copy would contradict checkout the first
    /// time the owner changed a rate, so none is allowed; the policy numbers (14 days, 48 hours) are not money.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeededHtml))]
    public void Seeded_copy_quotes_no_delivery_fee(string name, string html)
    {
        Assert.False(MoneyPattern().IsMatch(html), $"{name} quotes a fee: {MoneyPattern().Match(html).Value}");
    }

    [Theory]
    [MemberData(nameof(SeededHtml))]
    public void Seeded_copy_states_the_owners_terms_and_no_retired_ones(string name, string html)
    {
        // PayPal-only (owner decision 2026-10-21): no copy promises a bank-transfer refund or bank-transfer payment,
        // and none says cash on delivery is offered.
        Assert.DoesNotContain("by bank transfer", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("island-wide only", html, StringComparison.OrdinalIgnoreCase);
        // The one marker allowed: where the Shipping page draws its live fee list.
        Assert.DoesNotContain("{{", html.Replace(PolicyPageContent.DeliveryFeesMarker, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/contact\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Final sale", html, StringComparison.Ordinal);
        Assert.DoesNotContain("email you when", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ready to collect", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[default", html, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void The_policy_copy_carries_the_owner_defaults()
    {
        Assert.Contains("14 days", PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
        Assert.Contains("48 hours", PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
        Assert.Contains("7 working days", PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
        Assert.Contains("original payment method", PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
        Assert.Contains("Customs duties", PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
        Assert.Contains("worldwide", ContentSettingDefinitions.DeliveryInfoSeed, StringComparison.Ordinal);
        Assert.Contains("overseas", PolicyPageContent.ShippingBody, StringComparison.Ordinal);
        Assert.Contains("Cash on delivery is not available", PolicyPageContent.ShippingBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_and_the_returns_page_name_the_same_final_sale_items_and_soften_the_48_hour_rule()
    {
        foreach (var item in new[] { "lingerie", "swimwear", "socks", "hosiery", "pierced jewellery", "sale items" })
        {
            Assert.Contains(item, ContentSettingDefinitions.ReturnsInfoSeed, StringComparison.Ordinal);
            Assert.Contains(item, PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
        }

        foreach (var html in new[] { ContentSettingDefinitions.ReturnsInfoSeed, PolicyPageContent.ReturnsBody })
        {
            Assert.Contains("damaged, faulty or wrong", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("as soon as you notice", html, StringComparison.Ordinal);
            Assert.Contains("48 hours", html, StringComparison.Ordinal);
        }

        // Every delivery time is counted from dispatch, in the texts and on the page.
        Assert.Contains("from dispatch", ContentSettingDefinitions.DeliveryInfoSeed, StringComparison.Ordinal);
        Assert.Contains("from dispatch", PolicyPageContent.ShippingBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipping_page_marks_where_the_fee_list_goes_under_delivery_within_sri_lanka()
    {
        var body = PolicyPageContent.ShippingBody;
        var section = body.IndexOf("<h2>Delivery within Sri Lanka</h2>", StringComparison.Ordinal);
        var marker = body.IndexOf("<p>" + PolicyPageContent.DeliveryFeesMarker + "</p>", StringComparison.Ordinal);
        var next = body.IndexOf("<h2>", section + 1, StringComparison.Ordinal);

        Assert.InRange(marker, section + 1, next - 1);
    }

    [Fact]
    public void The_snippet_profile_keeps_no_picture_and_no_size()
    {
        var cleaned = new HtmlSanitiser().SanitiseSnippet(
            "<p>Hi</p><img src=\"/a.webp\" alt=\"A\" width=\"2400\"><table width=\"2400\"><tr><td height=\"900\">x</td></tr></table>"
            + "<figure><figcaption>c</figcaption></figure><a href=\"/shipping\" title=\"t\">s</a>");

        Assert.DoesNotContain("<img", cleaned.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("width", cleaned.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("height", cleaned.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<figure", cleaned.Html, StringComparison.Ordinal);
        Assert.Contains("<table>", cleaned.Html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/shipping\" title=\"t\">s</a>", cleaned.Html, StringComparison.Ordinal);
        Assert.Contains("<img>", cleaned.Removed);
        Assert.Contains("width", cleaned.Removed);

        // The page profile is unchanged: a content page may still carry a sized picture.
        Assert.Contains("width=\"2400\"", new HtmlSanitiser().Sanitise("<img src=\"/a.webp\" alt=\"A\" width=\"2400\">").Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_shipping_and_returns_pages_are_seeded_with_copy()
    {
        Assert.Equal(
            [ContentPageKeys.Returns, ContentPageKeys.Shipping],
            PolicyPageContent.Bodies.Keys.Order(StringComparer.Ordinal));
        Assert.All(PolicyPageContent.Bodies.Values, body => Assert.InRange(body.Length, 1, ContentPage.BodyHtmlMaxLength));
    }

    [Fact]
    public void The_product_page_texts_are_text_html_settings_edit_and_fit_the_save_cap()
    {
        foreach (var definition in ContentSettingDefinitions.All)
        {
            Assert.Equal(SettingKeys.Content.Group, definition.GroupKey);
            Assert.Equal(SettingValueType.TextHtml, definition.ValueType);
            Assert.Equal(Permissions.Settings.Edit, definition.RequiredPermission);
            Assert.InRange(definition.SeedValue!.Length, 1, SettingKeys.Content.PdpInfoMaxLength);
        }
    }

    // ---- an untouched page --------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_policy_page_is_untouched(string body)
    {
        Assert.True(HomeContentSeeder.IsUntouched(new ContentPage { SystemKey = ContentPageKeys.Shipping, BodyHtml = body }));
    }

    [Fact]
    public void A_policy_page_with_anybodys_words_is_not_untouched()
    {
        Assert.False(HomeContentSeeder.IsUntouched(new ContentPage { SystemKey = ContentPageKeys.Returns, BodyHtml = "<p>Ours.</p>" }));

        // Not even the current seed: version 1 has no earlier seed to treat as untouched.
        Assert.False(HomeContentSeeder.IsUntouched(new ContentPage { SystemKey = ContentPageKeys.Returns, BodyHtml = PolicyPageContent.ReturnsBody }));
    }

    // ---- couriers ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_seeded_courier_passes_the_constraints_and_the_admin_validator()
    {
        Assert.Equal(
            SalesReferenceData.Couriers.Count,
            SalesReferenceData.Couriers.Select(courier => courier.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var courier in SalesReferenceData.Couriers)
        {
            Assert.InRange(courier.Name.Length, 1, 80);
            Assert.InRange(courier.IntroducedIn, 1, SalesReferenceData.CourierSeedVersion);

            if (courier.Phone is { } phone)
            {
                Assert.InRange(phone.Length, 1, 20);
                Assert.All(phone, character => Assert.InRange(character, ' ', '~'));
            }

            if (courier.TrackingUrlTemplate is { } template)
            {
                Assert.Contains(Courier.TrackingNumberPlaceholder, template, StringComparison.Ordinal);
                Assert.StartsWith("https://", template, StringComparison.Ordinal);
                Assert.True(Uri.TryCreate(template, UriKind.Absolute, out _), template);
                Assert.InRange(template.Length, 1, 500);
            }
        }
    }

    [Fact]
    public void The_three_verified_link_couriers_are_on_and_everything_else_is_off()
    {
        Assert.Equal(
            ["Domex", "Koombiyo Delivery", "Citypak"],
            SalesReferenceData.Couriers.Where(courier => courier.IsActive).Select(courier => courier.Name));
        Assert.All(
            SalesReferenceData.Couriers.Where(courier => courier.IsActive),
            courier => Assert.NotNull(courier.TrackingUrlTemplate));
        Assert.All(
            SalesReferenceData.Couriers.Where(courier => !courier.IsActive),
            courier => Assert.Null(courier.TrackingUrlTemplate));
        Assert.Contains(SalesReferenceData.Couriers, courier => courier.Name == "DHL Express");
        Assert.Contains(SalesReferenceData.Couriers, courier => courier.Name == "Sri Lanka Post EMS");
        Assert.Equal(8, SalesReferenceData.Couriers.Count);
    }

    [Theory]
    [InlineData("Domex", "https://www.domex.lk/Order-Details.php?wbno=DX%2012%2F3")]
    [InlineData("Koombiyo Delivery", "https://koombiyodelivery.lk/Track/track_id?id=DX%2012%2F3&phone=")]
    [InlineData("Citypak", "https://track.citypak.lk/track?tracking_number=DX%2012%2F3")]
    public void A_seeded_template_renders_a_link_with_the_number_escaped(string name, string expected)
    {
        var template = SalesReferenceData.Couriers.Single(courier => courier.Name == name).TrackingUrlTemplate;

        Assert.Equal(expected, OrderTrackingLink.Build(template, "DX 12/3"));
    }

    [Theory]
    [InlineData("Pronto Lanka")]
    [InlineData("Trans Express")]
    [InlineData("Sri Lanka Post (Speed Post)")]
    [InlineData("Sri Lanka Post EMS")]
    [InlineData("DHL Express")]
    public void A_courier_with_no_template_renders_no_link_so_the_number_is_shown_as_text(string name)
    {
        var template = SalesReferenceData.Couriers.Single(courier => courier.Name == name).TrackingUrlTemplate;

        Assert.Null(OrderTrackingLink.Build(template, "PL-12345"));
    }

    [GeneratedRegex(@"(?:Rs\.?|LKR)\s?\d", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyPattern();
}
