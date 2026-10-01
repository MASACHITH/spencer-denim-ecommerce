using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// Field and reference rules shared by creating and editing a category, so the two cannot drift apart —
/// a create that accepts a value the edit refuses is a row an admin can make and then never save again.
/// </summary>
internal static class CategoryWriteRules
{
    public static void CheckFields(
        ICollection<ValidationError> errors,
        string? name,
        string? slug,
        string? description,
        string? bannerLinkUrl,
        string? seoTitle,
        string? seoDescription,
        byte? megaMenuColumn)
    {
        CatalogFieldRules.RequireText(errors, "name", name, CatalogFieldRules.CategoryNameMaxLength, "The name");
        CatalogFieldRules.RequireSlug(errors, "slug", slug, CatalogFieldRules.CategorySlugMaxLength);
        CatalogFieldRules.LimitText(
            errors, "description", description, CatalogFieldRules.CategoryDescriptionMaxLength, "The description");
        CatalogFieldRules.CheckBannerLink(errors, "bannerLinkUrl", bannerLinkUrl);
        CatalogFieldRules.LimitText(errors, "seoTitle", seoTitle, CatalogFieldRules.SeoTitleMaxLength, "The SEO title");
        CatalogFieldRules.LimitText(
            errors, "seoDescription", seoDescription, CatalogFieldRules.SeoDescriptionMaxLength, "The SEO description");
        CatalogFieldRules.CheckMegaMenuColumn(errors, "megaMenuColumn", megaMenuColumn);
    }

    /// <summary>
    /// Checks the ids that are foreign keys. Without this a mistyped id is a <c>DbUpdateException</c> out of
    /// <c>SaveChanges</c> naming a constraint, which reaches the admin as a 500 and tells them nothing.
    /// </summary>
    public static async Task CheckReferencesAsync(
        ICollection<ValidationError> errors,
        ICatalogStructureStore store,
        int? imageAssetId,
        int? bannerAssetId,
        int? ageGroupId,
        int? sizeGuideId,
        CancellationToken cancellationToken)
    {
        if (imageAssetId is { } image && !await store.ReadyImageAssetExistsAsync(image, cancellationToken))
        {
            errors.Add(new ValidationError(
                "imageAssetId", "That tile image is not in the media library, or is not a ready image."));
        }

        if (bannerAssetId is { } banner && !await store.ReadyImageAssetExistsAsync(banner, cancellationToken))
        {
            errors.Add(new ValidationError(
                "bannerAssetId", "That banner image is not in the media library, or is not a ready image."));
        }

        if (ageGroupId is { } ageGroup && !await store.AgeGroupExistsAsync(ageGroup, cancellationToken))
        {
            errors.Add(new ValidationError("ageGroupId", "That age group does not exist."));
        }

        if (sizeGuideId is { } sizeGuide && !await store.SizeGuideExistsAsync(sizeGuide, cancellationToken))
        {
            errors.Add(new ValidationError("sizeGuideId", "That size guide does not exist."));
        }
    }

    /// <summary>
    /// The fields an audit diff is taken over. The materialised path and depth are in it because a move
    /// changes them and nothing else on any screen would show that it had.
    /// </summary>
    public static Dictionary<string, object?> Snapshot(Category category) => new(StringComparer.Ordinal)
    {
        ["name"] = category.Name,
        ["slug"] = category.Slug,
        ["description"] = category.Description,
        ["parentCategoryId"] = category.ParentCategoryId,
        ["path"] = category.Path,
        ["depth"] = category.Depth,
        ["sortOrder"] = category.SortOrder,
        ["imageAssetId"] = category.ImageAssetId,
        ["bannerAssetId"] = category.BannerAssetId,
        ["bannerLinkUrl"] = category.BannerLinkUrl,
        ["ageGroupId"] = category.AgeGroupId,
        ["sizeGuideId"] = category.SizeGuideId,
        ["showInMegaMenu"] = category.ShowInMegaMenu,
        ["megaMenuColumn"] = category.MegaMenuColumn,
        ["seoTitle"] = category.SeoTitle,
        ["seoDescription"] = category.SeoDescription,
    };

