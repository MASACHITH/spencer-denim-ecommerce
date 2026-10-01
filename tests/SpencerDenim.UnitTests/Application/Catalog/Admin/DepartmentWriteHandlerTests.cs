using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;

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
        Assert.DoesNotContain("name", row.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second half of the W1-02 follow-up: a department slug now has a write path, so it now has the
    /// same command-level check a product slug is owed in issue #24.
    /// </summary>
    [Fact]
    public async Task A_bad_department_slug_is_a_field_error_rather_than_an_exception()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        var result = await Update(Edit(women) with { Slug = "Women's Clothing" });

        Assert.Equal("slug", Assert.Single(Assert.IsType<CatalogWriteResult.Invalid>(result).Errors).Field);
        Assert.Equal("women", women.Slug);
        Assert.Empty(_audit.Enlisted);
    }

    [Fact]
    public async Task A_name_or_slug_another_department_holds_is_refused_by_the_field_that_holds_it()
    {
        var women = _store.Departments.Single(department => department.Id == 1);

        var bySlug = Assert.IsType<CatalogWriteResult.Invalid>(await Update(Edit(women) with { Slug = "men" }));
        Assert.Equal("slug", Assert.Single(bySlug.Errors).Field);

        var byName = Assert.IsType<CatalogWriteResult.Invalid>(await Update(Edit(women) with { Name = "Men" }));
        Assert.Equal("name", Assert.Single(byName.Errors).Field);
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
            404, "Ghost", "ghost", null, null, null, null, "AAAA", Actor)));

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

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(women) with { Name = "Womenswear" }));

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
        department.Name,
        department.Slug,
        department.Description,
        department.HeroImageAssetId,
        department.SeoTitle,
        department.SeoDescription,
        FakeCatalogStructureStore.Token(department.RowVersion),
        Actor);
}
