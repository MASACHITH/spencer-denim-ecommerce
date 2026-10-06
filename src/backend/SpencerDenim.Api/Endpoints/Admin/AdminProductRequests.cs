using SpencerDenim.Application.Catalog.Admin.Products;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The JSON bodies the ADM-10 product endpoints accept.
/// <para>
/// <b>Every field is nullable, including the booleans and the numbers</b>, for the reason
/// <c>AdminCatalogRequests</c> states: a non-nullable <c>int</c> binds a missing field to 0, which reads as
/// "the admin chose zero" and cannot be told apart from a client that forgot to send it; and an
/// out-of-range value on a <c>byte</c> or an <c>enum</c> property is a <c>JsonException</c>, which would
/// come back as "malformed JSON" when what is wrong is one box an admin filled in. Nullable everywhere
/// means the use case decides and every refusal names its field.
/// </para>
/// <para>
/// <b>The three collections are nullable for a second, sharper reason.</b> A <c>null</c> collection means
/// "this request says nothing about it"; an empty list means "make it empty". W1-12 conflated those and
/// every save silently cleared a block's images.
/// <para>
/// <b>That claim was true of <c>media</c> only, and the gap was a second way to lose work silently.</b>
/// Omitting <c>extraCategoryIds</c> unlinked a product from every extra category; omitting
/// <c>variants</c> deleted every size whose stock and order history allowed it. Both answered 204. The
/// rule now holds for all five collections — <c>extraCategoryIds</c>, <c>colours</c>, <c>variants</c>,
/// <c>media</c> and <c>relatedProducts</c> — so a client that has not been updated to send one cannot
/// destroy it, and a client that means to clear one still can by sending <c>[]</c>.
/// </para>
/// </para>
/// </summary>
public sealed record ProductColourRequest(
    int? Id,
    string? Name,
    int? ColourFamilyId,
    string? SwatchHex,
    Guid? SwatchAssetPublicId,
    int? SortOrder);

/// <inheritdoc cref="ProductColourRequest"/>
public sealed record ProductVariantRequest(
    int? Id,
    string? ColourName,
    int? SizeId,
    string? Sku,
    string? Barcode,
    decimal? PriceOverride,
    decimal? CompareAtPriceOverride,
    bool? IsActive,
    int? SortOrder);

/// <inheritdoc cref="ProductColourRequest"/>
/// <param name="MediaRole">
/// <c>Gallery</c>, <c>Hover</c> or <c>Video</c> — the <b>name</b>, which is what the API emits.
/// <para>
/// <b>A name rather than a number, and this is the decision that makes the payload round-trip.</b>
/// <c>Program.cs</c> registers a global <c>JsonStringEnumConverter</c> with its reason written down:
/// "Enums travel as their names … so a client never has to keep a copy of our numeric values in sync."
/// Every enum in a <i>response</i> therefore serialises as a string — including
/// <c>AdminProductMediaDto.MediaRole</c>. This field was declared <c>int?</c>, so the read model could not
/// be handed back to the write model: a screen that had loaded a saved product and then nudged a
/// photograph sent <c>"mediaRole": "Gallery"</c> and got a raw 400 about a JSON value that could not be
/// converted. Taking the name here is the half of the fix that belongs to the API; the other half is that
/// the client stops inventing numbers.
/// </para>
/// <para>
/// <see cref="ProductRequestMapper"/> parses it with an explicit allow-list rather than
/// <see cref="Enum.IsDefined(Type, object)"/>, so an unknown name is a named field error and never a 500.
/// </para>
/// </param>
public sealed record ProductMediaRequest(
    Guid? AssetPublicId,
    string? ColourName,
    string? MediaRole,
    int? SortOrder,
    string? AltTextOverride);

/// <inheritdoc cref="ProductColourRequest"/>
public sealed record RelatedProductRequest(int? RelatedProductId, int? SortOrder);

