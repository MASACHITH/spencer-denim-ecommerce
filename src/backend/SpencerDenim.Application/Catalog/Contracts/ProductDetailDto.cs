using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>A breadcrumb step (PLP-01, PDP-01).</summary>
public sealed record CategoryRefDto(int Id, string Name, string Slug);

/// <summary>The PDP-05 details block: material, fit, occasion and age group, resolved to names.</summary>
public sealed record ProductAttributesDto(
    string? Material,
    string? MaterialSlug,
    string? Fit,
    string? FitSlug,
    string? Occasion,
    string? OccasionSlug,
    string? AgeGroup,
    string? AgeGroupSlug);

/// <summary>
/// One gallery item (PDP-01). Videos carry a poster image.
/// <para>
/// The gallery draws the same asset in three places at three very different sizes, so it is given
/// three URLs rather than being left to guess: the rail, the stage and the full-screen viewer. Each of
/// the rendition fields falls back to <paramref name="Url"/> being the original when the asset has no
/// rendition set, so a gallery never has a slot with nothing in it.
/// </para>
/// </summary>
/// <param name="Url">The stage: the <c>large</c> rendition, or the original when there is none.</param>
/// <param name="SrcSet">
/// Candidates for the stage, up to <c>zoom</c> — a full-width stage on a retina tablet genuinely wants
/// more than <c>large</c>. Null when the asset has no renditions.
/// </param>
/// <param name="ThumbnailUrl">The rail: the <c>thumb</c> rendition, or the original when there is none.</param>
/// <param name="ZoomUrl">
/// The full-screen viewer: the largest rendition that exists — <c>zoom</c>, or <c>large</c> when the
/// source was too small for the two to differ — or the original when the asset has no renditions at
/// all. It is a separate field rather than the largest <paramref name="SrcSet"/> candidate because the
/// viewer is opened deliberately and magnifies past 1×, so it wants the biggest file unconditionally —
/// which is exactly the choice a <c>srcset</c> is designed to stop the browser making.
/// </param>
/// <param name="PosterUrl">
/// A video's poster frame. Always an original: a poster is a separate asset that the pipeline renders
/// no renditions for.
/// </param>
public sealed record ProductImageDto(
    int Id,
    string Url,
    string? SrcSet,
    string? ThumbnailUrl,
    string? ZoomUrl,
    string? AltText,
    ProductMediaRole Role,
    int? ColourId,
    int? Width,
    int? Height,
    string? PosterUrl,
    short SortOrder);

/// <summary>One size offered by a product, with the scale it belongs to (PDP-03, PLP-02).</summary>
public sealed record ProductSizeDto(int Id, string Label, string ScaleName, short SortOrder, bool IsInStock);

/// <summary>
/// One buyable colour × size combination (PDP-03). Price comes from the variant override where there
/// is one, so the page can re-price when the customer picks a size.
/// </summary>
public sealed record ProductVariantDto(
    int Id,
    string Sku,
    int ColourId,
    string ColourName,
    int SizeId,
    string SizeLabel,
    string ScaleName,
    PriceDto Price,
    StockStatus StockStatus,
    bool IsInStock,
    int AvailableQuantity);

