using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// Create a custom content page (ADM-17, GLB-10, doc 11 P13 step 12: "or creates extra pages").
/// </summary>
/// <param name="Title">The heading. Required.</param>
/// <param name="Slug">The address. Required, and must be free and not one the storefront already answers.</param>
/// <param name="BodyHtml">The copy. Sanitised here, and may be empty.</param>
/// <param name="SeoTitle">Optional.</param>
/// <param name="SeoDescription">Optional.</param>
/// <param name="Actor">Who is asking, for the audit row.</param>
public sealed record CreateContentPageCommand(
    string? Title,
    string? Slug,
    string? BodyHtml,
    string? SeoTitle,
    string? SeoDescription,
    AdminActor Actor);

/// <summary>
/// Edit a content page's title, slug, body and SEO fields (ADM-17, GLB-10, doc 11 P13 step 12).
/// <para>
/// <b>Absent and null are different, and that is the single most important thing about this command.</b>
/// Each field is a <see cref="Patched{T}"/>: <c>Unset</c> means "I am not writing this field" and a set
/// value of <see langword="null"/> means "clear it". The two were collapsed three times in this project's
/// history — W1-12 cleared every category image, and W1-03's mapper cleared four of five collections
/// behind a comment promising it could not — and a page editor with two optional SEO fields beside a
/// required title is exactly that shape: a caller who sends only <c>bodyHtml</c> would otherwise wipe the
/// SEO description the client wrote last month.
/// </para>
/// <para>
/// The endpoint reads presence off the JSON rather than binding a record, because a bound record cannot
/// express it — a missing property and an explicit null both arrive as null. That is the arrangement
/// <c>UpdateContactAndSocialCommand</c> adopted after the same reasoning, and the comment on it says why
/// in full.
/// </para>
/// </summary>
/// <param name="PageId">Which page.</param>
/// <param name="Title">Required when set; it cannot be cleared, because a page needs a heading.</param>
/// <param name="Slug">Required when set; it cannot be cleared, because a page needs an address.</param>
/// <param name="BodyHtml">Set to the empty string to empty the page; sanitised when set.</param>
/// <param name="SeoTitle">Set to null to clear.</param>
/// <param name="SeoDescription">Set to null to clear.</param>
/// <param name="RowVersion">The row's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit row.</param>
public sealed record UpdateContentPageCommand(
    int PageId,
    Patched<string?> Title,
    Patched<string?> Slug,
    Patched<string?> BodyHtml,
    Patched<string?> SeoTitle,
    Patched<string?> SeoDescription,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Publish or unpublish a page (ADM-17, GLB-10). Its own command and its own route, so taking a page off
/// the shop is a deliberate act with its own <c>StatusChange</c> audit row and never a side effect of
/// saving a form whose checkbox was stale — the rule <c>SetProductPublishedCommand</c> established.
/// </summary>
/// <param name="PageId">Which page.</param>
/// <param name="IsPublished">Where it should end up.</param>
/// <param name="RowVersion">The row's token.</param>
/// <param name="Actor">Who is asking.</param>
public sealed record SetContentPagePublishedCommand(
    int PageId,
    bool IsPublished,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Remove a custom content page (ADM-17, GLB-10). A soft delete, which releases the slug it held
/// (<c>UX_ContentPages_Slug</c> is filtered). Refused for one of the six system pages.
/// </summary>
/// <param name="PageId">Which page.</param>
/// <param name="RowVersion">The row's token.</param>
/// <param name="Actor">Who is asking.</param>
public sealed record DeleteContentPageCommand(int PageId, string? RowVersion, AdminActor Actor);

/// <summary>
/// A field that may or may not have been sent.
/// <para>
/// A two-state wrapper rather than the dictionary of strings <c>UpdateContactAndSocialCommand</c> uses.
/// That one carries four values of one type and so a dictionary loses nothing; this command's fields
/// differ in what "set" is allowed to mean — the title cannot be cleared, the body can be emptied, the
/// two SEO fields can be nulled — and a dictionary would push each of those decisions out of the type and
/// into the handler's prose.
/// </para>
/// </summary>
/// <typeparam name="T">The field's type.</typeparam>
/// <param name="IsSet">Whether the request carried this field at all.</param>
/// <param name="Value">The value it carried. Meaningless, and not read, when <paramref name="IsSet"/> is false.</param>
public readonly record struct Patched<T>(bool IsSet, T Value)
{
    /// <summary>
    /// <c>true</c> and the value when it was set. Written as a <c>Try</c> so a handler cannot read
    /// <see cref="Value"/> without having asked <see cref="IsSet"/> first, which is the mistake the whole
    /// type exists to prevent.
    /// </summary>
    public bool TryGet(out T value)
    {
        value = Value;
        return IsSet;
    }
}

/// <summary>
/// Builders for <see cref="Patched{T}"/>. A non-generic companion rather than static members on the
/// generic struct itself, which is CA1000 and also reads worse at the call site: <c>Patched.Set(title)</c>
/// infers the type where <c>Patched&lt;string?&gt;.Set(title)</c> has to be told it.
/// </summary>
public static class Patched
{
    /// <summary>A field the request carried, with the value it carried.</summary>
    public static Patched<T> Set<T>(T value) => new(true, value);

    /// <summary>
    /// A field the request did not carry. <c>default</c>, so a command field nobody filled in is unset
    /// rather than an instruction to clear the column.
    /// </summary>
    public static Patched<T> Unset<T>() => default;
}

/// <summary>
/// The four content-page write use cases (ADM-17, GLB-10). One class, because they share the validation,
/// the sanitising and the audit shape, and splitting them would mean four copies of each.
/// <para>
/// <b>The sanitiser runs on every body this class writes</b>, on the way in, with no path around it: doc
/// 08 §3.7 puts the cleaning on save, and a body that reached the column uncleaned would be rendered as
/// markup on a public page. <c>ContentPage.BodyHtml</c>'s own documentation states the invariant and these
/// are the only use cases that write it.
/// </para>
/// </summary>
public sealed class ContentPageWriteHandlers(
    IContentPageStore store,
    IHtmlSanitiser sanitiser,
    IAuditLogWriter auditLog,
    IContentPageLinksCache linksCache,
    IClock clock)
    : ICommandHandler<CreateContentPageCommand, ContentWriteResult>,
      ICommandHandler<UpdateContentPageCommand, ContentWriteResult>,
      ICommandHandler<SetContentPagePublishedCommand, ContentWriteResult>,
      ICommandHandler<DeleteContentPageCommand, ContentWriteResult>
{
    public async Task<ContentWriteResult> HandleAsync(
        CreateContentPageCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        var title = RequireTitle(errors, command.Title);
        var slug = await RequireSlugAsync(errors, command.Slug, null, cancellationToken);
        var seoTitle = CheckSeoTitle(errors, command.SeoTitle);
        var seoDescription = CheckSeoDescription(errors, command.SeoDescription);
        var body = CheckBody(errors, command.BodyHtml);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var now = clock.UtcNow.UtcDateTime;

        var page = new ContentPage
        {
            // No SystemKey: only the seeder creates one of the six, and there is no way to ask for one
            // here. A request that could name a system key would be a request that could create a second
            // `privacy`, or claim the protection against deletion for a page a client added.
            SystemKey = null,
            Slug = slug!,
            Title = title!,
            BodyHtml = body.Html,
            SeoTitle = seoTitle,
            SeoDescription = seoDescription,

            // A new page is always a draft, whatever the request said — and the request cannot say. Going
            // on the shop is its own act with its own audit row (SetContentPagePublishedCommand), and a
            // create-and-publish in one step would skip the alt-text checklist.
            IsPublished = false,
        };

        store.Add(page);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            ContentAuditTrail.ContentPageEntityType,
            "new",
            before: null,
            after: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = page.Slug,
                ["title"] = page.Title,
                [ContentAuditTrail.BodyField] = ContentAuditTrail.DescribeBody(page.BodyHtml),
                ["seoTitle"] = page.SeoTitle,
                ["seoDescription"] = page.SeoDescription,
                ["isPublished"] = false,
            },
            command.Actor,
            now));

        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) is { } outcome && outcome != ContentSaveOutcome.Saved)
        {
            return FromSaveOutcome(outcome);
        }

        // A new page is a draft, so nothing published changed and the footer's list is still correct. The
        // cache is evicted anyway: it is a sixty-second window on a table this very statement wrote, and
        // reasoning about which writes can safely skip an eviction is how a stale cache gets shipped.
        linksCache.Evict();

        return new ContentWriteResult.Succeeded(page.Id, body.Removed);
    }

    public async Task<ContentWriteResult> HandleAsync(
        UpdateContentPageCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var page = await store.FindAsync(command.PageId, cancellationToken);
        if (page is null)
        {
            return NotFound(command.PageId);
        }

        if (ContentWriteSupport.CheckRowVersion(page.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var errors = new List<ValidationError>();

        // Each field is read only when the request carried it. Absent means "say nothing", so the stored
        // value is what the `before` snapshot holds and what the `after` snapshot holds — the diff is
        // empty for it and no statement touches the column.
        var title = page.Title;
        if (command.Title.TryGet(out var suppliedTitle))
        {
            title = RequireTitle(errors, suppliedTitle) ?? title;
        }

        var slug = page.Slug;
        if (command.Slug.TryGet(out var suppliedSlug))
        {
            slug = await RequireSlugAsync(errors, suppliedSlug, page.Id, cancellationToken) ?? slug;
        }

        var body = new SanitisedHtml(page.BodyHtml, [], 0, 0);
        var bodyChanged = false;
        if (command.BodyHtml.TryGet(out var suppliedBody))
        {
            body = CheckBody(errors, suppliedBody);
            bodyChanged = true;
        }

        var seoTitle = page.SeoTitle;
        if (command.SeoTitle.TryGet(out var suppliedSeoTitle))
        {
            seoTitle = CheckSeoTitle(errors, suppliedSeoTitle);
        }

        var seoDescription = page.SeoDescription;
        if (command.SeoDescription.TryGet(out var suppliedSeoDescription))
        {
            seoDescription = CheckSeoDescription(errors, suppliedSeoDescription);
        }

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        // W1-03's `published_product_would_break`, one feature over: an edit must not leave a page that is
        // *on the shop* in a state its own publish rule forbids. Refused rather than applied, and rather
        // than silently unpublishing, because taking a page off the shop is its own deliberate act.
        if (bodyChanged && page.IsPublished && body.ImagesMissingAltText > 0)
        {
            return AltTextConflict(
                ContentConflictCodes.PublishedPageWouldBreak,
                body.ImagesMissingAltText,
                "This page is on the shop, and the body you have sent holds "
                + Images(body.ImagesMissingAltText)
                + " with no alt text. Add the alt text, or take the page off the shop first.");
        }

        var before = Snapshot(page);

        page.Title = title;
        page.Slug = slug;
        page.SeoTitle = seoTitle;
        page.SeoDescription = seoDescription;

        if (bodyChanged)
        {
            page.BodyHtml = body.Html;
        }

        var after = Snapshot(page);
        var now = clock.UtcNow.UtcDateTime;

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            ContentAuditTrail.ContentPageEntityType,
            Id(page.Id),
            before,
            after,
            command.Actor,
            now);

        if (audit is null)
        {
            // Nothing moved, so no statement and no audit row. The removal report still travels: a paste
            // that the sanitiser reduced to exactly what was already stored is a save that changed nothing
            // *and* dropped something, and the editor needs to be told the second part.
            return body.Removed.Count > 0
                ? new ContentWriteResult.Succeeded(page.Id, body.Removed)
                : new ContentWriteResult.Unchanged(page.Id);
        }

        auditLog.Enlist(audit);

        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) is { } outcome && outcome != ContentSaveOutcome.Saved)
        {
            return FromSaveOutcome(outcome);
        }

        linksCache.Evict();

        return new ContentWriteResult.Succeeded(page.Id, body.Removed);
    }

    public async Task<ContentWriteResult> HandleAsync(
        SetContentPagePublishedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var page = await store.FindAsync(command.PageId, cancellationToken);
        if (page is null)
        {
            return NotFound(command.PageId);
        }

        if (ContentWriteSupport.CheckRowVersion(page.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        // Doc 08 §3.3 and GLB-12: "public images require alt text before the content that uses them is
        // published". Read off the *stored* body, because that is what a visitor would be served — and
        // through the sanitiser, which is the one component that parses this markup, so the publish gate
        // and the editor's warning can never disagree about the count.
        if (command.IsPublished)
        {
            var missing = sanitiser.Sanitise(page.BodyHtml).ImagesMissingAltText;
            if (missing > 0)
            {
                return AltTextConflict(
                    ContentConflictCodes.AltTextRequired,
                    missing,
                    $"This page holds {Images(missing)} with no alt text, so it cannot go on the shop yet. "
                    + "Describe each image for someone who cannot see it.");
            }
        }

        var now = clock.UtcNow.UtcDateTime;

        if (!page.SetPublished(command.IsPublished, now))
        {
            return new ContentWriteResult.Unchanged(page.Id);
        }

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.StatusChange,
            ContentAuditTrail.ContentPageEntityType,
            Id(page.Id),
            before: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["isPublished"] = !command.IsPublished,
            },
            after: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["isPublished"] = command.IsPublished,
                ["slug"] = page.Slug,
            },
            command.Actor,
            now));

        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) is { } outcome && outcome != ContentSaveOutcome.Saved)
        {
            return FromSaveOutcome(outcome);
        }

        // The one eviction that is load-bearing: this statement is what adds a page to the footer and to
        // sitemap.xml, or takes it out of both.
        linksCache.Evict();

        return new ContentWriteResult.Succeeded(page.Id);
    }

    public async Task<ContentWriteResult> HandleAsync(
        DeleteContentPageCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var page = await store.FindAsync(command.PageId, cancellationToken);
        if (page is null)
        {
            return NotFound(command.PageId);
        }

        if (ContentWriteSupport.CheckRowVersion(page.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        // Doc 11 P13's business rule, enforced here and not only hidden on the screen: the six pages the
        // footer links to and the storefront expects cannot be removed. A 409 and not a 403 — the caller
        // holds Content.Delete, and nothing about their permissions would change the answer.
        if (!page.CanBeDeleted)
        {
            return new ContentWriteResult.Conflict(
                ContentConflictCodes.SystemPageNotDeletable,
                $"“{page.Title}” is one of the shop's six built-in pages, so it cannot be removed. "
                + "Take it off the shop instead, and it stops being visible to shoppers.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["systemKey"] = page.SystemKey });
        }

        var now = clock.UtcNow.UtcDateTime;
        page.SoftDelete(now, command.Actor.UserId);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.SoftDelete,
            ContentAuditTrail.ContentPageEntityType,
            Id(page.Id),
            before: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = page.Slug,
                ["title"] = page.Title,
                ["isPublished"] = page.IsPublished,
                [ContentAuditTrail.BodyField] = ContentAuditTrail.DescribeBody(page.BodyHtml),
            },
            after: null,
            command.Actor,
            now));

        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) is { } outcome && outcome != ContentSaveOutcome.Saved)
        {
            return FromSaveOutcome(outcome);
        }

        linksCache.Evict();

        return new ContentWriteResult.Succeeded(page.Id);
    }

    /// <summary>
    /// The fields the audit diff is taken over. The body is described, never copied — see
    /// <see cref="ContentAuditTrail"/>.
    /// </summary>
    private static Dictionary<string, object?> Snapshot(ContentPage page) =>
        new(StringComparer.Ordinal)
        {
            ["slug"] = page.Slug,
            ["title"] = page.Title,
            [ContentAuditTrail.BodyField] = ContentAuditTrail.DescribeBody(page.BodyHtml),
            ["seoTitle"] = page.SeoTitle,
            ["seoDescription"] = page.SeoDescription,
        };

    private static string? RequireTitle(List<ValidationError> errors, string? supplied)
    {
        var title = supplied?.Trim();

        if (string.IsNullOrEmpty(title))
        {
            errors.Add(new ValidationError("title", "Give the page a heading."));
            return null;
        }

        if (title.Length > ContentPage.TitleMaxLength)
        {
            errors.Add(new ValidationError(
                "title",
                $"Keep the heading to {ContentPage.TitleMaxLength} characters or fewer."));
            return null;
        }

        return title;
    }

    /// <summary>
    /// The slug rules, in the order a staff member would want to hear about them: is it a slug at all, is
    /// it an address the storefront already answers, is another page on it.
    /// </summary>
    private async Task<string?> RequireSlugAsync(
        List<ValidationError> errors,
        string? supplied,
        int? exceptPageId,
        CancellationToken cancellationToken)
    {
        var slug = supplied?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(slug))
        {
            errors.Add(new ValidationError("slug", "Give the page an address, for example size-guide."));
            return null;
        }

        if (slug.Length > ContentPage.SlugMaxLength)
        {
            errors.Add(new ValidationError(
                "slug",
                $"Keep the address to {ContentPage.SlugMaxLength} characters or fewer."));
            return null;
        }

        if (!CatalogSlug.IsValid(slug))
        {
            errors.Add(new ValidationError(
                "slug",
                "An address is lower-case letters, digits and single hyphens — for example "
                + "delivery-and-returns."));
            return null;
        }

        if (ContentPageSlugRules.IsReserved(slug))
        {
            errors.Add(new ValidationError(
                "slug",
                $"“/{slug}” is already part of the shop, so a page saved there could never be "
                + "opened. Choose another address."));
            return null;
        }

        if (await store.DepartmentSlugTakenAsync(slug, cancellationToken))
        {
            errors.Add(new ValidationError(
                "slug",
                $"“/{slug}” is a department listing, so a page saved there could never be opened. "
                + "Choose another address."));
            return null;
        }

        if (await store.SlugTakenAsync(slug, exceptPageId, cancellationToken))
        {
            errors.Add(new ValidationError("slug", $"Another page is already at “/{slug}”."));
            return null;
        }

        return slug;
    }

    private static string? CheckSeoTitle(List<ValidationError> errors, string? supplied)
    {
        var value = supplied?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > ContentPage.SeoTitleMaxLength)
        {
            errors.Add(new ValidationError(
                "seoTitle",
                $"A search-engine title is at most {ContentPage.SeoTitleMaxLength} characters."));
            return null;
        }

        return value;
    }

    private static string? CheckSeoDescription(List<ValidationError> errors, string? supplied)
    {
        var value = supplied?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > ContentPage.SeoDescriptionMaxLength)
        {
            errors.Add(new ValidationError(
                "seoDescription",
                $"A search-engine description is at most {ContentPage.SeoDescriptionMaxLength} characters."));
            return null;
        }

        return value;
    }

    /// <summary>
    /// Sanitises a body and bounds its length. The length is checked <b>after</b> cleaning, because the
    /// stored value is the cleaned one — refusing a 300 KB paste that cleans down to 4 KB of text would be
    /// refusing something that fits.
    /// </summary>
    private SanitisedHtml CheckBody(List<ValidationError> errors, string? supplied)
    {
        var cleaned = sanitiser.Sanitise(supplied);

        if (cleaned.Html.Length > ContentPage.BodyHtmlMaxLength)
        {
            errors.Add(new ValidationError(
                "bodyHtml",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"That is {cleaned.Html.Length:N0} characters of markup, and a page holds at most "
                    + $"{ContentPage.BodyHtmlMaxLength:N0}. Split it into two pages.")));
        }

        return cleaned;
    }

    private static ContentWriteResult.NotFound NotFound(int pageId) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"No content page has the id {pageId}."));

    private static ContentWriteResult.Conflict AltTextConflict(string code, int missing, string message) =>
        new(
            code,
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["imagesMissingAltText"] = missing });

    private static ContentWriteResult.Conflict FromSaveOutcome(ContentSaveOutcome outcome) => outcome switch
    {
        // A change that landed between this handler's read and its save. The message differs from the
        // pre-check's deliberately: nothing was written, and the staff member has not lost their edit.
        ContentSaveOutcome.ConcurrencyConflict => new ContentWriteResult.Conflict(
            ContentConflictCodes.StaleRowVersion,
            "Somebody else changed this page while your change was being saved, so nothing was written. "
            + "Reload the page and try again."),
        ContentSaveOutcome.DuplicateKey => new ContentWriteResult.Conflict(
            ContentConflictCodes.DuplicateKey,
            "Another page took that address a moment before this save. Choose another one."),
        _ => throw new InvalidOperationException($"Unhandled save outcome {outcome}."),
    };

    private static string Id(int pageId) => pageId.ToString(CultureInfo.InvariantCulture);

    private static string Images(int count) =>
        count == 1 ? "an image" : string.Create(CultureInfo.InvariantCulture, $"{count} images");
}