/// <summary>
/// The product form's body, shared by <c>POST /products</c> and <c>PUT /products/{id}</c>.
/// </summary>
/// <param name="ConfirmSlugChange">
/// Required to change the slug of a product that has been published at least once (doc 11 P1's "Slug
/// change after first publish shows a 'this breaks existing links' confirmation"). Ignored on a create and
/// on a product that has never been published.
/// </param>
/// <param name="BadgeOverride">
/// <c>New</c>, <c>Sale</c>, <c>OnlineExclusive</c> or <c>BestSeller</c> — the <b>name</b>, for the reason
/// <see cref="ProductMediaRequest.MediaRole"/> gives. <c>LowStock</c> is refused: it is worked out at read
/// time and never stored (doc 08 §3.2).
/// </param>
/// <param name="RowVersion">
/// Base64 of <c>catalog.Products.RowVersion</c>, from the admin read model. May be sent in the
/// <c>If-Match</c> header instead; see <c>AdminWriteRequest.RowVersion</c>.
/// </param>
public sealed record ProductRequestBody(
    string? ProductCode,
    string? Name,
    string? Slug,
    int? DepartmentId,
    int? PrimaryCategoryId,
    IReadOnlyList<int>? ExtraCategoryIds,
    int? AgeGroupId,
    string? ShortDescription,
    string? Description,
    string? CareInstructions,
    int? MaterialId,
    int? FitId,
    int? OccasionId,
    decimal? BasePrice,
    decimal? CompareAtPrice,
    string? BadgeOverride,
    int? RecommendedRank,
    int? SizeGuideId,
    string? SeoTitle,
    string? SeoDescription,
    IReadOnlyList<ProductColourRequest>? Colours,
    IReadOnlyList<ProductVariantRequest>? Variants,
    IReadOnlyList<ProductMediaRequest>? Media,
    IReadOnlyList<RelatedProductRequest>? RelatedProducts,
    bool? ConfirmSlugChange,
    string? RowVersion);

/// <summary>Publish or unpublish a product (ADM-10, doc 11 P1 steps 14–15).</summary>
public sealed record SetProductPublishedRequest(bool? IsPublished, string? RowVersion);

/// <summary>The id of a product that has just been created, so the screen can open it without a reload.</summary>
public sealed record CreatedProductResponse(int Id);

/// <summary>
/// Turns a request body into the use case's input, refusing the handful of values that cannot be expressed
/// as a field error later because they would not reach a field at all.
/// </summary>
internal static class ProductRequestMapper
{
    /// <summary>
    /// The three ids a form cannot do without, and the badge, which is an enum rather than a free number.
    /// </summary>
    /// <returns>
    /// The mapped form, or the field name and message to refuse with. A tuple rather than an exception,
    /// because every one of these is ordinary input from a screen that may be a build behind.
    /// </returns>
    public static (ProductFormInput? Form, string? Field, string? Message) Map(ProductRequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.DepartmentId is null)
        {
            return (null, "departmentId", "Choose a department.");
        }

        if (body.PrimaryCategoryId is null)
        {
            return (null, "primaryCategoryId", "Choose the category this product belongs in.");
        }

        if (body.BasePrice is null)
        {
            return (null, "basePrice", "Enter the price.");
        }

        ProductBadge? badge = null;
        if (CleanEnum(body.BadgeOverride) is { } badgeName)
        {
            if (!TryReadEnum(badgeName, AssignableBadges, out var parsedBadge))
            {
                return (
                    null,
                    "badgeOverride",
                    "A badge is New, Sale, OnlineExclusive or BestSeller. LowStock is worked out from stock "
                    + "when the card is shown, so it cannot be set here.");
            }

            badge = parsedBadge;
        }

        List<ProductColourInput>? colours = null;
        foreach (var colour in body.Colours ?? [])
        {
            colours ??= new List<ProductColourInput>(body.Colours!.Count);

            if (colour.ColourFamilyId is null)
            {
                return (
                    null,
                    "colours",
                    $"The colour '{colour.Name ?? "(unnamed)"}' has no colour family. Choose the family its "
                    + "filter groups under.");
            }

            colours.Add(new ProductColourInput(
                colour.Id,
                colour.Name,
                colour.ColourFamilyId.Value,
                colour.SwatchHex,
                colour.SwatchAssetPublicId,
                ToSortOrder(colour.SortOrder)));
        }

        List<ProductVariantInput>? variants = null;
        foreach (var variant in body.Variants ?? [])
        {
            variants ??= new List<ProductVariantInput>(body.Variants!.Count);

            if (variant.SizeId is null)
            {
                return (null, "variants", "Every size row has to say which size it is.");
            }

            variants.Add(new ProductVariantInput(
                variant.Id,
                variant.ColourName,
                variant.SizeId.Value,
                variant.Sku,
                variant.Barcode,
                variant.PriceOverride,
                variant.CompareAtPriceOverride,

                // A row that does not say is on: a staff member adding a size means to sell it. Switching
                // one off is the deliberate act (doc 08 §3.2's "deactivate once ordered, never delete").
                variant.IsActive ?? true,
                ToSortOrder(variant.SortOrder)));
        }

        List<ProductMediaInput>? media = null;
        if (body.Media is not null)
        {
            media = new List<ProductMediaInput>(body.Media.Count);

            foreach (var row in body.Media)
            {
                if (row.AssetPublicId is null)
                {
                    return (null, "media", "Every photograph or video row has to name a media asset.");
                }

                if (!TryReadEnum(CleanEnum(row.MediaRole), MediaRoles, out var role))
                {
                    return (
                        null,
                        "media",
                        "A media row is Gallery (a gallery photograph), Hover (the hover photograph) or "
                        + "Video (the product video).");
                }

                media.Add(new ProductMediaInput(
                    row.AssetPublicId.Value,
                    row.ColourName,
                    role,
                    ToSortOrder(row.SortOrder),
                    row.AltTextOverride));
            }
        }

