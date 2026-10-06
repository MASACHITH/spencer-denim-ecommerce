using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// <c>ProductRequestMapper</c>: the layer between the ADM-10 request bodies and the use-case input.
/// <para>
/// <b>This file did not exist, and that is why M1 shipped.</b> The badge branch called
/// <c>Enum.IsDefined(typeof(ProductBadge), badgeValue)</c> with an <c>int</c> against a <c>byte</c> enum,
/// which throws <see cref="ArgumentException"/> — so <i>every</i> badge value answered 500, the range check
/// beside it was dead code, and the field error it was written to produce was unreachable. A grep for
/// <c>badgeOverride</c> across the whole test tree returned nothing, which is how 1,255 unit, 883
/// integration, 348 Vitest and 43 browser tests stayed green over a broken scope bullet.
/// </para>
/// <para>
/// The mapper is a pure function, so every one of these costs nothing. There is no excuse for the gap and
/// no reason to leave it anywhere near this shape again.
/// </para>
/// </summary>
public sealed class ProductRequestMapperTests
{
    // ---- the badge, which is M1 -------------------------------------------------------------------

    [Theory]
    [InlineData("New", ProductBadge.New)]
    [InlineData("Sale", ProductBadge.Sale)]
    [InlineData("OnlineExclusive", ProductBadge.OnlineExclusive)]
    [InlineData("BestSeller", ProductBadge.BestSeller)]
    public void Every_assignable_badge_is_accepted(string name, ProductBadge expected)
    {
        var (form, field, message) = ProductRequestMapper.Map(Body() with { BadgeOverride = name });

        Assert.Null(field);
        Assert.Null(message);
        Assert.Equal(expected, form!.BadgeOverride);
    }

