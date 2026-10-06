using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.UnitTests.Application.Catalog.Admin;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin.Products;

/// <summary>
/// The ADM-10 product use cases (W1-03): what decides whether a write happens at all, and what the
/// aggregate and its derived data look like afterwards.
/// </summary>
public sealed class ProductWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly AdminActor Actor = new(5, "manager@example.com", "203.0.113.4", "agent", "corr");

    private readonly FakeProductWriteStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();
    private readonly CatalogOptions _options = new() { NewArrivalDays = 30 };

    public ProductWriteHandlerTests()
    {
        _store.Departments[1] = ("Women", "women");
        _store.Departments[3] = ("Kids", "kids");

        _store.Categories[10] = new CategoryReference(10, 1, "Clothing", "/10/", true, ["Clothing"]);
        _store.Categories[11] = new CategoryReference(
            11, 1, "Jeans", "/10/11/", true, ["Clothing", "Jeans"]);
        _store.Categories[12] = new CategoryReference(12, 1, "Sale", "/12/", true, ["Sale"]);
        _store.Categories[30] = new CategoryReference(30, 3, "Girls", "/30/", true, ["Girls"]);

        _store.AgeGroupIds.Add(2);
        _store.AttributeOptions[41] = (AttributeOptionType.Material, "Denim");
        _store.AttributeOptions[42] = (AttributeOptionType.Fit, "Slim");
        _store.AttributeOptions[43] = (AttributeOptionType.Occasion, "Everyday");
        _store.ColourFamilies[51] = "Blue";
        _store.ColourFamilies[52] = "Black";
        _store.Sizes[61] = new SizeReference(61, "Alpha", "S", 1);
        _store.Sizes[62] = new SizeReference(62, "Alpha", "M", 2);
        _store.Sizes[63] = new SizeReference(63, "Alpha", "L", 3);
        _store.SizeGuideIds.Add(70);
    }

    // ---- create ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_product_is_unpublished_and_carries_its_whole_aggregate()
    {
        var photo = _store.AddImageAsset();
        var hover = _store.AddImageAsset();

        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S"), Variant("Indigo Rinse", 62, "SKU-M")],
            media:
            [
                new ProductMediaInput(photo, null, ProductMediaRole.Gallery, 0, "Front view"),
                new ProductMediaInput(hover, null, ProductMediaRole.Hover, 1, "Back view"),
            ],
            extraCategoryIds: [12]));

        var created = Assert.IsType<CatalogWriteResult.Succeeded>(result);
        var product = _store.Products[created.EntityId];

        Assert.False(product.IsPublished);
        Assert.Null(product.FirstPublishedAtUtc);
        Assert.Equal("wide-leg-rinse-jeans", product.Slug);
        Assert.Equal(2, product.Variants.Count);
        Assert.Single(product.Colours);

        // Doc 08 §3.2: the primary category is stored in ProductCategories too, so the link set is the
        // primary plus the extras.
        Assert.Equal([11, 12], product.CategoryLinks.Select(link => link.CategoryId).Order());

        // The composite FK (ProductColourId, ProductId): every variant points at the colourway created in
        // the same request, which has no identity value until the insert.
        var colourId = product.Colours.Single().Id;
        Assert.All(product.Variants, variant => Assert.Equal(colourId, variant.ProductColourId));

        // Doc 11 P1 step 10: a stock row per variant, so the availability join can see them.
        Assert.Equal(product.Variants.Select(variant => variant.Id).Order(), _store.StockRowsCreated.Order());

        var entry = Assert.Single(_audit.Enlisted);
        Assert.Equal(AuditActions.Create, entry.Action);
        Assert.Equal(ProductAuditTrail.ProductEntityType, entry.EntityType);
        Assert.Equal(created.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.EntityId);
    }

    [Fact]
    public async Task A_new_product_denormalises_its_card_and_hover_images_and_claims_their_usage_rows()
    {
        var first = _store.AddImageAsset();
        var second = _store.AddImageAsset();
        var hover = _store.AddImageAsset();

        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S")],
            media:
            [
                new ProductMediaInput(first, null, ProductMediaRole.Gallery, 0, "One"),
                new ProductMediaInput(second, null, ProductMediaRole.Gallery, 1, "Two"),
                new ProductMediaInput(hover, null, ProductMediaRole.Hover, 0, "Hover"),
            ]));

        var product = _store.Products[Assert.IsType<CatalogWriteResult.Succeeded>(result).EntityId];

        Assert.Equal(_store.MediaAssets[first].Id, product.CardImageAssetId);
        Assert.Equal(_store.MediaAssets[hover].Id, product.HoverImageAssetId);

        // Doc 08 §3.3: without these rows the media library's own soft delete would let a staff member take
        // the photographs off a live product and be told they were unused.
        var usages = Assert.IsType<List<ProductMediaUsage>>(_store.LastMediaUsages);
        Assert.Contains(new ProductMediaUsage(_store.MediaAssets[first].Id, MediaUsageRoles.Card), usages);
        Assert.Contains(new ProductMediaUsage(_store.MediaAssets[first].Id, MediaUsageRoles.Gallery), usages);
        Assert.Contains(new ProductMediaUsage(_store.MediaAssets[hover].Id, MediaUsageRoles.Hover), usages);
    }

    /// <summary>
    /// CARD-01's hover effect is the next photograph of the same product, so a product with two gallery
    /// images and no explicit hover gets the second one — but a product with one image gets no hover at
    /// all rather than a hover of itself, which reads to a shopper as a card that is broken.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task The_hover_image_falls_back_to_the_second_gallery_image(int galleryCount, bool expectHover)
    {
        var media = Enumerable.Range(0, galleryCount)
            .Select(index => new ProductMediaInput(
                _store.AddImageAsset(), null, ProductMediaRole.Gallery, (short)index, $"Photo {index}"))
            .ToList();

        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S")],
            media: media));

        var product = _store.Products[Assert.IsType<CatalogWriteResult.Succeeded>(result).EntityId];

        Assert.NotNull(product.CardImageAssetId);
        Assert.Equal(expectHover, product.HoverImageAssetId is not null);
    }

    [Fact]
    public async Task A_kids_product_without_an_age_group_is_refused_by_name()
    {
        var result = await Create(Form(departmentId: 3, primaryCategoryId: 30, ageGroupId: null));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("ageGroupId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_kids_product_with_an_age_group_is_accepted()
    {
        var result = await Create(Form(departmentId: 3, primaryCategoryId: 30, ageGroupId: 2));

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
    }

    /// <summary>
    /// Doc 08 §3.2: Material, Fit and Occasion are three foreign keys into one table, so the type is the
    /// only thing that stops a fit being stored as a material. No constraint can check it.
    /// </summary>
    [Fact]
    public async Task A_fit_option_cannot_be_saved_as_the_material()
    {
        var result = await Create(Form(materialId: 42));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("materialId", Assert.Single(invalid.Errors).Field);
        Assert.Contains("fit option", Assert.Single(invalid.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_primary_category_from_another_department_is_refused()
    {
        var result = await Create(Form(departmentId: 1, primaryCategoryId: 30));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("primaryCategoryId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_slug_that_is_not_a_slug_is_a_named_field_error_rather_than_an_exception()
    {
        var result = await Create(Form(slug: "Wide Leg Jeans!"));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("slug", Assert.Single(invalid.Errors).Field);
    }

    /// <summary>
    /// <c>UX_ProductVariants_Sku</c> has no soft-delete filter, so a SKU is unique across every variant the
    /// shop has ever had. Telling the admin it is free and then handing them a 409 from the index would be
    /// the wrong answer twice.
    /// </summary>
    [Fact]
    public async Task A_sku_that_belongs_to_a_removed_products_variant_is_refused_against_its_own_row()
    {
        _store.TakenSkus.Add("SKU-S");

        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S")]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("variants[0].sku", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task Two_rows_of_one_product_cannot_share_a_sku()
    {
        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SAME"), Variant("Indigo Rinse", 62, "SAME")]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("variants[1].sku", Assert.Single(invalid.Errors).Field);
    }

    /// <summary>Doc 08 §3.2's <c>UX_ProductVariants_Colour_Size</c>: one row per colour × size.</summary>
    [Fact]
    public async Task One_colour_and_size_cannot_appear_twice()
    {
        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "A"), Variant("Indigo Rinse", 61, "B")]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("variants[1].sizeId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_variant_naming_a_colour_the_request_does_not_declare_is_refused()
    {
        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Jet Black", 61, "SKU-S")]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("variants[0].colourName", Assert.Single(invalid.Errors).Field);
    }

    /// <summary>
    /// Doc 08 §3.2: the cross-table case — a variant price against the product's own original price —
    /// "is validated on save", because a check constraint cannot see across the two tables.
    /// </summary>
    [Fact]
    public async Task A_variant_priced_above_the_products_original_price_is_refused()
    {
        var result = await Create(Form(
            basePrice: 5490m,
            compareAtPrice: 6990m,
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S") with { PriceOverride = 7990m }]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("variants[0].priceOverride", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task An_original_price_at_or_below_the_price_is_refused()
    {
        var result = await Create(Form(basePrice: 5490m, compareAtPrice: 5490m));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("compareAtPrice", Assert.Single(invalid.Errors).Field);
    }

    [Theory]
    [InlineData(MediaAssetType.Video, MediaAssetStatus.Ready, MediaVisibility.Public)]
    [InlineData(MediaAssetType.Image, MediaAssetStatus.Processing, MediaVisibility.Public)]
    [InlineData(MediaAssetType.Image, MediaAssetStatus.Failed, MediaVisibility.Public)]
    [InlineData(MediaAssetType.Image, MediaAssetStatus.Ready, MediaVisibility.Private)]
    public async Task A_gallery_image_refuses_an_asset_that_would_render_as_nothing(
        MediaAssetType type,
        MediaAssetStatus status,
        MediaVisibility visibility)
    {
        var asset = _store.AddAsset(type, status, visibility);

        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S")],
            media: [new ProductMediaInput(asset, null, ProductMediaRole.Gallery, 0, "Alt")]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("media[0].assetPublicId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task The_video_slot_refuses_an_image_and_accepts_an_mp4()
    {
        var image = _store.AddImageAsset();
        var video = _store.AddVideoAsset();
        var gallery = _store.AddImageAsset();

        var refused = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S")],
            media: [new ProductMediaInput(image, null, ProductMediaRole.Video, 0, null)]));

        Assert.IsType<CatalogWriteResult.Invalid>(refused);

        var accepted = await Create(Form(
            productCode: "SD-W-0002",
            slug: "second-product",
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S2")],
            media:
            [
                new ProductMediaInput(gallery, null, ProductMediaRole.Gallery, 0, "Alt"),
                new ProductMediaInput(video, null, ProductMediaRole.Video, 1, null),
            ]));

        var product = _store.Products[Assert.IsType<CatalogWriteResult.Succeeded>(accepted).EntityId];
        Assert.Single(product.Media, media => media.MediaRole == ProductMediaRole.Video);

        // A video is never the card image: doc 11 P1's WooCommerce comparison is explicit that it "can
        // never be the main image", and a card whose src was an MP4 renders as nothing.
        Assert.Equal(_store.MediaAssets[gallery].Id, product.CardImageAssetId);
    }

    [Fact]
    public async Task A_product_has_only_one_video()
    {
        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S")],
            media:
            [
                new ProductMediaInput(_store.AddVideoAsset(), null, ProductMediaRole.Video, 0, null),
                new ProductMediaInput(_store.AddVideoAsset(), null, ProductMediaRole.Video, 1, null),
            ]));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("media[1].mediaRole", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_product_cannot_be_related_to_itself()
    {
        var existing = await Seed();

        var result = await Update(existing.Id, FormFrom(existing) with
        {
            RelatedProducts = [new RelatedProductInput(existing.Id, 0)],
        });

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("relatedProducts[0].relatedProductId", Assert.Single(invalid.Errors).Field);
    }

    // ---- the W1-12 defect: media must survive a save that does not mention it --------------------

    /// <summary>
    /// <b>The defect W1-12 shipped, in the place it would be worst.</b> Every save of a homepage block
    /// cleared its images, and 1,571 passing tests missed it because every fixture set those columns to
    /// null. So this fixture has <i>real media attached</i> — three gallery images, a hover image and a
    /// video with a poster — and the save under test says nothing about any of it.
    /// </summary>
    [Fact]
    public async Task A_save_that_does_not_mention_media_keeps_every_photograph_and_the_video()
    {
        var existing = await SeedWithMedia();

        var mediaBefore = existing.Media
            .Select(media => (media.Id, media.MediaAssetId, media.MediaRole, media.SortOrder, media.AltTextOverride))
            .OrderBy(row => row.Id)
            .ToList();

        Assert.Equal(5, mediaBefore.Count);
        var cardBefore = existing.CardImageAssetId;
        var hoverBefore = existing.HoverImageAssetId;

        // A whole-product PUT that renames it and says nothing at all about media.
        var result = await Update(existing.Id, FormFrom(existing) with { Name = "Wide-Leg Rinse Jeans (2026)", Media = null });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var after = _store.Products[existing.Id];

        Assert.Equal("Wide-Leg Rinse Jeans (2026)", after.Name);
        Assert.Equal(
            mediaBefore,
            after.Media
                .Select(media => (media.Id, media.MediaAssetId, media.MediaRole, media.SortOrder, media.AltTextOverride))
                .OrderBy(row => row.Id)
                .ToList());

        Assert.Equal(cardBefore, after.CardImageAssetId);
        Assert.Equal(hoverBefore, after.HoverImageAssetId);
        Assert.NotNull(after.CardImageAssetId);
        Assert.Contains(after.Media, media => media.MediaRole == ProductMediaRole.Video);
    }

    /// <summary>The other half of the same rule: an empty list is an instruction, and it is obeyed.</summary>
    [Fact]
    public async Task A_save_that_sends_an_empty_media_list_clears_the_photographs()
    {
        var existing = await SeedWithMedia();

        var result = await Update(existing.Id, FormFrom(existing) with { Media = [] });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var after = _store.Products[existing.Id];
        Assert.Empty(after.Media);
        Assert.Null(after.CardImageAssetId);
        Assert.Null(after.HoverImageAssetId);
        Assert.Empty(Assert.IsType<List<ProductMediaUsage>>(_store.LastMediaUsages));
    }

    /// <summary>
    /// A save that does not mention media still has to drop the gallery rows of a colourway it removed:
    /// <c>ProductMedia.ProductColourId</c> is a real foreign key with <c>NO ACTION</c>, so leaving them
    /// would make the save fail at the database with a constraint name and no field.
    /// </summary>
    [Fact]
    public async Task Removing_a_colour_takes_its_own_gallery_rows_with_it_even_when_media_is_not_sent()
    {
        var existing = await SeedWithMedia();
        var colourScoped = existing.Media.Count(media => media.ProductColour is not null);
        Assert.True(colourScoped > 0, "the fixture is meant to have a colour-scoped gallery row");

        var form = FormFrom(existing);

        var result = await Update(existing.Id, form with
        {
            Media = null,
            Colours = form.Colours!.Where(colour => colour.Name != "Jet Black").ToList(),
            Variants = form.Variants!.Where(variant => variant.ColourName != "Jet Black").ToList(),
        });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var after = _store.Products[existing.Id];
        Assert.Single(after.Colours);
        Assert.DoesNotContain(after.Media, media => media.ProductColour?.Name == "Jet Black");
        Assert.NotEmpty(after.Media);
    }

    // ---- the slug decision ----------------------------------------------------------------------

    [Fact]
    public async Task An_unpublished_products_slug_changes_without_a_confirmation()
    {
        var existing = await Seed();

        var result = await Update(existing.Id, FormFrom(existing) with { Slug = "renamed-jeans" });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal("renamed-jeans", _store.Products[existing.Id].Slug);
    }

    [Fact]
    public async Task A_published_products_slug_needs_a_confirmation_and_names_both_slugs()
    {
        var existing = await SeedWithMedia();
        existing.Publish(Now.UtcDateTime, 30);

        var result = await Update(existing.Id, FormFrom(existing) with { Slug = "renamed-jeans" });

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.SlugChangeNeedsConfirmation, conflict.Code);
        Assert.Equal("wide-leg-rinse-jeans", conflict.Details!["currentSlug"]);
        Assert.Equal("renamed-jeans", conflict.Details["requestedSlug"]);

        // Nothing was written.
        Assert.Equal("wide-leg-rinse-jeans", _store.Products[existing.Id].Slug);
    }

    [Fact]
    public async Task A_confirmed_slug_change_on_a_published_product_goes_through()
    {
        var existing = await SeedWithMedia();
        existing.Publish(Now.UtcDateTime, 30);

        var result = await Update(
            existing.Id, FormFrom(existing) with { Slug = "renamed-jeans" }, confirmSlugChange: true);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal("renamed-jeans", _store.Products[existing.Id].Slug);
    }

    /// <summary>
    /// A rename is not a slug change. A product that has been published has links to its slug, and an
    /// admin fixing a typo in the name is not asking to break them.
    /// </summary>
    [Fact]
    public async Task Renaming_a_product_leaves_its_slug_alone()
    {
        var existing = await SeedWithMedia();
        existing.Publish(Now.UtcDateTime, 30);

        var result = await Update(existing.Id, FormFrom(existing) with { Name = "Wide-Leg Rinse Jean" });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal("wide-leg-rinse-jeans", _store.Products[existing.Id].Slug);
    }

    // ---- variants with orders -------------------------------------------------------------------

    /// <summary>
    /// Doc 11 P1 step 10, for a size added to a product that already exists. It is carried on the variant's
    /// own navigation rather than inserted afterwards, so it is part of whichever save created the variant
    /// — the create path had it and the edit path did not, which left a new size permanently out of stock.
    /// </summary>
    [Fact]
    public async Task A_size_added_to_an_existing_product_carries_a_stock_row_at_zero()
    {
        var existing = await Seed();
        var form = FormFrom(existing);

        var result = await Update(existing.Id, form with
        {
            Variants = [..form.Variants!, Variant("Indigo Rinse", 63, "SKU-EXTRA")],
        });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var added = _store.Products[existing.Id].Variants.Single(variant => variant.Sku == "SKU-EXTRA");
        Assert.NotNull(added.StockLevel);
        Assert.Equal(0, added.StockLevel!.OnHand);
        Assert.Equal(0, added.StockLevel.Reserved);
    }

    [Fact]
    public async Task A_variant_an_order_names_cannot_be_deleted_and_the_refusal_names_the_sku()
    {
        var existing = await Seed();
        var ordered = existing.Variants.First();
        _store.VariantIdsWithOrders.Add(ordered.Id);

        var form = FormFrom(existing);
        var result = await Update(existing.Id, form with
        {
            Variants = form.Variants!.Where(variant => variant.Id != ordered.Id).ToList(),
        });

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.VariantNotDeletable, conflict.Code);
        Assert.Contains(ordered.Sku, (IEnumerable<string>)conflict.Details!["orderedSkus"]!);
        Assert.Empty((IEnumerable<string>)conflict.Details["stockedSkus"]!);

        // Nothing was written: the variant is still there, and nothing was handed to the store to delete.
        Assert.Contains(_store.Products[existing.Id].Variants, variant => variant.Id == ordered.Id);
        Assert.Empty(_store.RemovedVariants);
    }

    /// <summary>
    /// The second reason a variant cannot be deleted, and the one a real database insists on:
    /// <c>FK_StockLevels_ProductVariants_VariantId</c> is <c>NO ACTION</c>, and deleting the row would take
    /// a count of real garments with it. Stock belongs to ADM-12's ledger, not to this form.
    /// </summary>
    [Fact]
    public async Task A_variant_that_holds_stock_cannot_be_deleted_either()
    {
        var existing = await Seed();
        var stocked = existing.Variants.Last();
        _store.VariantIdsHoldingStock.Add(stocked.Id);

        var form = FormFrom(existing);
        var result = await Update(existing.Id, form with
        {
            Variants = form.Variants!.Where(variant => variant.Id != stocked.Id).ToList(),
        });

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.VariantNotDeletable, conflict.Code);
        Assert.Contains(stocked.Sku, (IEnumerable<string>)conflict.Details!["stockedSkus"]!);
        Assert.Contains("Inventory", conflict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A variant with nothing against it goes, and goes <b>through the store</b> — the collection removal
    /// alone is a 500 against real EF, because the relationship is <c>NO ACTION</c> rather than a cascade.
    /// </summary>
    [Fact]
    public async Task A_variant_with_no_orders_and_no_stock_is_handed_to_the_store_to_delete()
    {
        var existing = await Seed();
        var gone = existing.Variants.Last();

        var form = FormFrom(existing);
        var result = await Update(existing.Id, form with
        {
            Variants = form.Variants!.Where(variant => variant.Id != gone.Id).ToList(),
        });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.DoesNotContain(_store.Products[existing.Id].Variants, variant => variant.Id == gone.Id);
        Assert.Equal([gone.Id], _store.RemovedVariants.Select(variant => variant.Id));
    }

    [Fact]
    public async Task An_ordered_variant_can_be_switched_off_instead()
    {
        var existing = await Seed();
        var ordered = existing.Variants.First();
        _store.VariantIdsWithOrders.Add(ordered.Id);

        var form = FormFrom(existing);
        var result = await Update(existing.Id, form with
        {
            Variants = form.Variants!
                .Select(variant => variant.Id == ordered.Id ? variant with { IsActive = false } : variant)
                .ToList(),
        });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.False(_store.Products[existing.Id].Variants.Single(v => v.Id == ordered.Id).IsActive);
    }

    /// <summary>
    /// A colourway is matched by its id when the form carries one, so a rename reaches the right row rather
    /// than being read as "remove Indigo, add Indigo Rinse" — which would take its variants with it, and
    /// be refused outright once one of them had been ordered.
    /// </summary>
    [Fact]
    public async Task Renaming_a_colour_keeps_its_variants()
    {
        var existing = await Seed();
        var colour = existing.Colours.First();
        var variantIds = existing.Variants.Select(variant => variant.Id).Order().ToList();

        var form = FormFrom(existing);
        var result = await Update(existing.Id, form with
        {
            Colours = [form.Colours!.Single() with { Name = "Indigo Rinse Deep" }],
            Variants = form.Variants!.Select(variant => variant with { ColourName = "Indigo Rinse Deep" }).ToList(),
        });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var after = _store.Products[existing.Id];
        Assert.Equal(colour.Id, after.Colours.Single().Id);
        Assert.Equal("Indigo Rinse Deep", after.Colours.Single().Name);
        Assert.Equal(variantIds, after.Variants.Select(variant => variant.Id).Order());
    }

    // ---- concurrency and no-ops -----------------------------------------------------------------

    [Fact]
    public async Task An_edit_without_the_rows_current_version_is_refused()
    {
        var existing = await Seed();

        var result = await Update(existing.Id, FormFrom(existing), rowVersion: "AAAAAAAAAAA=");

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.StaleRowVersion, conflict.Code);
    }

    [Fact]
    public async Task An_edit_with_no_version_at_all_is_refused_rather_than_taken_as_no_opinion()
    {
        var existing = await Seed();

        var result = await Update(existing.Id, FormFrom(existing), rowVersion: null);

        Assert.Equal(
            CatalogConflictCodes.StaleRowVersion,
            Assert.IsType<CatalogWriteResult.Conflict>(result).Code);
    }

    [Fact]
    public async Task Saving_an_untouched_form_writes_nothing_and_logs_nothing()
    {
        var existing = await SeedWithMedia();
        _audit.Enlisted.Clear();
        var savesBefore = _store.Saves;

        var result = await Update(existing.Id, FormFrom(existing));

        Assert.IsType<CatalogWriteResult.Unchanged>(result);
        Assert.Empty(_audit.Enlisted);
        Assert.Equal(savesBefore, _store.Saves);
    }

    [Fact]
    public async Task An_edit_that_moves_a_price_is_audited_with_the_before_and_after()
    {
        var existing = await Seed();
        _audit.Enlisted.Clear();

        var result = await Update(existing.Id, FormFrom(existing) with { BasePrice = 4990m });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var entry = Assert.Single(_audit.Enlisted);
        Assert.Equal(AuditActions.Update, entry.Action);
        Assert.Contains("5490", entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("4990", entry.AfterJson!, StringComparison.Ordinal);

        // Changed properties only (doc 08 §3.8): the name did not move, so it is not in the row.
        Assert.DoesNotContain("\"name\"", entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_refreshes_the_price_summary_and_the_search_text()
    {
        var existing = await Seed();

        var result = await Update(existing.Id, FormFrom(existing) with { BasePrice = 4990m, CompareAtPrice = 6990m });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        var after = _store.Products[existing.Id];
        Assert.Equal(4990m, after.Stats!.MinListPrice);
        Assert.Equal(6990m, after.Stats.MaxOriginalPrice);
        Assert.True(after.Stats.HasDiscount);

        // Doc 08 §3.2: the search text carries the category path names, the colour and its family, the
        // attributes, the code and the SKUs.
        Assert.Contains("clothing jeans", after.SearchIndex!.SearchText, StringComparison.Ordinal);
        Assert.Contains("indigo rinse", after.SearchIndex.SearchText, StringComparison.Ordinal);
        Assert.Contains("blue", after.SearchIndex.SearchText, StringComparison.Ordinal);
        Assert.Contains("sku-s", after.SearchIndex.SearchText, StringComparison.Ordinal);
    }

    // ---- publish and remove ---------------------------------------------------------------------

    [Fact]
    public async Task Publishing_a_product_with_no_active_variant_is_refused_with_the_reason()
    {
        var existing = await SeedWithMedia();

        foreach (var variant in existing.Variants)
        {
            variant.IsActive = false;
        }

        var result = await SetPublished(existing.Id, true);

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.PublishChecklistFailed, conflict.Code);
        Assert.Contains(
            "switched on",
            Assert.IsType<List<string>>(conflict.Details!["blockers"]).Single(),
            StringComparison.OrdinalIgnoreCase);

        Assert.False(_store.Products[existing.Id].IsPublished);
    }

    [Fact]
    public async Task Publishing_a_product_with_no_photograph_is_refused()
    {
        var existing = await Seed();

        var result = await SetPublished(existing.Id, true);

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.PublishChecklistFailed, conflict.Code);
        Assert.Contains("gallery photograph", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_a_product_whose_photograph_has_no_alt_text_anywhere_is_refused()
    {
        var existing = await SeedWithMedia();

        foreach (var media in existing.Media)
        {
            media.AltTextOverride = null;
            if (media.MediaAsset is not null)
            {
                media.MediaAsset.AltText = null;
            }
        }

        var result = await SetPublished(existing.Id, true);

        Assert.Equal(
            CatalogConflictCodes.PublishChecklistFailed,
            Assert.IsType<CatalogWriteResult.Conflict>(result).Code);
    }

    /// <summary>
    /// The checklist accepts the library asset's own alt text. Insisting on an override would refuse to
    /// publish a product whose library alt text was already right.
    /// </summary>
    [Fact]
    public async Task The_library_assets_own_alt_text_satisfies_the_publish_checklist()
    {
        var existing = await SeedWithMedia();

        foreach (var media in existing.Media)
        {
            media.AltTextOverride = null;
        }

        var result = await SetPublished(existing.Id, true);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task Publishing_sets_first_published_once_and_defaults_the_new_window()
    {
        var existing = await SeedWithMedia();

        Assert.IsType<CatalogWriteResult.Succeeded>(await SetPublished(existing.Id, true));

        var published = _store.Products[existing.Id];
        Assert.True(published.IsPublished);
        Assert.Equal(Now.UtcDateTime, published.FirstPublishedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddDays(30), published.NewUntilUtc);

        var first = published.FirstPublishedAtUtc;

        Assert.IsType<CatalogWriteResult.Succeeded>(await SetPublished(existing.Id, false));
        Assert.False(_store.Products[existing.Id].IsPublished);

        // Unpublishing leaves the history and the New window alone: a product hidden for an afternoon must
        // not get a fresh thirty days of the New badge.
        Assert.Equal(first, _store.Products[existing.Id].FirstPublishedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddDays(30), _store.Products[existing.Id].NewUntilUtc);

        Assert.IsType<CatalogWriteResult.Succeeded>(await SetPublished(existing.Id, true));
        Assert.Equal(first, _store.Products[existing.Id].FirstPublishedAtUtc);
    }

    [Fact]
    public async Task Publishing_a_product_that_is_already_published_changes_nothing()
    {
        var existing = await SeedWithMedia();
        Assert.IsType<CatalogWriteResult.Succeeded>(await SetPublished(existing.Id, true));
        _audit.Enlisted.Clear();

        var result = await SetPublished(existing.Id, true);

        Assert.IsType<CatalogWriteResult.Unchanged>(result);
        Assert.Empty(_audit.Enlisted);
    }

    [Fact]
    public async Task Unpublishing_never_runs_the_checklist()
    {
        var existing = await SeedWithMedia();
        Assert.IsType<CatalogWriteResult.Succeeded>(await SetPublished(existing.Id, true));

        // Take the photographs away, which is a state the checklist refuses to publish.
        existing.Media.Clear();

        Assert.IsType<CatalogWriteResult.Succeeded>(await SetPublished(existing.Id, false));
    }

    [Fact]
    public async Task Removing_a_product_soft_deletes_it_and_keeps_its_variants()
    {
        var existing = await Seed();
        var variantCount = existing.Variants.Count;

        var result = await Remove(existing.Id);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.True(existing.IsDeleted);
        Assert.Equal(variantCount, existing.Variants.Count);
        Assert.DoesNotContain(existing.Id, _store.Products.Keys);

        var entry = _audit.Enlisted.Last();
        Assert.Equal(AuditActions.SoftDelete, entry.Action);
    }

    [Fact]
    public async Task Every_write_refuses_a_product_that_is_not_there()
    {
        Assert.IsType<CatalogWriteResult.NotFound>(await Update(9999, Form()));
        Assert.IsType<CatalogWriteResult.NotFound>(await SetPublished(9999, true));
        Assert.IsType<CatalogWriteResult.NotFound>(await Remove(9999));
    }

    [Fact]
    public async Task A_lost_concurrency_race_at_the_database_is_reported_as_a_conflict()
    {
        var existing = await Seed();
        _store.NextSaveOutcome = CatalogSaveOutcome.ConcurrencyConflict;

        var result = await Update(existing.Id, FormFrom(existing) with { BasePrice = 4990m });

        Assert.Equal(
            CatalogConflictCodes.StaleRowVersion,
            Assert.IsType<CatalogWriteResult.Conflict>(result).Code);
    }

    [Fact]
    public async Task A_unique_index_firing_after_the_check_is_reported_as_a_conflict()
    {
        var existing = await Seed();
        _store.NextSaveOutcome = CatalogSaveOutcome.DuplicateKey;

        var result = await Update(existing.Id, FormFrom(existing) with { BasePrice = 4990m });

        Assert.Equal(
            CatalogConflictCodes.DuplicateKey,
            Assert.IsType<CatalogWriteResult.Conflict>(result).Code);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private Task<CatalogWriteResult> Create(ProductFormInput form) =>
        new CreateProductCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(new CreateProductCommand(form, Actor), TestContext.Current.CancellationToken);

    private Task<CatalogWriteResult> Update(
        int productId,
        ProductFormInput form,
        bool confirmSlugChange = false,
        string? rowVersion = "current")
    {
        var token = rowVersion == "current"
            ? FakeProductWriteStore.Token(
                _store.Products.TryGetValue(productId, out var product)
                    ? product.RowVersion
                    : BitConverter.GetBytes((long)productId))
            : rowVersion;

        return new UpdateProductCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(
                new UpdateProductCommand(productId, form, confirmSlugChange, token, Actor),
                TestContext.Current.CancellationToken);
    }

    private Task<CatalogWriteResult> SetPublished(int productId, bool isPublished)
    {
        var token = FakeProductWriteStore.Token(
            _store.Products.TryGetValue(productId, out var product)
                ? product.RowVersion
                : BitConverter.GetBytes((long)productId));

        return new SetProductPublishedCommandHandler(_store, _audit, new FixedClock(Now), _options)
            .HandleAsync(
                new SetProductPublishedCommand(productId, isPublished, token, Actor),
                TestContext.Current.CancellationToken);
    }

    private Task<CatalogWriteResult> Remove(int productId)
    {
        var token = FakeProductWriteStore.Token(
            _store.Products.TryGetValue(productId, out var product)
                ? product.RowVersion
                : BitConverter.GetBytes((long)productId));

        return new RemoveProductCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(
                new RemoveProductCommand(productId, token, Actor), TestContext.Current.CancellationToken);
    }

    /// <summary>A plain product with one colour and two sizes and no media.</summary>
    private async Task<Product> Seed()
    {
        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51)],
            variants: [Variant("Indigo Rinse", 61, "SKU-S"), Variant("Indigo Rinse", 62, "SKU-M")]));

        _audit.Enlisted.Clear();
        return _store.Products[Assert.IsType<CatalogWriteResult.Succeeded>(result).EntityId];
    }

    /// <summary>
    /// A product with <b>real media attached</b>: two whole-product gallery images, a colour-scoped gallery
    /// image, a hover image and a video. This is the fixture the W1-12 image-clearing defect would have
    /// needed — see
    /// <see cref="A_save_that_does_not_mention_media_keeps_every_photograph_and_the_video"/>.
    /// </summary>
    private async Task<Product> SeedWithMedia()
    {
        var first = _store.AddImageAsset("Front view");
        var second = _store.AddImageAsset("Side view");
        var blackOnly = _store.AddImageAsset("Jet Black, front");
        var hover = _store.AddImageAsset("Back view");
        var video = _store.AddVideoAsset();

        var result = await Create(Form(
            colours: [Colour("Indigo Rinse", 51), Colour("Jet Black", 52, sortOrder: 1)],
            variants:
            [
                Variant("Indigo Rinse", 61, "SKU-IR-S"),
                Variant("Indigo Rinse", 62, "SKU-IR-M"),
                Variant("Jet Black", 61, "SKU-JB-S"),
            ],
            media:
            [
                new ProductMediaInput(first, null, ProductMediaRole.Gallery, 0, "Front view"),
                new ProductMediaInput(second, null, ProductMediaRole.Gallery, 1, "Side view"),
                new ProductMediaInput(blackOnly, "Jet Black", ProductMediaRole.Gallery, 0, "Jet Black, front"),
                new ProductMediaInput(hover, null, ProductMediaRole.Hover, 0, "Back view"),
                new ProductMediaInput(video, null, ProductMediaRole.Video, 0, null),
            ]));

        _audit.Enlisted.Clear();

        var product = _store.Products[Assert.IsType<CatalogWriteResult.Succeeded>(result).EntityId];

        // The handler's loaded graph carries each media row's asset, because the publish checklist reads
        // the library's own alt text through it. The fake store hands back the same instance the create
        // built, so the navigation is wired here.
        foreach (var media in product.Media)
        {
            var reference = _store.MediaAssets.Values.Single(asset => asset.Id == media.MediaAssetId);
            media.MediaAsset = new MediaAsset
            {
                Id = reference.Id,
                PublicId = reference.PublicId,
                MediaType = reference.MediaType,
                Status = reference.Status,
                Visibility = reference.Visibility,
                AltText = reference.AltText,
            };
        }

        return product;
    }

    private static ProductFormInput Form(
        string? productCode = "SD-W-0001",
        string? name = "Wide-Leg Rinse Jeans",
        string? slug = "wide-leg-rinse-jeans",
        int departmentId = 1,
        int primaryCategoryId = 11,
        IReadOnlyList<int>? extraCategoryIds = null,
        int? ageGroupId = null,
        int? materialId = 41,
        decimal basePrice = 5490m,
        decimal? compareAtPrice = null,
        IReadOnlyList<ProductColourInput>? colours = null,
        IReadOnlyList<ProductVariantInput>? variants = null,
        IReadOnlyList<ProductMediaInput>? media = null) =>
        new(
            productCode,
            name,
            slug,
            departmentId,
            primaryCategoryId,
            extraCategoryIds,
            ageGroupId,
            "Everyday denim.",
            "<p>Everyday denim.</p>",
            "Machine wash cold.",
            materialId,
            42,
            43,
            basePrice,
            compareAtPrice,
            null,
            null,
            70,
            "Wide-Leg Rinse Jeans | Colombo Wardrobe",
            "Everyday denim, cut for Sri Lankan weather.",
            colours ?? [],
            variants ?? [],
            media,
            []);

    /// <summary>The form a screen would send back for a product it has just read.</summary>
    private static ProductFormInput FormFrom(Product product) => new(
        product.ProductCode,
        product.Name,
        product.Slug,
        product.DepartmentId,
        product.PrimaryCategoryId,
        product.CategoryLinks
            .Select(link => link.CategoryId)
            .Where(id => id != product.PrimaryCategoryId)
            .ToList(),
        product.AgeGroupId,
        product.ShortDescription,
        product.Description,
        product.CareInstructions,
        product.MaterialId,
        product.FitId,
        product.OccasionId,
        product.BasePrice,
        product.CompareAtPrice,
        product.BadgeOverride,
        product.RecommendedRank,
        product.SizeGuideId,
        product.SeoTitle,
        product.SeoDescription,
        product.Colours
            .OrderBy(colour => colour.SortOrder)
            .Select(colour => new ProductColourInput(
                colour.Id, colour.Name, colour.ColourFamilyId, colour.SwatchHex, null, colour.SortOrder))
            .ToList(),
        product.Variants
            .OrderBy(variant => variant.SortOrder)
            .Select(variant => new ProductVariantInput(
                variant.Id,
                product.Colours.Single(colour => colour.Id == variant.ProductColourId).Name,
                variant.SizeId,
                variant.Sku,
                variant.Barcode,
                variant.PriceOverride,
                variant.CompareAtPriceOverride,
                variant.IsActive,
                variant.SortOrder))
            .ToList(),

        // Null on purpose: a screen that is not editing the gallery sends no media field, and the product's
        // photographs must survive that. See the W1-12 tests above.
        null,
        product.RelatedProducts
            .Select(related => new RelatedProductInput(related.RelatedProductId, related.SortOrder))
            .ToList());

    private static ProductColourInput Colour(string name, int familyId, short sortOrder = 0) =>
        new(null, name, familyId, "#1a2b3c", null, sortOrder);

    private static ProductVariantInput Variant(string colourName, int sizeId, string sku) =>
        new(null, colourName, sizeId, sku, null, null, null, true, 0);
}
