using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// An in-memory <see cref="IContentPageStore"/> for the handler tests.
/// <para>
/// <b>It is deliberately not more forgiving than the real one</b> — MEMORY's lesson is that a stub shaped
/// more politely than the real API tests nothing. So entities are the same instances the handler mutates
/// (as EF's tracked ones are), the slug check honours the real unique index's shape (one live row per
/// slug), and a soft-deleted page leaves every query the way the global query filter makes it leave the
/// real ones.
/// </para>
/// <para>
/// What it cannot model is the two outcomes only SQL Server decides — a lost concurrency race and a
/// unique index firing — which are forced with <see cref="NextSaveOutcome"/> and proved for real against
/// SQL Server in the integration tests.
/// </para>
/// </summary>
internal sealed class FakeContentPageStore : IContentPageStore
{
    private int _nextId = 1000;

    public List<ContentPage> Pages { get; } = [];

    /// <summary>Soft-deleted pages, kept so a test can assert they are invisible to every query.</summary>
    public List<ContentPage> Removed { get; } = [];

    public HashSet<string> DepartmentSlugs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forces the next save's answer, standing in for a race only the database can lose.</summary>
    public ContentSaveOutcome? NextSaveOutcome { get; set; }

    public int Saves { get; private set; }

    public int? LastActingUserId { get; private set; }

    public Task<ContentPage?> FindAsync(int pageId, CancellationToken cancellationToken) =>
        Task.FromResult(Pages.FirstOrDefault(page => page.Id == pageId));

    public void Add(ContentPage page)
    {
        page.Id = _nextId++;
        Pages.Add(page);
    }

    public Task<bool> SlugTakenAsync(string slug, int? exceptPageId, CancellationToken cancellationToken) =>
        Task.FromResult(Pages.Any(page =>
            string.Equals(page.Slug, slug, StringComparison.OrdinalIgnoreCase)
            && (exceptPageId is null || page.Id != exceptPageId)));

    public Task<bool> DepartmentSlugTakenAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(DepartmentSlugs.Contains(slug));

    public Task<ContentSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        Saves++;
        LastActingUserId = actingUserId;

        if (NextSaveOutcome is { } forced)
        {
            NextSaveOutcome = null;
            return Task.FromResult(forced);
        }

        // The soft-delete interceptor's effect, modelled: a page the handler marked deleted leaves the
        // live set on save, exactly as the global query filter makes it leave the real queries.
        foreach (var deleted in Pages.Where(page => page.IsDeleted).ToList())
        {
            Pages.Remove(deleted);
            Removed.Add(deleted);
        }

        return Task.FromResult(ContentSaveOutcome.Saved);
    }

    /// <summary>A live page, with a row version, as the read model would have handed it to a screen.</summary>
    public ContentPage Seed(
        int id,
        string slug,
        string title,
        string? systemKey = null,
        string bodyHtml = "",
        string? seoTitle = null,
        string? seoDescription = null,
        bool isPublished = false)
    {
        var page = new ContentPage
        {
            Id = id,
            SystemKey = systemKey,
            Slug = slug,
            Title = title,
            BodyHtml = bodyHtml,
            SeoTitle = seoTitle,
            SeoDescription = seoDescription,
            IsPublished = isPublished,
            RowVersion = [1, 2, 3, 4, 5, 6, 7, (byte)id],
        };

        Pages.Add(page);

        return page;
    }

    /// <summary>The row version as a screen would send it back.</summary>
    public static string Token(ContentPage page) => Convert.ToBase64String(page.RowVersion!);
}

/// <summary>Collects the audit rows a handler enlists, so a test can assert what the trail says.</summary>
internal sealed class RecordingAuditLogWriter : IAuditLogWriter
{
    public List<AuditLog> Entries { get; } = [];

    public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public void Enlist(AuditLog entry) => Entries.Add(entry);
}

/// <summary>A clock that does not move, so a stamped timestamp is assertable.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Records the evictions, so a test can assert the footer's cache is dropped on a publish.</summary>
internal sealed class RecordingLinksCache : IContentPageLinksCache
{
    public int Evictions { get; private set; }

    public ContentPageLinksDto? GetLinks() => null;

    public void SetLinks(ContentPageLinksDto links)
    {
    }

    public void Evict() => Evictions++;
}

/// <summary>
/// Records the homepage cache's evictions (#192: every content write evicts it, doc 08 §3.7 "evicted on
/// save"). The page is never served from here; these tests assert the write side only.
/// </summary>
internal sealed class RecordingHomePageCache : IHomePageCache
{
    public int Evictions { get; private set; }

    public HomePageDto? GetPage() => null;

    public void SetPage(HomePageDto page, DateTime? validUntilUtc)
    {
    }

    public void Evict() => Evictions++;
}
