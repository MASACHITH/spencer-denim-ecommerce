using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin;

/// <summary>
/// An in-memory <see cref="ICatalogStructureStore"/> for the handler tests.
/// <para>
/// <b>It is deliberately not more forgiving than the real one.</b> MEMORY's lesson is that a stub shaped
/// more politely than the real API tests nothing, so this one keeps the behaviours the handlers actually
/// depend on: entities are the same instances the handler mutates (as EF's tracked ones are), the subtree
/// and sibling queries use the same predicates, the slug check honours the real unique index's shape
/// (department + parent + slug, among live rows), and a soft-deleted category leaves every query on the
/// next save the way the global query filter makes it leave the real ones.
/// </para>
/// <para>
/// What it cannot model is the three outcomes only SQL Server decides: a lost concurrency race, a unique
/// index firing, and "nothing was different so no statement was issued". Those are forced with
/// <see cref="NextSaveOutcome"/> here and proved for real against SQL Server in
/// <c>AdminCatalogEndpointTests</c>.
/// </para>
/// </summary>
internal sealed class FakeCatalogStructureStore : ICatalogStructureStore
{
    private int _nextCategoryId = 1000;

    public List<Department> Departments { get; } = [];

    public List<Category> Categories { get; } = [];

    /// <summary>Soft-deleted categories, kept so a test can assert they are invisible to every query.</summary>
    public List<Category> Removed { get; } = [];

    public Dictionary<int, CategoryLinkCounts> Links { get; } = [];

    public HashSet<int> AgeGroupIds { get; } = [];

    public HashSet<int> SizeGuideIds { get; } = [];

    public HashSet<int> ReadyImageAssetIds { get; } = [];

    /// <summary>Forces the next save's answer, standing in for a race only the database can lose.</summary>
    public CatalogSaveOutcome? NextSaveOutcome { get; set; }

    public int Saves { get; private set; }

    public Task<Department?> FindDepartmentAsync(int departmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Departments.FirstOrDefault(department => department.Id == departmentId));

    public Task<IReadOnlyList<Department>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Department>>(
            Departments.OrderBy(department => department.SortOrder).ThenBy(department => department.Id).ToList());

