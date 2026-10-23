using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin;

/// <summary>
/// The ADM-11 department use cases. Doc 08 §3.2: the nav is fixed, so there is no create and no delete —
/// only an edit, a reorder and the <c>IsActive</c> switch of doc 08 §7.1.
/// </summary>
public sealed class DepartmentWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AdminActor Actor = new(5, "manager@example.com", null, null, null);

    private readonly FakeCatalogStructureStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();

    public DepartmentWriteHandlerTests()
    {
        _store.AddDepartment(1, "Women", "women", sortOrder: 0);
        _store.AddDepartment(2, "Men", "men", sortOrder: 1);
        _store.AddDepartment(3, "Kids", "kids", sortOrder: 2);
        _store.ReadyImageAssetIds.Add(41);
    }

    [Fact]
    public async Task Editing_the_text_writes_a_diff_of_what_moved()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        var result = await Update(Edit(women) with { Description = "Everyday essentials." });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal("Everyday essentials.", women.Description);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.Equal("Department", row.EntityType);
        Assert.Equal("1", row.EntityId);
        Assert.Contains("description", row.AfterJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("seoTitle", row.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's decision of 2026-10-02, confirming doc 11 P1 step 1: the name and the slug are not this
    /// command's to change, so there is nowhere to put one — the command does not carry them and the handler
    /// could not apply them if it did.
    /// <para>
    /// The W1-02 follow-up is still served, and this says where: <c>Department.Slug</c>'s write path is the
    /// seeder, the setter guard refuses a bad value there (<c>CatalogSlugTests</c>), and
    /// <c>CK_Departments_Slug</c> stands behind it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_update_command_cannot_carry_a_name_or_a_slug_at_all()
    {
        var fields = typeof(UpdateDepartmentCommand).GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("Name", fields);
        Assert.DoesNotContain("Slug", fields);
        Assert.Contains("Description", fields);
        Assert.Contains("HeroImageAssetId", fields);
    }

    [Fact]
    public async Task Editing_a_department_leaves_its_name_and_slug_exactly_as_they_were()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { Description = "New copy" }));

        Assert.Equal("Women", women.Name);
        Assert.Equal("women", women.Slug);

        // And the trail cannot claim otherwise: the diff is taken over the editable fields only.
        var row = Assert.Single(_audit.Enlisted);
        Assert.DoesNotContain("\"name\"", row.AfterJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"slug\"", row.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server half of the fix-round-1 blocker. <c>PUT</c> replaces the row, so a body that omits the hero
    /// image really does clear it — correct HTTP, and exactly why the client has to send what it loaded.
    /// Pinning it here means the day somebody argues the API should quietly ignore a missing field, the
    /// argument happens against a test rather than against a column full of nulls.
    /// </summary>
    [Fact]
    public async Task An_omitted_hero_image_clears_it_because_a_PUT_replaces_the_row()
    {
        var women = _store.Departments.Single(department => department.Id == 1);
        women.HeroImageAssetId = 41;

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { HeroImageAssetId = null }));

        Assert.Null(women.HeroImageAssetId);
    }

    [Fact]
    public async Task A_hero_image_that_is_not_a_ready_image_in_the_library_is_refused()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        var result = await Update(Edit(women) with { HeroImageAssetId = 999 });

        Assert.Equal("heroImageAssetId", Assert.Single(Assert.IsType<CatalogWriteResult.Invalid>(result).Errors).Field);

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { HeroImageAssetId = 41 }));
        Assert.Equal(41, women.HeroImageAssetId);
    }

    /// <summary>
    /// #223: the hero is handed to the store as a claim — locked after this department's own row and recorded as
    /// a <c>Department</c>/<c>Hero</c> usage row — so the media library refuses to delete it.
    /// </summary>
    [Fact]
    public async Task A_hero_save_claims_the_picture_after_locking_the_department_row()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { HeroImageAssetId = 41 }));

        var (ownerId, claim) = _store.LastMediaSave!.Value;
        Assert.Equal(1, ownerId);
        Assert.Equal(1, claim.LockDepartmentId);
        Assert.Equal(MediaUsageOwnerTypes.Department, claim.UsageOwnerType);
        Assert.Equal(new CatalogMediaSlot(41, MediaUsageRoles.Hero, "heroImageAssetId"), Assert.Single(claim.Slots));
        Assert.Single(_store.Usages[(MediaUsageOwnerTypes.Department, 1)]);
    }

    [Fact]
    public async Task Clearing_the_hero_releases_its_usage_row()
    {
        var women = _store.Departments.Single(department => department.Id == 1);
        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { HeroImageAssetId = 41 }));

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { HeroImageAssetId = null }));

        Assert.Empty(_store.Usages[(MediaUsageOwnerTypes.Department, 1)]);
    }

    /// <summary>#223: a delete that locked the picture first is a 400 on the field, in the handler's own shape.</summary>
    [Fact]
    public async Task A_hero_deleted_before_the_save_could_lock_it_is_a_field_error()
    {
        var women = _store.Departments.Single(department => department.Id == 1);
        _store.UnusableAssetIds.Add(41);

        var result = await Update(Edit(women) with { HeroImageAssetId = 41 });

        var error = Assert.Single(Assert.IsType<CatalogWriteResult.Invalid>(result).Errors);
        Assert.Equal("heroImageAssetId", error.Field);
        Assert.Contains("removed from the media library", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task A_stale_row_version_is_refused_before_anything_is_touched()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(await Update(
            Edit(women) with { Description = "New copy", RowVersion = Convert.ToBase64String(new byte[] { 7 }) }));

        Assert.Equal(CatalogConflictCodes.StaleRowVersion, conflict.Code);
        Assert.Null(women.Description);
    }

    [Fact]
    public async Task Editing_a_department_that_does_not_exist_is_a_not_found() =>
        Assert.IsType<CatalogWriteResult.NotFound>(await Update(new UpdateDepartmentCommand(
            404, null, null, null, null, "AAAA", Actor)));

    [Fact]
    public async Task Saving_an_unedited_department_form_writes_nothing()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        Assert.IsType<CatalogWriteResult.Unchanged>(await Update(Edit(women)));
        Assert.Empty(_audit.Enlisted);
        Assert.Equal(0, _store.Saves);
    }

    /// <summary>
    /// The edit deliberately cannot move <c>IsActive</c> or <c>SortOrder</c>: those have their own
    /// commands, so a form saved from a stale page cannot empty a third of the shop as a side effect.
    /// </summary>
    [Fact]
    public async Task Editing_the_text_cannot_change_whether_the_department_is_live_or_where_it_sits()
    {
        var women = _store.Departments.Single(department => department.Id == 1);
        women.IsActive = true;
        women.SortOrder = 0;

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { Description = "Womenswear." }));

        Assert.True(women.IsActive);
        Assert.Equal((short)0, women.SortOrder);
    }

    [Fact]
    public async Task Deactivating_a_department_writes_a_StatusChange_row()
    {
        var kids = _store.Departments.Single(department => department.Id == 3);

        var result = await new SetDepartmentActiveCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(
                new SetDepartmentActiveCommand(3, false, FakeCatalogStructureStore.Token(kids.RowVersion), Actor),
                TestContext.Current.CancellationToken);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.False(kids.IsActive);
        Assert.Equal(AuditActions.StatusChange, Assert.Single(_audit.Enlisted).Action);
    }

    [Fact]
    public async Task A_reorder_renumbers_every_department_from_zero()
    {
        var result = await Reorder([3, 1, 2]);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal((short)0, _store.Departments.Single(department => department.Id == 3).SortOrder);
        Assert.Equal((short)1, _store.Departments.Single(department => department.Id == 1).SortOrder);
        Assert.Equal((short)2, _store.Departments.Single(department => department.Id == 2).SortOrder);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Equal("DepartmentOrder", row.EntityType);
        Assert.Equal("all", row.EntityId);
        Assert.Contains("\"order\":\"1,2,3\"", row.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("\"order\":\"3,1,2\"", row.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reorder_that_leaves_a_department_out_is_refused()
    {
        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(await Reorder([1, 2]));

        Assert.Equal(CatalogConflictCodes.StaleOrder, conflict.Code);
        Assert.Equal((short)0, _store.Departments.Single(department => department.Id == 1).SortOrder);
    }

    [Fact]
    public async Task A_reorder_of_the_order_they_are_already_in_writes_nothing()
    {
        Assert.IsType<CatalogWriteResult.Unchanged>(await Reorder([1, 2, 3]));
        Assert.Empty(_audit.Enlisted);
    }

    private Task<CatalogWriteResult> Update(UpdateDepartmentCommand command) =>
        new UpdateDepartmentCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(command, TestContext.Current.CancellationToken);

    private Task<CatalogWriteResult> Reorder(int[] order) =>
        new ReorderDepartmentsCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(new ReorderDepartmentsCommand(order, Actor), TestContext.Current.CancellationToken);

    /// <summary>An edit that changes nothing, built from the row's own values.</summary>
    private static UpdateDepartmentCommand Edit(Department department) => new(
        department.Id,
        department.Description,
        department.HeroImageAssetId,
        department.SeoTitle,
        department.SeoDescription,
        FakeCatalogStructureStore.Token(department.RowVersion),
        Actor);
}
