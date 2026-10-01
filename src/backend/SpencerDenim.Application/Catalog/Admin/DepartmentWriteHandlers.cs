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
        CatalogFieldRules.RequireText(
            errors, "name", command.Name, CatalogFieldRules.DepartmentNameMaxLength, "The name");
        CatalogFieldRules.RequireSlug(errors, "slug", command.Slug, CatalogFieldRules.DepartmentSlugMaxLength);
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

        var name = CatalogFieldRules.Clean(command.Name)!;
        var slug = CatalogFieldRules.Clean(command.Slug)!;

        if (await store.DepartmentNameTakenAsync(name, department.Id, cancellationToken))
        {
            errors.Add(new ValidationError("name", "Another department already has that name."));
        }

        if (await store.DepartmentSlugTakenAsync(slug, department.Id, cancellationToken))
        {
            errors.Add(new ValidationError("slug", "Another department already uses that URL slug."));
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

        department.Name = name;
        department.Slug = slug;
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
        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            department.Id);
    }

    /// <summary>
    /// The fields this use case may change, and only those. <c>IsActive</c> and <c>SortOrder</c> are not
    /// here because they are not this command's to move — a diff that listed them would report "no change"
    /// on every save and quietly hide the day one of them did move.
    /// </summary>
    private static Dictionary<string, object?> Snapshot(Department department) => new(StringComparer.Ordinal)
    {
        ["name"] = department.Name,
        ["slug"] = department.Slug,
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
