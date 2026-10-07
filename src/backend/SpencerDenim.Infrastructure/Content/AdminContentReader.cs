using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Contracts;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The admin content screens' reads (ADM-17, GLB-10).
/// <para>
/// <b>No <c>IsPublished</c> filter anywhere in this class</b>, which is the whole reason it is a separate
/// reader from <see cref="ContentPageReader"/>: on a fresh shop all six pages are drafts, so a filtered
/// reader would show the client an empty screen and no way to publish anything.
/// </para>
/// <para>
/// <b>The alt-text count is computed in memory, not in SQL</b>, and the cost is bounded on purpose: it
/// needs an HTML parse, which SQL Server cannot do, and the list is at most a few dozen pages that one
/// staff member is looking at. It is the same sanitiser the publish gate uses, so the number the screen
/// shows and the number that refuses a publish cannot disagree.
/// </para>
/// </summary>
internal sealed class AdminContentReader(AppDbContext dbContext, IHtmlSanitiser sanitiser) : IAdminContentReader
{
    /// <summary>
    /// How much of a message the inbox list shows. Short, because the list is the payload most likely to
    /// be cached, screenshotted or widened into an export later, and doc 11 P8 records that the message
    /// may hold bank account details.
    /// </summary>
    private const int PreviewLength = 120;

    public async Task<IReadOnlyList<AdminContentPageListItemDto>> GetPagesAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.ContentPages
            .AsNoTracking()
            .Select(page => new
            {
                page.Id,
                page.SystemKey,
                page.Slug,
                page.Title,
                page.BodyHtml,
                page.IsPublished,
                page.PublishedAtUtc,
                page.UpdatedAtUtc,
                page.RowVersion,
            })
            .ToListAsync(cancellationToken);