/// <summary>
/// The measurement table of a size guide: <c>catalog.SizeGuides.TableJson</c> parsed into a grid
/// (doc 08 §1.4, DB15).
/// <para>
/// <b>Every cell is plain text, not HTML.</b> Unlike <see cref="SizeGuideContentDto.IntroHtml"/> and
/// <see cref="SizeGuideContentDto.MeasuringTipsHtml"/>, a cell is whatever string the stored JSON held
/// and nothing has sanitised it, so a client interpolates a cell and must never bind one to
/// <c>innerHTML</c>. There is no reason to want markup in a cell — a cell is "88-93" — so the shape
/// says so rather than leaving it to a convention.
/// </para>
/// <para>
/// <see cref="Rows"/> is rectangular by construction: <see cref="SizeGuideTable.Parse"/> pads a short
/// row and truncates a long one, so a renderer can count <see cref="Columns"/> once and trust it.
/// </para>
/// </summary>
public sealed record SizeGuideTableDto(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// What a shopper reads when the PDP-04 modal opens: <c>catalog.SizeGuides</c> projected column for
/// column.
/// <para>
/// It is <see langword="null"/> on <see cref="SizeGuideRefDto"/> when the guide holds nothing a
/// shopper could read — every field empty, or a <c>TableJson</c> that is <c>{}</c> or unparseable.
/// A guide with a name and no measurements is not worth a button, and the storefront's
/// <c>hasSizeGuideContent</c> is the single condition that decides it, so "nothing to show" has to be
/// sayable in one check rather than inferred from four empty fields. (It is serialised as
/// <c>"content": null</c>, a present key with a null value, which is what <c>hasSizeGuideContent</c>
/// tests.)
/// </para>
/// </summary>
/// <param name="IntroHtml">
/// <b>Rendered as markup by the client, and not sanitised by anything on the way out.</b> The
/// intention is the same contract <c>Products.Description</c> carries, but doc 08 §3.2 does not
/// actually mark this column sanitised — see the type remarks on <see cref="SizeGuideRefDto"/> for
/// what holds today and what is owed before ADM-11.
/// </param>
/// <param name="Table">The measurement grid, or <see langword="null"/> when there is no usable one.</param>
/// <param name="MeasuringTipsHtml">Markup, on exactly the same terms as <paramref name="IntroHtml"/>.</param>
/// <param name="ImageUrl">
/// A measuring diagram, or <see langword="null"/>. Only ever set for an asset that is
/// <c>Ready</c> — a row still processing, or one whose processing failed, has no file behind it, so
/// linking it would put a 404 where a diagram should have been.
/// </param>
/// <param name="ImageAltText">
/// The diagram's alt text, from <c>media.MediaAssets.AltText</c>, or <see langword="null"/> when the
/// asset has none.
/// <para>
/// <b>Added to the shape the storefront wrote down</b>, which had <c>imageUrl</c> and nothing to
/// describe it. A measuring diagram is informative, not decorative, so a client given only a URL has
/// to choose between <c>alt=""</c> — which hides a measurement chart from a screen reader
/// outright — and inventing a sentence about a picture it cannot see. Neither is acceptable, and the
/// fix was one more projected column. <b>ADM-11 should require alt text when a diagram is
/// attached</b>; until then the client falls back to naming the guide, which is honest chrome rather
/// than a description of the image.
/// </para>
/// </param>
public sealed record SizeGuideContentDto(
    string? IntroHtml,
    SizeGuideTableDto? Table,
    string? MeasuringTipsHtml,
    string? ImageUrl,
    string? ImageAltText);

/// <summary>
/// The size guide the product page opens (PDP-04), inherited from the primary category when the
/// product sets none of its own.
/// <para>
/// <b>Why the content travels with the product rather than from a route of its own.</b> The guide is
/// the same handful of rows for every product in a category and is not read until someone opens the
/// modal, which argues for <c>GET /api/catalog/size-guides/{id}</c> — but the control that would do
/// the opening does not exist until the content has arrived: the storefront hides the trigger unless
/// the guide already has something to show, deliberately, because a button that opens an empty modal
/// is worse than no button. Fetching on open can therefore never fire, and the only lazy shape that
/// would work is a speculative request on every product page — one extra round trip for every
/// visitor, to save about 600 bytes from a payload that is already tens of kilobytes. Embedded, it
/// costs no extra query (it is projected in the same product query), renders into the server-side
/// HTML, and is cached and invalidated by whatever caches the product payload.
/// </para>
/// <para>
/// <b>What makes <paramref name="IntroHtml"/> and <paramref name="MeasuringTipsHtml"/> safe.</b> Four
/// facts, kept short because this paragraph has now been wrong three times and every correction was a
/// claim that read well and did not hold.
/// </para>
/// <list type="number">
/// <item>The only writer is <c>CatalogReferenceData</c>. There is no admin write path yet (ADM-11).</item>
/// <item>
/// Nothing sanitises these fields on the way in. There is no HTML sanitiser in this backend at all.
/// </item>
/// <item>
/// Doc 08 §3.2 does <b>not</b> mark these columns sanitised — the Notes are blank for both, and
/// "Sanitised HTML" appears against <c>Products.Description</c> only.
/// </item>
/// <item>
/// The storefront binds both through <c>[innerHTML]</c> (<c>SizeGuideModal</c>), so Angular's
/// <c>SecurityContext.HTML</c> strips scripts, event handlers and <c>javascript:</c> URLs. That is a
/// real defence for that client and second-line by nature: it says nothing about an admin preview, a
/// native client or an export.
/// </item>
/// </list>
/// <para>
/// So the safety rests on (1). The moment an admin can type into these columns it stops holding, and
/// there is nothing behind it. Inbound sanitisation is owed before ADM-11 ships, with doc 08 §3.2
/// updated to match: <b>issue #70</b>.
/// </para>
/// </summary>
/// <param name="Content">
/// The measurements, or <see langword="null"/> when the guide has none — see
/// <see cref="SizeGuideContentDto"/>.
/// </param>
public sealed record SizeGuideRefDto(int Id, string Name, SizeGuideContentDto? Content);

/// <summary>Meta tags for server-side rendering (GLB-11).</summary>
public sealed record SeoDto(string Title, string? Description);

/// <summary>
/// The whole product page payload (PDP-01..06) in one response: gallery, colours, sizes, variants with
/// stock, price and discount, attributes and related products.
/// </summary>
public sealed record ProductDetailDto(
    int Id,
    string ProductCode,
    string Name,
    string Slug,
    CategoryRefDto Department,
    IReadOnlyList<CategoryRefDto> Breadcrumb,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    ProductAttributesDto Attributes,
    PriceDto Price,
    decimal MinPrice,
    decimal MaxPrice,
    RatingDto Rating,
    ProductBadge? Badge,
    StockStatus StockStatus,
    bool IsInStock,
    SeoDto Seo,
    SizeGuideRefDto? SizeGuide,
    DateTime? NewUntilUtc,
    IReadOnlyList<ProductImageDto> Images,
    IReadOnlyList<ColourSwatchDto> Colours,
    IReadOnlyList<ProductSizeDto> Sizes,
    IReadOnlyList<ProductVariantDto> Variants,
    IReadOnlyList<ProductCardDto> RelatedProducts);
