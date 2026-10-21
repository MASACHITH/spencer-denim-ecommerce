using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin.Products;

/// <summary>
/// An in-memory <see cref="IProductWriteStore"/> for the ADM-10 handler tests.
/// <para>
/// <b>It is deliberately not more forgiving than the real one</b>, which is MEMORY's lesson from W1-12: a
/// stub shaped more politely than the real API tests nothing. So it keeps the behaviours the handlers
/// actually depend on — the entity it hands back is the same instance the handler mutates (as EF's tracked
/// one is), the SKU check is unfiltered the way <c>UX_ProductVariants_Sku</c> is while the slug and code
/// checks are filtered the way their indexes are, and <see cref="AddProductAsync"/> assigns identity values
/// to the whole graph before it calls back, because that is the one ordering a real insert forces.
/// </para>
/// <para>
/// What it cannot model is the three outcomes only SQL Server decides — a lost concurrency race, a unique
/// index firing, and "nothing was different so no statement was issued". Those are forced with
/// <see cref="NextSaveOutcome"/> here and proved for real against SQL Server in
/// <c>AdminProductEndpointTests</c>.
/// </para>
/// </summary>
internal sealed class FakeProductWriteStore : IProductWriteStore
{
    private int _nextProductId = 500;
    private int _nextColourId = 900;
    private int _nextVariantId = 1300;
    private int _nextMediaId = 1700;

    /// <summary>Live products, by id. The handler mutates these instances.</summary>
    public Dictionary<int, Product> Products { get; } = [];

    /// <summary>Products of other rows that only exist to make a slug, code or SKU taken.</summary>
    public List<(int ProductId, string Slug, string ProductCode)> OtherProducts { get; } = [];