        List<RelatedProductInput>? related = null;
        foreach (var pick in body.RelatedProducts ?? [])
        {
            related ??= new List<RelatedProductInput>(body.RelatedProducts!.Count);

            if (pick.RelatedProductId is null)
            {
                return (null, "relatedProducts", "Every related row has to name a product.");
            }

            related.Add(new RelatedProductInput(pick.RelatedProductId.Value, ToSortOrder(pick.SortOrder)));
        }

        return (
            new ProductFormInput(
                body.ProductCode,
                body.Name,
                body.Slug,
                body.DepartmentId.Value,
                body.PrimaryCategoryId.Value,
                body.ExtraCategoryIds,
                body.AgeGroupId,
                body.ShortDescription,
                body.Description,
                body.CareInstructions,
                body.MaterialId,
                body.FitId,
                body.OccasionId,
                body.BasePrice.Value,
                body.CompareAtPrice,
                badge,
                body.RecommendedRank,
                body.SizeGuideId,
                body.SeoTitle,
                body.SeoDescription,
                // An empty list sent explicitly is an instruction; an absent field is silence. The
                // foreach above only allocates once it has something to add, so `[]` in the body reaches
                // the use case as an empty list and a missing field reaches it as null.
                body.Colours is null ? null : colours ?? [],
                body.Variants is null ? null : variants ?? [],
                media,
                body.RelatedProducts is null ? null : related ?? []),
            null,
            null);
    }

    /// <summary>The badges a staff member may assign (doc 08 §3.2's <c>CK_Products_BadgeOverride</c>).</summary>
    private static readonly ProductBadge[] AssignableBadges =
        [ProductBadge.New, ProductBadge.Sale, ProductBadge.OnlineExclusive, ProductBadge.BestSeller];

    /// <summary>The roles a product media row may take (doc 08 §3.2's <c>CK_ProductMedia_MediaRole</c>).</summary>
    private static readonly ProductMediaRole[] MediaRoles =
        [ProductMediaRole.Gallery, ProductMediaRole.Hover, ProductMediaRole.Video];

    /// <summary>Trims an enum name a client sent, turning blank into <c>null</c> as "not set".</summary>
    private static string? CleanEnum(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Reads an enum name against an explicit allow-list.
    /// <para>
    /// <b>Why not <see cref="Enum.IsDefined(Type, object)"/>, which is what this replaced.</b> That overload
    /// compares the boxed argument's type against the enum's <i>underlying</i> type, so
    /// <c>Enum.IsDefined(typeof(ProductBadge), someInt)</c> throws <see cref="ArgumentException"/> —
    /// <c>ProductBadge</c> is a <c>byte</c> enum, and the argument was an <c>int</c>. It threw before the
    /// range check beside it could run, which made that check dead code and the field error it was written
    /// to produce unreachable: <i>every</i> badge value answered 500 and only <c>null</c> succeeded.
    /// </para>
    /// <para>
    /// <b>And why an allow-list rather than the parse's own answer.</b>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> accepts any numeric string, so "9"
    /// parses happily into an undefined value. It also accepts names outside the allow-list —
    /// <c>LowStock</c> is a real <see cref="ProductBadge"/> that doc 08 §3.2 forbids storing. Membership of
    /// the list is the only check that answers the question actually being asked.
    /// </para>
    /// </summary>
    private static bool TryReadEnum<TEnum>(string? name, TEnum[] allowed, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        if (name is null)
        {
            return false;
        }

        // A numeric string is refused outright, although Enum.TryParse would take it. "3" parsing happily
        // into OnlineExclusive would keep the numeric vocabulary alive in a client's head, which is the one
        // thing Program.cs's converter exists to stop — and it is how this field came to be int? in the
        // first place. One representation, and it is the name the API emits.
        if (name.All(character => char.IsAsciiDigit(character) || character is '-' or '+'))
        {
            return false;
        }

        if (!Enum.TryParse(name, ignoreCase: true, out TEnum parsed) || !allowed.Contains(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Clamps a sort order into <c>smallint</c>, the column's type (doc 08 §3.2).
    /// <para>
    /// Clamped rather than refused: a sort order is the screen's own arithmetic, not something an admin
    /// types, so a number out of range is a client bug and the useful answer is the end of the list rather
    /// than a validation error about a control that is not on the form.
    /// </para>
    /// </summary>
    private static short ToSortOrder(int? value) => value switch
    {
        null => 0,
        < short.MinValue => short.MinValue,
        > short.MaxValue => short.MaxValue,
        _ => (short)value.Value,
    };
}
