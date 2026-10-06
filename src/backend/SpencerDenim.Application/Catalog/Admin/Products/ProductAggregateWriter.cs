using System.Globalization;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>
/// Puts a validated form onto a <see cref="Product"/> aggregate: the scalar fields, the category links, the
/// colourways, the variant matrix, the media rows, the related picks, and then the derived data of doc 11
/// P1 step 13.
/// <para>
/// <b>Everything is reconciled in place, never cleared and rebuilt.</b> Removing eleven rows and inserting
/// eleven identical ones would be a correct end state reached the wrong way: every row would get a new
/// identity value, so the gallery's sort order would churn, <c>media.MediaUsages</c> would move its
/// <c>CreatedAtUtc</c>, the audit diff would claim everything changed, and — the one that actually breaks —
/// a variant an order line points at would be deleted and re-created with a different id. Matching first
/// also means a save of an untouched form dirties nothing, which is what makes
/// <see cref="CatalogWriteResult.Unchanged"/> honest.
/// </para>
/// </summary>
internal static class ProductAggregateWriter
{
    /// <summary>
    /// Applies <paramref name="form"/> to <paramref name="product"/>, or returns the refusal.
    /// </summary>
    /// <param name="variantIdsWithOrders">
    /// Variants an order line already names. Doc 08 §3.2: such a variant is deactivated, never deleted. A
    /// request that drops one is refused with the SKUs named, so the screen can offer the deactivation
    /// instead (doc 11 P1's "Removing a variant that has orders → offered as 'deactivate'").
    /// </param>
    public static CatalogWriteResult? Apply(
        Product product,
        ValidatedProductForm form,
        IReadOnlySet<int> variantIdsWithOrders,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(variantIdsWithOrders);

        ApplyScalars(product, form);
        ApplyCategoryLinks(product, form);

        var coloursByName = ApplyColours(product, form);

        if (ApplyVariants(product, form, coloursByName, variantIdsWithOrders) is { } refusal)
        {
            return refusal;
        }

        ApplyMedia(product, form, coloursByName);
        ApplyRelated(product, form);

        // Doc 11 P1 step 13, in this order: the card and hover images come from the media rows, and the
        // search text and the price summary come from the colours and the variants — so all three run after
        // the collections are settled, in the same unit of work that writes them.
        ProductDerivedData.RefreshCardAndHoverImages(product);
        ProductDerivedData.RefreshStats(product, utcNow);
        ProductDerivedData.RefreshSearchIndex(product, form.Categories, form.References, utcNow);

        return null;
    }

    private static void ApplyScalars(Product product, ValidatedProductForm form)
    {
        product.ProductCode = form.ProductCode;
        product.Name = form.Name;
        product.Slug = form.Slug;
        product.DepartmentId = form.Form.DepartmentId;
        product.PrimaryCategoryId = form.Form.PrimaryCategoryId;
        product.AgeGroupId = form.Form.AgeGroupId;
        product.ShortDescription = CatalogFieldRules.Clean(form.Form.ShortDescription);
        product.Description = CatalogFieldRules.Clean(form.Form.Description);
        product.CareInstructions = CatalogFieldRules.Clean(form.Form.CareInstructions);
        product.MaterialId = form.Form.MaterialId;
        product.FitId = form.Form.FitId;
        product.OccasionId = form.Form.OccasionId;
        product.BasePrice = form.Form.BasePrice;
        product.CompareAtPrice = form.Form.CompareAtPrice;
        product.BadgeOverride = form.Form.BadgeOverride;
        product.RecommendedRank = form.Form.RecommendedRank;
        product.SizeGuideId = form.Form.SizeGuideId;
        product.SeoTitle = CatalogFieldRules.Clean(form.Form.SeoTitle);
        product.SeoDescription = CatalogFieldRules.Clean(form.Form.SeoDescription);
    }

