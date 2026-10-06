using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Catalog.Admin.Products.Abstractions;

/// <summary>
/// Write port for a product and everything that hangs off it (ADM-10, W1-03): the colourways, the variant
/// matrix, the media links, the extra category links and the related picks.
/// <para>
/// Shaped like <see cref="ICatalogStructureStore"/> and for the same reasons: it hands back tracked domain
/// entities, it validates nothing, and one call commits the whole unit of work. A product save touches
/// nine tables, and a save that commits as it goes has a half-applied state that no screen shows as wrong
/// — a product with its new variants but its old price summary is a product the listing prices incorrectly
/// and nobody can tell by looking.
/// </para>
/// <para>
/// <b>Media is addressed by <see cref="MediaAsset.PublicId"/>, never by the internal id.</b> That is the
/// convention ADM-22 set, for the reason it states: an internal id is a sequential integer, and accepting
/// one on a write makes enumeration the key to attaching somebody else's asset. The resolution to internal
/// ids happens in <see cref="LoadReferencesAsync"/>, which is also where an asset's type, status and
/// visibility are read so a gallery can refuse a video and a product tile can refuse an asset that is
/// still being processed.
/// </para>
/// </summary>
public interface IProductWriteStore
{
    /// <summary>
    /// One live product loaded for update with its colourways, variants, media (and each media row's
    /// asset), category links and related picks, or <c>null</c> when no live product has that id.
    /// <para>
    /// The media rows' assets are loaded because the publish checklist of doc 11 §7 G-01 asks whether every
    /// public image has alt text, and a product media row may take its alt text from the library asset
    /// rather than override it. Without the asset the checklist would have to insist on an override, which
    /// would refuse to publish a product whose library alt text was already right.
    /// </para>
    /// </summary>
    Task<Product?> FindProductAsync(int productId, CancellationToken cancellationToken);

    /// <summary><c>UX_Products_Slug</c> (filtered <c>IsDeleted = 0</c>).</summary>
    Task<bool> ProductSlugTakenAsync(string slug, int? exceptProductId, CancellationToken cancellationToken);