    /// <summary>The position a category takes when it joins a sibling list: the end of it.</summary>
    public static int NextSortOrder(IReadOnlyList<Category> siblings) =>
        siblings.Count == 0 ? 0 : siblings.Max(sibling => sibling.SortOrder) + 1;
}

/// <summary>Creates a category (ADM-11, doc 11 P1 steps 2–3).</summary>
public sealed class CreateCategoryCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<CreateCategoryCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        CategoryWriteRules.CheckFields(
            errors,
            command.Name,
            command.Slug,
            command.Description,
            command.BannerLinkUrl,
            command.SeoTitle,
            command.SeoDescription,
            command.MegaMenuColumn);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        if (await store.FindDepartmentAsync(command.DepartmentId, cancellationToken) is null)
        {
            return new CatalogWriteResult.Invalid(
                "departmentId",
                string.Create(CultureInfo.InvariantCulture, $"No department has the id {command.DepartmentId}."));
        }

        Category? parent = null;
        if (command.ParentCategoryId is { } parentId)
        {
            parent = await store.FindCategoryAsync(parentId, cancellationToken);
            if (parent is null)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    string.Create(CultureInfo.InvariantCulture, $"No category has the id {parentId}."));
            }

            if (parent.DepartmentId != command.DepartmentId)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    "The parent category is in another department. A category belongs to one department only.");
            }

            if (parent.Depth + 1 > Category.MaxDepth)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{parent.Name}' is already at level {parent.Depth}, and the tree allows " +
                        $"{Category.MaxDepth} levels (doc 08 CK_Categories_Depth)."));
            }
        }

        var slug = CatalogFieldRules.Clean(command.Slug)!;
        if (await store.CategorySlugTakenAsync(
            command.DepartmentId, command.ParentCategoryId, slug, null, cancellationToken))
        {
            errors.Add(new ValidationError("slug", "Another category in the same place already uses that URL slug."));
        }

        await CategoryWriteRules.CheckReferencesAsync(
            errors,
            store,
            command.ImageAssetId,
            command.BannerAssetId,
            command.AgeGroupId,
            command.SizeGuideId,
            cancellationToken);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var siblings = await store.ListSiblingsAsync(
            command.DepartmentId, command.ParentCategoryId, cancellationToken);

        var category = new Category
        {
            DepartmentId = command.DepartmentId,
            ParentCategoryId = command.ParentCategoryId,
            Depth = (byte)((parent?.Depth ?? 0) + 1),
            Name = CatalogFieldRules.Clean(command.Name)!,
            Slug = slug,
            Description = CatalogFieldRules.Clean(command.Description),
            ImageAssetId = command.ImageAssetId,
            BannerAssetId = command.BannerAssetId,
            BannerLinkUrl = CatalogFieldRules.Clean(command.BannerLinkUrl),
            AgeGroupId = command.AgeGroupId,
            SizeGuideId = command.SizeGuideId,
            SortOrder = CategoryWriteRules.NextSortOrder(siblings),
            ShowInMegaMenu = command.ShowInMegaMenu,
            MegaMenuColumn = command.MegaMenuColumn,

            // The column's own default (doc 08 §3.2). A new category is live, and whether it appears in the
            // mega menu is the admin's explicit choice on the same form (ShowInMegaMenu) rather than
            // something inferred here.
            IsActive = true,
            SeoTitle = CatalogFieldRules.Clean(command.SeoTitle),
            SeoDescription = CatalogFieldRules.Clean(command.SeoDescription),
        };

        var occurredAtUtc = clock.UtcNow.UtcDateTime;

        var outcome = await store.AddCategoryAsync(
            category,
            parent?.Path,

            // Called inside the store's transaction, once the identity value and the path exist, and again
            // on each retry — so the row always names the id that was really inserted.
            inserted => auditLog.Enlist(CatalogAuditTrail.Row(
                AuditActions.Create,
                CatalogAuditTrail.CategoryEntityType,
                inserted.Id.ToString(CultureInfo.InvariantCulture),
                before: null,
                after: CategoryWriteRules.Snapshot(inserted),
                command.Actor,
                occurredAtUtc)),
            command.Actor.UserId,
            cancellationToken);

        return CatalogWriteSupport.ToResult(outcome, category.Id);
    }
}

