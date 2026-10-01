using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Abstractions;

/// <summary>
/// Write port for the admin catalogue structure (ADM-11, W1-12): departments and the category tree.
/// <para>
/// It hands back tracked domain entities and takes one <see cref="SaveAsync"/> for the whole unit of
/// work, so a use case that touches several rows — a reorder, or a move that re-paths a subtree — is one
/// transaction rather than a row at a time. That is doc 08 §10.1's "<c>Categories.Path/Depth</c> …
/// maintained by the move use case (one transaction)" and the W1-13 lesson in a new place: a multi-row
/// change that commits as it goes has a half-applied state, and for a materialised path the half-applied
/// state is a tree the storefront's subtree predicate answers wrongly without complaining.
/// </para>
/// <para>
/// Nothing here validates. Every rule — the slug spelling, the depth bound, the sibling-slug clash, the
/// remove guard — lives in the Application handlers, where it can be unit-tested and where a failure is a
/// named field in a 400 rather than an exception. The port's only judgements are the two the database
/// alone can make: a row somebody else changed first, and a unique index.
/// </para>
/// </summary>
public interface ICatalogStructureStore
{
    /// <summary>One department for update, or <c>null</c>.</summary>
    Task<Department?> FindDepartmentAsync(int departmentId, CancellationToken cancellationToken);

    /// <summary>Every department for update, in sort order then id.</summary>
    Task<IReadOnlyList<Department>> ListDepartmentsAsync(CancellationToken cancellationToken);

    /// <summary><c>UX_Departments_Slug</c>, asked before the insert so the answer can name the field.</summary>
    Task<bool> DepartmentSlugTakenAsync(string slug, int exceptDepartmentId, CancellationToken cancellationToken);

    /// <summary><c>UX_Departments_Name</c>.</summary>
    Task<bool> DepartmentNameTakenAsync(string name, int exceptDepartmentId, CancellationToken cancellationToken);

    /// <summary>One live category for update, or <c>null</c> when it does not exist or is soft-deleted.</summary>
    Task<Category?> FindCategoryAsync(int categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// The live categories sharing one parent, for update, in sibling order then id. The root of a
    /// department is <paramref name="parentCategoryId"/> <c>null</c>.
    /// </summary>
    Task<IReadOnlyList<Category>> ListSiblingsAsync(
        int departmentId,
        int? parentCategoryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every live category whose <see cref="Category.Path"/> starts with <paramref name="path"/>, for
    /// update — the category itself included. This is the subtree a move has to re-path.
    /// </summary>
    Task<IReadOnlyList<Category>> ListSubtreeAsync(string path, CancellationToken cancellationToken);

    /// <summary><c>UX_Categories_Dept_Parent_Slug</c> (filtered <c>IsDeleted = 0</c>).</summary>
    Task<bool> CategorySlugTakenAsync(
        int departmentId,
        int? parentCategoryId,
        string slug,
        int? exceptCategoryId,
        CancellationToken cancellationToken);

    /// <summary>What still points at a category, for the remove guard (doc 08 §3.2, doc 11 P1 step 5).</summary>
    Task<CategoryLinkCounts> CountLinksAsync(int categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the referenced rows exist, so a bad id is a named 400 instead of a foreign-key violation
    /// surfacing as a 500.
    /// </summary>
    Task<bool> AgeGroupExistsAsync(int ageGroupId, CancellationToken cancellationToken);

    /// <inheritdoc cref="AgeGroupExistsAsync"/>
    Task<bool> SizeGuideExistsAsync(int sizeGuideId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a media asset exists, is an image and is <c>Ready</c>. A category tile pointed at a video,
    /// or at an asset still being processed, renders as nothing — so it is refused on the way in.
    /// </summary>
    Task<bool> ReadyImageAssetExistsAsync(int assetId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a category and fills in its materialised <see cref="Category.Path"/>, which needs the
    /// identity value the insert itself assigns, <b>both statements in one transaction</b>. Without the
    /// transaction a failure between them leaves a row whose path is a placeholder, and a wrong path is
    /// not a visible defect: the row reads correctly on every screen and the storefront's subtree
    /// predicate quietly returns the wrong products.
    /// <para>
    /// <paramref name="enlistAuditRow"/> is called once the identity value and the path are known and
    /// before the commit, so the audit row naming the new category's id is part of the same transaction.
    /// It is called again on every retry: the implementation starts each attempt from a clean change
    /// tracker, because a retry on a context still holding the failed attempt's edits is worse than no
    /// retry (the W1-13 lesson).
    /// </para>
    /// </summary>
    Task<CatalogSaveOutcome> AddCategoryAsync(
        Category category,
        string? parentPath,
        Action<Category> enlistAuditRow,
        int? actingUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits everything the handler changed, stamping <paramref name="actingUserId"/> into the audit
    /// columns of doc 08 §1.5 [A].
    /// </summary>
    Task<CatalogSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>What still references a category (doc 08 §3.2's soft-delete guard).</summary>
/// <param name="LinkedProducts">
/// Non-deleted products linked through <c>catalog.ProductCategories</c>, published or not, the primary
/// link included.
/// <para>
/// <b>Not just published ones</b>, and the reason matters: <c>Products.PrimaryCategoryId</c> is NOT
/// NULL, so removing a category under an unpublished product would leave that product pointing at a
/// soft-deleted row. It would then be unpublishable-but-broken — invisible on the storefront with no
/// explanation on the product form. Doc 11 P1 step 5 says "active products"; a product that is not
/// deleted is the safe reading of it.
/// </para>
/// </param>
/// <param name="ChildCategories">Live child categories, direct children only — one level is enough to refuse.</param>
public sealed record CategoryLinkCounts(int LinkedProducts, int ChildCategories);

/// <summary>What the database made of a write.</summary>
public enum CatalogSaveOutcome
{
    /// <summary>Committed.</summary>
    Saved,

    /// <summary>
    /// Nothing was different, so no statement was issued: the row version does not move and no audit
    /// row is written. A save of an untouched form is not an event.
    /// </summary>
    NothingToSave,

    /// <summary>
    /// Somebody else changed one of these rows first (<c>RowVersion</c>, doc 08 §1.5 [RV]). Nothing was
    /// written; the caller reloads and re-applies.
    /// </summary>
    ConcurrencyConflict,

    /// <summary>
    /// A unique index refused the write — <c>UX_Categories_Dept_Parent_Slug</c>,
    /// <c>UX_Departments_Slug</c> or <c>UX_Departments_Name</c>. The handler checks first so this is the
    /// race, not the ordinary case.
    /// </summary>
    DuplicateKey,
}
