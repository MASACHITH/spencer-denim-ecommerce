using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>
/// One colourway in a create or edit request (doc 11 P1 step 8).
/// </summary>
/// <param name="Name">
/// The marketing name ("Indigo Rinse"), and the <b>key</b> of the colourway inside the request: variants
/// and gallery rows name it rather than an id, because a colour added in the same body has no id yet.
/// <c>UX_ProductColours_Product_Name</c> already makes the name unique within a product, so this is the
/// natural key the database agrees with rather than one invented for the wire.
/// </param>
public sealed record ProductColourInput(
    int? Id,
    string? Name,
    int ColourFamilyId,
    string? SwatchHex,
    Guid? SwatchAssetPublicId,
    short SortOrder);

/// <summary>One variant in a create or edit request (doc 11 P1 step 9).</summary>
/// <param name="ColourName">Which <see cref="ProductColourInput.Name"/> this variant belongs to.</param>
public sealed record ProductVariantInput(
    int? Id,
    string? ColourName,
    int SizeId,
    string? Sku,
    string? Barcode,
    decimal? PriceOverride,
    decimal? CompareAtPriceOverride,
    bool IsActive,
    short SortOrder);

/// <summary>One image or video attached to a product (doc 11 P1 step 11).</summary>
/// <param name="ColourName">
/// The colourway whose gallery this belongs to, or <c>null</c> for every colourway. A video is always
/// whole-product: doc 08's gallery swap is per colour and a product has one video.
/// </param>
public sealed record ProductMediaInput(
    Guid AssetPublicId,
    string? ColourName,
    ProductMediaRole MediaRole,
    short SortOrder,
    string? AltTextOverride);

/// <summary>One related-product pick (PDP-06, doc 11 P1 step 12).</summary>
public sealed record RelatedProductInput(int RelatedProductId, short SortOrder);

/// <summary>
/// The fields a product form sends, shared by create and edit so the two cannot drift apart — a create
/// that accepts a value the edit refuses is a row an admin can make and then never save again.
/// </summary>
/// <param name="ExtraCategoryIds">
/// Categories besides the primary one. The primary category is linked automatically (doc 08 §3.2: "The
/// primary category is also stored here"), so a form cannot accidentally unlink a product from its own
/// category by leaving it out of a list.
/// </param>
/// <param name="Colours">
/// Every colourway the product should end up with. A colourway the request omits is <b>removed</b>, along
/// with its variants — which is refused when any of those variants has been ordered, because doc 08 §3.2
/// keeps an ordered variant for ever.
/// </param>
/// <param name="Variants">
/// Every variant the product should end up with, one per colour × size. The request is the whole matrix,
/// not a delta: the same argument <see cref="ReorderCategoriesCommand"/> makes for a reorder, and the
/// reason W1-12's staged reorder being silently discarded cannot happen here — there is no staging, the
/// matrix on screen is what is sent.
/// </param>
/// <param name="Media">
/// Every gallery image, hover image and video the product should end up with. <b>A request that omits this
/// field entirely leaves the product's media alone</b>; a request that sends an empty list clears it. See
/// <c>ProductWriteRules.ResolveMedia</c> for why those two are not the same thing — W1-12 shipped a save
/// that silently cleared every image because they were conflated.
/// </param>
public sealed record ProductFormInput(
    string? ProductCode,
    string? Name,
    string? Slug,
    int DepartmentId,
    int PrimaryCategoryId,
    IReadOnlyList<int>? ExtraCategoryIds,
    int? AgeGroupId,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    int? MaterialId,
    int? FitId,
    int? OccasionId,
    decimal BasePrice,
    decimal? CompareAtPrice,
    ProductBadge? BadgeOverride,
    int? RecommendedRank,
    int? SizeGuideId,
    string? SeoTitle,
    string? SeoDescription,
    IReadOnlyList<ProductColourInput>? Colours,
    IReadOnlyList<ProductVariantInput>? Variants,
    IReadOnlyList<ProductMediaInput>? Media,
    IReadOnlyList<RelatedProductInput>? RelatedProducts);

/// <summary>
/// Create a product (ADM-10, doc 11 P1 steps 6–13).
/// <para>
/// It is created <b>unpublished</b>, always. Doc 11 P1 step 13 says so and step 14 makes publishing its own
/// act, which is also the only way the publish checklist (§7 G-01) can mean anything: a create that could
/// publish in the same request would have to pass a checklist about media the admin has not attached yet.
/// </para>
/// </summary>
public sealed record CreateProductCommand(ProductFormInput Form, AdminActor Actor);

/// <summary>
/// Edit a product (ADM-10).
/// <para>
/// <b>The slug stays editable, and that is a decision rather than an omission.</b> A department's name and
/// slug are not editable (owner, 2026-10-02) because a department slug is the first segment of every
/// listing URL beneath it and redirects are deferred. A product slug is one page's URL, products are
/// corrected constantly in the first weeks of a season, and doc 11 P1's business rules state the answer
/// outright: "Slug change after first publish shows a 'this breaks existing links' confirmation; redirects
/// are not in v1". So: freely editable before first publish, and afterwards only with
/// <paramref name="ConfirmSlugChange"/> — a 409 naming the old and the new slug otherwise, which is what
/// lets the screen put the warning in front of the admin instead of the API silently breaking a link.
/// </para>
/// <para>
/// A rename does <b>not</b> touch the slug by itself. Nothing derives one from the other after the create,
/// because a product that has been published has links to its slug and an admin fixing a typo in the name
/// is not asking to break them.
/// </para>
/// </summary>
public sealed record UpdateProductCommand(
    int ProductId,
    ProductFormInput Form,
    bool ConfirmSlugChange,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Publish or unpublish a product (ADM-10's "publish/unpublish", doc 11 P1 steps 14–15).
/// <para>
/// Its own command rather than a field on the form, for the reason <see cref="SetCategoryActiveCommand"/>
/// gives: putting a product on sale or taking it off is the write with the largest visible effect on the
/// shop, so it is a deliberate act with its own <c>StatusChange</c> audit row and never a side effect of
/// saving a form whose checkbox was stale.
/// </para>
/// <para>
/// Publishing runs the checklist of doc 11 §7 G-01 — at least one active variant, a card image, and alt
/// text on every public image — and refuses with the unmet rules named. Unpublishing never refuses.
/// </para>
/// </summary>
public sealed record SetProductPublishedCommand(
    int ProductId,
    bool IsPublished,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Remove a product (ADM-10's "remove"): a soft delete (doc 08 §10.2, doc 11 P1 step 15).
/// <para>
/// Nothing is cascaded and nothing is refused. Doc 11 P1: "Soft delete keeps variants, colours, media
/// links and order lines; the product disappears from storefront queries". A product in a customer's bag
/// is handled where it is read — the bag shows the line as unavailable and checkout blocks it (P5) — so
/// refusing the removal here would leave an admin unable to take a product off sale because somebody left
/// it in a bag.
/// </para>
/// </summary>
public sealed record RemoveProductCommand(int ProductId, string? RowVersion, AdminActor Actor);