/// <summary>
/// Edits a category and, when its parent changes, moves it — re-pathing its whole subtree in the same
/// transaction (ADM-11, doc 11 P1 step 4, doc 08 §10.1).
/// </summary>
public sealed class UpdateCategoryCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<UpdateCategoryCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        CategoryWriteRules.CheckFields(
            errors,
            command.Name,
            command.Slug,
            command.Description,
            command.BannerLinkUrl,
            command.SeoTitle,
            command.SeoDescription,
            command.MegaMenuColumn);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var category = await store.FindCategoryAsync(command.CategoryId, cancellationToken);
        if (category is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No category has the id {command.CategoryId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(category.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var moving = command.ParentCategoryId != category.ParentCategoryId;
        Category? newParent = null;

        if (moving && command.ParentCategoryId is { } newParentId)
        {
            if (newParentId == category.Id)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId", "A category cannot be its own parent.");
            }

            newParent = await store.FindCategoryAsync(newParentId, cancellationToken);
            if (newParent is null)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    string.Create(CultureInfo.InvariantCulture, $"No category has the id {newParentId}."));
            }

            if (newParent.DepartmentId != category.DepartmentId)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    "A category cannot move to another department: its products belong to this one.");
            }

            // The cycle guard, and the reason the path carries a trailing slash: without it "/1/" would
            // appear to contain "/12/" and this would refuse a perfectly legal move.
            if (CategoryPath.IsWithin(newParent.Path, category.Path))
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    $"'{newParent.Name}' is inside '{category.Name}', so moving it there would make the tree " +
                    "a loop with no top.");
            }
        }

        // Read before anything is re-pathed: this is the set whose paths the move rewrites.
        var subtree = moving
            ? await store.ListSubtreeAsync(category.Path, cancellationToken)
            : [];

        if (moving)
        {
            var deepestBelow = subtree.Count == 0 ? category.Depth : subtree.Max(node => node.Depth);
            var newDepth = (byte)((newParent?.Depth ?? 0) + 1);
            var deepestAfter = newDepth + (deepestBelow - category.Depth);

            if (deepestAfter > Category.MaxDepth)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{category.Name}' has {deepestBelow - category.Depth} level(s) of categories under it, " +
                        $"so moving it there would make the tree {deepestAfter} levels deep and only " +
                        $"{Category.MaxDepth} are allowed."));
            }
        }

        var slug = CatalogFieldRules.Clean(command.Slug)!;
        if (await store.CategorySlugTakenAsync(
            category.DepartmentId, command.ParentCategoryId, slug, category.Id, cancellationToken))
        {
            errors.Add(new ValidationError("slug", "Another category in the same place already uses that URL slug."));
        }

        await CategoryWriteRules.CheckReferencesAsync(
            errors,
            store,
            command.ImageAssetId,
            command.BannerAssetId,
            command.AgeGroupId,
            command.SizeGuideId,
            cancellationToken);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var before = CategoryWriteRules.Snapshot(category);

        category.Name = CatalogFieldRules.Clean(command.Name)!;
        category.Slug = slug;
        category.Description = CatalogFieldRules.Clean(command.Description);
        category.ImageAssetId = command.ImageAssetId;
        category.BannerAssetId = command.BannerAssetId;
        category.BannerLinkUrl = CatalogFieldRules.Clean(command.BannerLinkUrl);
        category.AgeGroupId = command.AgeGroupId;
        category.SizeGuideId = command.SizeGuideId;
        category.ShowInMegaMenu = command.ShowInMegaMenu;
        category.MegaMenuColumn = command.MegaMenuColumn;
        category.SeoTitle = CatalogFieldRules.Clean(command.SeoTitle);
        category.SeoDescription = CatalogFieldRules.Clean(command.SeoDescription);

        var after = CategoryWriteRules.Snapshot(category);

        if (moving)
        {
            var oldPath = category.Path;
            var newPath = CategoryPath.Build(newParent?.Path, category.Id);

            // It joins the end of its new sibling list. Read before the parent is reassigned, and with this
            // category filtered out of it either way: its own sort order belongs to the list it is leaving,
            // and counting it would push the newcomer past the end of the list it is joining.
            //
            // The gap it leaves behind is harmless: sibling order is relative, and renumbering the old list
            // would be a second, unasked-for reorder of rows the admin did not touch — and another chance to
            // fight a concurrent reorder there.
            var newSiblings = (await store.ListSiblingsAsync(
                    category.DepartmentId, command.ParentCategoryId, cancellationToken))
                .Where(sibling => sibling.Id != category.Id)
                .ToList();

            category.ParentCategoryId = command.ParentCategoryId;
            category.SortOrder = CategoryWriteRules.NextSortOrder(newSiblings);

            // The whole subtree, the category itself included, in one unit of work. A half-rewritten path
            // is not a visible defect: every row reads correctly and the storefront's subtree predicate
            // silently answers with the wrong products.
            foreach (var node in subtree)
            {
                node.Path = CategoryPath.Rebase(node.Path, oldPath, newPath);
                node.Depth = CategoryPath.DepthOf(node.Path);
            }

            after["parentCategoryId"] = category.ParentCategoryId;
            after["path"] = category.Path;
            after["depth"] = category.Depth;
            after["sortOrder"] = category.SortOrder;
            after["subtreeCategoriesRepathed"] = subtree.Count;
        }

        var audit = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.CategoryEntityType,
            category.Id.ToString(CultureInfo.InvariantCulture),
            before,
            after,
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new CatalogWriteResult.Unchanged(category.Id);
        }

        auditLog.Enlist(audit);
        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            category.Id);
    }
}