    /// <summary>The names are what the API emits, so a round-trip must not care about case.</summary>
    [Theory]
    [InlineData("sale")]
    [InlineData("SALE")]
    [InlineData("  Sale  ")]
    public void A_badge_name_is_read_case_insensitively_and_trimmed(string name)
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with { BadgeOverride = name });

        Assert.Null(field);
        Assert.Equal(ProductBadge.Sale, form!.BadgeOverride);
    }

    [Fact]
    public void No_badge_means_the_shop_works_one_out()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with { BadgeOverride = null });

        Assert.Null(field);
        Assert.Null(form!.BadgeOverride);
    }

    /// <summary>A blank string is "not set", not a value — an admin who cleared a select sends either.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_badge_is_read_as_not_set(string name)
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with { BadgeOverride = name });

        Assert.Null(field);
        Assert.Null(form!.BadgeOverride);
    }

    /// <summary>
    /// Doc 08 §3.2: <c>LowStock</c> is computed at read time and never stored, so it is a real
    /// <see cref="ProductBadge"/> that this field must refuse. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// would accept it, which is why the mapper checks an allow-list rather than the parse's own answer.
    /// </summary>
    [Fact]
    public void Low_stock_is_refused_although_it_is_a_real_badge()
    {
        Assert.Contains(nameof(ProductBadge.LowStock), Enum.GetNames<ProductBadge>());

        var (form, field, message) = ProductRequestMapper.Map(
            Body() with { BadgeOverride = nameof(ProductBadge.LowStock) });

        Assert.Null(form);
        Assert.Equal("badgeOverride", field);
        Assert.Contains("LowStock", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The numeric strings <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would accept,
    /// including ones outside the enum. Each has to be a named field error rather than an undefined value
    /// in the database or a 500.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("9")]
    [InlineData("255")]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("Badge")]
    [InlineData("new-arrival")]
    public void Anything_that_is_not_an_assignable_badge_name_is_a_named_field_error(string name)
    {
        var (form, field, message) = ProductRequestMapper.Map(Body() with { BadgeOverride = name });

        Assert.Null(form);
        Assert.Equal("badgeOverride", field);
        Assert.NotNull(message);
    }

    // ---- the media role, which is M2 --------------------------------------------------------------

    [Theory]
    [InlineData("Gallery", ProductMediaRole.Gallery)]
    [InlineData("Hover", ProductMediaRole.Hover)]
    [InlineData("Video", ProductMediaRole.Video)]
    [InlineData("gallery", ProductMediaRole.Gallery)]
    public void Every_media_role_name_the_api_emits_is_accepted(string name, ProductMediaRole expected)
    {
        var (form, field, message) = ProductRequestMapper.Map(Body() with { Media = [Media(name)] });

        Assert.Null(field);
        Assert.Null(message);
        Assert.Equal(expected, Assert.Single(form!.Media!).MediaRole);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("Poster")]
    [InlineData("Swatch")]
    public void Anything_that_is_not_a_media_role_name_is_a_named_field_error(string? name)
    {
        var (form, field, message) = ProductRequestMapper.Map(Body() with { Media = [Media(name)] });

        Assert.Null(form);
        Assert.Equal("media", field);
        Assert.NotNull(message);
    }

    [Fact]
    public void A_media_row_with_no_asset_is_a_named_field_error()
    {
        var (form, field, _) = ProductRequestMapper.Map(
            Body() with { Media = [Media("Gallery") with { AssetPublicId = null }] });

        Assert.Null(form);
        Assert.Equal("media", field);
    }

    /// <summary>
    /// The distinction the W1-12 defect turned on: an absent <c>media</c> field means "say nothing about
    /// the photographs", an empty array means "remove them".
    /// </summary>
    [Fact]
    public void An_absent_media_field_stays_absent_and_an_empty_one_stays_empty()
    {
        Assert.Null(ProductRequestMapper.Map(Body() with { Media = null }).Form!.Media);
        Assert.Empty(ProductRequestMapper.Map(Body() with { Media = [] }).Form!.Media!);
    }

    // ---- the three required ids --------------------------------------------------------------------

    [Fact]
    public void A_body_with_no_department_is_a_named_field_error()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with { DepartmentId = null });

        Assert.Null(form);
        Assert.Equal("departmentId", field);
    }

    [Fact]
    public void A_body_with_no_primary_category_is_a_named_field_error()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with { PrimaryCategoryId = null });

        Assert.Null(form);
        Assert.Equal("primaryCategoryId", field);
    }

    [Fact]
    public void A_body_with_no_price_is_a_named_field_error()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with { BasePrice = null });

        Assert.Null(form);
        Assert.Equal("basePrice", field);
    }

    [Fact]
    public void A_colour_with_no_family_is_a_named_field_error_that_says_which_colour()
    {
        var (form, field, message) = ProductRequestMapper.Map(Body() with
        {
            Colours = [new ProductColourRequest(null, "Indigo Rinse", null, null, null, 0)],
        });

        Assert.Null(form);
        Assert.Equal("colours", field);
        Assert.Contains("Indigo Rinse", message!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variant_with_no_size_is_a_named_field_error()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with
        {
            Variants = [new ProductVariantRequest(null, "Indigo Rinse", null, "SKU", null, null, null, true, 0)],
        });

        Assert.Null(form);
        Assert.Equal("variants", field);
    }

    [Fact]
    public void A_related_row_with_no_product_is_a_named_field_error()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with
        {
            RelatedProducts = [new RelatedProductRequest(null, 0)],
        });

        Assert.Null(form);
        Assert.Equal("relatedProducts", field);
    }

    // ---- sort orders -------------------------------------------------------------------------------

    /// <summary>
    /// <c>SortOrder</c> is <c>smallint</c> (doc 08 §3.2). It is clamped rather than refused because the
    /// screen computes it, so a number out of range is a client bug and the useful answer is the end of
    /// the list rather than an error about a control that is not on the form.
    /// </summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData(7, 7)]
    [InlineData(int.MaxValue, short.MaxValue)]
    [InlineData(int.MinValue, short.MinValue)]
    public void A_sort_order_is_clamped_into_smallint(int? supplied, short expected)
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with
        {
            Colours = [new ProductColourRequest(null, "Indigo Rinse", 51, null, null, supplied)],
        });

        Assert.Null(field);
        Assert.Equal(expected, Assert.Single(form!.Colours!).SortOrder);
    }

    /// <summary>A size row that does not say is on: adding a size means meaning to sell it.</summary>
    [Fact]
    public void A_variant_that_does_not_say_whether_it_is_active_is_on()
    {
        var (form, field, _) = ProductRequestMapper.Map(Body() with
        {
            Variants = [new ProductVariantRequest(null, "Indigo Rinse", 61, "SKU", null, null, null, null, 0)],
        });

        Assert.Null(field);
        Assert.True(Assert.Single(form!.Variants!).IsActive);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>A body that maps cleanly, so each test changes exactly the one field it is about.</summary>
    private static ProductRequestBody Body() => new(
        "SD-W-0001",
        "Wide-Leg Rinse Jeans",
        "wide-leg-rinse-jeans",
        1,
        11,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        5490m,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    private static ProductMediaRequest Media(string? mediaRole) =>
        new(Guid.NewGuid(), null, mediaRole, 0, "Front view");
}
