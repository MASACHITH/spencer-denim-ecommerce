using System.Net;
using System.Text.Json;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Catalog;

/// <summary>
/// Review moderation (ADM-16; doc 11 P12; #189): the role matrix, the list's filters, approve and hide with their
/// audit rows and rating recompute, stale and concurrent moderation, and the approval switch on the Settings screen.
/// </summary>
public sealed class AdminReviewEndpointTests(SqlServerDatabaseFixture sqlServer) : ReviewTestBase(sqlServer)
{
    private const string ListUrl = "/api/admin/reviews";

    private static string ApproveUrl(int id) => $"/api/admin/reviews/{id}/approve";

    private static string HideUrl(int id) => $"/api/admin/reviews/{id}/hide";

    // ---- permissions ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_route_needs_its_own_permission()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        var version = await RowVersionAsync(reviewId);

        using var anonymous = CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetJsonAsync(anonymous, ListUrl)).Status);

        var nothing = await StaffAsync(factory, Permissions.Dashboard.View);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetJsonAsync(nothing, ListUrl)).Status);

        var viewOnly = await StaffAsync(factory, Permissions.Reviews.View);
        Assert.Equal(HttpStatusCode.OK, (await GetJsonAsync(viewOnly, ListUrl)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(viewOnly, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(viewOnly, HttpMethod.Post, HideUrl(reviewId), new { rowVersion = version })).Status);

        // Hide is Reviews.Edit and approve is Reviews.Approve (doc 11 P12's table), and neither implies the other.
        var editOnly = await StaffAsync(factory, Permissions.Reviews.Edit);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(editOnly, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version })).Status);

        var approveOnly = await StaffAsync(factory, Permissions.Reviews.Approve);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(approveOnly, HttpMethod.Post, HideUrl(reviewId), new { rowVersion = version })).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(approveOnly, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version })).Status);
        version = await RowVersionAsync(reviewId);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(editOnly, HttpMethod.Post, HideUrl(reviewId), new { rowVersion = version })).Status);
    }

    /// <summary>Doc 02 §G's default roles: Content Editor and Store Manager moderate; Order Staff do not.</summary>
    [Theory]
    [InlineData("Content Editor", true)]
    [InlineData("Store Manager", true)]
    [InlineData("Order Staff", false)]
    public async Task The_default_roles_moderate_as_doc_02_says(string roleName, bool moderates)
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);

        var role = DefaultRoleDefinitions.All.Single(definition => definition.Name == roleName);
        var staff = await StaffAsync(factory, [.. role.Permissions]);

        var expected = moderates ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        Assert.Equal(expected, (await GetJsonAsync(staff, ListUrl)).Status);
        Assert.Equal(expected, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = await RowVersionAsync(reviewId) })).Status);
        Assert.Equal(expected, (await SendAsync(staff, HttpMethod.Post, HideUrl(reviewId), new { rowVersion = await RowVersionAsync(reviewId) })).Status);
    }

    [Fact]
    public async Task A_moderation_without_the_antiforgery_header_is_refused()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve);
        staff.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");

        var (status, body) = await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = await RowVersionAsync(reviewId) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_antiforgery_token", body.GetProperty("code").GetString());
        Assert.Equal(ReviewStatus.Pending, (await ReviewRowAsync(reviewId))!.Status);
    }

    // ---- approve, hide, un-hide ---------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_and_hide_recompute_the_rating_and_write_one_audit_row_each()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var a = await CustomerAsync(factory, "a");
        var b = await CustomerAsync(factory, "b");
        var c = await CustomerAsync(factory, "c");
        var five = await MustCreateAsync(a.Client, product, 5);
        var four = await MustCreateAsync(b.Client, product, 4);
        var two = await MustCreateAsync(c.Client, product, 2);
        var staff = await StaffAsync(factory, Permissions.Reviews.View, Permissions.Reviews.Approve, Permissions.Reviews.Edit);
        var mark = await AuditMarkAsync();

        await ApproveAsync(staff, five);
        Assert.Equal((5m, 1), await StatsAsync(product));
        await ApproveAsync(staff, four);
        Assert.Equal((4.5m, 2), await StatsAsync(product));
        await ApproveAsync(staff, two, "Honest but fine");
        Assert.Equal((3.67m, 3), await StatsAsync(product));

        await HideAsync(staff, five, "Contains a phone number");
        Assert.Equal((3m, 2), await StatsAsync(product));

        // Un-hide: doc 11 P12 step 5, "approve a hidden one".
        await ApproveAsync(staff, five);
        Assert.Equal((3.67m, 3), await StatsAsync(product));

        var audits = await AuditAfterAsync(mark, "ProductReview");
        Assert.Equal(5, audits.Count);
        Assert.Equal(
            [AuditActions.Approve, AuditActions.Approve, AuditActions.Approve, AuditActions.StatusChange, AuditActions.Approve],
            audits.Select(audit => audit.Action));

        var hide = audits[3];
        Assert.Equal(five.ToString(System.Globalization.CultureInfo.InvariantCulture), hide.EntityId);
        Assert.Contains("\"Approved\"", hide.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"Hidden\"", hide.AfterJson, StringComparison.Ordinal);
        Assert.Contains("\"noteGiven\":true", hide.AfterJson, StringComparison.Ordinal);

        // The note is personal data the PDPA erasure deletes with the review; the audit log must not copy it.
        Assert.DoesNotContain(audits, audit => (audit.AfterJson ?? string.Empty).Contains("phone number", StringComparison.Ordinal));

        var row = await ReviewRowAsync(five);
        Assert.Equal(ReviewStatus.Approved, row!.Status);
        Assert.NotNull(row.ModeratedAtUtc);
        Assert.NotNull(row.ModeratedByUserId);

        // The public summary agrees with ProductStats.
        using var anonymous = CreateClient(factory);
        var (_, page) = await GetJsonAsync(anonymous, $"/api/catalog/products/{product.Slug}/reviews");
        Assert.Equal(3.67m, page.GetProperty("summary").GetProperty("average").GetDecimal());
        Assert.Equal(3, page.GetProperty("summary").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_review_already_in_the_target_status_is_left_alone_and_not_audited()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve);
        await ApproveAsync(staff, reviewId);
        var mark = await AuditMarkAsync();

        var (status, body) = await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = await RowVersionAsync(reviewId) });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("changed").GetBoolean());
        Assert.Empty(await AuditAfterAsync(mark, "ProductReview"));
    }

    [Fact]
    public async Task A_moderator_working_from_a_stale_version_is_refused_and_shown_the_current_review()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4, "Before");
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve, Permissions.Reviews.Edit);
        var loaded = await RowVersionAsync(reviewId);

        // The author edits after the moderator loaded the list.
        Assert.Equal(HttpStatusCode.OK, (await EditReviewAsync(customer.Client, product, new { rating = 1, title = "After" })).Status);
        var mark = await AuditMarkAsync();

        var (status, body) = await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = loaded });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("stale_review", body.GetProperty("code").GetString());
        Assert.Equal("After", body.GetProperty("review").GetProperty("title").GetString());
        Assert.Equal(ReviewStatus.Pending, (await ReviewRowAsync(reviewId))!.Status);
        Assert.Empty(await AuditAfterAsync(mark, "ProductReview"));
    }

    /// <summary>Two moderators on one review: the second, working from the same version, is told.</summary>
    [Fact]
    public async Task Two_moderators_deciding_the_same_review_at_once_apply_exactly_one_decision()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        var approver = await StaffAsync(factory, Permissions.Reviews.Approve);
        var hider = await StaffAsync(factory, Permissions.Reviews.Edit);
        var version = await RowVersionAsync(reviewId);
        var mark = await AuditMarkAsync();

        var results = await Task.WhenAll(
            SendAsync(approver, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version }),
            SendAsync(hider, HttpMethod.Post, HideUrl(reviewId), new { rowVersion = version }));

        Assert.Single(results, result => result.Status == HttpStatusCode.OK);
        Assert.Single(results, result => result.Status == HttpStatusCode.Conflict);
        Assert.Single(await AuditAfterAsync(mark, "ProductReview"));
    }

    [Fact]
    public async Task Moderation_refuses_a_missing_or_malformed_version_an_overlong_note_and_an_unknown_review()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve);
        var version = await RowVersionAsync(reviewId);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { note = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = "AAAA" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version, note = new string('n', 501) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version, note = 5 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(int.MaxValue), new { rowVersion = version })).Status);
        Assert.Equal(ReviewStatus.Pending, (await ReviewRowAsync(reviewId))!.Status);
    }

    // ---- concurrency: no lost update ------------------------------------------------------------------------------

    /// <summary>
    /// Approvals of different reviews of one product, all at once. A test connection holds the product's stats row
    /// while every request is sent, so they are all in flight together; then it lets go. Each recompute must count
    /// the others: a read-then-write, or an RCSI snapshot taken before the other approvals committed, would leave
    /// the count short (each would write "1").
    /// </summary>
    [Fact]
    public async Task Simultaneous_approvals_of_one_product_lose_no_update()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();

        var ratings = new byte[] { 5, 4, 3, 5, 1, 2 };
        var reviewIds = new List<int>();

        for (var index = 0; index < ratings.Length; index++)
        {
            var customer = await CustomerAsync(factory, $"race{index}");
            reviewIds.Add(await MustCreateAsync(customer.Client, product, ratings[index]));
        }

        var moderators = new List<HttpClient>();
        var versions = new List<string>();
        foreach (var id in reviewIds)
        {
            moderators.Add(await StaffAsync(factory, Permissions.Reviews.Approve));
            versions.Add(await RowVersionAsync(id));
        }

        Task<(HttpStatusCode Status, JsonElement Body)[]> all;

        await using (var hold = await HoldStatsRowAsync(product))
        {
            all = Task.WhenAll(reviewIds.Select((id, index) =>
                SendAsync(moderators[index], HttpMethod.Post, ApproveUrl(id), new { rowVersion = versions[index] })));

            await hold.WaitForBlockedAsync(reviewIds.Count);
        }

        var results = await all;

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.Status));
        Assert.Equal((ReviewRules.AverageOf(ratings), ratings.Length), await StatsAsync(product));
    }

    /// <summary>The same, for customer writes with approval off: each publish counts every other.</summary>
    [Fact]
    public async Task Simultaneous_published_reviews_lose_no_update()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();

        var customers = new List<TestCustomer>();
        for (var index = 0; index < 5; index++)
        {
            customers.Add(await CustomerAsync(factory, $"publish{index}"));
        }

        Task<(HttpStatusCode Status, JsonElement Body)[]> all;

        await using (var hold = await HoldStatsRowAsync(product))
        {
            all = Task.WhenAll(customers.Select((customer, index) =>
                CreateReviewAsync(customer.Client, product, new { rating = index + 1 })));

            await hold.WaitForBlockedAsync(customers.Count);
        }

        var results = await all;

        Assert.All(results, result => Assert.Equal(HttpStatusCode.Created, result.Status));
        Assert.Equal((3m, 5), await StatsAsync(product));
    }

    // ---- the list -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_filters_by_status_verified_and_search_and_reads_pending_oldest_first()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var buyer = await CustomerAsync(factory, "buyer", "Verified", "Buyer");
        await OrderAsync(product, buyer.Id, buyer.Email, SpencerDenim.Domain.Sales.OrderStatus.Delivered);
        var older = await MustCreateAsync(buyer.Client, product, 5, "Zebra stripes fit");
        var browser = await CustomerAsync(factory, "browser", "Just", "Looking");
        var newer = await MustCreateAsync(browser.Client, product, 2, "Too small");
        var staff = await StaffAsync(factory, Permissions.Reviews.View, Permissions.Reviews.Approve);

        var token = Guid.NewGuid().ToString("N")[..10];
        var tagged = await CustomerAsync(factory, "tagged", "Tagged", null);
        var taggedId = await MustCreateAsync(tagged.Client, product, 3, null, $"Searchable {token} text");
        await ApproveAsync(staff, taggedId);

        var (_, pending) = await GetJsonAsync(staff, $"{ListUrl}?status=Pending&pageSize=100");
        var pendingIds = pending.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToList();
        Assert.True(pendingIds.IndexOf(older) < pendingIds.IndexOf(newer), "Pending reads oldest first.");
        Assert.DoesNotContain(taggedId, pendingIds);
        Assert.True(pending.GetProperty("counts").GetProperty("pending").GetInt32() >= 2);

        var (_, approved) = await GetJsonAsync(staff, $"{ListUrl}?status=Approved&search={token}");
        Assert.Equal(1, approved.GetProperty("totalCount").GetInt32());
        var item = approved.GetProperty("items")[0];
        Assert.Equal(taggedId, item.GetProperty("id").GetInt32());
        Assert.Equal(product.Slug, item.GetProperty("productSlug").GetString());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("rowVersion").GetString()));
        Assert.False(string.IsNullOrEmpty(item.GetProperty("moderatedByName").GetString()));

        var (_, verified) = await GetJsonAsync(staff, $"{ListUrl}?status=All&verified=true&search=Zebra%20stripes");
        Assert.Equal([older], verified.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()));

        var (_, unverified) = await GetJsonAsync(staff, $"{ListUrl}?status=All&verified=false&search=Zebra%20stripes");
        Assert.Equal(0, unverified.GetProperty("totalCount").GetInt32());

        var (_, byName) = await GetJsonAsync(staff, $"{ListUrl}?status=All&search=Just%20L");
        Assert.Contains(newer, byName.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()));
    }

    [Theory]
    [InlineData("status=Open")]
    [InlineData("status=1")]
    [InlineData("status=Pending,Hidden")]
    [InlineData("verified=yes")]
    public async Task The_list_refuses_a_filter_it_does_not_know(string query)
    {
        await ReadyAsync();
        await using var factory = CreateHost();
        var staff = await StaffAsync(factory, Permissions.Reviews.View);

        Assert.Equal(HttpStatusCode.BadRequest, (await GetJsonAsync(staff, $"{ListUrl}?{query}")).Status);
    }

    // ---- the approval switch on the Settings screen -----------------------------------------------------------------

    [Fact]
    public async Task The_approval_switch_is_seeded_on_editable_audited_and_obeyed()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var staff = await StaffAsync(factory, Permissions.Settings.View, Permissions.Settings.Edit);

        var (status, settings) = await GetJsonAsync(staff, "/api/admin/settings");
        Assert.Equal(HttpStatusCode.OK, status);
        var (value, rowVersion) = ReadSwitch(settings);
        Assert.Equal("true", value);

        var mark = await AuditMarkAsync();

        var (saved, _) = await SendAsync(staff, HttpMethod.Put, "/api/admin/settings/reviews", new { requireApproval = false, rowVersion });
        Assert.Equal(HttpStatusCode.NoContent, saved);

        var audit = Assert.Single(await AuditAfterAsync(mark, "Setting"));
        Assert.Contains("Reviews.RequireApproval", audit.EntityId ?? string.Empty, StringComparison.Ordinal);

        var customer = await CustomerAsync(factory);
        var (_, published) = await CreateReviewAsync(customer.Client, product, new { rating = 5 });
        Assert.Equal("Published", published.GetProperty("outcome").GetString());

        // Stale version: refused.
        var (stale, _) = await SendAsync(staff, HttpMethod.Put, "/api/admin/settings/reviews", new { requireApproval = true, rowVersion });
        Assert.Equal(HttpStatusCode.Conflict, stale);

        var (_, after) = await GetJsonAsync(staff, "/api/admin/settings");
        var (valueAfter, versionAfter) = ReadSwitch(after);
        Assert.Equal("false", valueAfter);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(staff, HttpMethod.Put, "/api/admin/settings/reviews", new { requireApproval = (bool?)null, rowVersion = versionAfter })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(staff, HttpMethod.Put, "/api/admin/settings/reviews", new { requireApproval = "no", rowVersion = versionAfter })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(staff, HttpMethod.Put, "/api/admin/settings/reviews", new { requireApproval = true, rowVersion = versionAfter })).Status);

        var other = await CustomerAsync(factory, "after");
        var (_, pending) = await CreateReviewAsync(other.Client, product, new { rating = 4 });
        Assert.Equal("Pending", pending.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Only_a_settings_editor_may_change_the_switch()
    {
        await ReadyAsync();
        await using var factory = CreateHost();
        var moderator = await StaffAsync(factory, Permissions.Reviews.View, Permissions.Reviews.Approve, Permissions.Reviews.Edit);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator, HttpMethod.Put, "/api/admin/settings/reviews", new { requireApproval = false, rowVersion = "x" })).Status);
    }

    private static (string? Value, string RowVersion) ReadSwitch(JsonElement settings)
    {
        foreach (var group in settings.GetProperty("groups").EnumerateArray())
        {
            foreach (var setting in group.GetProperty("settings").EnumerateArray())
            {
                if (setting.GetProperty("key").GetString() == "Reviews.RequireApproval")
                {
                    return (setting.GetProperty("value").GetString(), group.GetProperty("rowVersion").GetString()!);
                }
            }
        }

        throw new InvalidOperationException("Reviews.RequireApproval is not on the settings screen: " + settings);
    }

    private Task ApproveAsync(HttpClient staff, int reviewId, string? note = null) =>
        ModerateAsync(staff, ApproveUrl(reviewId), reviewId, note);

    private Task HideAsync(HttpClient staff, int reviewId, string? note = null) =>
        ModerateAsync(staff, HideUrl(reviewId), reviewId, note);

    private async Task ModerateAsync(HttpClient staff, string url, int reviewId, string? note)
    {
        var (status, body) = await SendAsync(staff, HttpMethod.Post, url, new { rowVersion = await RowVersionAsync(reviewId), note });
        Assert.True(status == HttpStatusCode.OK, $"Moderation answered {(int)status}: {body}");
        Assert.True(body.GetProperty("changed").GetBoolean());
    }
}
