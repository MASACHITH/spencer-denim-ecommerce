using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Domain.Catalog;

/// <summary>
/// One slug rule for the whole catalogue. The storefront refuses a URL that is not a slug, so a row
/// that is not one is a product the listing links to and nobody can open; the guard is on the setter
/// so an admin write cannot create that dead end. The database CHECK is a W1-03 migration.
/// </summary>
public sealed class CatalogSlugTests
{
    [Theory]
    [InlineData("relaxed-linen-blend-shirt")]
    [InlineData("tee")]
    [InlineData("tee2")]
    [InlineData("100-cotton")]
    public void A_slug_is_lower_case_letters_digits_and_single_hyphens(string value)
    {
        Assert.True(CatalogSlug.IsValid(value));
        Assert.Equal(value, CatalogSlug.Require(value, "slug"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("poplin_formal_shirt")]
    [InlineData("Poplin-Formal-Shirt")]
    [InlineData("poplin formal shirt")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("<script>")]
    [InlineData("100%")]
    [InlineData("../../etc/passwd")]
    public void Anything_else_is_not_a_slug(string? value)
    {
        Assert.False(CatalogSlug.IsValid(value));
        Assert.Throws<ArgumentException>(() => CatalogSlug.Require(value, "slug"));
    }

    /// <summary>
    /// The exact case from the defect report: the listing linked to a product whose own page answered
    /// 400, because nothing stopped the underscore being written.
    /// </summary>
    [Fact]
    public void A_product_cannot_be_given_a_slug_its_own_page_would_refuse()
    {
        var product = new Product { Slug = "poplin-formal-shirt" };

        var exception = Assert.Throws<ArgumentException>(() => product.Slug = "poplin_formal_shirt");

        Assert.Equal("Slug", exception.ParamName);

        // The rejected write leaves the product as it was, rather than half-applied.
        Assert.Equal("poplin-formal-shirt", product.Slug);
    }

    /// <summary>
    /// W1-12's half of the W1-02 follow-up. Until ADM-11 these two columns had no write path, so the guard
    /// was only ever on <see cref="Product.Slug"/>; a category or department whose slug is not a slug is the
    /// same defect one level up — the mega menu links to a listing whose own URL answers 400.
    /// </summary>
    [Fact]
    public void A_category_cannot_be_given_a_slug_its_own_listing_would_refuse()
    {
        var category = new Category { Slug = "denim-jackets" };

        var exception = Assert.Throws<ArgumentException>(() => category.Slug = "Denim Jackets");

        Assert.Equal("Slug", exception.ParamName);
        Assert.Equal("denim-jackets", category.Slug);
    }

    /// <inheritdoc cref="A_category_cannot_be_given_a_slug_its_own_listing_would_refuse"/>
    [Fact]
    public void A_department_cannot_be_given_a_slug_its_own_page_would_refuse()
    {
        var department = new Department { Slug = "women" };

        var exception = Assert.Throws<ArgumentException>(() => department.Slug = "women/clothing");

        Assert.Equal("Slug", exception.ParamName);
        Assert.Equal("women", department.Slug);
    }
}
