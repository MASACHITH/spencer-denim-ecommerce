using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// The EF Core side of <see cref="IProductWriteStore"/> (ADM-10, W1-03).
/// <para>
/// It hands out tracked entities and keeps one unit of work, so the handlers decide what a change is and
/// this class decides only how it reaches SQL Server — the division
/// <see cref="CatalogStructureStore"/> already draws for ADM-11. The two judgements it makes are the two
/// the database alone can make: a row somebody else changed first, and a unique index.
/// </para>
/// </summary>
internal sealed class ProductWriteStore(AppDbContext dbContext, AuditAndSoftDeleteInterceptor auditColumns)
    : IProductWriteStore
{
    /// <summary>SQL Server: 2627 is a unique-constraint violation, 2601 a unique-index one.</summary>
    private static readonly int[] DuplicateKeyErrors = [2601, 2627];

    public Task<Product?> FindProductAsync(int productId, CancellationToken cancellationToken) =>
        dbContext.Products
            .Include(product => product.Colours)
            .Include(product => product.Variants)
            .Include(product => product.Media)
                .ThenInclude(media => media.MediaAsset)
            .Include(product => product.CategoryLinks)
            .Include(product => product.RelatedProducts)
            .Include(product => product.Stats)
            .Include(product => product.SearchIndex)
            .FirstOrDefaultAsync(product => product.Id == productId, cancellationToken);

    public Task<bool> ProductSlugTakenAsync(
        string slug,
        int? exceptProductId,
        CancellationToken cancellationToken) =>
        dbContext.Products.AnyAsync(
            product => product.Slug == slug && (exceptProductId == null || product.Id != exceptProductId),
            cancellationToken);

    public Task<bool> ProductCodeTakenAsync(
        string productCode,
        int? exceptProductId,
        CancellationToken cancellationToken) =>
        dbContext.Products.AnyAsync(
            product => product.ProductCode == productCode
                && (exceptProductId == null || product.Id != exceptProductId),
            cancellationToken);

    /// <summary>
    /// <inheritdoc cref="IProductWriteStore.SkusTakenAsync" path="/summary/node()"/>
    /// <para>
    /// <c>IgnoreQueryFilters</c> is the whole point: <c>ProductVariants</c> has no soft-delete column of
    /// its own, but <c>AppDbContext</c> filters a variant out when its product is soft-deleted, and
    /// <c>UX_ProductVariants_Sku</c> does not. Without this, an admin reusing the SKU of a removed
    /// product's variant would be told it was free and then handed a 409 from the index.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> SkusTakenAsync(
        IReadOnlyCollection<string> skus,
        int? exceptProductId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skus);

        if (skus.Count == 0)
        {
            return [];
        }

        var candidates = skus.ToList();

        return await dbContext.ProductVariants
            .IgnoreQueryFilters()
            .Where(variant =>
                candidates.Contains(variant.Sku)
                && (exceptProductId == null || variant.ProductId != exceptProductId))
            .Select(variant => variant.Sku)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc cref="SkusTakenAsync"/>
    public async Task<IReadOnlyList<string>> BarcodesTakenAsync(
        IReadOnlyCollection<string> barcodes,
        int? exceptProductId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(barcodes);

        if (barcodes.Count == 0)
        {
            return [];
        }

        var candidates = barcodes.ToList();

        return await dbContext.ProductVariants
            .IgnoreQueryFilters()
            .Where(variant =>
                variant.Barcode != null
                && candidates.Contains(variant.Barcode)
                && (exceptProductId == null || variant.ProductId != exceptProductId))
            .Select(variant => variant.Barcode!)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductReferenceData> LoadReferencesAsync(
        ProductReferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var department = await dbContext.Departments
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.DepartmentId)
            .Select(candidate => new { candidate.Name, candidate.Slug })
            .FirstOrDefaultAsync(cancellationToken);

        var categoryIds = request.CategoryIds.ToList();

        var categoryRows = categoryIds.Count == 0
            ? []
            : await dbContext.Categories
                .AsNoTracking()
                .Where(category => categoryIds.Contains(category.Id))
                .Select(category => new
                {
                    category.Id,
                    category.DepartmentId,
                    category.Name,
                    category.Path,
                    category.IsActive,
                })
                .ToListAsync(cancellationToken);

        // The ancestors, for the path names doc 08 §3.2 puts in the search text. One query for all of
        // them: a materialised path means an ancestor's id is a segment of the descendant's path, so the
        // whole set is known before anything is read and no walk is needed.
        var ancestorIds = categoryRows
            .SelectMany(row => CategoryPath.IdsIn(row.Path))
            .Distinct()
            .ToList();

        var namesById = ancestorIds.Count == 0
            ? new Dictionary<int, string>()
            : await dbContext.Categories
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(category => ancestorIds.Contains(category.Id))
                .ToDictionaryAsync(category => category.Id, category => category.Name, cancellationToken);

        var categories = categoryRows.ToDictionary(
            row => row.Id,
            row => new CategoryReference(
                row.Id,
                row.DepartmentId,
                row.Name,
                row.Path,
                row.IsActive,
                CategoryPath.IdsIn(row.Path)
                    .Select(id => namesById.TryGetValue(id, out var name) ? name : null)
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .ToList()));

        var ageGroupExists = request.AgeGroupId is { } ageGroupId
            && await dbContext.AgeGroups.AnyAsync(group => group.Id == ageGroupId, cancellationToken);

        var attributeIds = request.AttributeOptionIds.ToList();
        var attributeOptions = attributeIds.Count == 0
            ? new Dictionary<int, (AttributeOptionType Type, string Name)>()
            : (await dbContext.AttributeOptions
                .AsNoTracking()
                .Where(option => attributeIds.Contains(option.Id))
                .Select(option => new { option.Id, option.AttributeType, option.Name })
                .ToListAsync(cancellationToken))
                .ToDictionary(option => option.Id, option => (option.AttributeType, option.Name));

        var familyIds = request.ColourFamilyIds.ToList();
        var colourFamilies = familyIds.Count == 0
            ? new Dictionary<int, string>()
            : await dbContext.ColourFamilies
                .AsNoTracking()
                .Where(family => familyIds.Contains(family.Id))
                .ToDictionaryAsync(family => family.Id, family => family.Name, cancellationToken);

        var sizeIds = request.SizeIds.ToList();
        var sizes = sizeIds.Count == 0
            ? new Dictionary<int, SizeReference>()
            : (await dbContext.Sizes
                .AsNoTracking()
                .Where(size => sizeIds.Contains(size.Id))
                .Select(size => new SizeReference(size.Id, size.ScaleName, size.Label, size.SortOrder))
                .ToListAsync(cancellationToken))
                .ToDictionary(size => size.Id);

        var sizeGuideExists = request.SizeGuideId is { } sizeGuideId
            && await dbContext.SizeGuides.AnyAsync(guide => guide.Id == sizeGuideId, cancellationToken);

        var assetPublicIds = request.MediaAssetPublicIds.ToList();
        var assets = assetPublicIds.Count == 0
            ? new Dictionary<Guid, MediaAssetReference>()
            : (await dbContext.MediaAssets
                .AsNoTracking()
                .Where(asset => assetPublicIds.Contains(asset.PublicId))
                .Select(asset => new MediaAssetReference(
                    asset.Id,
                    asset.PublicId,
                    asset.MediaType,
                    asset.Status,
                    asset.Visibility,
                    asset.AltText))
                .ToListAsync(cancellationToken))
                .ToDictionary(asset => asset.PublicId);

        var relatedIds = request.RelatedProductIds.ToList();
        var related = relatedIds.Count == 0
            ? new Dictionary<int, RelatedProductReference>()
            : (await dbContext.Products
                .AsNoTracking()
                .Where(product => relatedIds.Contains(product.Id))
                .Select(product => new RelatedProductReference(
                    product.Id, product.Name, product.ProductCode, product.IsPublished))
                .ToListAsync(cancellationToken))
                .ToDictionary(product => product.Id);

        return new ProductReferenceData(
            department?.Name,
            department?.Slug,
            categories,
            ageGroupExists,
            attributeOptions,
            colourFamilies,
            sizes,
            sizeGuideExists,
            assets,
            related);
    }

    public async Task<IReadOnlySet<int>> VariantIdsWithOrdersAsync(
        int productId,
        CancellationToken cancellationToken)
    {
        // Doc 08 §3.5: an order line keeps its own snapshot of the name, SKU and price *and* a real
        // foreign key to the variant, so the line survives a product being soft-deleted. That key is what
        // makes the variant undeletable: doc 08 §3.2 keeps an ordered variant for ever and deactivates it
        // instead.
        var ids = await dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.Variant!.ProductId == productId)
            .Select(line => line.VariantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<int, int>> OnHandByVariantAsync(
        int productId,
        CancellationToken cancellationToken) =>
        await dbContext.StockLevels
            .AsNoTracking()
            .Where(stock => stock.Variant!.ProductId == productId)
            .ToDictionaryAsync(stock => stock.VariantId, stock => stock.OnHand, cancellationToken);

    public async Task SyncProductMediaUsagesAsync(
        int productId,
        IReadOnlyCollection<ProductMediaUsage> desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);

        var ownerId = productId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var existing = await dbContext.MediaUsages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Product && usage.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        var wanted = desired.ToHashSet();

        // Removed first, and only the rows that are genuinely not wanted: a row that is still wanted is
        // left untouched so its CreatedAtUtc does not move, which is what the library's "used in…" panel
        // shows as "attached on".
        foreach (var gone in existing.Where(usage =>
            !wanted.Contains(new ProductMediaUsage(usage.MediaAssetId, usage.UsageRole))))
        {
            dbContext.MediaUsages.Remove(gone);
        }

        var held = existing
            .Select(usage => new ProductMediaUsage(usage.MediaAssetId, usage.UsageRole))
            .ToHashSet();

        foreach (var usage in wanted.Where(usage => !held.Contains(usage)))
        {
            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = usage.MediaAssetId,
                OwnerType = MediaUsageOwnerTypes.Product,
                OwnerId = ownerId,
                UsageRole = usage.UsageRole,
            });
        }
    }

    public async Task EnsureStockRowsAsync(int productId, CancellationToken cancellationToken)
    {
        var variantIds = await dbContext.ProductVariants
            .Where(variant => variant.ProductId == productId)
            .Select(variant => variant.Id)
            .ToListAsync(cancellationToken);

        if (variantIds.Count == 0)
        {
            return;
        }

        var withStock = await dbContext.StockLevels
            .Where(stock => variantIds.Contains(stock.VariantId))
            .Select(stock => stock.VariantId)
            .ToListAsync(cancellationToken);

        // Rows already enlisted in this unit of work but not yet committed, so a second call inside one
        // transaction does not try to insert the same key twice.
        var pending = dbContext.ChangeTracker
            .Entries<StockLevel>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.VariantId)
            .ToList();

        var known = withStock.Concat(pending).ToHashSet();

        foreach (var variantId in variantIds.Where(id => !known.Contains(id)))
        {
            // Doc 11 P1 step 10: on-hand 0. The first real figure is an `Initial` inventory movement in
            // P2 (ADM-12), which is the only place stock is allowed to move.
            dbContext.StockLevels.Add(new StockLevel { VariantId = variantId, OnHand = 0, Reserved = 0 });
        }
    }

    public async Task<CatalogSaveOutcome> AddProductAsync(
        Product product,
        Func<Product, CancellationToken, Task> afterInsert,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(afterInsert);

        auditColumns.CurrentUserId = actingUserId;

        // Two statements — the graph, then the rows that need its identity values (the audit row naming the
        // new id, the MediaUsages rows, the stock rows) — and they have to be one transaction. A failure
        // between them would leave a product with photographs no usage row protects and variants with no
        // stock row, neither of which any screen shows as wrong: the pictures become deletable from the
        // library and the variants read as permanently out of stock.
        //
        // EnableRetryOnFailure is on (doc 08 §10.3) and its execution strategy refuses a user-initiated
        // transaction unless the whole unit of work is inside it, so the *attempt* is what retries and each
        // attempt starts from a clean change tracker (the W1-13 lesson).
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            dbContext.ChangeTracker.Clear();

            ResetIdentityValues(product);

            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

            dbContext.Products.Add(product);

            try
            {
                await dbContext.SaveChangesAsync(token);

                await afterInsert(product, token);

                await dbContext.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return CatalogSaveOutcome.Saved;
            }
            catch (DbUpdateException exception) when (IsDuplicateKey(exception))
            {
                await transaction.RollbackAsync(token);
                return CatalogSaveOutcome.DuplicateKey;
            }
        },
        cancellationToken);
    }

    public async Task<CatalogSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        auditColumns.CurrentUserId = actingUserId;

        if (!dbContext.ChangeTracker.HasChanges())
        {
            return CatalogSaveOutcome.NothingToSave;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return CatalogSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the UPDATE's RowVersion predicate matched no row, so SQL Server applied
            // none of the batch. The caller reloads and re-applies (doc 08 §1.5).
            return CatalogSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            return CatalogSaveOutcome.DuplicateKey;
        }
    }

    /// <summary>
    /// Clears the identity values and row versions a failed attempt may have left on the graph.
    /// <para>
    /// A retry re-adds the same objects, and re-adding an entity that already carries a key would be an
    /// explicit-identity insert, which SQL Server refuses. The row version matters for the same reason:
    /// EF reads one back on insert, and an <c>Added</c> entity carrying one is a batch SQL Server will not
    /// accept. <c>CatalogStructureStore.AddCategoryAsync</c> makes the same reset for one row; a product is
    /// a graph, so every level of it needs clearing.
    /// </para>
    /// </summary>
    private static void ResetIdentityValues(Product product)
    {
        product.Id = 0;
        product.RowVersion = null;

        foreach (var colour in product.Colours)
        {
            colour.Id = 0;
            colour.ProductId = 0;
        }

        foreach (var variant in product.Variants)
        {
            variant.Id = 0;
            variant.ProductId = 0;
            variant.ProductColourId = 0;
            variant.RowVersion = null;
        }

        foreach (var media in product.Media)
        {
            media.Id = 0;
            media.ProductId = 0;
            media.ProductColourId = null;
        }

        foreach (var link in product.CategoryLinks)
        {
            link.ProductId = 0;
        }

        foreach (var related in product.RelatedProducts)
        {
            related.ProductId = 0;
        }

        if (product.Stats is { } stats)
        {
            stats.ProductId = 0;
        }

        if (product.SearchIndex is { } searchIndex)
        {
            searchIndex.ProductId = 0;
        }
    }

    /// <summary>
    /// Whether a failed save was a unique index refusing the row.
    /// <para>
    /// The handlers ask first, so reaching here means two requests raced between the check and the write —
    /// the one case a read-then-write cannot cover, and the reason the answer is a 409 rather than a 500
    /// with a constraint name in it.
    /// </para>
    /// </summary>
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException sql && DuplicateKeyErrors.Contains(sql.Number);
}
