using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Catalog;

/// <summary>
/// The storefront's review routes (PDP-08; doc 11 P12; #189): the product page's list and summary, and a signed-in
/// customer's own review — the verified-buyer rule, one review per product, validation, plain text, the approval
/// switch and the rate limit.
/// </summary>
public sealed class ReviewSubmissionEndpointTests(SqlServerDatabaseFixture sqlServer) : ReviewTestBase(sqlServer)
{
    // ---- who may write ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_signed_out_shopper_cannot_read_write_or_edit_a_review()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        using var anonymous = CreateClient(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetJsonAsync(anonymous, Url(product))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateReviewAsync(anonymous, product, new { rating = 5 })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EditReviewAsync(anonymous, product, new { rating = 5 })).Status);
    }

    /// <summary>A staff session is not a customer session: the customer scheme rejects it.</summary>
    [Fact]
    public async Task A_staff_session_cannot_write_a_review()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var staff = await StaffAsync(factory, SpencerDenim.Domain.Identity.Permissions.Reviews.Approve);

        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateReviewAsync(staff, product, new { rating = 5 })).Status);
    }

    [Fact]
    public async Task A_product_that_is_not_published_cannot_be_reviewed_or_listed()
    {
        await ReadyAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        int unpublishedId;
        using (var scope = SqlServer.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            unpublishedId = await db.Products.AsNoTracking().Where(product => !product.IsPublished).Select(product => product.Id).FirstOrDefaultAsync(Token);
        }

        var missing = new TestProduct(unpublishedId == 0 ? int.MaxValue : unpublishedId, "no-such-product", 0, 0, 0);

        var (status, body) = await CreateReviewAsync(customer.Client, missing, new { rating = 4 });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("product_not_found", body.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await GetJsonAsync(customer.Client, Url(missing))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetJsonAsync(customer.Client, "/api/catalog/products/no-such-product-189/reviews")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetJsonAsync(customer.Client, "/api/catalog/products/NOT%20A%20SLUG/reviews")).Status);
    }

    /// <summary>
    /// #189 D1, at the door: registration and the profile refuse a name carrying a control or bidi character, so a
    /// new account can never give a review a public name that reorders the text beside it.
    /// </summary>
    [Fact]
    public async Task Registration_and_the_profile_refuse_a_name_with_hidden_characters()
    {
        await ReadyAsync();
        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        using var register = await client.PostAsJsonAsync(
            "/api/account/register",
            new { firstName = "Ni" + (char)0x202E + "mal", lastName = "Perera", email = UniqueEmail("bidi-register"), password = GoodPassword, phone = (string?)null },
            Token);
        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.True((await register.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("errors").TryGetProperty("firstName", out _));

        var customer = await CustomerAsync(factory, "bidi-profile");
        using var profile = await customer.Client.PutAsJsonAsync(
            "/api/account/profile",
            new { firstName = "Nimal", lastName = (char)0x2066 + "Perera", phone = (string?)null, defaultDistrictId = (int?)null },
            Token);
        Assert.Equal(HttpStatusCode.BadRequest, profile.StatusCode);
        Assert.True((await profile.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("errors").TryGetProperty("lastName", out _));
    }

    /// <summary>A 400 body does not spend the hourly budget; anything that reaches the database does.</summary>
    [Fact]
    public async Task A_refused_body_does_not_spend_the_hourly_budget()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        for (var attempt = 0; attempt < 15; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await CreateReviewAsync(customer.Client, product, new { rating = 9 })).Status);
        }

        Assert.Equal(HttpStatusCode.Created, (await CreateReviewAsync(customer.Client, product, new { rating = 4 })).Status);
    }

    // ---- approval switch ------------------------------------------------------------------------------------

    /// <summary>The seeded default: the review waits, is not listed, and does not move the rating.</summary>
    [Fact]
    public async Task By_default_a_review_waits_for_approval_and_changes_nothing_a_shopper_sees()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        var before = await StatsAsync(product);
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        var (status, body) = await CreateReviewAsync(customer.Client, product, new { rating = 5, title = "Great", body = "Fits well." });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Pending", body.GetProperty("outcome").GetString());
        Assert.Equal("Pending", body.GetProperty("review").GetProperty("status").GetString());
        Assert.Equal("Nimal P.", body.GetProperty("review").GetProperty("displayName").GetString());

        var (_, page) = await GetJsonAsync(customer.Client, $"/api/catalog/products/{product.Slug}/reviews");
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, page.GetProperty("summary").GetProperty("count").GetInt32());

        // Nothing approved, so the recompute writes the truth: no rating. The demo seed's figure, if any, is gone.
        Assert.Equal((null, 0), await StatsAsync(product));
        _ = before;
    }

    [Fact]
    public async Task With_approval_off_a_review_is_published_at_once_and_feeds_the_rating()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var first = await CustomerAsync(factory, "first");
        var second = await CustomerAsync(factory, "second", "Kamala", "Silva");

        var (_, body) = await CreateReviewAsync(first.Client, product, new { rating = 5 });
        Assert.Equal("Published", body.GetProperty("outcome").GetString());
        await MustCreateAsync(second.Client, product, 4, "Good", "Nice cloth");

        Assert.Equal((4.5m, 2), await StatsAsync(product));

        var (status, page) = await GetJsonAsync(first.Client, $"/api/catalog/products/{product.Slug}/reviews");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(4.5m, page.GetProperty("summary").GetProperty("average").GetDecimal());

        // Newest first.
        Assert.Equal("Kamala S.", page.GetProperty("items")[0].GetProperty("displayName").GetString());

        var distribution = page.GetProperty("summary").GetProperty("distribution").EnumerateArray()
            .Select(bucket => (bucket.GetProperty("stars").GetInt32(), bucket.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal([(5, 1), (4, 1), (3, 0), (2, 0), (1, 0)], distribution);
    }

    // ---- verified buyer -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_delivered_order_linked_to_the_account_makes_the_review_verified()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var orderId = await OrderAsync(product, customer.Id, customer.Email, OrderStatus.Delivered);

        var (_, mine) = await GetJsonAsync(customer.Client, Url(product));
        Assert.True(mine.GetProperty("wouldBeVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("review").ValueKind);

        var reviewId = await MustCreateAsync(customer.Client, product, 5);

        var row = await ReviewRowAsync(reviewId);
        Assert.True(row!.IsVerifiedBuyer);
        Assert.Equal(orderId, row.OrderId);
    }

    /// <summary>
    /// #162's lesson: a guest order with the customer's email address is not theirs until it is linked, and linking
    /// needs the address confirmed. An email match alone never earns the badge.
    /// </summary>
    [Fact]
    public async Task A_delivered_guest_order_with_the_same_email_is_not_enough()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        await OrderAsync(product, customerUserId: null, customer.Email, OrderStatus.Delivered);

        var (_, mine) = await GetJsonAsync(customer.Client, Url(product));
        Assert.False(mine.GetProperty("wouldBeVerified").GetBoolean());

        var reviewId = await MustCreateAsync(customer.Client, product, 5);
        var row = await ReviewRowAsync(reviewId);
        Assert.False(row!.IsVerifiedBuyer);
        Assert.Null(row.OrderId);
    }

    [Fact]
    public async Task Another_accounts_delivered_order_is_not_enough_even_with_this_email_on_it()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var other = await CustomerAsync(factory, "other");

        // The other account's order, carrying this customer's address: the account decides, not the address.
        await OrderAsync(product, other.Id, customer.Email, OrderStatus.Delivered);

        var reviewId = await MustCreateAsync(customer.Client, product, 3);
        Assert.False((await ReviewRowAsync(reviewId))!.IsVerifiedBuyer);
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Processing)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Refunded)]
    public async Task Only_a_delivered_order_counts(OrderStatus status)
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        await OrderAsync(product, customer.Id, customer.Email, status);

        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        Assert.False((await ReviewRowAsync(reviewId))!.IsVerifiedBuyer);
    }

    [Fact]
    public async Task A_delivered_order_of_a_different_product_does_not_count()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        var otherProduct = await ProductAsync(1);
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        await OrderAsync(otherProduct, customer.Id, customer.Email, OrderStatus.Delivered);

        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        Assert.False((await ReviewRowAsync(reviewId))!.IsVerifiedBuyer);
    }

    /// <summary>Doc 11 P12: reviewing before delivery is allowed without the badge; an edit after delivery gains it.</summary>
    [Fact]
    public async Task An_edit_after_delivery_gains_the_badge()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4);
        Assert.False((await ReviewRowAsync(reviewId))!.IsVerifiedBuyer);

        var orderId = await OrderAsync(product, customer.Id, customer.Email, OrderStatus.Delivered);
        var (status, body) = await EditReviewAsync(customer.Client, product, new { rating = 5 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("review").GetProperty("isVerifiedBuyer").GetBoolean());
        Assert.Equal(orderId, (await ReviewRowAsync(reviewId))!.OrderId);
    }

    // ---- one review per customer per product --------------------------------------------------------------

    [Fact]
    public async Task A_second_review_of_the_same_product_is_refused_and_offered_as_an_edit()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 4, "First");

        var (status, body) = await CreateReviewAsync(customer.Client, product, new { rating = 1, title = "Second" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("already_reviewed", body.GetProperty("code").GetString());
        Assert.Equal(reviewId, body.GetProperty("review").GetProperty("id").GetInt32());
        Assert.Equal("First", (await ReviewRowAsync(reviewId))!.Title);
    }

    [Fact]
    public async Task Editing_with_nothing_to_edit_is_404_not_reviewed()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        var (status, body) = await EditReviewAsync(customer.Client, product, new { rating = 3 });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_reviewed", body.GetProperty("code").GetString());
    }

    /// <summary>Two tabs submitting at once: the stats-row lock serialises them, so one wins and one is told.</summary>
    [Fact]
    public async Task Simultaneous_first_reviews_from_one_customer_store_exactly_one()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            CreateReviewAsync(customer.Client, product, new { rating = 5, title = $"Tab {index}" })));

        Assert.Equal(1, attempts.Count(attempt => attempt.Status == HttpStatusCode.Created));
        Assert.Equal(5, attempts.Count(attempt => attempt.Status == HttpStatusCode.Conflict));

        using var scope = SqlServer.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductReviews
            .CountAsync(review => review.ProductId == product.Id && review.CustomerUserId == customer.Id, Token));
    }

    /// <summary>Doc 11 §7 G-32: an edited approved review returns to Pending and leaves the rating.</summary>
    [Fact]
    public async Task Editing_an_approved_review_returns_it_to_pending_and_out_of_the_rating()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 2);
        Assert.Equal((2m, 1), await StatsAsync(product));

        await SetRequireApprovalAsync(true);
        var (status, body) = await EditReviewAsync(customer.Client, product, new { rating = 5, body = "Changed my mind" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Pending", body.GetProperty("outcome").GetString());
        Assert.Equal((null, 0), await StatsAsync(product));

        var row = await ReviewRowAsync(reviewId);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.NotNull(row.UpdatedAtUtc);
        Assert.Equal("Nimal P.", row.DisplayName);
    }

    [Fact]
    public async Task Editing_a_hidden_review_keeps_it_hidden_even_with_approval_off()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var reviewId = await MustCreateAsync(customer.Client, product, 1, "Rude");
        await ForceStatusAsync(reviewId, ReviewStatus.Hidden);

        var (_, body) = await EditReviewAsync(customer.Client, product, new { rating = 1, title = "Still rude" });

        Assert.Equal("StillHidden", body.GetProperty("outcome").GetString());
        Assert.Equal(ReviewStatus.Hidden, (await ReviewRowAsync(reviewId))!.Status);
        Assert.Equal((null, 0), await StatsAsync(product));
    }

    // ---- validation edges -----------------------------------------------------------------------------------

    public static TheoryData<string, string> RefusedBodies => new()
    {
        { """{"rating":0}""", "rating" },
        { """{"rating":6}""", "rating" },
        { """{"rating":4.5}""", "rating" },
        { """{"rating":"4"}""", "rating" },
        { """{"rating":true}""", "rating" },
        { """{}""", "rating" },
        { """{"rating":null}""", "rating" },
        { """{"rating":4,"title":7}""", "title" },
        { """{"rating":4,"body":["x"]}""", "body" },
        { """{"rating":4,"title":"line one\nline two"}""", "title" },
        { """{"rating":4,"body":"bell \u0007"}""", "body" },
        { """{"rating":4,"body":"flip \u202E txt"}""", "body" },
    };

    [Theory]
    [MemberData(nameof(RefusedBodies))]
    public async Task A_malformed_review_is_refused_with_the_field_named_and_nothing_stored(string json, string field)
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await customer.Client.PostAsync(new Uri(Url(product), UriKind.Relative), content, Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());

        using var scope = SqlServer.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductReviews
            .AnyAsync(review => review.ProductId == product.Id, Token));
    }

    [Fact]
    public async Task Title_and_body_at_their_limits_are_stored_and_one_over_is_refused()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        var (over, overBody) = await CreateReviewAsync(customer.Client, product, new { rating = 4, title = new string('t', 121) });
        Assert.Equal(HttpStatusCode.BadRequest, over);
        Assert.True(overBody.GetProperty("errors").TryGetProperty("title", out _));

        var (overText, _) = await CreateReviewAsync(customer.Client, product, new { rating = 4, body = new string('b', 4001) });
        Assert.Equal(HttpStatusCode.BadRequest, overText);

        var reviewId = await MustCreateAsync(customer.Client, product, 4, new string('t', 120), new string('b', 4000));
        var row = await ReviewRowAsync(reviewId);
        Assert.Equal(120, row!.Title!.Length);
        Assert.Equal(4000, row.Body!.Length);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_415()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        using var content = new StringContent("rating=5", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await customer.Client.PostAsync(new Uri(Url(product), UriKind.Relative), content, Token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ---- plain text -------------------------------------------------------------------------------------------

    /// <summary>
    /// Markup is stored as typed and travels as a JSON string; the storefront renders it as text (its own tests pin
    /// that). Nothing server-side turns it into HTML or mangles it into entities.
    /// </summary>
    [Fact]
    public async Task Markup_in_a_review_is_kept_verbatim_as_plain_text()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        const string body = "<script>alert('x')</script><img src=x onerror=alert(1)> & \"quotes\"";

        await MustCreateAsync(customer.Client, product, 5, "<b>Bold</b>", body);

        using var response = await customer.Client.GetAsync(new Uri($"/api/catalog/products/{product.Slug}/reviews", UriKind.Relative), Token);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var item = page.GetProperty("items")[0];
        Assert.Equal("<b>Bold</b>", item.GetProperty("title").GetString());
        Assert.Equal(body, item.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Sinhala_tamil_and_emoji_round_trip()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        var reviewId = await MustCreateAsync(customer.Client, product, 5, "සුපිරි 👍", "மிகவும் நல்லது");

        var row = await ReviewRowAsync(reviewId);
        Assert.Equal("සුපිරි 👍", row!.Title);
        Assert.Equal("மிகவும் நல்லது", row.Body);
    }

    // ---- caching ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_customers_own_review_is_never_cacheable()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);

        using var response = await customer.Client.GetAsync(new Uri(Url(product), UriKind.Relative), Token);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Cookie", response.Headers.Vary);
    }

    // ---- paging -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_public_list_pages_newest_first_and_clamps_the_page_size()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var product = await ProductAsync();
        await using var factory = CreateHost();

        for (var index = 0; index < 3; index++)
        {
            var customer = await CustomerAsync(factory, $"pager{index}", $"Shopper{index}", null);
            await MustCreateAsync(customer.Client, product, 3 + index);
        }

        using var anonymous = CreateClient(factory);
        var (_, first) = await GetJsonAsync(anonymous, $"/api/catalog/products/{product.Slug}/reviews?page=1&pageSize=2");
        var (_, second) = await GetJsonAsync(anonymous, $"/api/catalog/products/{product.Slug}/reviews?page=2&pageSize=2");
        var (_, huge) = await GetJsonAsync(anonymous, $"/api/catalog/products/{product.Slug}/reviews?pageSize=5000");

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(["Shopper2", "Shopper1"], first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
        Assert.Equal(["Shopper0"], second.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
        Assert.Equal(50, huge.GetProperty("pageSize").GetInt32());
        Assert.Equal(4m, first.GetProperty("summary").GetProperty("average").GetDecimal());
    }

    // ---- rate limit -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_eleventh_write_in_an_hour_is_refused_for_that_customer_only()
    {
        await ReadyAsync();
        var product = await ProductAsync();
        await using var factory = CreateHost();
        var customer = await CustomerAsync(factory);
        var other = await CustomerAsync(factory, "other");

        await MustCreateAsync(customer.Client, product, 3);

        for (var edit = 0; edit < 9; edit++)
        {
            Assert.Equal(HttpStatusCode.OK, (await EditReviewAsync(customer.Client, product, new { rating = 1 + (edit % 5) })).Status);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Url(product), UriKind.Relative))
        {
            Content = JsonContent.Create(new { rating = 5 }),
        };
        using var refused = await customer.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(TimeSpan.FromHours(1), refused.Headers.RetryAfter?.Delta);

        // Another customer's budget is their own.
        Assert.Equal(HttpStatusCode.Created, (await CreateReviewAsync(other.Client, product, new { rating = 4 })).Status);
    }

    // ---- PDPA ---------------------------------------------------------------------------------------------------

    /// <summary>Doc 08 §3.1 step 2: the hook P17 will call deletes the reviews and recomputes every affected rating.</summary>
    [Fact]
    public async Task Erasing_a_customers_reviews_deletes_them_and_recomputes_each_rating()
    {
        await ReadyAsync();
        await SetRequireApprovalAsync(false);
        var first = await ProductAsync();
        var second = await ProductAsync(1);
        await using var factory = CreateHost();
        var leaving = await CustomerAsync(factory, "leaving");
        var staying = await CustomerAsync(factory, "staying");

        await MustCreateAsync(leaving.Client, first, 1);
        await MustCreateAsync(leaving.Client, second, 1);
        await MustCreateAsync(staying.Client, first, 5);
        Assert.Equal((3m, 2), await StatsAsync(first));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var erasure = scope.ServiceProvider.GetRequiredService<SpencerDenim.Application.Catalog.Reviews.IProductReviewErasure>();
            Assert.Equal(2, await erasure.EraseForCustomerAsync(leaving.Id, Token));
        }

        Assert.Equal((5m, 1), await StatsAsync(first));
        Assert.Equal((null, 0), await StatsAsync(second));

        using var check = SqlServer.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().ProductReviews
            .AnyAsync(review => review.CustomerUserId == leaving.Id, Token));
    }
}