        // System pages first, in the seeded order, then the custom pages by title. The six the client has
        // to fill in are what they came to this screen for, and ordering by title alone would scatter them
        // through a list of their own extra pages.
        return rows
            .OrderBy(row => SystemKeyOrder(row.SystemKey))
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .Select(row => new AdminContentPageListItemDto(
                row.Id,
                row.SystemKey,
                row.Slug,
                row.Title,
                row.IsPublished,
                HasBody: row.BodyHtml.Length > 0,
                ImagesMissingAltText: sanitiser.Sanitise(row.BodyHtml).ImagesMissingAltText,
                row.PublishedAtUtc,
                row.UpdatedAtUtc,
                CanBeDeleted: !ContentPageKeys.IsSystemKey(row.SystemKey),
                RowVersion: Token(row.RowVersion)))
            .ToList();
    }

    public async Task<AdminContentPageDto?> GetPageAsync(int pageId, CancellationToken cancellationToken)
    {
        var row = await dbContext.ContentPages
            .AsNoTracking()
            .Where(page => page.Id == pageId)
            .Select(page => new
            {
                page.Id,
                page.SystemKey,
                page.Slug,
                page.Title,
                page.BodyHtml,
                page.SeoTitle,
                page.SeoDescription,
                page.IsPublished,
                page.PublishedAtUtc,
                page.UpdatedAtUtc,
                page.RowVersion,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new AdminContentPageDto(
                row.Id,
                row.SystemKey,
                row.Slug,
                row.Title,
                row.BodyHtml,
                row.SeoTitle,
                row.SeoDescription,
                row.IsPublished,
                row.PublishedAtUtc,
                row.UpdatedAtUtc,
                CanBeDeleted: !ContentPageKeys.IsSystemKey(row.SystemKey),
                ImagesMissingAltText: sanitiser.Sanitise(row.BodyHtml).ImagesMissingAltText,
                RowVersion: Token(row.RowVersion));
    }

    public async Task<AdminContactSubmissionsDto> GetSubmissionsAsync(
        ContactSubmissionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var filtered = status is { } wanted
            ? dbContext.ContactSubmissions.Where(submission => submission.Status == wanted)
            : dbContext.ContactSubmissions;

        var total = await filtered.CountAsync(cancellationToken);

        // Across the whole inbox rather than the filter, because it is the badge's number: a count that
        // changed when a filter did would be a badge nobody could trust.
        var newCount = await dbContext.ContactSubmissions
            .CountAsync(submission => submission.Status == ContactSubmissionStatus.New, cancellationToken);

        // IX_ContactSubmissions_Status_Created (doc 08 §3.7) leads on Status and then CreatedAtUtc
        // descending, which is exactly the filtered case. The unfiltered case orders on the second column
        // of that index and so sorts; the table is small and one staff member is reading it.
        var rows = await filtered
            .AsNoTracking()
            .OrderByDescending(submission => submission.CreatedAtUtc)
            .ThenByDescending(submission => submission.Id)
            .Skip(skip)
            .Take(take)
            .Select(submission => new
            {
                submission.Id,
                submission.Name,
                submission.Email,
                submission.Subject,

                // Truncated in SQL, so the rest of a four-thousand-character message never crosses the
                // wire for a list. Doc 11 P8's bank-details case is why that is worth a Substring: the
                // list is the payload most likely to be cached or screenshotted.
                Preview = submission.Message.Length > PreviewLength
                    ? submission.Message.Substring(0, PreviewLength)
                    : submission.Message,
                submission.OrderNumber,
                submission.Status,
                submission.HandledByUserId,
                submission.HandledAtUtc,
                submission.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var handlers = await HandlerEmailsAsync(
            rows.Select(row => row.HandledByUserId), cancellationToken);

        var items = rows
            .Select(row => new AdminContactSubmissionListItemDto(
                row.Id,
                row.Name,
                row.Email,
                row.Subject,
                row.Preview,
                row.OrderNumber,
                row.Status,
                row.HandledByUserId is { } userId ? handlers.GetValueOrDefault(userId) : null,
                row.HandledAtUtc,
                row.CreatedAtUtc))
            .ToList();

        return new AdminContactSubmissionsDto(items, total, newCount);
    }

    public async Task<AdminContactSubmissionDto?> GetSubmissionAsync(
        int submissionId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.ContactSubmissions
            .AsNoTracking()
            .Where(submission => submission.Id == submissionId)
            .Select(submission => new
            {
                submission.Id,
                submission.Name,
                submission.Email,
                submission.Phone,
                submission.Subject,
                submission.Message,
                submission.OrderNumber,
                submission.Status,
                submission.HandledByUserId,
                submission.HandledAtUtc,
                submission.IpAddress,
                submission.CreatedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var handlers = await HandlerEmailsAsync([row.HandledByUserId], cancellationToken);

        return new AdminContactSubmissionDto(
            row.Id,
            row.Name,
            row.Email,
            row.Phone,
            row.Subject,
            row.Message,
            row.OrderNumber,
            row.Status,
            row.HandledByUserId is { } userId ? handlers.GetValueOrDefault(userId) : null,
            row.HandledAtUtc,
            row.IpAddress,
            row.CreatedAtUtc);
    }

    /// <summary>
    /// The email of each staff member who has handled one of these enquiries, in one query.
    /// <para>
    /// A second query rather than a join, because <c>ContactSubmissions.HandledByUserId</c> has no
    /// navigation — doc 08 §1.5's convention for user columns, and a Domain entity cannot reference
    /// Infrastructure's <see cref="AppUser"/>. One extra round trip for a page of twenty-five rows is the
    /// price, and it is paid on an admin screen rather than on the storefront.
    /// </para>
    /// </summary>
    private async Task<Dictionary<int, string?>> HandlerEmailsAsync(
        IEnumerable<int?> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.OfType<int>().Distinct().ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);
    }

    /// <summary>
    /// Where a system page sits in the list. The seeded order of <see cref="ContentPageKeys.Seeds"/>, so
    /// the list's order and the seeder's order are one decision; a custom page sorts after all of them.
    /// </summary>
    private static int SystemKeyOrder(string? systemKey)
    {
        if (systemKey is null)
        {
            return int.MaxValue;
        }

        for (var index = 0; index < ContentPageKeys.Seeds.Count; index++)
        {
            if (string.Equals(ContentPageKeys.Seeds[index].SystemKey, systemKey, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        // A system key this build does not know — written by a later build, or by a data fix. It is still a
        // system page, so it sorts with them rather than disappearing among the custom pages.
        return ContentPageKeys.Seeds.Count;
    }

    /// <summary>
    /// The row version as the client round-trips it. An empty string for a row with none, which
    /// <c>ContentWriteSupport.CheckRowVersion</c> refuses — a page whose token could not be read must not
    /// be writable, and the alternative (null) would have the client omit the field and look like a client
    /// that simply forgot.
    /// </summary>
    private static string Token(byte[]? rowVersion) =>
        rowVersion is null ? string.Empty : Convert.ToBase64String(rowVersion);
}
