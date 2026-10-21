using SpencerDenim.Application.Content.Admin.Contracts;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin.Abstractions;

/// <summary>
/// Read port for the admin content screens (ADM-17, GLB-10).
/// <para>
/// <b>It does not filter on <c>IsPublished</c></b>, which is the whole reason it is a separate port from
/// <see cref="SpencerDenim.Application.Content.Abstractions.IContentPageReader"/> rather than a flag on it
/// — exactly the split <c>IAdminCatalogReader</c> makes against the storefront's catalogue reader. A
/// screen that could not see a draft could not publish one, and on a fresh shop every page is a draft, so
/// the screen would be empty.
/// </para>
/// <para>
/// It <b>does</b> filter on <c>IsDeleted</c>: a removed custom page is gone, not hidden, and there is no
/// restore in this build.
/// </para>
/// </summary>
public interface IAdminContentReader
{
    /// <summary>
    /// Every live page, system pages first in their seeded order and then the custom ones by title — so
    /// the six the client has to fill in are at the top of the list on the day they are looking for them.
    /// </summary>
    Task<IReadOnlyList<AdminContentPageListItemDto>> GetPagesAsync(CancellationToken cancellationToken);

    /// <summary>One live page by id, or <see langword="null"/>.</summary>
    Task<AdminContentPageDto?> GetPageAsync(int pageId, CancellationToken cancellationToken);

    /// <summary>
    /// A page of the enquiry inbox, newest first (doc 11 P15 step 7).
    /// </summary>
    /// <param name="status">A status to show only, or <see langword="null"/> for all of them.</param>
    /// <param name="skip">How many to skip.</param>
    /// <param name="take">How many to return. The endpoint bounds this; the reader trusts it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminContactSubmissionsDto> GetSubmissionsAsync(
        ContactSubmissionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>One enquiry by id, or <see langword="null"/>.</summary>
    Task<AdminContactSubmissionDto?> GetSubmissionAsync(int submissionId, CancellationToken cancellationToken);
}

/// <summary>
/// Write port for the admin content pages (ADM-17, GLB-10).
/// <para>
/// Hands back tracked entities and takes one <see cref="SaveAsync"/>, the arrangement
/// <c>ICatalogStructureStore</c> established, so a change and its audit row are one transaction.
/// Nothing here validates: every rule — the slug spelling, the reserved-slug clash, the duplicate, the
/// publish checklist, "a system page cannot be deleted" — lives in the handlers, where it is unit-testable
/// and where a failure is a named field in a 400 rather than an exception. The port's only judgements are
/// the two the database alone can make: a row somebody else changed first, and a unique index.
/// </para>
/// </summary>
public interface IContentPageStore
{
    /// <summary>One live page for update, or <see langword="null"/>.</summary>
    Task<ContentPage?> FindAsync(int pageId, CancellationToken cancellationToken);

    /// <summary>Adds a new custom page to the unit of work. It is written by <see cref="SaveAsync"/>.</summary>
    void Add(ContentPage page);

    /// <summary><c>UX_ContentPages_Slug</c> (filtered <c>IsDeleted = 0</c>).</summary>
    Task<bool> SlugTakenAsync(string slug, int? exceptPageId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a <b>department</b> already answers this single-segment address. See
    /// <see cref="ContentPageSlugRules"/> for why this is a query and not a constant: the department list
    /// is rows, and a hard-coded copy of it is the thing that goes stale.
    /// </summary>
    Task<bool> DepartmentSlugTakenAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Commits the unit of work. Answers <see cref="ContentSaveOutcome.ConcurrencyConflict"/> rather than
    /// throwing when somebody else got there first, and <see cref="ContentSaveOutcome.DuplicateKey"/> when
    /// a unique index refused the write after the handler's own check passed — which is the race, not a
    /// substitute for the check.
    /// </summary>
    /// <param name="actingUserId">
    /// Who is saving, for the audit columns of doc 08 §1.5. Passed to the store rather than read from a
    /// context, because the Application layer has no request to read one from — the arrangement
    /// <c>ICatalogStructureStore.SaveAsync</c> established.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ContentSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>How a content-page save ended.</summary>
public enum ContentSaveOutcome
{
    Saved = 0,

    /// <summary>The row version did not match; nothing was written.</summary>
    ConcurrencyConflict = 1,

    /// <summary><c>UX_ContentPages_Slug</c> or <c>UX_ContentPages_SystemKey</c> refused it.</summary>
    DuplicateKey = 2,

    /// <summary>
    /// A picture or video the row would point at was deleted, or stopped being ready, public or of its kind,
    /// between the handler's check and the save (#215 tester M1). Read under an update lock on the asset row, the
    /// lock <c>MediaService.DeleteAsync</c> also takes first (doc 08 §5.3), so this is the authoritative answer.
    /// Nothing was written; <see cref="Admin.Abstractions.ContentMediaUsageChange.Unavailable"/> names the slots.
    /// </summary>
    MediaUnavailable = 3,

    /// <summary>
    /// The owner's group is full, counted under an update lock on the row that serialises it (a mega-menu card's
    /// department, #215 tester M2). Nothing was written.
    /// </summary>
    LimitReached = 4,
}