    /// <summary><c>UX_Products_ProductCode</c> (filtered <c>IsDeleted = 0</c>).</summary>
    Task<bool> ProductCodeTakenAsync(string productCode, int? exceptProductId, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="skus"/> already belong to a variant of another product.
    /// <para>
    /// <b>Unfiltered, unlike the slug and the code.</b> <c>UX_ProductVariants_Sku</c> has no soft-delete
    /// filter and the table has no <c>IsDeleted</c> column, so a SKU is unique across every variant ever
    /// created (doc 08 §3.2) — the variants of a removed product included. Checking only live products
    /// would tell the admin a SKU was free and then hand them a 409 from the index.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> SkusTakenAsync(
        IReadOnlyCollection<string> skus,
        int? exceptProductId,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="SkusTakenAsync"/>
    Task<IReadOnlyList<string>> BarcodesTakenAsync(
        IReadOnlyCollection<string> barcodes,
        int? exceptProductId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The reference rows a product form names, so a mistyped id is a named 400 rather than a foreign-key
    /// violation arriving as a 500 — and so the derived search text can be built from the real names.
    /// </summary>
    Task<ProductReferenceData> LoadReferencesAsync(
        ProductReferenceRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Which of this product's variants may not be hard-deleted, and why.
    /// <para>
    /// Two reasons, and they are different facts about the same row. <b>An order line names it</b>
    /// (doc 08 §3.2: "Deactivate once ordered; never delete", doc 11 P1's "Removing a variant that has
    /// orders -&gt; offered as 'deactivate' instead"). Or <b>it holds stock</b>: deleting it would take a
    /// count of real garments in a real warehouse with it, and stock belongs to ADM-12 and its ledger, not
    /// to a product form. In both cases the answer is to switch the variant off.
    /// </para>
    /// </summary>
    Task<VariantDeleteGuards> VariantDeleteGuardsAsync(int productId, CancellationToken cancellationToken);

    /// <summary>
    /// Brings <c>media.MediaUsages</c> for this product in line with <paramref name="desired"/>, enlisting
    /// the inserts and deletes in the same unit of work rather than saving them.
    /// <para>
    /// <b>Why this is not optional.</b> <c>ProductMedia</c> has a real foreign key to <c>MediaAssets</c>, so
    /// a gallery image cannot be hard-deleted from under a product — but the media library's own delete is
    /// a <i>soft</i> delete guarded by the usage list (doc 08 §3.3), and a soft-deleted asset stops being
    /// served. Without a usage row, a staff member tidying the library can take the picture off a live
    /// product page and the API will tell them it was unused. ADM-22 records the same argument for the
    /// shop's logo.
    /// </para>
    /// </summary>
    Task SyncProductMediaUsagesAsync(
        int productId,
        IReadOnlyCollection<ProductMediaUsage> desired,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a stock row with on-hand 0 for every variant of this product that has none, enlisting the
    /// inserts in the same unit of work (doc 11 P1 step 10).
    /// <para>
    /// Called after the variants have their identity values. A variant with no <c>StockLevels</c> row is
    /// invisible to the availability join that <c>ProductCatalogReader</c> uses, so it would read as out of
    /// stock for ever and ADM-12 would have nothing to update.
    /// </para>
    /// </summary>
    Task EnsureStockRowsAsync(int productId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a product and the whole graph hanging off it in one transaction, then calls
    /// <paramref name="afterInsert"/> with the identity values assigned and before the commit — which is
    /// where the audit row naming the new id, the <c>MediaUsages</c> rows and the stock rows are enlisted.
    /// </summary>
    /// <param name="afterInsert">
    /// Called inside the transaction once the ids exist, and called again from the start on every retry:
    /// the implementation clears the change tracker between attempts, because a retry on a context still
    /// holding the failed attempt's edits is worse than no retry (the W1-13 lesson, as
    /// <c>CatalogStructureStore.AddCategoryAsync</c> records it).
    /// </param>
    Task<CatalogSaveOutcome> AddProductAsync(
        Product product,
        Func<Product, CancellationToken, Task> afterInsert,
        int? actingUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks variants for deletion, enlisting the deletes in the same unit of work.
    /// <para>
    /// <b>Why this cannot be "take it out of the collection".</b> <c>ProductVariants to Products</c> is
    /// <c>NO ACTION</c>, not a cascade (doc 08 §1.6 lists the cascades and variants are not among
    /// them), so removing a variant from <see cref="Product.Variants"/> severs a <i>required</i>
    /// relationship and EF Core refuses the save: the association between a product and a variant "has
    /// been severed". It is the one place in this aggregate where the orphan is not deleted for us, and
    /// no test with an in-memory store can see it -- this exact save answered <b>500</b> against real SQL
    /// Server while 1,312 unit tests and 42 browser tests were green.
    /// </para>
    /// </summary>
    Task RemoveVariantsAsync(
        IReadOnlyCollection<ProductVariant> variants,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits everything the handler changed, stamping <paramref name="actingUserId"/> into the audit
    /// columns of doc 08 §1.5 [A].
    /// </summary>
    Task<CatalogSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>
/// One row of <c>media.MediaUsages</c> a product wants to own: the asset and the slot it fills
/// (<see cref="MediaUsageRoles"/>).
/// </summary>
public sealed record ProductMediaUsage(int MediaAssetId, string UsageRole);

/// <summary>
/// The variants of one product that may not be hard-deleted (see
/// <see cref="IProductWriteStore.VariantDeleteGuardsAsync"/>).
/// </summary>
/// <param name="Ordered">An order line names it.</param>
/// <param name="HoldingStock">
/// <c>inventory.StockLevels</c> records some on hand, or some reserved.
/// <para>
/// <b>And a forward note for ADM-12.</b> <c>inventory.InventoryMovements</c> does not exist yet. When it
/// does it is append-only and never deleted (doc 08 §1.6), so a variant with a movement row will not be
/// deletable either and this guard will need a third set. Until then the only foreign key in the way is
/// <c>FK_StockLevels_ProductVariants_VariantId</c>, which is <c>NO ACTION</c> — a DELETE through it is the
/// 500 this whole pair of members exists because of.
/// </para>
/// </param>
public sealed record VariantDeleteGuards(IReadOnlySet<int> Ordered, IReadOnlySet<int> HoldingStock)
{
    /// <summary>Nothing is guarded, which is every variant of a product that is being created.</summary>
    public static VariantDeleteGuards None { get; } =
        new(new HashSet<int>(), new HashSet<int>());
}

/// <summary>Which reference rows a handler needs looked up (see <see cref="IProductWriteStore.LoadReferencesAsync"/>).</summary>
/// <param name="MediaAssetPublicIds">Gallery, hover, video and swatch assets, by their public token.</param>
/// <param name="RelatedProductIds">The merchandiser's picks, which must be live products other than this one.</param>
public sealed record ProductReferenceRequest(
    int DepartmentId,
    IReadOnlyCollection<int> CategoryIds,
    int? AgeGroupId,
    IReadOnlyCollection<int> AttributeOptionIds,
    IReadOnlyCollection<int> ColourFamilyIds,
    IReadOnlyCollection<int> SizeIds,
    int? SizeGuideId,
    IReadOnlyCollection<Guid> MediaAssetPublicIds,
    IReadOnlyCollection<int> RelatedProductIds);

/// <summary>A category row as the product rules read it.</summary>
/// <param name="PathNames">
/// This category's name preceded by every ancestor's, root first — "Clothing", "Dresses", "Midi". Doc 08
/// §3.2 puts "category path names" in <c>ProductSearchIndex.SearchText</c>, so a shopper searching for
/// "dresses" finds a product filed under Midi; reconstructing them from <paramref name="Path"/> needs the
/// ancestors, which only the store can read.
/// </param>
public sealed record CategoryReference(
    int Id,
    int DepartmentId,
    string Name,
    string Path,
    bool IsActive,
    IReadOnlyList<string> PathNames);

/// <summary>A media asset as the product rules read it: enough to refuse one that would render as nothing.</summary>
public sealed record MediaAssetReference(
    int Id,
    Guid PublicId,
    MediaAssetType MediaType,
    MediaAssetStatus Status,
    MediaVisibility Visibility,
    string? AltText);

/// <summary>A size row: the scale and the label, both of which go into the search text.</summary>
public sealed record SizeReference(int Id, string ScaleName, string Label, short SortOrder);

/// <summary>A related pick as the rules read it.</summary>
public sealed record RelatedProductReference(int Id, string Name, string ProductCode, bool IsPublished);

/// <summary>
/// What <see cref="IProductWriteStore.LoadReferencesAsync"/> found. Anything the request asked for and this
/// does not contain does not exist, which is what turns a mistyped id into a named field error.
/// </summary>
/// <param name="DepartmentSlug">
/// Needed for one rule only, and it is doc 08 §3.2's: <c>AgeGroupId</c> is "required for Kids (app)". The
/// department a product belongs to is identified by its slug rather than its id, because an id is whatever
/// the seeder happened to assign on this database and a slug is in the URL.
/// </param>
/// <param name="AttributeOptions">
/// Id to (type, name). The <i>type</i> is the point: <c>MaterialId</c>, <c>FitId</c> and <c>OccasionId</c>
/// are all foreign keys into the same table, so without it a fit could be stored as a material and doc 08
/// §3.2's "type-checked in app" would be a comment rather than a rule.
/// </param>
public sealed record ProductReferenceData(
    string? DepartmentName,
    string? DepartmentSlug,
    IReadOnlyDictionary<int, CategoryReference> Categories,
    bool AgeGroupExists,
    IReadOnlyDictionary<int, (AttributeOptionType Type, string Name)> AttributeOptions,
    IReadOnlyDictionary<int, string> ColourFamilies,
    IReadOnlyDictionary<int, SizeReference> Sizes,
    bool SizeGuideExists,
    IReadOnlyDictionary<Guid, MediaAssetReference> MediaAssets,
    IReadOnlyDictionary<int, RelatedProductReference> RelatedProducts);
