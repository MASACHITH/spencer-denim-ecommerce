using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// The newsletter subscriber screen's reads (ADM-21).
/// </summary>
/// <remarks>
/// <b>Filtered and ordered on the entity, projected last</b> — #126's lesson; <c>AdminNewsletterEndpointTests</c>
/// is this reader's backend test. Ordered by <c>ConsentAtUtc</c> because that is the date the list and its
/// filter are about (see <see cref="NewsletterSubscriberRow.ConsentAtUtc"/>); the id breaks ties so paging
/// is stable.
/// </remarks>
internal sealed class NewsletterSubscriberReader(AppDbContext dbContext) : INewsletterSubscriberReader
{
    public async Task<NewsletterSubscriberPage> ReadPageAsync(
        NewsletterSubscriberFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var matching = Matching(filter);

        if (filter.Status is { } status)
        {
            matching = matching.Where(subscriber => subscriber.Status == status);
        }

        var total = await matching.CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await matching
                .OrderByDescending(subscriber => subscriber.ConsentAtUtc)
                .ThenByDescending(subscriber => subscriber.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(subscriber => new
                {
                    subscriber.Id,
                    subscriber.Email,
                    subscriber.Status,
                    subscriber.Source,
                    subscriber.ConsentAtUtc,
                    subscriber.UnsubscribedAtUtc,
                })
                .ToListAsync(cancellationToken);

        // Over the whole table, ignoring the filters: a GROUP BY Status is served by
        // IX_NewsletterSubscribers_Status_CreatedAtUtc's leading column.
        var counts = await dbContext.NewsletterSubscribers
            .AsNoTracking()
            .GroupBy(subscriber => subscriber.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return new NewsletterSubscriberPage(
            rows
                .Select(row => new NewsletterSubscriberRow(
                    row.Id,
                    row.Email,
                    row.Status.ToString(),
                    row.Source,
                    row.ConsentAtUtc,
                    row.UnsubscribedAtUtc))
                .ToList(),
            page,
            pageSize,
            total,
            counts.FirstOrDefault(row => row.Status == NewsletterSubscriberStatus.Subscribed)?.Count ?? 0,
            counts.FirstOrDefault(row => row.Status == NewsletterSubscriberStatus.Unsubscribed)?.Count ?? 0);
    }

    public async Task<IReadOnlyList<NewsletterExportRow>> ReadSubscribedForExportAsync(
        NewsletterSubscriberFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Subscribed, in the WHERE clause, whatever the filter's own status says — see the port's remarks.
        // A constant, so it compiles to a literal.
        return await Matching(filter)
            .Where(subscriber => subscriber.Status == NewsletterSubscriberStatus.Subscribed)
            .OrderBy(subscriber => subscriber.ConsentAtUtc)
            .ThenBy(subscriber => subscriber.Id)
            .Select(subscriber => new NewsletterExportRow(
                subscriber.Email,
                subscriber.Source,
                subscriber.ConsentAtUtc))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The search and the date range, which the list and the export share. Not the status.</summary>
    private IQueryable<NewsletterSubscriber> Matching(NewsletterSubscriberFilter filter)
    {
        var subscribers = dbContext.NewsletterSubscribers.AsNoTracking();

        if (filter.NormalisedSearch is { } search)
        {
            subscribers = subscribers.Where(subscriber => subscriber.EmailNormalized.Contains(search));
        }

        if (filter.FromUtc is { } from)
        {
            subscribers = subscribers.Where(subscriber => subscriber.ConsentAtUtc >= from);
        }

        if (filter.ToUtcExclusive is { } to)
        {
            subscribers = subscribers.Where(subscriber => subscriber.ConsentAtUtc < to);
        }

        return subscribers;
    }
}
