using SpencerDenim.Application.Content;

namespace SpencerDenim.UnitTests.Application.Content;

/// <summary>
/// <c>content.HomeSections.SettingsJson</c>, read (doc 08 §3.7).
/// <para>
/// This column is typed by a non-developer in ADM-17, so lesson L1 applies: every one of these
/// cases is something an admin form can really produce, and none of them may cost more than the
/// section's settings.
/// </para>
/// </summary>
public sealed class HomeSectionSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"mode\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Anything_unreadable_falls_back_to_the_defaults(string? json)
    {
        var settings = HomeSectionSettings.Parse(json);

        Assert.Equal(HomeSectionMode.Auto, settings.Mode);
        Assert.Equal(HomeSectionSettings.DefaultCount, settings.Count);
        Assert.Null(settings.ListingUrl);
        Assert.Null(settings.LinkLabel);
        Assert.Null(settings.Overlay);
    }

    [Fact]
    public void Everything_the_client_set_is_read()
    {
        var settings = HomeSectionSettings.Parse(
            """{"mode":"manual","count":6,"listingUrl":"/women/jeans?sort=newest","linkLabel":"See all","overlay":"dark"}""");

        Assert.Equal(HomeSectionMode.Manual, settings.Mode);
        Assert.Equal(6, settings.Count);
        Assert.Equal("/women/jeans?sort=newest", settings.ListingUrl);
        Assert.Equal("See all", settings.LinkLabel);
        Assert.Equal("dark", settings.Overlay);
    }

    [Theory]
    [InlineData("\"manual\"", HomeSectionMode.Manual)]
    [InlineData("\"MANUAL\"", HomeSectionMode.Manual)]
    [InlineData("\"auto\"", HomeSectionMode.Auto)]
    // Anything the admin screens do not offer is read as auto rather than as a third behaviour.
    [InlineData("\"nonsense\"", HomeSectionMode.Auto)]
    [InlineData("7", HomeSectionMode.Auto)]
    [InlineData("null", HomeSectionMode.Auto)]
    public void The_mode_is_one_of_two_values(string json, HomeSectionMode expected)
    {
        Assert.Equal(expected, HomeSectionSettings.Parse($$"""{"mode":{{json}}}""").Mode);
    }

    [Theory]
    [InlineData("4", 4)]
    // An admin form posts numbers as strings as often as it posts them as numbers.
    [InlineData("\"4\"", 4)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    // A typo asking for a hundred products is clamped; showing 24 is nearer what was meant than
    // showing nothing, and the cap is what stops one row fetching the catalogue.
    [InlineData("100", HomeSectionSettings.MaxCount)]
    [InlineData("\"eight\"", HomeSectionSettings.DefaultCount)]
    [InlineData("true", HomeSectionSettings.DefaultCount)]
    [InlineData("1.5", HomeSectionSettings.DefaultCount)]
    public void The_count_is_clamped_into_a_range_a_row_can_show(string json, int expected)
    {
        Assert.Equal(expected, HomeSectionSettings.Parse($$"""{"count":{{json}}}""").Count);
    }

    /// <summary>
    /// The listing URL ends up in an <c>href</c> on the homepage, so only a site-relative path is
    /// accepted. A protocol-relative URL is the one that catches people out: <c>//evil.example</c>
    /// starts with a slash and is not on this site.
    /// </summary>
    [Theory]
    [InlineData("/sale", "/sale")]
    [InlineData("/women/jeans?sort=newest", "/women/jeans?sort=newest")]
    [InlineData("  /sale  ", "/sale")]
    [InlineData("//evil.example/sale", null)]
    [InlineData("https://evil.example/sale", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("sale", null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    public void Only_a_path_on_this_site_is_accepted(string json, string? expected)
    {
        var settings = HomeSectionSettings.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { listingUrl = json }));

        Assert.Equal(expected, settings.ListingUrl);
    }

    [Fact]
    public void A_listing_url_longer_than_the_column_is_dropped()
    {
        var tooLong = "/" + new string('a', HomeSectionSettings.MaxLinkLength);

        Assert.Null(HomeSectionSettings
            .Parse(System.Text.Json.JsonSerializer.Serialize(new { listingUrl = tooLong }))
            .ListingUrl);
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("LIGHT", "light")]
    [InlineData("none", "none")]
    [InlineData("chartreuse", null)]
    public void The_overlay_is_one_of_the_three_treatments_the_design_system_has(string json, string? expected)
    {
        var settings = HomeSectionSettings.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { overlay = json }));

        Assert.Equal(expected, settings.Overlay);
    }

    /// <summary>
    /// A key the admin form does not know about is ignored rather than fatal, which is what lets a
    /// later milestone add one without a migration of every existing row.
    /// </summary>
    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var settings = HomeSectionSettings.Parse("""{"futureKey":{"nested":true},"count":3}""");

        Assert.Equal(3, settings.Count);
    }
}
