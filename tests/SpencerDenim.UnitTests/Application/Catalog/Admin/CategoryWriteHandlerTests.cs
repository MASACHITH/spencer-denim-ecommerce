using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin;

/// <summary>
/// The ADM-11 category use cases (W1-12): the rules that decide whether a write happens at all, and what
/// the materialised path looks like afterwards.
/// </summary>
public sealed class CategoryWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AdminActor Actor = new(5, "manager@example.com", "203.0.113.4", "agent", "corr");

    private readonly FakeCatalogStructureStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();

    public CategoryWriteHandlerTests()
    {
        _store.AddDepartment(1, "Women", "women");
        _store.AddDepartment(2, "Men", "men");
        _store.AddCategory(10, 1, "Clothing", "clothing");
        _store.AddCategory(11, 1, "Dresses", "dresses", parentCategoryId: 10, sortOrder: 0);
        _store.AddCategory(12, 1, "Midi", "midi", parentCategoryId: 11, sortOrder: 0);
        _store.AddCategory(13, 1, "Jeans", "jeans", parentCategoryId: 10, sortOrder: 1);
        _store.AddCategory(20, 2, "Shirts", "shirts");
    }

    // ---- create ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_top_level_category_goes_to_the_end_of_its_sibling_list()
    {
        var result = await Create(new CreateCategoryCommand(
            1, null, "Knitwear", "knitwear", null, null, null, null, null, null, true, 2, null, null, Actor));

        var created = Assert.IsType<CatalogWriteResult.Succeeded>(result);
        var category = _store.Categories.Single(candidate => candidate.Id == created.EntityId);

        Assert.Equal(1, category.DepartmentId);
        Assert.Null(category.ParentCategoryId);
        Assert.Equal((byte)1, category.Depth);
        Assert.Equal($"/{category.Id}/", category.Path);
        Assert.True(category.IsActive);

        // "Clothing" is at sort order 0, so the newcomer is 1 rather than sharing the position.
        Assert.Equal(1, category.SortOrder);
    }

    [Fact]
    public async Task A_new_child_inherits_its_parents_path_and_one_more_level()
    {
        var result = await Create(new CreateCategoryCommand(
            1, 11, "Maxi", "maxi", null, null, null, null, null, null, true, null, null, null, Actor));

        var created = Assert.IsType<CatalogWriteResult.Succeeded>(result);
        var category = _store.Categories.Single(candidate => candidate.Id == created.EntityId);

        Assert.Equal($"/10/11/{category.Id}/", category.Path);
        Assert.Equal((byte)3, category.Depth);
        Assert.Equal(1, category.SortOrder);
    }

    /// <summary>Doc 08 §3.2's <c>CK_Categories_Depth</c>, refused with the reason rather than by the database.</summary>
    [Fact]
    public async Task A_fourth_level_is_refused_with_the_depth_named()
    {
        var result = await Create(new CreateCategoryCommand(
            1, 12, "Pleated", "pleated", null, null, null, null, null, null, true, null, null, null, Actor));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("parentCategoryId", Assert.Single(invalid.Errors).Field);
        Assert.Contains("3 levels", Assert.Single(invalid.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parent_in_another_department_is_refused()
    {
        var result = await Create(new CreateCategoryCommand(
            1, 20, "Oxford", "oxford", null, null, null, null, null, null, true, null, null, null, Actor));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("parentCategoryId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_slug_already_used_by_a_sibling_is_refused_and_a_cousin_s_is_not()
    {
        var clash = await Create(new CreateCategoryCommand(
            1, 10, "Jeans again", "jeans", null, null, null, null, null, null, true, null, null, null, Actor));
        Assert.Equal("slug", Assert.Single(Assert.IsType<CatalogWriteResult.Invalid>(clash).Errors).Field);

        // The unique index is per department and parent, so the same slug under a different parent is fine.
        var elsewhere = await Create(new CreateCategoryCommand(
            1, 11, "Jeans", "jeans", null, null, null, null, null, null, true, null, null, null, Actor));
        Assert.IsType<CatalogWriteResult.Succeeded>(elsewhere);
    }

    [Fact]
    public async Task A_bad_slug_is_a_field_error_and_nothing_is_written()
    {
        var result = await Create(new CreateCategoryCommand(
            1, null, "Knitwear", "Knit Wear", null, null, null, null, null, null, true, null, null, null, Actor));

        Assert.Equal("slug", Assert.Single(Assert.IsType<CatalogWriteResult.Invalid>(result).Errors).Field);
        Assert.Equal(0, _store.Saves);
        Assert.Empty(_audit.Enlisted);
    }

    [Fact]
    public async Task An_unknown_age_group_size_guide_or_image_is_named_rather_than_left_to_a_foreign_key()
    {
        var result = await Create(new CreateCategoryCommand(
            1, null, "Knitwear", "knitwear", null, 99, 98, null, 97, 96, true, null, null, null, Actor));

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal(
            ["ageGroupId", "bannerAssetId", "imageAssetId", "sizeGuideId"],
            invalid.Errors.Select(error => error.Field).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Creating_writes_one_Create_audit_row_naming_the_new_id()
    {
        var result = await Create(new CreateCategoryCommand(
            1, null, "Knitwear", "knitwear", null, null, null, null, null, null, true, null, null, null, Actor));

        var created = Assert.IsType<CatalogWriteResult.Succeeded>(result);
        var row = Assert.Single(_audit.Enlisted);

        Assert.Equal(AuditActions.Create, row.Action);
        Assert.Equal("Category", row.EntityType);
        Assert.Equal(created.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture), row.EntityId);
        Assert.Null(row.BeforeJson);
        Assert.Contains("knitwear", row.AfterJson!, StringComparison.Ordinal);
        Assert.Equal(5, row.UserId);
        Assert.Equal("manager@example.com", row.UserEmail);

        // Enlisted, never written on its own: the trail and the change commit together (issue #59).
        Assert.Empty(_audit.Written);
    }

    // ---- edit and move --------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_field_writes_a_diff_of_that_field_alone()
    {
        var category = _store.Categories.Single(candidate => candidate.Id == 13);

        var result = await Update(Edit(category, name: "Denim"));

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal("Denim", category.Name);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.Contains("\"name\":\"Jeans\"", row.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Denim\"", row.AfterJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("slug", row.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>A save of a form nobody edited is not an event, so nothing is written at all.</summary>
    [Fact]
    public async Task Saving_an_unedited_form_writes_no_audit_row_and_no_statement()
    {
        var category = _store.Categories.Single(candidate => candidate.Id == 13);

        var result = await Update(Edit(category));

        Assert.IsType<CatalogWriteResult.Unchanged>(result);
        Assert.Empty(_audit.Enlisted);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task A_stale_row_version_is_refused_before_anything_is_touched()
    {
        var category = _store.Categories.Single(candidate => candidate.Id == 13);
        var command = Edit(category, name: "Denim") with { RowVersion = Convert.ToBase64String(new byte[] { 9, 9 }) };

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(await Update(command));

        Assert.Equal(CatalogConflictCodes.StaleRowVersion, conflict.Code);
        Assert.Equal("Jeans", category.Name);
        Assert.Equal(0, _store.Saves);
    }

    /// <summary>
    /// A client that sends no token cannot have seen the current row. Allowing the write would make
    /// optimistic concurrency decorative.
    /// </summary>
    [Fact]
    public async Task A_missing_row_version_is_refused_too()
    {
        var category = _store.Categories.Single(candidate => candidate.Id == 13);

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(
            await Update(Edit(category, name: "Denim") with { RowVersion = null }));

        Assert.Equal(CatalogConflictCodes.StaleRowVersion, conflict.Code);
    }

    /// <summary>
    /// The move: doc 11 P1 step 4 and doc 08 §10.1. Every descendant's path and depth follow, in the one
    /// unit of work.
    /// </summary>
    [Fact]
    public async Task Moving_a_category_re_paths_its_whole_subtree()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);
        var midi = _store.Categories.Single(candidate => candidate.Id == 12);

        // Dresses (with Midi under it) moves from under Clothing to the department's top level.
        var result = await Update(Edit(dresses) with { ParentCategoryId = null });

        Assert.IsType<CatalogWriteResult.Succeeded>(result);

        Assert.Null(dresses.ParentCategoryId);
        Assert.Equal("/11/", dresses.Path);
        Assert.Equal((byte)1, dresses.Depth);

        Assert.Equal("/11/12/", midi.Path);
        Assert.Equal((byte)2, midi.Depth);

        // It joins the end of its new sibling list: Clothing holds 0, so this takes 1.
        Assert.Equal(1, dresses.SortOrder);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Contains("\"path\":\"/11/\"", row.AfterJson!, StringComparison.Ordinal);
        Assert.Contains("\"subtreeCategoriesRepathed\":2", row.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A move recomputes the stored sale price of every product under the subtree it moved.</b>
    /// </summary>
    /// <remarks>
    /// This is W3-05c's third item and a real defect before it. A campaign targeting a category with
    /// <c>IncludeSubcategories</c> reaches products through <c>Categories.Path</c>, and a move rebases
    /// <c>Path</c> for the whole subtree — so the live price (the bag, the checkout) changes at once while
    /// <c>catalog.ProductStats</c> keeps the old figure until the campaign ends.
    /// <c>RefreshCampaignPricing</c> does not correct it, because no boundary has moved.
    /// <para>
    /// The ids asserted are the subtree's, <b>including the category itself</b>: a campaign may target
    /// Dresses directly, so moving Dresses changes what reaches Dresses' own products as well as Midi's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Moving_a_category_recomputes_the_stored_price_of_the_products_under_it()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);

        _campaigns.ProductsUnderCategories.AddRange([41, 42]);

        Assert.IsType<CatalogWriteResult.Succeeded>(
            await Update(Edit(dresses) with { ParentCategoryId = null }));

        // Dresses and Midi — the whole subtree the move re-paths.
        Assert.Equal([11, 12], _campaigns.AskedAboutCategories.Order().ToList());
        Assert.Equal([41, 42], _campaigns.Refreshed.Order().ToList());
    }

    /// <summary>
    /// <b>An edit that is not a move asks for nothing</b>, which is the half that keeps this cheap.
    /// </summary>
    /// <remarks>
    /// A rename, a re-slug, a reorder and switching a category off all leave every <c>Path</c> alone, and
    /// no campaign predicate reads a category's <c>IsActive</c> — so there is nothing for the recompute to
    /// put right. Asserted rather than assumed: a handler that refreshed on every edit would run a
    /// set-based <c>UPDATE</c> over a department's whole catalogue every time somebody fixed a typo.
    /// </remarks>
    [Fact]
    public async Task Renaming_a_category_asks_for_no_recompute()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);

        Assert.IsType<CatalogWriteResult.Succeeded>(
            await Update(Edit(dresses) with { Name = "Dresses and skirts" }));

        Assert.Empty(_campaigns.AskedAboutCategories);
        Assert.Empty(_campaigns.Refreshed);
    }

    [Fact]
    public async Task Moving_a_category_under_its_own_descendant_is_refused()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(
            await Update(Edit(dresses) with { ParentCategoryId = 12 }));

        Assert.Equal("parentCategoryId", Assert.Single(invalid.Errors).Field);
        Assert.Equal("/10/11/", dresses.Path);
    }

    [Fact]
    public async Task A_category_cannot_be_its_own_parent()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(
            await Update(Edit(dresses) with { ParentCategoryId = 11 }));

        Assert.Equal("parentCategoryId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_category_cannot_move_to_another_department()
    {
        var jeans = _store.Categories.Single(candidate => candidate.Id == 13);

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(
            await Update(Edit(jeans) with { ParentCategoryId = 20 }));

        Assert.Contains("another department", Assert.Single(invalid.Errors).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard has to look at the <i>deepest descendant</i>, not at the category itself: Dresses is one
    /// level deep and legal to put under Jeans, but Midi underneath it would land at level four.
    /// </summary>
    [Fact]
    public async Task A_move_that_would_push_a_descendant_past_three_levels_is_refused()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(
            await Update(Edit(dresses) with { ParentCategoryId = 13 }));

        Assert.Equal("parentCategoryId", Assert.Single(invalid.Errors).Field);
        Assert.Contains("4 levels deep", Assert.Single(invalid.Errors).Message, StringComparison.Ordinal);

        // Nothing moved.
        Assert.Equal(10, dresses.ParentCategoryId);
        Assert.Equal("/10/11/", dresses.Path);
        Assert.Equal("/10/11/12/", _store.Categories.Single(candidate => candidate.Id == 12).Path);
    }

    /// <summary>
    /// The same move <i>is</i> legal for a leaf, which is what shows the previous test is measuring the
    /// subtree and not just refusing every move to depth 2.
    /// </summary>
    [Fact]
    public async Task The_same_move_is_allowed_for_a_category_with_nothing_under_it()
    {
        var midi = _store.Categories.Single(candidate => candidate.Id == 12);

        Assert.IsType<CatalogWriteResult.Succeeded>(await Update(Edit(midi) with { ParentCategoryId = 13 }));
        Assert.Equal("/10/13/12/", midi.Path);
        Assert.Equal((byte)3, midi.Depth);
    }

    [Fact]
    public async Task Editing_a_category_that_is_gone_is_a_not_found()
    {
        var result = await Update(new UpdateCategoryCommand(
            404, null, "Ghost", "ghost", null, null, null, null, null, null, true, null, null, null, "AAAA", Actor));

        Assert.IsType<CatalogWriteResult.NotFound>(result);
    }

    // ---- reorder --------------------------------------------------------------------------------

    [Fact]
    public async Task A_reorder_renumbers_the_whole_sibling_list_from_zero()
    {
        var result = await Reorder(new ReorderCategoriesCommand(1, 10, [13, 11], Actor));

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.Equal(0, _store.Categories.Single(candidate => candidate.Id == 13).SortOrder);
        Assert.Equal(1, _store.Categories.Single(candidate => candidate.Id == 11).SortOrder);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Equal("CategoryOrder", row.EntityType);
        Assert.Equal("1/10", row.EntityId);
        Assert.Contains("\"order\":\"11,13\"", row.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("\"order\":\"13,11\"", row.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reorder_of_an_unchanged_list_writes_nothing()
    {
        Assert.IsType<CatalogWriteResult.Unchanged>(await Reorder(new ReorderCategoriesCommand(1, 10, [11, 13], Actor)));
        Assert.Empty(_audit.Enlisted);
    }

    [Fact]
    public async Task A_reorder_made_from_a_stale_list_is_refused_rather_than_partly_applied()
    {
        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(
            await Reorder(new ReorderCategoriesCommand(1, 10, [13], Actor)));

        Assert.Equal(CatalogConflictCodes.StaleOrder, conflict.Code);
        Assert.Equal(0, _store.Categories.Single(candidate => candidate.Id == 11).SortOrder);
        Assert.Equal(1, _store.Categories.Single(candidate => candidate.Id == 13).SortOrder);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task A_reorder_naming_a_parent_in_another_department_is_refused()
    {
        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(
            await Reorder(new ReorderCategoriesCommand(1, 20, [20], Actor)));

        Assert.Equal("parentCategoryId", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_reorder_in_a_department_that_does_not_exist_is_a_not_found() =>
        Assert.IsType<CatalogWriteResult.NotFound>(
            await Reorder(new ReorderCategoriesCommand(404, null, [1], Actor)));

    /// <summary>
    /// The race the whole-list design is for: the second of two concurrent reorders loses on
    /// <c>RowVersion</c>, so the list is one admin's order rather than an interleaving of both.
    /// </summary>
    [Fact]
    public async Task The_second_of_two_concurrent_reorders_is_told_to_reload()
    {
        _store.NextSaveOutcome = CatalogSaveOutcome.ConcurrencyConflict;

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(
            await Reorder(new ReorderCategoriesCommand(1, 10, [13, 11], Actor)));

        Assert.Equal(CatalogConflictCodes.StaleRowVersion, conflict.Code);
    }

    // ---- activate and remove --------------------------------------------------------------------

    [Fact]
    public async Task Deactivating_a_category_writes_a_StatusChange_row()
    {
        var jeans = _store.Categories.Single(candidate => candidate.Id == 13);

        var result = await new SetCategoryActiveCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(
                new SetCategoryActiveCommand(13, false, FakeCatalogStructureStore.Token(jeans.RowVersion), Actor),
                TestContext.Current.CancellationToken);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.False(jeans.IsActive);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Equal(AuditActions.StatusChange, row.Action);
        Assert.Contains("\"isActive\":true", row.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("\"isActive\":false", row.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_a_category_to_the_state_it_is_already_in_writes_nothing()
    {
        var jeans = _store.Categories.Single(candidate => candidate.Id == 13);

        var result = await new SetCategoryActiveCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(
                new SetCategoryActiveCommand(13, true, FakeCatalogStructureStore.Token(jeans.RowVersion), Actor),
                TestContext.Current.CancellationToken);

        Assert.IsType<CatalogWriteResult.Unchanged>(result);
        Assert.Empty(_audit.Enlisted);
    }

    [Fact]
    public async Task Removing_an_empty_category_soft_deletes_it()
    {
        var midi = _store.Categories.Single(candidate => candidate.Id == 12);

        var result = await Remove(12, midi.RowVersion);

        Assert.IsType<CatalogWriteResult.Succeeded>(result);
        Assert.True(midi.IsDeleted);
        Assert.Equal(5, midi.DeletedByUserId);
        Assert.Equal(Now.UtcDateTime, midi.DeletedAtUtc);
        Assert.DoesNotContain(_store.Categories, candidate => candidate.Id == 12);

        var row = Assert.Single(_audit.Enlisted);
        Assert.Equal(AuditActions.SoftDelete, row.Action);
        Assert.Contains("\"isDeleted\":true", row.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 08 §3.2 and doc 11 P1 step 5: a guard, never a cascade. The refusal names what is in the way,
    /// because "cannot be removed" on its own leaves an admin pressing the same button.
    /// </summary>
    [Fact]
    public async Task Removing_a_category_with_children_is_refused_with_the_counts()
    {
        var dresses = _store.Categories.Single(candidate => candidate.Id == 11);
        _store.Links[11] = new CategoryLinkCounts(0, 1);

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(await Remove(11, dresses.RowVersion));

        Assert.Equal(CatalogConflictCodes.CategoryInUse, conflict.Code);
        Assert.Equal(1, Assert.IsType<int>(conflict.Details!["childCategories"]));
        Assert.Contains("1 category underneath it", conflict.Message, StringComparison.Ordinal);
        Assert.False(dresses.IsDeleted);
        Assert.Empty(_audit.Enlisted);
    }

    [Fact]
    public async Task Removing_a_category_with_products_in_it_is_refused_with_the_counts()
    {
        var jeans = _store.Categories.Single(candidate => candidate.Id == 13);
        _store.Links[13] = new CategoryLinkCounts(4, 0);

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(await Remove(13, jeans.RowVersion));

        Assert.Equal(CatalogConflictCodes.CategoryInUse, conflict.Code);
        Assert.Equal(4, Assert.IsType<int>(conflict.Details!["linkedProducts"]));
        Assert.Contains("4 products in it", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_category_with_a_stale_row_version_is_refused()
    {
        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(
            await Remove(12, new byte[] { 1, 2, 3 }));

        Assert.Equal(CatalogConflictCodes.StaleRowVersion, conflict.Code);
        Assert.False(_store.Categories.Single(candidate => candidate.Id == 12).IsDeleted);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private Task<CatalogWriteResult> Create(CreateCategoryCommand command) =>
        new CreateCategoryCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(command, TestContext.Current.CancellationToken);

    private Task<CatalogWriteResult> Update(UpdateCategoryCommand command) =>
        new UpdateCategoryCommandHandler(_store, _audit, new FixedClock(Now), _campaigns)
            .HandleAsync(command, TestContext.Current.CancellationToken);

    private readonly RecordingCampaignRefresher _campaigns = new();

    /// <summary>
    /// The campaign recompute a category <b>move</b> owes (W3-05c, doc 08 §7.4).
    /// </summary>
    /// <remarks>
    /// It records what it was asked, so the two tests above it can assert that a move asks and a rename
    /// does not. A move that stopped asking would leave the stored summary advertising a discount the bag
    /// refuses — or withholding one the bag gives — until the campaign ended, because
    /// <c>RefreshCampaignPricing</c> only looks at campaigns whose boundary has passed and a category
    /// move moves no boundary.
    /// </remarks>
    private sealed class RecordingCampaignRefresher : ICampaignPricingRefresher
    {
        /// <summary>The category ids the handler asked about, in the order it asked.</summary>
        public List<int> AskedAboutCategories { get; } = [];

        /// <summary>The product ids it then asked to have recomputed.</summary>
        public List<int> Refreshed { get; } = [];

        /// <summary>What <see cref="ProductsLinkedToCategoriesAsync"/> answers.</summary>
        public List<int> ProductsUnderCategories { get; } = [];

        public Task<CampaignPricingRefreshPass> RefreshProductsAsync(
            IReadOnlyCollection<int> productIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(productIds);

            Refreshed.AddRange(productIds);

            return Task.FromResult(new CampaignPricingRefreshPass(productIds.Count, 1, productIds.Count));
        }

        public Task<IReadOnlyList<int>> ProductsTargetedByAsync(
            IReadOnlyCollection<int> campaignIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>([]);

        public Task<IReadOnlyList<int>> ProductsLinkedToCategoriesAsync(
            IReadOnlyCollection<int> categoryIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(categoryIds);

            AskedAboutCategories.AddRange(categoryIds);

            return Task.FromResult<IReadOnlyList<int>>(ProductsUnderCategories);
        }

        public Task<CampaignPricingRefreshPass> RefreshBoundariesAsync(
            DateTime sinceUtc,
            DateTime untilUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CampaignPricingRefreshPass(0, 0, 0));
    }

    private Task<CatalogWriteResult> Reorder(ReorderCategoriesCommand command) =>
        new ReorderCategoriesCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(command, TestContext.Current.CancellationToken);

    private Task<CatalogWriteResult> Remove(int categoryId, byte[]? rowVersion) =>
        new RemoveCategoryCommandHandler(_store, _audit, new FixedClock(Now))
            .HandleAsync(
                new RemoveCategoryCommand(categoryId, FakeCatalogStructureStore.Token(rowVersion), Actor),
                TestContext.Current.CancellationToken);

    /// <summary>
    /// An edit that changes nothing, as a starting point. Written from the entity's own values so a test
    /// states only the field it is about — the alternative, fifteen literals per test, is where a
    /// "nothing changed" assertion quietly becomes "everything changed".
    /// </summary>
    private static UpdateCategoryCommand Edit(Category category, string? name = null) => new(
        category.Id,
        category.ParentCategoryId,
        name ?? category.Name,
        category.Slug,
        category.Description,
        category.ImageAssetId,
        category.BannerAssetId,
        category.BannerLinkUrl,
        category.AgeGroupId,
        category.SizeGuideId,
        category.ShowInMegaMenu,
        category.MegaMenuColumn,
        category.SeoTitle,
        category.SeoDescription,
        FakeCatalogStructureStore.Token(category.RowVersion),
        Actor);
}
