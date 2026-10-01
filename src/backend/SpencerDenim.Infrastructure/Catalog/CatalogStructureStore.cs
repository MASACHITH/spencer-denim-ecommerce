using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// The EF Core side of <see cref="ICatalogStructureStore"/> (ADM-11, W1-12).
/// <para>
/// It hands out tracked entities and keeps one unit of work, so the handlers decide what a change is and
/// this class decides only how it reaches SQL Server. The two judgements it does make are the two the
/// database alone can make: a row somebody else changed first, and a unique index.
/// </para>
/// </summary>
internal sealed class CatalogStructureStore(AppDbContext dbContext, AuditAndSoftDeleteInterceptor auditColumns)
    : ICatalogStructureStore
{
    /// <summary>SQL Server: 2627 is a unique-constraint violation, 2601 a unique-index one.</summary>
    private static readonly int[] DuplicateKeyErrors = [2601, 2627];

    public Task<Department?> FindDepartmentAsync(int departmentId, CancellationToken cancellationToken) =>
        dbContext.Departments.FirstOrDefaultAsync(department => department.Id == departmentId, cancellationToken);

    public async Task<IReadOnlyList<Department>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
        await dbContext.Departments
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .ToListAsync(cancellationToken);

    public Task<bool> DepartmentSlugTakenAsync(string slug, int exceptDepartmentId, CancellationToken cancellationToken) =>
        dbContext.Departments
            .AnyAsync(
                department => department.Id != exceptDepartmentId && department.Slug == slug,
                cancellationToken);

    public Task<bool> DepartmentNameTakenAsync(string name, int exceptDepartmentId, CancellationToken cancellationToken) =>
        dbContext.Departments
            .AnyAsync(
                department => department.Id != exceptDepartmentId && department.Name == name,
                cancellationToken);

    public Task<Category?> FindCategoryAsync(int categoryId, CancellationToken cancellationToken) =>
        dbContext.Categories.FirstOrDefaultAsync(category => category.Id == categoryId, cancellationToken);

    public async Task<IReadOnlyList<Category>> ListSiblingsAsync(
        int departmentId,
        int? parentCategoryId,
        CancellationToken cancellationToken) =>
        await dbContext.Categories
            .Where(category =>
                category.DepartmentId == departmentId && category.ParentCategoryId == parentCategoryId)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Category>> ListSubtreeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // IX_Categories_Path (doc 08 §3.2). The trailing slash on `path` is what keeps "/1/" from
        // collecting "/12/" — see CategoryPath.
        return await dbContext.Categories
            .Where(category => category.Path.StartsWith(path))
            .OrderBy(category => category.Depth)
            .ThenBy(category => category.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> CategorySlugTakenAsync(
        int departmentId,
        int? parentCategoryId,
        string slug,
        int? exceptCategoryId,
        CancellationToken cancellationToken) =>
        dbContext.Categories
            .AnyAsync(
                category =>
                    category.DepartmentId == departmentId
                    && category.ParentCategoryId == parentCategoryId
                    && category.Slug == slug
                    && (exceptCategoryId == null || category.Id != exceptCategoryId),
                cancellationToken);

    public async Task<CategoryLinkCounts> CountLinksAsync(int categoryId, CancellationToken cancellationToken)
    {
        // Counted through the Products set, not through ProductCategories alone: that join table carries no
        // soft-delete filter, so counting its rows would hold the removal shut because of a product nobody
        // can see or un-link (doc 08 §10.2).
        var linkedProducts = await dbContext.ProductCategories
            .CountAsync(
                link => link.CategoryId == categoryId
                    && dbContext.Products.Any(product => product.Id == link.ProductId),
                cancellationToken);

        var childCategories = await dbContext.Categories
            .CountAsync(category => category.ParentCategoryId == categoryId, cancellationToken);

        return new CategoryLinkCounts(linkedProducts, childCategories);
    }

    public Task<bool> AgeGroupExistsAsync(int ageGroupId, CancellationToken cancellationToken) =>
        dbContext.AgeGroups.AnyAsync(ageGroup => ageGroup.Id == ageGroupId, cancellationToken);

    public Task<bool> SizeGuideExistsAsync(int sizeGuideId, CancellationToken cancellationToken) =>
        dbContext.SizeGuides.AnyAsync(sizeGuide => sizeGuide.Id == sizeGuideId, cancellationToken);

    public Task<bool> ReadyImageAssetExistsAsync(int assetId, CancellationToken cancellationToken) =>
        dbContext.MediaAssets.AnyAsync(
            asset =>
                asset.Id == assetId
                && asset.MediaType == MediaAssetType.Image
                && asset.Status == MediaAssetStatus.Ready
                && asset.Visibility == MediaVisibility.Public,
            cancellationToken);

    public async Task<CatalogSaveOutcome> AddCategoryAsync(
        Category category,
        string? parentPath,
        Action<Category> enlistAuditRow,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(enlistAuditRow);

        auditColumns.CurrentUserId = actingUserId;

        // The path needs the identity value the insert assigns, so this is two statements, and they have to
        // be one transaction: a failure between them leaves a row whose Path is a placeholder, which no
        // screen shows as wrong and which makes the storefront's `LIKE path + '%'` subtree predicate answer
        // with the wrong products for ever.
        //
        // EnableRetryOnFailure is on (doc 08 §10.3), and its execution strategy refuses a user-initiated
        // transaction unless the whole unit of work is inside it — so the *attempt* is what retries, and
        // each attempt starts from a clean change tracker. A retry on a context still holding the failed
        // attempt's edits is worse than no retry (the W1-13 lesson).
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            dbContext.ChangeTracker.Clear();

            // The previous attempt may have got as far as being given an identity value and a row version.
            // Re-adding the entity with either still set would be an explicit-identity insert, which SQL
            // Server refuses.
            category.Id = 0;
            category.RowVersion = null;
            category.Path = string.Empty;

            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

            dbContext.Categories.Add(category);

            try
            {
                await dbContext.SaveChangesAsync(token);

                category.Path = CategoryPath.Build(parentPath, category.Id);
                category.Depth = CategoryPath.DepthOf(category.Path);

                enlistAuditRow(category);

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
    /// Whether a failed save was a unique index refusing the row.
    /// <para>
    /// The handlers ask first, so reaching here means two requests raced between the check and the write —
    /// the one case a read-then-write cannot cover, and the reason the answer is 409 rather than a 500 with
    /// a constraint name in it.
    /// </para>
    /// </summary>
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException sql && DuplicateKeyErrors.Contains(sql.Number);
}
