using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Catalog;

/// <summary>
/// The independent tester's additions for #189 (PDP-08, ADM-16): mass assignment, the verified-buyer edges the
/// developer's suite left out (an order linked later, a refund after the badge, another variant), IDOR, paging
/// abuse, the rating through every transition, and the races the stats-row lock exists for — run with the
/// execution strategy's retries <b>off</b>, so a deadlock the lock should have prevented surfaces as a 500
/// instead of being replayed out of sight.
/// </summary>
public sealed class ReviewTesterAttackTests(SqlServerDatabaseFixture sqlServer) : ReviewTestBase(sqlServer)
{
    private static string ApproveUrl(int id) => $"/api/admin/reviews/{id}/approve";

    private static string HideUrl(int id) => $"/api/admin/reviews/{id}/hide";

    /// <summary>
    /// The review host with EF's retry-on-failure switched off. Under the default (3 retries) a deadlock victim is
    /// silently replayed and the test still sees 200, which is how mutant M5 (no stats-row lock, READCOMMITTEDLOCK
    /// kept) survived the developer's concurrency tests: it deadlocks every time and the retry hides it.
    /// </summary>
    private ApiFactory CreateHostWithoutRetries() => CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["CustomerSecurity:RegistrationsPerWindow"] = "1000",
        ["CustomerSecurity:SignInAttemptsPerWindow"] = "1000",
        ["Database:MaxRetryCount"] = "0",
    });

    private async Task ModerateOkAsync(HttpClient staff, string url, int reviewId)
    {
        var (status, body) = await SendAsync(staff, HttpMethod.Post, url, new { rowVersion = await RowVersionAsync(reviewId) });
        Assert.True(status == HttpStatusCode.OK, $"{url} answered {(int)status}: {body}");
    }

    private static async Task<(decimal? Average, int Count)> PublicSummaryAsync(HttpClient client, TestProduct product)
    {
        var (status, body) = await GetJsonAsync(client, $"/api/catalog/products/{product.Slug}/reviews");
        Assert.Equal(HttpStatusCode.OK, status);
        var summary = body.GetProperty("summary");
        var average = summary.GetProperty("average");

        return (average.ValueKind == JsonValueKind.Null ? null : average.GetDecimal(), summary.GetProperty("count").GetInt32());
    }

    /// <summary>The stats row and the public summary agree, and both equal the expected figure.</summary>
    private async Task AssertRatingAsync(HttpClient client, TestProduct product, decimal? average, int count)
    {
        Assert.Equal((average, count), await StatsAsync(product));
        Assert.Equal((average, count), await PublicSummaryAsync(client, product));
    }

    // ---- mass assignment ----------------------------------------------------------------------------------------

    /// <summary>
    /// Nothing but rating, title and body is read from the body: a forged badge, order, status, display name or
    /// customer id is ignored on create and on edit.
    /// </summary>
    [Fact]
    public async Task Forged_badge_order_status_name_and_customer_in_the_body_are_ignored()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var victim = await CustomerAsync(factory, "victim", "Victim", "Buyer");
        var victimsOrder = await OrderAsync(product, victim.Id, victim.Email, OrderStatus.Delivered);
        var attacker = await CustomerAsync(factory, "attacker", "Plain", "Shopper");

        var forged = new
        {
            rating = 5,
            title = "Forged",
            isVerifiedBuyer = true,
            orderId = victimsOrder,
            status = "Approved",
            displayName = "Colombo Wardrobe",
            customerUserId = victim.Id,
            id = 1,
            productId = product.Id + 1,
        };

        var (created, body) = await CreateReviewAsync(attacker.Client, product, forged);
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.False(body.GetProperty("review").GetProperty("isVerifiedBuyer").GetBoolean());
        Assert.Equal("Pending", body.GetProperty("review").GetProperty("status").GetString());
        Assert.Equal("Plain S.", body.GetProperty("review").GetProperty("displayName").GetString());

        var (edited, _) = await EditReviewAsync(attacker.Client, product, forged);
        Assert.Equal(HttpStatusCode.OK, edited);

        var row = await ReviewRowAsync(body.GetProperty("review").GetProperty("id").GetInt32());
        Assert.NotNull(row);
        Assert.Equal(attacker.Id, row.CustomerUserId);
        Assert.Equal(product.Id, row.ProductId);
        Assert.Null(row.OrderId);
        Assert.False(row.IsVerifiedBuyer);
        Assert.Equal(ReviewStatus.Pending, row.Status);
        Assert.Equal("Plain S.", row.DisplayName);

        // The victim can still write their own review of the product: the forged customer id took nothing of theirs.
        Assert.Equal(HttpStatusCode.Created, (await CreateReviewAsync(victim.Client, product, new { rating = 4 })).Status);
    }

    // ---- verified buyer edges --------------------------------------------------------------------------------------

    /// <summary>
    /// A guest order is not enough until it is linked to the account; once linked, an edit gains the badge and
    /// links the order; a refund afterwards leaves both (doc 11 §7 G-27), and the CHECK keeps them together.
    /// </summary>
    [Fact]
    public async Task A_guest_order_linked_later_gains_the_badge_on_edit_and_a_refund_does_not_remove_it()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory, "linker");
        var guestOrder = await OrderAsync(product, null, customer.Email, OrderStatus.Delivered);

        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        Assert.False((await ReviewRowAsync(reviewId))!.IsVerifiedBuyer);

        await SetOrderAsync(guestOrder, customerUserId: customer.Id, status: null);

        var (_, mine) = await GetJsonAsync(customer.Client, Url(product));
        Assert.True(mine.GetProperty("wouldBeVerified").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await EditReviewAsync(customer.Client, product, new { rating = 5 })).Status);
        var verified = await ReviewRowAsync(reviewId);
        Assert.True(verified!.IsVerifiedBuyer);
        Assert.Equal(guestOrder, verified.OrderId);

        await SetOrderAsync(guestOrder, customerUserId: customer.Id, status: OrderStatus.Refunded);

        Assert.Equal(HttpStatusCode.OK, (await EditReviewAsync(customer.Client, product, new { rating = 2 })).Status);
        var afterRefund = await ReviewRowAsync(reviewId);
        Assert.True(afterRefund!.IsVerifiedBuyer);
        Assert.Equal(guestOrder, afterRefund.OrderId);
    }

    /// <summary>The badge is about the product, not the variant: an order for another size or colour counts.</summary>
    [Fact]
    public async Task A_delivered_order_of_another_variant_of_the_product_counts()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory, "variant");

        int otherVariant;
        using (var scope = SqlServer.CreateScope())
        {
            otherVariant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
                .Where(variant => variant.ProductId == product.Id)
                .MaxAsync(variant => variant.Id, Token);
        }

        await OrderAsync(product with { VariantId = otherVariant }, customer.Id, customer.Email, OrderStatus.Delivered);

        var (status, body) = await CreateReviewAsync(customer.Client, product, new { rating = 5 });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.True(body.GetProperty("review").GetProperty("isVerifiedBuyer").GetBoolean());
    }

    /// <summary>The CHECK refuses a badge without an order, and an order without a badge, whoever writes the row.</summary>
    [Fact]
    public async Task The_database_refuses_a_badge_without_its_order_and_an_order_without_its_badge()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory, "check");
        var order = await OrderAsync(product, customer.Id, customer.Email, OrderStatus.Delivered);
        var reviewId = await MustCreateAsync(customer.Client, product, 3);

        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True((await ReviewRowAsync(reviewId))!.IsVerifiedBuyer);

        var badgeOnly = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.ProductReviews SET IsVerifiedBuyer = 0 WHERE Id = {0};", [reviewId], Token));
        Assert.NotNull(badgeOnly);
        Assert.Contains("CK_ProductReviews_Verified", badgeOnly.Message, StringComparison.Ordinal);

        await db.Database.ExecuteSqlRawAsync("UPDATE catalog.ProductReviews SET IsVerifiedBuyer = 0, OrderId = NULL WHERE Id = {0};", [reviewId], Token);

        var orderOnly = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.ProductReviews SET OrderId = {0} WHERE Id = {1};", [order, reviewId], Token));
        Assert.NotNull(orderOnly);
        Assert.Contains("CK_ProductReviews_Verified", orderOnly.Message, StringComparison.Ordinal);
    }

    // ---- IDOR -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Another customer's pending review is not readable, editable or listed: every account route is keyed by the
    /// session's customer, and the public route shows approved reviews only.
    /// </summary>
    [Fact]
    public async Task Another_customers_pending_review_cannot_be_read_edited_or_seen()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var author = await CustomerAsync(factory, "author");
        var snoop = await CustomerAsync(factory, "snoop");
        var reviewId = await MustCreateAsync(author.Client, product, 1, "Secret pending title", "Pending body");

        var (status, mine) = await GetJsonAsync(snoop.Client, Url(product));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("review").ValueKind);

        var (editStatus, editBody) = await EditReviewAsync(snoop.Client, product, new { rating = 5, title = "Hijacked" });
        Assert.Equal(HttpStatusCode.NotFound, editStatus);
        Assert.Equal("not_reviewed", editBody.GetProperty("code").GetString());
        Assert.Equal("Secret pending title", (await ReviewRowAsync(reviewId))!.Title);

        using var anonymous = CreateClient(factory);
        using var response = await anonymous.GetAsync(new Uri($"/api/catalog/products/{product.Slug}/reviews?pageSize=50", UriKind.Relative), Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("Secret pending title", text, StringComparison.Ordinal);

        // Hidden is no different from pending.
        var staff = await StaffAsync(factory, Permissions.Reviews.Edit);
        await ModerateOkAsync(staff, HideUrl(reviewId), reviewId);
        using var after = await anonymous.GetAsync(new Uri($"/api/catalog/products/{product.Slug}/reviews?pageSize=50", UriKind.Relative), Token);
        Assert.DoesNotContain("Secret pending title", await after.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    // ---- paging abuse -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("page=1&pageSize=0", 1, 1)]
    [InlineData("page=1&pageSize=-5", 1, 1)]
    [InlineData("page=1&pageSize=51", 1, 50)]
    [InlineData("page=-3&pageSize=10", 1, 10)]
    [InlineData("page=0", 1, 10)]
    [InlineData("page=2147483647&pageSize=50", 10000, 50)]
    public async Task Public_paging_is_clamped_never_refused_or_thrown(string query, int page, int pageSize)
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        using var anonymous = CreateClient(factory);

        var (status, body) = await GetJsonAsync(anonymous, $"/api/catalog/products/{product.Slug}/reviews?{query}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(page, body.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, body.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("pageSize=abc")]
    [InlineData("page=99999999999")]
    public async Task Public_paging_that_is_not_a_number_is_400_not_500(string query)
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        using var anonymous = CreateClient(factory);

        var (status, _) = await GetJsonAsync(anonymous, $"/api/catalog/products/{product.Slug}/reviews?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---- the rating through every transition ------------------------------------------------------------------------

    /// <summary>
    /// ProductStats and the public summary, after each of: publish, publish, publish, an approved review's rating
    /// edited with approval off, hide, show again, an edit back to Pending with approval on, and hiding the rest down
    /// to an empty product (NULL, 0). The averages exercise rounding (5/3 → 1.67, 3/2 → 1.5).
    /// </summary>
    [Fact]
    public async Task The_rating_follows_every_transition_and_edit_down_to_an_empty_product()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();
        using var anonymous = CreateClient(factory);
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve, Permissions.Reviews.Edit);

        var first = await CustomerAsync(factory, "t1");
        var second = await CustomerAsync(factory, "t2");
        var third = await CustomerAsync(factory, "t3");

        var firstId = await MustCreateAsync(first.Client, product, 5);
        await AssertRatingAsync(anonymous, product, 5m, 1);

        var secondId = await MustCreateAsync(second.Client, product, 2);
        await AssertRatingAsync(anonymous, product, 3.5m, 2);

        var thirdId = await MustCreateAsync(third.Client, product, 2);
        await AssertRatingAsync(anonymous, product, 3m, 3);

        // An approved review's rating edited with approval off stays approved and moves the rating: 1, 2, 2.
        var (_, edited) = await EditReviewAsync(first.Client, product, new { rating = 1 });
        Assert.Equal("Published", edited.GetProperty("outcome").GetString());
        await AssertRatingAsync(anonymous, product, 1.67m, 3);

        await ModerateOkAsync(staff, HideUrl(secondId), secondId);
        await AssertRatingAsync(anonymous, product, 1.5m, 2);

        await ModerateOkAsync(staff, ApproveUrl(secondId), secondId);
        await AssertRatingAsync(anonymous, product, 1.67m, 3);

        // With approval on, an edit takes the review out of the rating until it is approved again: 1, 2.
        await SetRequireApprovalAsync(true);
        var (_, backToPending) = await EditReviewAsync(third.Client, product, new { rating = 5 });
        Assert.Equal("Pending", backToPending.GetProperty("outcome").GetString());
        await AssertRatingAsync(anonymous, product, 1.5m, 2);

        await ModerateOkAsync(staff, ApproveUrl(thirdId), thirdId);
        await AssertRatingAsync(anonymous, product, 2.67m, 3);

        foreach (var id in new[] { firstId, secondId, thirdId })
        {
            await ModerateOkAsync(staff, HideUrl(id), id);
        }

        await AssertRatingAsync(anonymous, product, null, 0);
    }

    // ---- races ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Kills mutant M5. Approvals of different reviews of one product, all in flight together behind a held stats
    /// row, with retries off. Without the stats-row lock each approval holds its review row (X) and then the
    /// recompute's READCOMMITTEDLOCK read waits on the others' review rows while they wait on the stats row: a
    /// deadlock, every time (reproduced in plain T-SQL by the tester). With the lock they queue on the stats row
    /// and none deadlocks.
    /// </summary>
    [Fact]
    public async Task Simultaneous_approvals_of_one_product_never_deadlock_even_without_retries()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHostWithoutRetries();

        var ratings = new byte[] { 5, 4, 3, 5 };
        var reviewIds = new List<int>();

        for (var index = 0; index < ratings.Length; index++)
        {
            var customer = await CustomerAsync(factory, $"nodeadlock{index}");
            reviewIds.Add(await MustCreateAsync(customer.Client, product, ratings[index]));
        }

        var moderators = new List<HttpClient>();
        foreach (var _ in reviewIds)
        {
            moderators.Add(await StaffAsync(factory, Permissions.Reviews.Approve));
        }

        var versions = new List<string>();
        foreach (var id in reviewIds)
        {
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

        Assert.All(results, result => Assert.True(result.Status == HttpStatusCode.OK, $"An approval answered {(int)result.Status}: {result.Body}"));
        Assert.Equal((ReviewRules.AverageOf(ratings), ratings.Length), await StatsAsync(product));
    }

    /// <summary>
    /// A moderator approving while the author edits the same review, both held behind the stats row. Whichever goes
    /// first, the review ends Pending (approval is on, and an edit re-queues it — G-32, or the approval is refused as
    /// stale), the rating counts only what is approved, and an audit row exists only for an applied approval.
    /// </summary>
    [Fact]
    public async Task An_approval_racing_the_authors_edit_ends_consistent_whichever_wins()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHostWithoutRetries();
        var author = await CustomerAsync(factory, "racer");
        var bystander = await CustomerAsync(factory, "bystander");
        var reviewId = await MustCreateAsync(author.Client, product, 2, "Before");
        var otherId = await MustCreateAsync(bystander.Client, product, 4);
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve);
        await ModerateOkAsync(staff, ApproveUrl(otherId), otherId);
        var loaded = await RowVersionAsync(reviewId);
        var mark = await AuditMarkAsync();

        Task<(HttpStatusCode Status, JsonElement Body)> approve;
        Task<(HttpStatusCode Status, JsonElement Body)> edit;

        await using (var hold = await HoldStatsRowAsync(product))
        {
            approve = SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = loaded });
            edit = EditReviewAsync(author.Client, product, new { rating = 1, title = "After" });

            await hold.WaitForBlockedAsync(2);
        }

        var (approveStatus, approveBody) = await approve;
        var (editStatus, editBody) = await edit;

        Assert.True(editStatus == HttpStatusCode.OK, $"The edit answered {(int)editStatus}: {editBody}");
        Assert.True(
            approveStatus is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"The approval answered {(int)approveStatus}: {approveBody}");

        var row = await ReviewRowAsync(reviewId);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Equal("After", row.Title);
        Assert.Equal((4m, 1), await StatsAsync(product));
        Assert.Equal(approveStatus == HttpStatusCode.OK ? 1 : 0, (await AuditAfterAsync(mark, "ProductReview")).Count);
    }

    /// <summary>
    /// The PDPA erasure racing an approval of the leaving customer's review, both held behind the stats row: in
    /// either order the review is gone, the approval is applied-then-erased or a 404, and the rating is the others'.
    /// </summary>
    [Fact]
    public async Task An_approval_racing_the_pdpa_erasure_leaves_the_rating_right_either_way()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHostWithoutRetries();
        var leaving = await CustomerAsync(factory, "erased");
        var staying = await CustomerAsync(factory, "kept");
        var leavingId = await MustCreateAsync(leaving.Client, product, 1);
        var stayingId = await MustCreateAsync(staying.Client, product, 5);
        var staff = await StaffAsync(factory, Permissions.Reviews.Approve);
        await ModerateOkAsync(staff, ApproveUrl(stayingId), stayingId);
        var loaded = await RowVersionAsync(leavingId);

        Task<(HttpStatusCode Status, JsonElement Body)> approve;
        Task<int> erase;
        await using var scope = factory.Services.CreateAsyncScope();
        var erasure = scope.ServiceProvider.GetRequiredService<IProductReviewErasure>();

        await using (var hold = await HoldStatsRowAsync(product))
        {
            approve = SendAsync(staff, HttpMethod.Post, ApproveUrl(leavingId), new { rowVersion = loaded });
            erase = Task.Run(() => erasure.EraseForCustomerAsync(leaving.Id, Token), Token);

            await hold.WaitForBlockedAsync(2);
        }

        var (approveStatus, approveBody) = await approve;
        Assert.Equal(1, await erase);
        Assert.True(
            approveStatus is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"The approval answered {(int)approveStatus}: {approveBody}");

        Assert.Null(await ReviewRowAsync(leavingId));
        Assert.Equal((5m, 1), await StatsAsync(product));
    }

    // ---- content rules the display name and the title bypass (defects found by the tester) ------------------------

    /// <summary>
    /// DEFECT (tester, #189): the review text refuses bidi controls ("Trojan Source"), but the display name is copied
    /// from the profile's first and last name, which registration does not check, so a first name carrying U+202E
    /// reaches every product page and the admin screen. In the meta line it reverses the "Verified buyer" badge and
    /// the date that follow it in the same paragraph.
    /// </summary>
    [Fact]
    public async Task A_display_name_never_carries_bidi_or_control_characters_from_the_profile()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();
        // Registration now refuses these characters (#189 D1), so the names are written straight into the account,
        // as a name saved before that rule existed would be. The display name must still come out clean.
        var customer = await CustomerAsync(factory, "bidi");
        using (var scope = SqlServer.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
                .Where(user => user.Id == customer.Id)
                .ExecuteUpdateAsync(
                    user => user
                        .SetProperty(row => row.FirstName, "Ni" + (char)0x202E + "mal" + (char)0x07)
                        .SetProperty(row => row.LastName, (char)0x2066 + "Perera"),
                    Token);
        }

        var (status, body) = await CreateReviewAsync(customer.Client, product, new { rating = 5 });
        Assert.Equal(HttpStatusCode.Created, status);

        var displayName = body.GetProperty("review").GetProperty("displayName").GetString()!;
        Assert.DoesNotContain(displayName, character =>
            char.IsControl(character) || (character >= 0x202A && character <= 0x202E) || (character >= 0x2066 && character <= 0x2069)
            || character == 0x200E || character == 0x200F || character == 0x061C);
        Assert.Equal("Nimal P.", displayName);
    }

    /// <summary>
    /// DEFECT (tester, #189, low): "a title on two lines" is refused for \n and \r, but U+2028 LINE SEPARATOR and
    /// U+2029 PARAGRAPH SEPARATOR are neither control characters nor checked, so they pass.
    /// </summary>
    [Theory]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x0085)]
    public async Task A_title_with_a_unicode_line_or_paragraph_separator_is_refused(int separator)
    {
        var title = "First line" + (char)separator + "second line";
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory, "separator");

        var (status, body) = await CreateReviewAsync(customer.Client, product, new { rating = 3, title });

        Assert.True(status == HttpStatusCode.BadRequest, $"A title with a line separator answered {(int)status}: {body}");
        Assert.True(body.GetProperty("errors").TryGetProperty("title", out _));
    }

    // ---- a removed product ------------------------------------------------------------------------------------------

    /// <summary>
    /// Doc 11 P12: "Product removed → reviews stay stored but are not shown." A soft-deleted product's review is kept,
    /// is off the admin list and its counts, cannot be moderated (404) or edited (404), and its public route is 404.
    /// The product is restored afterwards (this test owns it for its length).
    /// </summary>
    [Fact]
    public async Task A_removed_products_review_is_kept_but_cannot_be_listed_moderated_or_edited()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory, "removed");
        var reviewId = await MustCreateAsync(customer.Client, product, 4, "Removed product review");
        var staff = await StaffAsync(factory, Permissions.Reviews.View, Permissions.Reviews.Approve, Permissions.Reviews.Edit);
        var version = await RowVersionAsync(reviewId);

        var (_, before) = await GetJsonAsync(staff, "/api/admin/reviews?status=All&pageSize=100&search=Removed%20product%20review");
        Assert.Contains(reviewId, before.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
        var pendingBefore = before.GetProperty("counts").GetProperty("pending").GetInt32();

        await SetProductDeletedAsync(product, deleted: true);

        try
        {
            var (_, after) = await GetJsonAsync(staff, "/api/admin/reviews?status=All&pageSize=100&search=Removed%20product%20review");
            Assert.DoesNotContain(reviewId, after.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
            Assert.Equal(pendingBefore - 1, after.GetProperty("counts").GetProperty("pending").GetInt32());

            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(staff, HttpMethod.Post, ApproveUrl(reviewId), new { rowVersion = version })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(staff, HttpMethod.Post, HideUrl(reviewId), new { rowVersion = version })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await EditReviewAsync(customer.Client, product, new { rating = 1 })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await GetJsonAsync(customer.Client, $"/api/catalog/products/{product.Slug}/reviews")).Status);

            var row = await ReviewRowAsync(reviewId);
            Assert.NotNull(row);
            Assert.Equal(ReviewStatus.Pending, row.Status);
            Assert.Equal(4, row.Rating);
        }
        finally
        {
            await SetProductDeletedAsync(product, deleted: false);
        }
    }

    private async Task SetProductDeletedAsync(TestProduct product, bool deleted)
    {
        using var scope = SqlServer.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE catalog.Products SET IsDeleted = {0} WHERE Id = {1};",
            [deleted, product.Id],
            CancellationToken.None);
    }

    private async Task SetOrderAsync(int orderId, int? customerUserId, OrderStatus? status)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Orders
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(order => order.SetProperty(row => row.CustomerUserId, customerUserId), Token);

        if (status is { } newStatus)
        {
            await db.Orders
                .Where(order => order.Id == orderId)
                .ExecuteUpdateAsync(order => order.SetProperty(row => row.Status, newStatus), Token);
        }
    }
}
