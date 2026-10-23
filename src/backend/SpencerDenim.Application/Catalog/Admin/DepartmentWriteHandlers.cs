using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// Edits one department (ADM-11, doc 11 P1 step 1). No create and no delete exist: the nav is fixed (Q5,
/// doc 08 §3.2).
/// </summary>
public sealed class UpdateDepartmentCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<UpdateDepartmentCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(UpdateDepartmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        CatalogFieldRules.LimitText(
            errors, "description", command.Description, CatalogFieldRules.DepartmentDescriptionMaxLength, "The description");
        CatalogFieldRules.LimitText(
            errors, "seoTitle", command.SeoTitle, CatalogFieldRules.SeoTitleMaxLength, "The SEO title");
        CatalogFieldRules.LimitText(
            errors, "seoDescription", command.SeoDescription, CatalogFieldRules.SeoDescriptionMaxLength, "The SEO description");

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var department = await store.FindDepartmentAsync(command.DepartmentId, cancellationToken);
        if (department is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No department has the id {command.DepartmentId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(department.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        if (command.HeroImageAssetId is { } heroId
            && !await store.ReadyImageAssetExistsAsync(heroId, cancellationToken))
        {
            errors.Add(new ValidationError(
                "heroImageAssetId",
                "That hero image is not in the media library, or is not a ready image."));
        }

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var before = Snapshot(department);

        department.Description = CatalogFieldRules.Clean(command.Description);
        department.HeroImageAssetId = command.HeroImageAssetId;
        department.SeoTitle = CatalogFieldRules.Clean(command.SeoTitle);
        department.SeoDescription = CatalogFieldRules.Clean(command.SeoDescription);

        var audit = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.DepartmentEntityType,
            department.Id.ToString(CultureInfo.InvariantCulture),
            before,
            Snapshot(department),
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new CatalogWriteResult.Unchanged(department.Id);
        }

        auditLog.Enlist(audit);

        // #223. Before this the admin department save wrote no usage row and took no lock, so the media library
        // could delete the hero a department was showing (only the client-asset importer recorded one). Now the
        // save records the hero as the client-asset importer does (OwnerType Department, role Hero) and follows
        // doc 08 §5.3's order: this department's row first, then the hero's asset row, then the writes — the
        // order a mega-menu card's save takes, which locks the same department row before its own picture.
        var media = new CatalogMediaClaim(department.HeroImageAssetId is { } hero
            ? [new CatalogMediaSlot(hero, MediaUsageRoles.Hero, "heroImageAssetId")]
            : [])
        {
            LockDepartmentId = department.Id,
            UsageOwnerType = MediaUsageOwnerTypes.Department,
        };

        return CatalogWriteSupport.ToResult(
            await store.SaveWithMediaAsync(department.Id, media, command.Actor.UserId, cancellationToken),
            department.Id,
            media);
    }

    /// <summary>
    /// The fields this use case may change, and only those. <c>IsActive</c> and <c>SortOrder</c> are not
    /// here because they are not this command's to move — a diff that listed them would report "no change"
    /// on every save and quietly hide the day one of them did move. <c>Name</c> and <c>Slug</c> are not here
    /// because nothing on this path may change them at all (owner, 2026-10-02).
    /// </summary>
    private static Dictionary<string, object?> Snapshot(Department department) => new(StringComparer.Ordinal)
    {
        ["description"] = department.Description,
        ["heroImageAssetId"] = department.HeroImageAssetId,
        ["seoTitle"] = department.SeoTitle,
        ["seoDescription"] = department.SeoDescription,
    };
}

/// <summary>
/// Switches a department on or off (ADM-11, doc 08 §7.1). Its own command rather than a field on the edit
/// form, for the same reason <see cref="SetCategoryActiveCommandHandler"/> is: this one write empties a
/// third of the shop, and it should never happen because a stale checkbox rode along with a text change.
/// </summary>
public sealed class SetDepartmentActiveCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<SetDepartmentActiveCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(SetDepartmentActiveCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var department = await store.FindDepartmentAsync(command.DepartmentId, cancellationToken);
        if (department is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No department has the id {command.DepartmentId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(department.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        if (department.IsActive == command.IsActive)
        {
            return new CatalogWriteResult.Unchanged(department.Id);
        }

        department.IsActive = command.IsActive;

        auditLog.Enlist(CatalogAuditTrail.Row(
            AuditActions.StatusChange,
            CatalogAuditTrail.DepartmentEntityType,
            department.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isActive"] = !command.IsActive },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isActive"] = command.IsActive },
            command.Actor,
            clock.UtcNow.UtcDateTime));

        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            department.Id);
    }
}

/// <summary>
/// Puts the departments in a new order (ADM-11): the nav's order, left to right.
/// <para>
/// Whole-list, like <see cref="ReorderCategoriesCommandHandler"/>, and refused outright when the list sent
/// is not the set of departments that exist — which for a fixed nav can only mean the client is working
/// from a stale page.
/// </para>
/// </summary>
public sealed class ReorderDepartmentsCommandHandler(
    ICatalogStructureStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<ReorderDepartmentsCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(ReorderDepartmentsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var departments = await store.ListDepartmentsAsync(cancellationToken);

        var check = ReorderList.Check(
            command.OrderedDepartmentIds,
            departments.Select(department => department.Id).ToList(),
            "orderedDepartmentIds",
            "department");
        if (check is not null)
        {
            return check;
        }

        var requested = command.OrderedDepartmentIds!;
        var byId = departments.ToDictionary(department => department.Id);
        var before = requested
            .Select(id => byId[id])
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => department.Id)
            .ToList();

        for (var position = 0; position < requested.Count; position++)
        {
            byId[requested[position]].SortOrder = (short)position;
        }

        var audit = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.DepartmentOrderEntityType,
            "all",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = string.Join(',', before) },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = string.Join(',', requested) },
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new CatalogWriteResult.Unchanged(0);
        }

        auditLog.Enlist(audit);
        return CatalogWriteSupport.ToResult(await store.SaveAsync(command.Actor.UserId, cancellationToken), 0);
    }
}