/// <summary>
/// Reorders one parent's children (ADM-11, doc 11 P1 step 4). See <see cref="ReorderList"/> for why the
/// command is a whole list and what makes two admins reordering at once safe.
/// </summary>
public sealed class ReorderCategoriesCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<ReorderCategoriesCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(ReorderCategoriesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await store.FindDepartmentAsync(command.DepartmentId, cancellationToken) is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No department has the id {command.DepartmentId}."));
        }

        if (command.ParentCategoryId is { } parentId)
        {
            var parent = await store.FindCategoryAsync(parentId, cancellationToken);
            if (parent is null)
            {
                return new CatalogWriteResult.NotFound(
                    string.Create(CultureInfo.InvariantCulture, $"No category has the id {parentId}."));
            }

            if (parent.DepartmentId != command.DepartmentId)
            {
                return new CatalogWriteResult.Invalid(
                    "parentCategoryId",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Category {parentId} is not in department {command.DepartmentId}."));
            }
        }

        var siblings = await store.ListSiblingsAsync(
            command.DepartmentId, command.ParentCategoryId, cancellationToken);

        var check = ReorderList.Check(
            command.OrderedCategoryIds,
            siblings.Select(sibling => sibling.Id).ToList(),
            "orderedCategoryIds",
            "category");
        if (check is not null)
        {
            return check;
        }

        var requested = command.OrderedCategoryIds!;
        var byId = siblings.ToDictionary(sibling => sibling.Id);
        var previous = siblings.Select(sibling => sibling.Id).ToList();

        for (var position = 0; position < requested.Count; position++)
        {
            byId[requested[position]].SortOrder = position;
        }

        var scope = CatalogAuditTrail.OrderScopeId(command.DepartmentId, command.ParentCategoryId);
        var audit = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.CategoryOrderEntityType,
            scope,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = string.Join(',', previous) },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = string.Join(',', requested) },
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new CatalogWriteResult.Unchanged(command.ParentCategoryId ?? 0);
        }

        auditLog.Enlist(audit);
        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            command.ParentCategoryId ?? 0);
    }
}

