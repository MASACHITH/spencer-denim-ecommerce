using System.Globalization;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>
/// Doc 11 §7 <b>G-01</b>, the product publish checklist: "Block publish without ≥ 1 active variant, a card
/// image and alt text".
/// <para>
/// <b>Why a list of sentences rather than a boolean.</b> A refusal that says only "this product cannot be
/// published" leaves an admin pressing the same button again. Each rule names what is missing and, where
/// there is more than one offender, how many — which is also what lets the form grey the button out and
/// explain itself <i>before</i> it is pressed, from the same function the API refuses with.
/// </para>
/// <para>
/// <b>Why it is a pure function over the aggregate.</b> The same three rules have to hold at the moment of
/// publishing (the API) and be shown while editing (the detail payload). One function read twice cannot
/// disagree with itself; two implementations would, and the one that drifted would be the screen's — which
/// is the version the admin believes.
/// </para>
/// </summary>
public static class ProductPublishChecklist
{
    /// <summary>
    /// What is stopping this product from being published, or an empty list when nothing is.
    /// </summary>
    /// <param name="product">
    /// A product loaded with its variants and its media, and with each media row's
    /// <see cref="ProductMedia.MediaAsset"/> — the alt-text rule accepts the library asset's own alt text,
    /// so without the asset it would have to insist on an override.
    /// </param>
    public static IReadOnlyList<string> Blockers(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var blockers = new List<string>(3);

        if (!product.Variants.Any(variant => variant.IsActive))
        {
            blockers.Add(
                product.Variants.Count == 0
                    ? "Add at least one colour and size, so there is something a customer can put in their bag."
                    : "At least one variant has to be switched on — every colour and size is currently off.");
        }

        // The card image is derived from the gallery on save (ProductDerivedData), so a product with any
        // gallery image has one. The rule is stated in terms of the gallery rather than the denormalised
        // column so the message says what to do about it.
        if (!product.Media.Any(media => media.MediaRole == ProductMediaRole.Gallery))
        {
            blockers.Add("Add at least one gallery photograph: it becomes the picture on the listing card.");
        }

        // Doc 08 §3.3 and WCAG 2.2: every image a shopper can see needs alt text. The video is excluded
        // because it is not an image and because its poster is an image of its own with its own alt text.
        var withoutAltText = product.Media
            .Where(media => media.MediaRole is ProductMediaRole.Gallery or ProductMediaRole.Hover)
            .Count(media =>
                string.IsNullOrWhiteSpace(media.AltTextOverride)
                && string.IsNullOrWhiteSpace(media.MediaAsset?.AltText));

        if (withoutAltText > 0)
        {
            var count = withoutAltText.ToString(CultureInfo.InvariantCulture);
            var noun = withoutAltText == 1 ? "photograph has" : "photographs have";

            blockers.Add(
                $"{count} {noun} no alt text. Describe each one, so the page works for a customer using a "
                + "screen reader.");
        }

        return blockers;
    }

    /// <summary>Whether <see cref="Blockers"/> is empty.</summary>
    public static bool CanBePublished(Product product) => Blockers(product).Count == 0;
}