    public Task<Category?> FindCategoryAsync(int categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(Categories.FirstOrDefault(category => category.Id == categoryId));

    public Task<IReadOnlyList<Category>> ListSiblingsAsync(
        int departmentId,
        int? parentCategoryId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Category>>(Categories
            .Where(category =>
                category.DepartmentId == departmentId && category.ParentCategoryId == parentCategoryId)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .ToList());

    public Task<IReadOnlyList<Category>> ListSubtreeAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Category>>(Categories
            .Where(category => category.Path.StartsWith(path, StringComparison.Ordinal))
            .OrderBy(category => category.Depth)
            .ThenBy(category => category.Id)
            .ToList());

    public Task<bool> CategorySlugTakenAsync(
        int departmentId,
        int? parentCategoryId,
        string slug,
        int? exceptCategoryId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Categories.Any(category =>
            category.DepartmentId == departmentId
            && category.ParentCategoryId == parentCategoryId
            && string.Equals(category.Slug, slug, StringComparison.OrdinalIgnoreCase)
            && category.Id != exceptCategoryId));

    public Task<CategoryLinkCounts> CountLinksAsync(int categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(Links.TryGetValue(categoryId, out var counts) ? counts : new CategoryLinkCounts(0, 0));

    public Task<bool> AgeGroupExistsAsync(int ageGroupId, CancellationToken cancellationToken) =>
        Task.FromResult(AgeGroupIds.Contains(ageGroupId));

    public Task<bool> SizeGuideExistsAsync(int sizeGuideId, CancellationToken cancellationToken) =>
        Task.FromResult(SizeGuideIds.Contains(sizeGuideId));

    public Task<bool> ReadyImageAssetExistsAsync(int assetId, CancellationToken cancellationToken) =>
        Task.FromResult(ReadyImageAssetIds.Contains(assetId));

    /// <summary>
    /// Asset ids the locked re-check of the next media save should find unusable (#223): a picture a
    /// media-library delete got to first. Only SQL Server can decide that race; the real proof is in
    /// <c>CatalogMediaLockRaceTests</c>.
    /// </summary>
    public HashSet<int> UnusableAssetIds { get; } = [];

    /// <summary>The claim the last media save was handed, and the owner id it was for.</summary>
    public (int OwnerId, CatalogMediaClaim Claim)? LastMediaSave { get; private set; }

    /// <summary>The usage rows each owner holds after its saves, keyed by (owner type, owner id).</summary>
    public Dictionary<(string OwnerType, int OwnerId), List<CatalogMediaSlot>> Usages { get; } = [];

    private bool Refuses(int ownerId, CatalogMediaClaim media)
    {
        LastMediaSave = (ownerId, media);
        media.Unavailable = media.Slots.Where(slot => UnusableAssetIds.Contains(slot.MediaAssetId)).ToList();
        return media.Unavailable.Count > 0;
    }

    private void RecordUsages(int ownerId, CatalogMediaClaim media)
    {
        if (media.UsageOwnerType is { } ownerType)
        {
            Usages[(ownerType, ownerId)] = media.Slots.ToList();
        }
    }

    public Task<CatalogSaveOutcome> AddCategoryAsync(
        Category category,
        string? parentPath,
        Action<Category> enlistAuditRow,
        CatalogMediaClaim media,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (NextSaveOutcome is { } forced)
        {
            NextSaveOutcome = null;
            return Task.FromResult(forced);
        }

        if (Refuses(0, media))
        {
            return Task.FromResult(CatalogSaveOutcome.MediaUnavailable);
        }

        category.Id = _nextCategoryId++;
        category.Path = CategoryPath.Build(parentPath, category.Id);
        category.Depth = CategoryPath.DepthOf(category.Path);
        category.RowVersion = BitConverter.GetBytes((long)category.Id);
        Categories.Add(category);

        // The real store calls this inside its transaction, once the identity value exists.
        enlistAuditRow(category);
        LastMediaSave = (category.Id, media);
        RecordUsages(category.Id, media);

        Saves++;
        return Task.FromResult(CatalogSaveOutcome.Saved);
    }

    public Task<CatalogSaveOutcome> SaveWithMediaAsync(
        int ownerId,
        CatalogMediaClaim media,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (NextSaveOutcome is null)
        {
            if (Refuses(ownerId, media))
            {
                return Task.FromResult(CatalogSaveOutcome.MediaUnavailable);
            }

            RecordUsages(ownerId, media);
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

        Saves++;

        // A soft-deleted category leaves every query, the way the global filter makes it leave the real ones.
        foreach (var deleted in Categories.Where(category => category.IsDeleted).ToList())
        {
            Categories.Remove(deleted);
            Removed.Add(deleted);
        }

        return Task.FromResult(CatalogSaveOutcome.Saved);
    }

    /// <summary>Adds a department with a row version a test can hand back as its token.</summary>
    public Department AddDepartment(int id, string name, string slug, short sortOrder = 0, bool isActive = true)
    {
        var department = new Department
        {
            Id = id,
            Name = name,
            Slug = slug,
            SortOrder = sortOrder,
            IsActive = isActive,
            RowVersion = BitConverter.GetBytes((long)id),
        };

        Departments.Add(department);
        return department;
    }

    /// <summary>Adds a category, deriving its path and depth from its parent the way the real store does.</summary>
    public Category AddCategory(
        int id,
        int departmentId,
        string name,
        string slug,
        int? parentCategoryId = null,
        int sortOrder = 0,
        bool isActive = true)
    {
        var parent = parentCategoryId is { } parentId
            ? Categories.First(candidate => candidate.Id == parentId)
            : null;

        var category = new Category
        {
            Id = id,
            DepartmentId = departmentId,
            ParentCategoryId = parentCategoryId,
            Name = name,
            Slug = slug,
            SortOrder = sortOrder,
            IsActive = isActive,
            Path = CategoryPath.Build(parent?.Path, id),
            RowVersion = BitConverter.GetBytes((long)id),
        };

        category.Depth = CategoryPath.DepthOf(category.Path);
        Categories.Add(category);
        return category;
    }

    /// <summary>The base64 token the admin read model would have sent for a row.</summary>
    public static string Token(byte[]? rowVersion) => Convert.ToBase64String(rowVersion!);
}

/// <summary>Collects the audit rows a handler enlists, so a test can assert what the trail would say.</summary>
internal sealed class RecordingAuditLogWriter : IAuditLogWriter
{
    public List<AuditLog> Enlisted { get; } = [];

    public List<AuditLog> Written { get; } = [];

    public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        Written.Add(entry);
        return Task.CompletedTask;
    }

    public void Enlist(AuditLog entry) => Enlisted.Add(entry);
}

/// <summary>A clock that does not move.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}