    /// <summary>SKUs that belong to some other product's variant, live or removed.</summary>
    public HashSet<string> TakenSkus { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="TakenSkus"/>
    public HashSet<string> TakenBarcodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<int, (string Name, string Slug)> Departments { get; } = [];

    public Dictionary<int, CategoryReference> Categories { get; } = [];

    public HashSet<int> AgeGroupIds { get; } = [];

    public Dictionary<int, (AttributeOptionType Type, string Name)> AttributeOptions { get; } = [];

    public Dictionary<int, string> ColourFamilies { get; } = [];

    public Dictionary<int, SizeReference> Sizes { get; } = [];

    public HashSet<int> SizeGuideIds { get; } = [];

    public Dictionary<Guid, MediaAssetReference> MediaAssets { get; } = [];

    public Dictionary<int, RelatedProductReference> RelatedProducts { get; } = [];

    /// <summary>Variants an order line names, so the delete guard has something to refuse.</summary>
    public HashSet<int> VariantIdsWithOrders { get; } = [];

    /// <summary>Variants with stock on hand or reserved, which is the delete guard's second reason.</summary>
    public HashSet<int> VariantIdsHoldingStock { get; } = [];

    /// <summary>Forces the next save's answer, standing in for a race only the database can lose.</summary>
    public CatalogSaveOutcome? NextSaveOutcome { get; set; }

    public int Saves { get; private set; }

    /// <summary>The usage set the handler last asked for, so a test can assert the delete guard is fed.</summary>
    public List<ProductMediaUsage>? LastMediaUsages { get; private set; }

    /// <summary>Variant ids that were given a stock row.</summary>
    public List<int> StockRowsCreated { get; } = [];

    public Task<Product?> FindProductAsync(int productId, CancellationToken cancellationToken) =>
        Task.FromResult(Products.TryGetValue(productId, out var product) ? product : null);

    public Task<bool> ProductSlugTakenAsync(
        string slug,
        int? exceptProductId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Products.Values.Any(product =>
                string.Equals(product.Slug, slug, StringComparison.OrdinalIgnoreCase)
                && product.Id != exceptProductId)
            || OtherProducts.Any(other =>
                string.Equals(other.Slug, slug, StringComparison.OrdinalIgnoreCase)
                && other.ProductId != exceptProductId));

    public Task<bool> ProductCodeTakenAsync(
        string productCode,
        int? exceptProductId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Products.Values.Any(product =>
                string.Equals(product.ProductCode, productCode, StringComparison.OrdinalIgnoreCase)
                && product.Id != exceptProductId)
            || OtherProducts.Any(other =>
                string.Equals(other.ProductCode, productCode, StringComparison.OrdinalIgnoreCase)
                && other.ProductId != exceptProductId));

    public Task<IReadOnlyList<string>> SkusTakenAsync(
        IReadOnlyCollection<string> skus,
        int? exceptProductId,
        CancellationToken cancellationToken)
    {
        var taken = skus.Where(TakenSkus.Contains).ToList();

        taken.AddRange(Products.Values
            .Where(product => product.Id != exceptProductId)
            .SelectMany(product => product.Variants)
            .Select(variant => variant.Sku)
            .Where(sku => skus.Contains(sku, StringComparer.OrdinalIgnoreCase)));

        return Task.FromResult<IReadOnlyList<string>>(
            taken.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<IReadOnlyList<string>> BarcodesTakenAsync(
        IReadOnlyCollection<string> barcodes,
        int? exceptProductId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(barcodes.Where(TakenBarcodes.Contains).ToList());

    public Task<ProductReferenceData> LoadReferencesAsync(
        ProductReferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var department = Departments.TryGetValue(request.DepartmentId, out var found) ? found : default;

        return Task.FromResult(new ProductReferenceData(
            department.Name,
            department.Slug,
            request.CategoryIds
                .Where(Categories.ContainsKey)
                .ToDictionary(id => id, id => Categories[id]),
            request.AgeGroupId is { } ageGroupId && AgeGroupIds.Contains(ageGroupId),
            request.AttributeOptionIds
                .Where(AttributeOptions.ContainsKey)
                .ToDictionary(id => id, id => AttributeOptions[id]),
            request.ColourFamilyIds
                .Where(ColourFamilies.ContainsKey)
                .ToDictionary(id => id, id => ColourFamilies[id]),
            request.SizeIds.Where(Sizes.ContainsKey).ToDictionary(id => id, id => Sizes[id]),
            request.SizeGuideId is { } sizeGuideId && SizeGuideIds.Contains(sizeGuideId),
            request.MediaAssetPublicIds
                .Where(MediaAssets.ContainsKey)
                .ToDictionary(id => id, id => MediaAssets[id]),
            request.RelatedProductIds
                .Where(RelatedProducts.ContainsKey)
                .ToDictionary(id => id, id => RelatedProducts[id])));
    }

    public Task<VariantDeleteGuards> VariantDeleteGuardsAsync(
        int productId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new VariantDeleteGuards(VariantIdsWithOrders, VariantIdsHoldingStock));

    public Task SyncProductMediaUsagesAsync(
        int productId,
        IReadOnlyCollection<ProductMediaUsage> desired,
        CancellationToken cancellationToken)
    {
        LastMediaUsages = desired.ToList();
        return Task.CompletedTask;
    }

    public Task EnsureStockRowsAsync(int productId, CancellationToken cancellationToken)
    {
        if (Products.TryGetValue(productId, out var product))
        {
            StockRowsCreated.AddRange(product.Variants.Select(variant => variant.Id));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Asset ids the locked re-check of the next media save should find unusable (#223): a picture a
    /// media-library delete got to first. Only SQL Server can decide that race; the real proof is in
    /// <c>CatalogMediaLockRaceTests</c>.
    /// </summary>
    public HashSet<int> UnusableAssetIds { get; } = [];

    /// <summary>The claim the last media save was handed, so a test can assert what it would lock.</summary>
    public CatalogMediaClaim? LastMediaClaim { get; private set; }

    private bool Refuses(CatalogMediaClaim media)
    {
        LastMediaClaim = media;
        media.Unavailable = media.Slots.Where(slot => UnusableAssetIds.Contains(slot.MediaAssetId)).ToList();
        return media.Unavailable.Count > 0;
    }

    public async Task<CatalogSaveOutcome> AddProductAsync(
        Product product,
        Func<Product, CancellationToken, Task> afterInsert,
        CatalogMediaClaim media,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(afterInsert);
        ArgumentNullException.ThrowIfNull(media);

        if (NextSaveOutcome is { } forced)
        {
            NextSaveOutcome = null;
            return forced;
        }

        if (Refuses(media))
        {
            return CatalogSaveOutcome.MediaUnavailable;
        }

        AssignIdentityValues(product);
        Products[product.Id] = product;

        // The real store calls this inside its transaction, once every identity value exists.
        await afterInsert(product, cancellationToken);

        Saves++;
        return CatalogSaveOutcome.Saved;
    }

    /// <summary>
    /// The variants the handler asked to be deleted.
    /// <para>
    /// Recorded rather than ignored, because this is the one call a fake cannot stand in for: against real
    /// EF a variant that leaves the collection without it is a 500, since <c>ProductVariants to
    /// Products</c> is <c>NO ACTION</c>. The proof lives in
    /// <c>AdminProductEndpointTests.An_edit_can_remove_a_variant_and_a_colour_that_owns_a_gallery_row</c>;
    /// what this records is that the handler makes the call at all.
    /// </para>
    /// </summary>
    public List<ProductVariant> RemovedVariants { get; } = [];

    /// <summary>Bag lines the fake pretends went with the variants, so the audit assertion has something.</summary>
    public int CartLinesPerRemovedVariant { get; set; }

    public Task<int> RemoveVariantsAsync(
        IReadOnlyCollection<ProductVariant> variants,
        CancellationToken cancellationToken)
    {
        RemovedVariants.AddRange(variants);

        return Task.FromResult(variants.Count * CartLinesPerRemovedVariant);
    }

    public Task<CatalogSaveOutcome> SaveWithMediaAsync(
        CatalogMediaClaim media,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (NextSaveOutcome is null && Refuses(media))
        {
            return Task.FromResult(CatalogSaveOutcome.MediaUnavailable);
        }

        return SaveAsync(actingUserId, cancellationToken);
    }

    public Task<CatalogSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        if (NextSaveOutcome is { } forced)
        {
            NextSaveOutcome = null;
            return Task.FromResult(forced);
        }

        foreach (var product in Products.Values)
        {
            AssignIdentityValues(product);
        }

        Saves++;

        // A soft-deleted product leaves every query, the way the global filter makes it leave the real ones.
        foreach (var deleted in Products.Values.Where(product => product.IsDeleted).ToList())
        {
            Products.Remove(deleted.Id);
        }

        return Task.FromResult(CatalogSaveOutcome.Saved);
    }

    /// <summary>
    /// Gives the graph the identity values and row versions an insert would, and performs the foreign-key
    /// fixup EF does from the navigation properties — which is the one piece of real behaviour the
    /// handlers depend on, because a colourway created in the same request has no id when its variants are
    /// built.
    /// </summary>
    private void AssignIdentityValues(Product product)
    {
        if (product.Id == 0)
        {
            product.Id = _nextProductId++;
        }

        product.RowVersion ??= BitConverter.GetBytes((long)product.Id);

        foreach (var colour in product.Colours)
        {
            if (colour.Id == 0)
            {
                colour.Id = _nextColourId++;
            }

            colour.ProductId = product.Id;
        }

        foreach (var variant in product.Variants)
        {
            if (variant.Id == 0)
            {
                variant.Id = _nextVariantId++;
            }

            variant.ProductId = product.Id;
            variant.RowVersion ??= BitConverter.GetBytes((long)variant.Id);

            if (variant.ProductColour is { } colour)
            {
                variant.ProductColourId = colour.Id;
            }
        }

        foreach (var media in product.Media)
        {
            if (media.Id == 0)
            {
                media.Id = _nextMediaId++;
            }

            media.ProductId = product.Id;
            media.ProductColourId = media.ProductColour?.Id;
        }

        foreach (var link in product.CategoryLinks)
        {
            link.ProductId = product.Id;
        }

        foreach (var related in product.RelatedProducts)
        {
            related.ProductId = product.Id;
        }

        if (product.Stats is { } stats)
        {
            stats.ProductId = product.Id;
        }

        if (product.SearchIndex is { } searchIndex)
        {
            searchIndex.ProductId = product.Id;
        }
    }

    /// <summary>The base64 token the admin read model would have sent for a product.</summary>
    public static string Token(byte[]? rowVersion) => Convert.ToBase64String(rowVersion!);

    /// <summary>A ready, public image in the library.</summary>
    public Guid AddImageAsset(string? altText = "A photograph", int id = 0)
    {
        var publicId = Guid.NewGuid();
        MediaAssets[publicId] = new MediaAssetReference(
            id == 0 ? MediaAssets.Count + 7000 : id,
            publicId,
            MediaAssetType.Image,
            MediaAssetStatus.Ready,
            MediaVisibility.Public,
            altText);

        return publicId;
    }

    /// <summary>A ready, public MP4 in the library.</summary>
    public Guid AddVideoAsset(int id = 0)
    {
        var publicId = Guid.NewGuid();
        MediaAssets[publicId] = new MediaAssetReference(
            id == 0 ? MediaAssets.Count + 7000 : id,
            publicId,
            MediaAssetType.Video,
            MediaAssetStatus.Ready,
            MediaVisibility.Public,
            null);

        return publicId;
    }

    /// <summary>An asset in some state a product must refuse.</summary>
    public Guid AddAsset(MediaAssetType type, MediaAssetStatus status, MediaVisibility visibility)
    {
        var publicId = Guid.NewGuid();
        MediaAssets[publicId] = new MediaAssetReference(
            MediaAssets.Count + 7000, publicId, type, status, visibility, "Alt");

        return publicId;
    }
}