    /// <summary>
    /// Doc 08 §3.2: "The primary category is also stored here", so the link set is the primary category plus
    /// the extras — which is why <c>ProductFormInput.ExtraCategoryIds</c> excludes the primary one and a
    /// form cannot unlink a product from its own category.
    /// </summary>
    private static void ApplyCategoryLinks(Product product, ValidatedProductForm form)
    {
        var wanted = form.CategoryIds.ToHashSet();

        foreach (var link in product.CategoryLinks.Where(link => !wanted.Contains(link.CategoryId)).ToList())
        {
            product.CategoryLinks.Remove(link);
        }

        var existing = product.CategoryLinks.Select(link => link.CategoryId).ToHashSet();

        foreach (var categoryId in form.CategoryIds.Where(id => !existing.Contains(id)))
        {
            product.CategoryLinks.Add(new ProductCategory { CategoryId = categoryId, Product = product });
        }
    }

    /// <summary>
    /// Reconciles the colourways and returns them by name — the key the variants and the media rows use,
    /// because a colourway added in the same request has no identity value yet.
    /// </summary>
    private static Dictionary<string, ProductColour> ApplyColours(Product product, ValidatedProductForm form)
    {
        var byId = product.Colours.Where(colour => colour.Id != 0).ToDictionary(colour => colour.Id);
        var byName = product.Colours.ToDictionary(colour => colour.Name, StringComparer.OrdinalIgnoreCase);

        var matched = new HashSet<ProductColour>();
        var result = new Dictionary<string, ProductColour>(StringComparer.OrdinalIgnoreCase);

        foreach (var wanted in form.Colours)
        {
            // By id when the form carried one — which is how a rename reaches the right row rather than
            // being read as "remove Indigo, add Indigo Rinse" and taking its variants with it.
            ProductColour? colour = null;
            if (wanted.Id is { } id && byId.TryGetValue(id, out var existingById))
            {
                colour = existingById;
            }
            else if (byName.TryGetValue(wanted.Name, out var existingByName) && !matched.Contains(existingByName))
            {
                colour = existingByName;
            }

            if (colour is null)
            {
                colour = new ProductColour { Product = product };
                product.Colours.Add(colour);
            }

            matched.Add(colour);

            colour.Name = wanted.Name;
            colour.ColourFamilyId = wanted.ColourFamilyId;
            colour.SwatchHex = wanted.SwatchHex;
            colour.SwatchAssetId = wanted.SwatchAssetId;
            colour.SortOrder = wanted.SortOrder;

            result[wanted.Name] = colour;
        }

        foreach (var gone in product.Colours.Where(colour => !matched.Contains(colour)).ToList())
        {
            product.Colours.Remove(gone);
        }

        return result;
    }