/// <summary>
/// Switches a category on or off (ADM-11). Doc 08 §7.1: this is the admin's only switch for taking a
/// section off the shop, so it is its own command with its own <c>StatusChange</c> audit row and its own
/// confirmation on screen, fed by
/// <see cref="IAdminCatalogReader.GetDeactivationImpactAsync"/>.
/// </summary>
public sealed class SetCategoryActiveCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<SetCategoryActiveCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(SetCategoryActiveCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var category = await store.FindCategoryAsync(command.CategoryId, cancellationToken);
        if (category is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No category has the id {command.CategoryId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(category.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        if (category.IsActive == command.IsActive)
        {
            return new CatalogWriteResult.Unchanged(category.Id);
        }

        category.IsActive = command.IsActive;

        auditLog.Enlist(CatalogAuditTrail.Row(
            AuditActions.StatusChange,
            CatalogAuditTrail.CategoryEntityType,
            category.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isActive"] = !command.IsActive },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["isActive"] = command.IsActive,
                ["name"] = category.Name,
                ["path"] = category.Path,
            },
            command.Actor,
            clock.UtcNow.UtcDateTime));

        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            category.Id);
    }
}

/// <summary>
/// Removes a category (ADM-11's "remove"): a soft delete, guarded rather than cascaded (doc 08 §3.2,
/// doc 11 P1 step 5).
/// </summary>
public sealed class RemoveCategoryCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<RemoveCategoryCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(RemoveCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var category = await store.FindCategoryAsync(command.CategoryId, cancellationToken);
        if (category is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No category has the id {command.CategoryId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(category.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var links = await store.CountLinksAsync(category.Id, cancellationToken);
        if (links.ChildCategories > 0 || links.LinkedProducts > 0)
        {
            return new CatalogWriteResult.Conflict(
                CatalogConflictCodes.CategoryInUse,
                Refusal(category.Name, links),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["childCategories"] = links.ChildCategories,
                    ["linkedProducts"] = links.LinkedProducts,
                });
        }

        var occurredAtUtc = clock.UtcNow.UtcDateTime;
        category.SoftDelete(occurredAtUtc, command.Actor.UserId);

        auditLog.Enlist(CatalogAuditTrail.Row(
            AuditActions.SoftDelete,
            CatalogAuditTrail.CategoryEntityType,
            category.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isDeleted"] = false },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["isDeleted"] = true,
                ["name"] = category.Name,
                ["slug"] = category.Slug,
                ["path"] = category.Path,
            },
            command.Actor,
            occurredAtUtc));

        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            category.Id);
    }

    /// <summary>
    /// Names what is in the way, because "cannot be removed" without the reason leaves an admin clicking
    /// the same button again.
    /// </summary>
    private static string Refusal(string name, CategoryLinkCounts links)
    {
        var reasons = new List<string>(2);
        if (links.ChildCategories > 0)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{links.ChildCategories} categor{(links.ChildCategories == 1 ? "y" : "ies")} underneath it"));
        }

        if (links.LinkedProducts > 0)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{links.LinkedProducts} product{(links.LinkedProducts == 1 ? "" : "s")} in it"));
        }

        return $"'{name}' still has {string.Join(" and ", reasons)}. Move or remove those first; nothing is " +
               "removed automatically, because a product whose category had gone would disappear from the " +
               "shop with nothing on screen to explain why.";
    }
}
