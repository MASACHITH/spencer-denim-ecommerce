using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Admin;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin;

/// <summary>
/// The ADM-11 field rules (issue #24: validate in the command, not only in the domain guard).
/// </summary>
public sealed class CatalogFieldRulesTests
{
    [Theory]
    [InlineData("denim-jackets")]
    [InlineData("a")]
    [InlineData("tops2")]
    public void A_good_slug_is_accepted(string slug)
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.RequireSlug(errors, "slug", slug, CatalogFieldRules.CategorySlugMaxLength);

        Assert.Empty(errors);
    }

    /// <summary>
    /// Each of these would throw out of the property setter if it reached the entity, which is issue #24's
    /// whole point: an admin's typo has to be a 400 naming the box, not a 500.
    /// </summary>
    [Theory]
    [InlineData("Denim-Jackets")]
    [InlineData("denim jackets")]
    [InlineData("denim_jackets")]
    [InlineData("-denim")]
    [InlineData("denim-")]
    [InlineData("denim--jackets")]
    [InlineData("denim/jackets")]
    public void A_bad_slug_is_a_field_error_rather_than_an_exception(string slug)
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.RequireSlug(errors, "slug", slug, CatalogFieldRules.CategorySlugMaxLength);

        var error = Assert.Single(errors);
        Assert.Equal("slug", error.Field);
        Assert.Contains("lower-case", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_slug_names_the_field_too()
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.RequireSlug(errors, "slug", "   ", CatalogFieldRules.CategorySlugMaxLength);

        Assert.Equal("slug", Assert.Single(errors).Field);
    }

    [Fact]
    public void A_slug_longer_than_the_column_is_refused_before_SQL_Server_truncates_it()
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.RequireSlug(
            errors, "slug", new string('a', CatalogFieldRules.CategorySlugMaxLength + 1), CatalogFieldRules.CategorySlugMaxLength);

        Assert.Contains("100", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/women/jeans")]
    [InlineData("/women?material=denim")]
    [InlineData("https://example.com/lookbook")]
    [InlineData("http://example.com")]
    public void A_safe_banner_link_is_accepted(string url)
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.CheckBannerLink(errors, "bannerLinkUrl", url);

        Assert.Empty(errors);
    }

    /// <summary>
    /// The point of the rule. Nothing projects this column to the storefront yet, which is exactly why the
    /// value has to be refused now rather than when something does: by then the rows are in the database.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("//evil.example.com/looks-relative")]
    [InlineData("women/jeans")]
    [InlineData("vbscript:msgbox(1)")]
    public void A_banner_link_that_is_neither_a_path_on_this_site_nor_http_is_refused(string url)
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.CheckBannerLink(errors, "bannerLinkUrl", url);

        Assert.Equal("bannerLinkUrl", Assert.Single(errors).Field);
    }

    [Fact]
    public void A_blank_banner_link_is_simply_absent()
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.CheckBannerLink(errors, "bannerLinkUrl", "  ");

        Assert.Empty(errors);
        Assert.Null(CatalogFieldRules.Clean("  "));
    }

    [Fact]
    public void Clearing_a_box_stores_null_rather_than_an_empty_string()
    {
        Assert.Null(CatalogFieldRules.Clean(string.Empty));
        Assert.Null(CatalogFieldRules.Clean(null));
        Assert.Equal("Jeans", CatalogFieldRules.Clean("  Jeans  "));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_mega_menu_column_outside_one_to_four_is_refused(int column)
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.CheckMegaMenuColumn(errors, "megaMenuColumn", (byte)column);

        Assert.Equal("megaMenuColumn", Assert.Single(errors).Field);
    }

    [Fact]
    public void No_mega_menu_column_is_allowed_because_the_column_is_nullable()
    {
        var errors = new List<ValidationError>();

        CatalogFieldRules.CheckMegaMenuColumn(errors, "megaMenuColumn", null);

        Assert.Empty(errors);
    }

    [Fact]
    public void A_row_version_token_round_trips_through_base64()
    {
        var bytes = new byte[] { 0, 0, 0, 0, 0, 0, 31, 164 };

        var read = CatalogFieldRules.ReadRowVersion(Convert.ToBase64String(bytes));

        Assert.Equal(bytes, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    public void A_missing_or_malformed_row_version_reads_as_no_token_rather_than_throwing(string? supplied) =>
        Assert.Null(CatalogFieldRules.ReadRowVersion(supplied));
}