    private static CatalogWriteResult.Conflict? ApplyVariants(
        Product product,
        ValidatedProductForm form,
        Dictionary<string, ProductColour> coloursByName,
        IReadOnlySet<int> variantIdsWithOrders)
    {
        var byId = product.Variants.Where(variant => variant.Id != 0).ToDictionary(variant => variant.Id);

        // The natural key of a variant row: its colour and its size (UX_ProductVariants_Colour_Size). Keyed
        // by the *colour entity* rather than by a name, so a renamed colour's existing variants are still
        // found.
        var bySlot = product.Variants
            .Where(variant => variant.ProductColour is not null)
            .ToDictionary(variant => (Colour: variant.ProductColour!, variant.SizeId));

        var matched = new HashSet<ProductVariant>();

        foreach (var wanted in form.Variants)
        {
            var colour = coloursByName[wanted.ColourName];

            ProductVariant? variant = null;
            if (wanted.Id is { } id && byId.TryGetValue(id, out var existingById))
            {
                variant = existingById;
            }
            else if (bySlot.TryGetValue((colour, wanted.SizeId), out var existingBySlot)
                && !matched.Contains(existingBySlot))
            {
                variant = existingBySlot;
            }

            if (variant is null)
            {
                variant = new ProductVariant { Product = product };
                product.Variants.Add(variant);
            }

            matched.Add(variant);

            // Both the navigation and the id: the navigation is what lets a colourway created in this very
            // request be pointed at (its identity value does not exist yet, and the foreign key is the
            // composite (ProductColourId, ProductId) against ProductColours (Id, ProductId), doc 08 §3.2),
            // and the id is what keeps an existing row's foreign key correct without a fixup pass.
            variant.ProductColour = colour;
            variant.ProductColourId = colour.Id;
            variant.SizeId = wanted.SizeId;
            variant.Sku = wanted.Sku;
            variant.Barcode = wanted.Barcode;
            variant.PriceOverride = wanted.PriceOverride;
            variant.CompareAtPriceOverride = wanted.CompareAtPriceOverride;
            variant.IsActive = wanted.IsActive;
            variant.SortOrder = wanted.SortOrder;
        }

        var removing = product.Variants.Where(variant => !matched.Contains(variant)).ToList();

        var ordered = removing
            .Where(variant => variant.Id != 0 && variantIdsWithOrders.Contains(variant.Id))
            .ToList();

        if (ordered.Count > 0)
        {
            var skus = ordered.Select(variant => variant.Sku).Order(StringComparer.Ordinal).ToList();

            var subject = skus.Count == 1
                ? "This variant has"
                : $"These {skus.Count.ToString(CultureInfo.InvariantCulture)} variants have";
            var pronoun = skus.Count == 1 ? "it" : "they";

            return new CatalogWriteResult.Conflict(
                CatalogConflictCodes.VariantHasOrders,
                $"{subject} already been ordered, so {pronoun} cannot be deleted: {string.Join(", ", skus)}. "
                + "Switch them off instead — the order history keeps pointing at them, and nothing new can be "
                + "bought.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["skus"] = skus });
        }

        foreach (var gone in removing)
        {
            product.Variants.Remove(gone);
        }

        return null;
    }

    /// <summary>
    /// Reconciles the media rows — or leaves them entirely alone when the request carried no media field.
    /// See <c>ProductWriteRules.NormaliseMedia</c> for why those two are different, and for the W1-12 defect
    /// that made it worth writing down twice.
    /// </summary>
    private static void ApplyMedia(
        Product product,
        ValidatedProductForm form,
        Dictionary<string, ProductColour> coloursByName)
    {
        if (form.Media is null)
        {
            // A colour the request removed takes its gallery rows with it, even on a request that says
            // nothing about media: without this the rows would survive pointing at a deleted colourway and
            // the FK (ProductMedia.ProductColourId → ProductColours, NO ACTION) would refuse the save.
            var liveColours = coloursByName.Values.ToHashSet();

            foreach (var orphan in product.Media
                .Where(media => media.ProductColour is not null && !liveColours.Contains(media.ProductColour))
                .ToList())
            {
                product.Media.Remove(orphan);
            }

            return;
        }

        // UX_ProductMedia_Product_Asset_Colour: the slot a media row occupies is (asset, colour).
        var bySlot = product.Media.ToDictionary(media => (media.MediaAssetId, Colour: media.ProductColour));
        var matched = new HashSet<ProductMedia>();

        foreach (var wanted in form.Media)
        {
            var colour = wanted.ColourName is null ? null : coloursByName[wanted.ColourName];

            if (!bySlot.TryGetValue((wanted.AssetId, colour), out var media) || matched.Contains(media))
            {
                media = new ProductMedia { Product = product, MediaAssetId = wanted.AssetId };
                product.Media.Add(media);
            }

            matched.Add(media);

            media.ProductColour = colour;
            media.ProductColourId = colour?.Id;
            media.MediaRole = wanted.MediaRole;
            media.SortOrder = wanted.SortOrder;
            media.AltTextOverride = wanted.AltTextOverride;
        }

        foreach (var gone in product.Media.Where(media => !matched.Contains(media)).ToList())
        {
            product.Media.Remove(gone);
        }
    }

    private static void ApplyRelated(Product product, ValidatedProductForm form)
    {
        var wanted = form.RelatedProducts.ToDictionary(related => related.RelatedProductId);

        foreach (var gone in product.RelatedProducts
            .Where(related => !wanted.ContainsKey(related.RelatedProductId))
            .ToList())
        {
            product.RelatedProducts.Remove(gone);
        }

        var existing = product.RelatedProducts.ToDictionary(related => related.RelatedProductId);

        foreach (var (relatedProductId, input) in wanted)
        {
            if (existing.TryGetValue(relatedProductId, out var link))
            {
                link.SortOrder = input.SortOrder;
                continue;
            }

            product.RelatedProducts.Add(new RelatedProduct
            {
                Product = product,
                RelatedProductId = relatedProductId,
                SortOrder = input.SortOrder,
            });
        }
    }
}
