using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Content;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// The four content-page write use cases (ADM-17, GLB-10).
/// <para>
/// <b>The sanitiser here is the real one</b>, not a stub. A fake that returned its input would make every
/// assertion about cleaning vacuous, and the cleaning is the security control; a fake that returned
/// something simpler would be a second implementation of the allow-list. Its own payload table lives in
/// <c>HtmlSanitiserTests</c>.
/// </para>
/// </summary>
public sealed class ContentPageWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 30, 0, TimeSpan.Zero);

    private static readonly AdminActor Actor = new(7, "staff@example.com", "203.0.113.9", "agent", "corr");

    private readonly FakeContentPageStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();
    private readonly RecordingLinksCache _cache = new();
    private readonly RecordingHomePageCache _homePage = new();

    private ContentPageWriteHandlers Handlers() =>
        new(_store, new HtmlSanitiser(), _audit, _cache, _homePage, new FixedClock(Now));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the defect this project has shipped three times ------------------------------------------

    /// <summary>
    /// <b>A save that does not mention a field must leave it exactly as it is.</b>
    /// <para>
    /// This is W1-12's blocker and W1-03's mapper, in the shape this feature has them: a page editor with
    /// two optional SEO fields beside a required title. A caller that sends only <c>bodyHtml</c> — which
    /// the API explicitly allows — would otherwise clear the search-engine title and description the
    /// client wrote last month, and nothing on any screen would say so.
    /// </para>
    /// <para>
    /// <b>The fixture holds real values in the fields the second save is silent about</b>, which is the
    /// part W1-12's 1,571 passing tests got wrong: every fixture there set those columns to
    /// <see langword="null"/>, so <c>null → null</c> round-tripped vacuously and proved nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_save_that_does_not_mention_the_seo_fields_leaves_them_alone()
    {
        var page = _store.Seed(
            1,
            "returns",
            "Returns and exchanges",
            systemKey: ContentPageKeys.Returns,
            bodyHtml: "<p>Old copy.</p>",
            seoTitle: "Returns and exchanges | Colombo Wardrobe",
            seoDescription: "How to return or exchange something you have bought from us.");

        var result = await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                page.Id,
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Set<string?>("<p>New copy.</p>"),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                FakeContentPageStore.Token(page),
                Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);

        // The field the request did mention.
        Assert.Equal("<p>New copy.</p>", page.BodyHtml);

        // The four it did not. Each held a real value, so a cleared one is a visible failure.
        Assert.Equal("returns", page.Slug);
        Assert.Equal("Returns and exchanges", page.Title);
        Assert.Equal("Returns and exchanges | Colombo Wardrobe", page.SeoTitle);
        Assert.Equal("How to return or exchange something you have bought from us.", page.SeoDescription);
    }

    /// <summary>
    /// The other half of the same rule: a field sent as <see langword="null"/> <i>is</i> cleared. Without
    /// this, "absent means leave it alone" could be implemented as "never clear anything", which would
    /// take away the only way a client can remove a search-engine description they no longer want.
    /// </summary>
    [Fact]
    public async Task A_save_that_sends_null_clears_the_field()
    {
        var page = _store.Seed(1, "about", "About us", seoTitle: "About us | Colombo Wardrobe");

        await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                page.Id,
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Set<string?>(null),
                Patched.Unset<string?>(),
                FakeContentPageStore.Token(page),
                Actor),
            Token);

        Assert.Null(page.SeoTitle);
    }

    /// <summary>A body sent as the empty string empties the page, which is how a client removes copy.</summary>
    [Fact]
    public async Task A_body_sent_empty_empties_the_page()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Our story.</p>");

        await Handlers().HandleAsync(Update(page, body: Patched.Set<string?>(string.Empty)), Token);

        Assert.Equal(string.Empty, page.BodyHtml);
    }

    /// <summary>
    /// The audit row describes the body rather than copying it, and the diff holds only what moved.
    /// <para>
    /// A trail where every row carries two copies of a terms-and-conditions page is a trail nobody reads
    /// and three years of <c>nvarchar(max)</c> nobody wanted (doc 08 §8.4).
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_audit_row_names_the_body_change_without_copying_the_body()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Old.</p>");

        await Handlers().HandleAsync(
            Update(page, body: Patched.Set<string?>("<p>A brand new paragraph about linen.</p>")),
            Token);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Update, entry.Action);
        Assert.Equal(ContentAuditTrail.ContentPageEntityType, entry.EntityType);
        Assert.Contains("characters of sanitised HTML", entry.AfterJson);
        Assert.DoesNotContain("linen", entry.AfterJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Old.", entry.BeforeJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Two bodies of the same length are two different bodies.</b>
    /// <para>
    /// This is the defect `A_save_that_does_not_mention_the_seo_fields_leaves_them_alone` found by
    /// accident, pinned deliberately. The audit trail describes a body by its character count, and the
    /// first version put that description into the snapshots the change detection reads — so a save that
    /// replaced sixteen characters with sixteen different characters looked like a save of nothing, the
    /// statement was never issued, and the screen said "Saved". The redaction now happens on the way into
    /// the audit row, after the comparison.
    /// </para>
    /// <para>
    /// The two strings below are deliberately the same length, and the assertion is that the body moved
    /// <i>and</i> that a statement went out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_body_replaced_by_one_of_exactly_the_same_length_is_still_saved()
    {
        const string Before = "<p>Old copy.</p>";
        const string After = "<p>New copy.</p>";
        Assert.Equal(Before.Length, After.Length);

        var page = _store.Seed(1, "about", "About us", bodyHtml: Before);

        var result = await Handlers().HandleAsync(Update(page, body: Patched.Set<string?>(After)), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal(After, page.BodyHtml);
        Assert.Equal(1, _store.Saves);
        Assert.Single(_audit.Entries);
    }

    /// <summary>A save of a form nobody edited is not an event: no statement, no audit row.</summary>
    [Fact]
    public async Task A_save_that_changes_nothing_writes_nothing()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Our story.</p>");

        var result = await Handlers().HandleAsync(
            Update(page, body: Patched.Set<string?>("<p>Our story.</p>")),
            Token);

        Assert.IsType<ContentWriteResult.Unchanged>(result);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _store.Saves);
    }

    /// <summary>
    /// A save that changed nothing but <i>dropped</i> something still tells the editor.
    /// <para>
    /// The case: a paste whose cleaned form is exactly what was already stored. Nothing moved, so there is
    /// no audit row — and the editor's <c>&lt;iframe&gt;</c> is still not on the page, which is the part
    /// they would otherwise never learn.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_save_that_changed_nothing_still_reports_what_was_removed()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Our story.</p>");

        var result = await Handlers().HandleAsync(
            Update(page, body: Patched.Set<string?>("<p>Our story.</p><iframe src=\"https://x.test\"></iframe>")),
            Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Contains("<iframe>", saved.RemovedFromBody, StringComparer.OrdinalIgnoreCase);
    }

    // ---- the sanitiser on the write path ----------------------------------------------------------

    /// <summary>
    /// The body reaching the column has been through the allow-list, and the result says what went.
    /// <para>
    /// The sanitiser's own payload table is <c>HtmlSanitiserTests</c>; what this proves is that the write
    /// path <i>uses</i> it, which is the part a refactor could remove without any of those tests noticing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_body_is_sanitised_on_save_and_the_removal_is_reported()
    {
        var page = _store.Seed(1, "about", "About us");

        var result = await Handlers().HandleAsync(
            Update(page, body: Patched.Set<string?>("<p onclick=\"steal()\">Hi</p><script>alert(1)</script>")),
            Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);

        Assert.DoesNotContain("script", page.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", page.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hi", page.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("<script>", saved.RemovedFromBody, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("onclick", saved.RemovedFromBody, StringComparer.OrdinalIgnoreCase);
    }

    // ---- the slug rules ---------------------------------------------------------------------------

    /// <summary>
    /// A slug the storefront already answers is refused, with the clash named.
    /// <para>
    /// Without this a page saved at <c>/bag</c> exists in the database, appears in the admin list,
    /// appears in the footer and in <c>sitemap.xml</c>, and <b>cannot be opened by anybody</b>: the
    /// address resolves to the bag, because that route is declared above the content-page route. A defect
    /// with no symptom on the screen that caused it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("bag")]
    [InlineData("search")]
    [InlineData("checkout")]
    [InlineData("wishlist")]
    [InlineData("product")]
    [InlineData("sale")]
    [InlineData("account")]
    [InlineData("api")]
    public async Task A_slug_the_storefront_already_answers_is_refused(string slug)
    {
        var result = await Handlers().HandleAsync(
            new CreateContentPageCommand("Size guide", slug, "", null, null, Actor),
            Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Equal("slug", Assert.Single(invalid.Errors).Field);
        Assert.Empty(_store.Pages);
    }

    /// <summary>
    /// A department's slug is refused too, and it is checked against the <b>table</b> rather than a
    /// constant: the department list is rows, and a hard-coded copy of it is the thing that goes stale.
    /// </summary>
    [Fact]
    public async Task A_department_slug_is_refused()
    {
        _store.DepartmentSlugs.Add("women");

        var result = await Handlers().HandleAsync(
            new CreateContentPageCommand("Women's guide", "women", "", null, null, Actor),
            Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Contains("department", Assert.Single(invalid.Errors).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Size Guide")]
    [InlineData("size guide")]
    [InlineData("-size")]
    [InlineData("size-")]
    [InlineData("size--guide")]
    [InlineData("")]
    public async Task A_slug_that_is_not_a_slug_is_refused(string slug)
    {
        var result = await Handlers().HandleAsync(
            new CreateContentPageCommand("Size guide", slug, "", null, null, Actor),
            Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Equal("slug", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_slug_another_page_holds_is_refused()
    {
        _store.Seed(1, "size-guide", "Size guide");

        var result = await Handlers().HandleAsync(
            new CreateContentPageCommand("Another guide", "size-guide", "", null, null, Actor),
            Token);

        Assert.IsType<ContentWriteResult.Invalid>(result);
    }

    /// <summary>
    /// A custom page is created as a draft and **never as a system page**, whatever the request said —
    /// and the request cannot say. A create that could name a system key could make a second `privacy`,
    /// or claim the protection against deletion for a page a client added.
    /// </summary>
    [Fact]
    public async Task A_created_page_is_a_draft_and_never_a_system_page()
    {
        var result = await Handlers().HandleAsync(
            new CreateContentPageCommand("Size guide", "size-guide", "<p>Measure here.</p>", null, null, Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);

        var page = Assert.Single(_store.Pages);
        Assert.Null(page.SystemKey);
        Assert.False(page.IsPublished);
        Assert.True(page.CanBeDeleted);
        Assert.Equal(7, _store.LastActingUserId);
    }

    // ---- publishing ------------------------------------------------------------------------------

    // ---- Major 1: markup the sanitiser will not parse ---------------------------------------------

    /// <summary>
    /// The depth bound reaching the staff member as a message on the field they typed into, rather than
    /// as a terminated process.
    /// <para>
    /// 8,500 opening tags are 25,501 characters against a 200,000 limit, so the length check in
    /// <c>CheckBody</c> is not involved and never could have been. What this pins is the <i>shape</i> of
    /// the answer: a validation error on <c>bodyHtml</c>, so the form shows it under the editor, nothing
    /// is written, and no audit row is issued.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_body_too_deeply_nested_to_parse_is_a_validation_error_on_the_field()
    {
        var page = _store.Seed(
            1, "returns", "Returns and exchanges", bodyHtml: "<p>The copy the client wrote.</p>");

        var result = await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                page.Id,
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Set<string?>(string.Concat(Enumerable.Repeat("<b>", 8_500)) + "x"),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                FakeContentPageStore.Token(page),
                Actor),
            Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == "bodyHtml");

        // And the copy that was there is still there: a refusal must not be a deletion.
        Assert.Equal("<p>The copy the client wrote.</p>", page.BodyHtml);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// The read-path half of the same finding. A row like this cannot be written through the API — the
    /// test above refuses it — so it got there through a support script, a data fix or a restore, which
    /// is exactly the case the sanitiser port's remarks say the API cannot assume away.
    /// <para>
    /// The publish gate has to read that body to count its images, cannot, and therefore refuses. A gate
    /// that cannot check must not pass: the alternative is a page going on the shop with its alt text
    /// unexamined.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Publishing_is_refused_when_the_stored_body_cannot_be_parsed()
    {
        var page = _store.Seed(
            1, "about", "About us", bodyHtml: string.Concat(Enumerable.Repeat("<b>", 8_500)) + "x");

        var result = await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, true, FakeContentPageStore.Token(page), Actor),
            Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.BodyNotReadable, conflict.Code);
        Assert.False(page.IsPublished);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// And taking such a page <i>off</i> the shop is still allowed, which is the whole reason the refusal
    /// above is on the publish branch only. Refusing both would leave a page nobody can unpublish.
    /// </summary>
    [Fact]
    public async Task Unpublishing_is_allowed_even_when_the_stored_body_cannot_be_parsed()
    {
        var page = _store.Seed(
            1,
            "about",
            "About us",
            bodyHtml: string.Concat(Enumerable.Repeat("<b>", 8_500)) + "x",
            isPublished: true);

        var result = await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, false, FakeContentPageStore.Token(page), Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.False(page.IsPublished);
    }

    /// <summary>
    /// Doc 08 §3.3 and GLB-12: a public image needs alt text before the content using it is published.
    /// The refusal is a 409 with the count, because nothing the staff member typed in this request is
    /// wrong — the publish instruction is well formed and the page is not ready.
    /// </summary>
    [Fact]
    public async Task Publishing_is_refused_while_an_image_has_no_alt_text()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Us</p><img src=\"/media/a.webp\">");

        var result = await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, true, FakeContentPageStore.Token(page), Actor),
            Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.AltTextRequired, conflict.Code);
        Assert.Equal(1, conflict.Details!["imagesMissingAltText"]);
        Assert.False(page.IsPublished);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Publishing_succeeds_once_every_image_has_alt_text()
    {
        var page = _store.Seed(
            1, "about", "About us", bodyHtml: "<p>Us</p><img src=\"/media/a.webp\" alt=\"Our studio\">");

        var result = await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, true, FakeContentPageStore.Token(page), Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.True(page.IsPublished);
        Assert.Equal(Now.UtcDateTime, page.PublishedAtUtc);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.StatusChange, entry.Action);

        // The eviction that is load-bearing: this statement is what puts the page in the footer and in
        // sitemap.xml.
        Assert.Equal(1, _cache.Evictions);
        Assert.Equal(1, _homePage.Evictions);
    }

    /// <summary>
    /// A page with no copy at all can still be published, and the Contact page is why that matters: its
    /// content is the enquiry form and the Settings details, so it is the one of the six a client can put
    /// on the shop without writing a word. Refusing an empty body would take the contact form off the
    /// shop until the client wrote an introduction nobody asked them for.
    /// </summary>
    [Fact]
    public async Task A_page_with_no_copy_can_still_be_published()
    {
        var page = _store.Seed(1, "contact", "Contact us", systemKey: ContentPageKeys.Contact);

        var result = await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, true, FakeContentPageStore.Token(page), Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.True(page.IsPublished);
    }

    /// <summary>
    /// Unpublishing keeps <c>PublishedAtUtc</c>: the date the client first published their returns policy
    /// does not stop being true because the page is being rewritten.
    /// </summary>
    [Fact]
    public async Task Unpublishing_keeps_the_date_it_was_first_published()
    {
        var page = _store.Seed(1, "about", "About us", isPublished: true);
        page.PublishedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, false, FakeContentPageStore.Token(page), Actor),
            Token);

        Assert.False(page.IsPublished);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), page.PublishedAtUtc);
    }

    [Fact]
    public async Task Publishing_a_page_that_is_already_published_is_unchanged()
    {
        var page = _store.Seed(1, "about", "About us", isPublished: true);

        var result = await Handlers().HandleAsync(
            new SetContentPagePublishedCommand(page.Id, true, FakeContentPageStore.Token(page), Actor),
            Token);

        Assert.IsType<ContentWriteResult.Unchanged>(result);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// W1-03's <c>published_product_would_break</c>, one feature over: an edit must not leave a page that
    /// is <b>on the shop</b> in a state its own publish rule forbids. Refused rather than applied, and
    /// rather than silently unpublishing — taking a page off the shop is its own deliberate act.
    /// </summary>
    [Fact]
    public async Task An_edit_that_would_break_a_published_page_is_refused()
    {
        var page = _store.Seed(
            1, "about", "About us", bodyHtml: "<p>Us</p>", isPublished: true);

        var result = await Handlers().HandleAsync(
            Update(page, body: Patched.Set<string?>("<p>Us</p><img src=\"/media/a.webp\">")),
            Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.PublishedPageWouldBreak, conflict.Code);
        Assert.Equal("<p>Us</p>", page.BodyHtml);
        Assert.True(page.IsPublished);
    }

    /// <summary>The same edit on a draft is allowed: a draft may hold an image that is not ready yet.</summary>
    [Fact]
    public async Task The_same_edit_on_a_draft_is_allowed()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Us</p>");

        var result = await Handlers().HandleAsync(
            Update(page, body: Patched.Set<string?>("<p>Us</p><img src=\"/media/a.webp\">")),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Contains("<img", page.BodyHtml, StringComparison.Ordinal);
    }

    // ---- deletion --------------------------------------------------------------------------------

    /// <summary>
    /// Doc 11 P13: "System pages cannot be deleted." A 409 and not a 403 — the caller holds
    /// <c>Content.Delete</c>, and nothing about their permissions would change the answer.
    /// </summary>
    [Theory]
    [InlineData(ContentPageKeys.About)]
    [InlineData(ContentPageKeys.Contact)]
    [InlineData(ContentPageKeys.Returns)]
    [InlineData(ContentPageKeys.Shipping)]
    [InlineData(ContentPageKeys.Privacy)]
    [InlineData(ContentPageKeys.Terms)]
    public async Task A_system_page_cannot_be_deleted(string systemKey)
    {
        var page = _store.Seed(1, systemKey, "A system page", systemKey: systemKey);

        var result = await Handlers().HandleAsync(
            new DeleteContentPageCommand(page.Id, FakeContentPageStore.Token(page), Actor),
            Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.SystemPageNotDeletable, conflict.Code);
        Assert.False(page.IsDeleted);
        Assert.Contains(page, _store.Pages);
    }

    [Fact]
    public async Task A_custom_page_is_soft_deleted_and_leaves_the_live_set()
    {
        var page = _store.Seed(1, "size-guide", "Size guide");

        var result = await Handlers().HandleAsync(
            new DeleteContentPageCommand(page.Id, FakeContentPageStore.Token(page), Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.True(page.IsDeleted);
        Assert.Equal(7, page.DeletedByUserId);
        Assert.DoesNotContain(page, _store.Pages);
        Assert.Contains(page, _store.Removed);
        Assert.Equal(AuditActions.SoftDelete, Assert.Single(_audit.Entries).Action);
    }

    // ---- concurrency -----------------------------------------------------------------------------

    /// <summary>
    /// Doc 11 P13's own edge case: "Two editors save the same page → the second is told it changed and
    /// must reload (row version)."
    /// </summary>
    [Fact]
    public async Task A_stale_row_version_is_refused_before_anything_is_written()
    {
        var page = _store.Seed(1, "about", "About us", bodyHtml: "<p>Us</p>");

        var result = await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                page.Id,
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Set<string?>("<p>Mine</p>"),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9]),
                Actor),
            Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.StaleRowVersion, conflict.Code);
        Assert.Equal("<p>Us</p>", page.BodyHtml);
        Assert.Equal(0, _store.Saves);
    }

    /// <summary>
    /// A request with no token at all is refused too. A screen that forgot to send one cannot have seen
    /// the current row, and allowing the write would make optimistic concurrency decorative.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    public async Task A_missing_or_malformed_row_version_is_refused(string? token)
    {
        var page = _store.Seed(1, "about", "About us");

        var result = await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                page.Id,
                Patched.Set<string?>("Changed"),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                token,
                Actor),
            Token);

        Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal("About us", page.Title);
    }

    [Fact]
    public async Task A_race_lost_at_the_database_is_a_conflict_rather_than_an_exception()
    {
        var page = _store.Seed(1, "about", "About us");
        _store.NextSaveOutcome = ContentSaveOutcome.ConcurrencyConflict;

        var result = await Handlers().HandleAsync(Update(page, title: Patched.Set<string?>("Changed")), Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.StaleRowVersion, conflict.Code);
    }

    [Fact]
    public async Task A_unique_index_firing_after_the_check_is_a_conflict()
    {
        var page = _store.Seed(1, "about", "About us");
        _store.NextSaveOutcome = ContentSaveOutcome.DuplicateKey;

        var result = await Handlers().HandleAsync(Update(page, slug: Patched.Set<string?>("our-story")), Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.DuplicateKey, conflict.Code);
    }

    [Fact]
    public async Task A_page_that_does_not_exist_is_not_found()
    {
        var result = await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                404,
                Patched.Set<string?>("Changed"),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                "AAAAAAAAAAA=",
                Actor),
            Token);

        Assert.IsType<ContentWriteResult.NotFound>(result);
    }

    /// <summary>An edit of a system page's own fields is allowed — doc 11 P13 step 12 lists them.</summary>
    [Fact]
    public async Task A_system_pages_heading_slug_and_body_can_all_be_edited()
    {
        var page = _store.Seed(1, "returns", "Returns", systemKey: ContentPageKeys.Returns);

        var result = await Handlers().HandleAsync(
            new UpdateContentPageCommand(
                page.Id,
                Patched.Set<string?>("Our returns promise"),
                Patched.Set<string?>("returns-and-exchanges"),
                Patched.Set<string?>("<p>Thirty days.</p>"),
                Patched.Unset<string?>(),
                Patched.Unset<string?>(),
                FakeContentPageStore.Token(page),
                Actor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal("Our returns promise", page.Title);
        Assert.Equal("returns-and-exchanges", page.Slug);

        // The key itself never moves: it is what makes the page undeletable and what the storefront
        // matches the contact panel on.
        Assert.Equal(ContentPageKeys.Returns, page.SystemKey);
    }

    private static UpdateContentPageCommand Update(
        ContentPage page,
        Patched<string?> title = default,
        Patched<string?> slug = default,
        Patched<string?> body = default) =>
        new(
            page.Id,
            title,
            slug,
            body,
            Patched.Unset<string?>(),
            Patched.Unset<string?>(),
            FakeContentPageStore.Token(page),
            Actor);
}
