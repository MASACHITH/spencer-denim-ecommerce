using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// Writes newsletter signups to <c>marketing.NewsletterSubscribers</c> (HOME-10, doc 08 §3.6).
/// </summary>
internal sealed class NewsletterSubscriptions(AppDbContext dbContext, IAuditLogWriter auditLog) : INewsletterSubscriptions
{
    /// <summary>SQL Server's errors for a unique index and a unique constraint violation.</summary>
    private const int DuplicateKeyError = 2601;

    private const int UniqueConstraintError = 2627;

    public async Task<NewsletterSubscriptionOutcome> SubscribeAsync(
        string email,
        string emailNormalized,
        string source,
        DateTime utcNow,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.NewsletterSubscribers
            .FirstOrDefaultAsync(
                subscriber => subscriber.EmailNormalized == emailNormalized,
                cancellationToken);

        if (existing is not null)
        {
            return await UpdateAsync(existing, source, utcNow, ipAddress, cancellationToken);
        }

        dbContext.NewsletterSubscribers.Add(new NewsletterSubscriber
        {
            Email = email,
            EmailNormalized = emailNormalized,
            Status = NewsletterSubscriberStatus.Subscribed,
            Source = source,
            ConsentAtUtc = utcNow,
            ConsentIpAddress = ipAddress,
            UnsubscribeToken = Guid.NewGuid(),
            CreatedAtUtc = utcNow,
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return NewsletterSubscriptionOutcome.Subscribed;
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            // Two people pressed Subscribe with the same address in the same moment, or one person
            // double-clicked. The unique index on EmailNormalized is the arbiter, not the read
            // above, which had already gone stale by the time this insert reached the server.
            // The row the winner wrote is the answer.
            dbContext.ChangeTracker.Clear();

            var winner = await dbContext.NewsletterSubscribers
                .FirstOrDefaultAsync(
                    subscriber => subscriber.EmailNormalized == emailNormalized,
                    cancellationToken);

            return winner is null
                ? NewsletterSubscriptionOutcome.AlreadySubscribed
                : await UpdateAsync(winner, source, utcNow, ipAddress, cancellationToken);
        }
    }

    private async Task<NewsletterSubscriptionOutcome> UpdateAsync(
        NewsletterSubscriber subscriber,
        string source,
        DateTime utcNow,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (subscriber.Status == NewsletterSubscriberStatus.Subscribed)
        {
            // Nothing is written. Overwriting the consent time on every repeat submission would
            // destroy the record of when the address actually opted in, which is the one thing
            // ConsentAtUtc exists to hold.
            return NewsletterSubscriptionOutcome.AlreadySubscribed;
        }

        subscriber.Resubscribe(source, utcNow, ipAddress);
        await dbContext.SaveChangesAsync(cancellationToken);

        return NewsletterSubscriptionOutcome.Resubscribed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A conditional <c>ExecuteUpdate</c> on <c>UX_NewsletterSubscribers_UnsubscribeToken</c>: the row is found by the
    /// unique index, and the status is part of the predicate, so the first request writes and every later one —
    /// a double click, a second tab, a link scanner — changes nothing and keeps the original time.
    /// </remarks>
    public async Task<bool> UnsubscribeByTokenAsync(Guid token, DateTime utcNow, CancellationToken cancellationToken)
    {
        var changed = await dbContext.NewsletterSubscribers
            .Where(subscriber =>
                subscriber.UnsubscribeToken == token && subscriber.Status == NewsletterSubscriberStatus.Subscribed)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(subscriber => subscriber.Status, NewsletterSubscriberStatus.Unsubscribed)
                    .SetProperty(subscriber => subscriber.UnsubscribedAtUtc, utcNow),
                cancellationToken);

        return changed == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>A conditional UPDATE and the audit row, in one transaction</b> (#262 tester). The status is in the UPDATE's
    /// predicate, so a subscriber who uses their own link between a read and a write cannot be "unsubscribed again" with
    /// a new time and an audit row for a change that had already happened: the UPDATE matches no row, nothing is
    /// written, and the answer is AlreadyUnsubscribed. The audit row is added only when the UPDATE changed the row, and
    /// commits with it (<see cref="IAuditLogWriter.Enlist"/>, #59). <c>EnableRetryOnFailure</c> refuses a user
    /// transaction outside its strategy, so the whole attempt is what retries, from a clean change tracker.
    /// </remarks>
    public async Task<StaffUnsubscribeOutcome> UnsubscribeByStaffAsync(
        int subscriberId,
        AuditLog audit,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

            var changed = await dbContext.NewsletterSubscribers
                .Where(subscriber =>
                    subscriber.Id == subscriberId && subscriber.Status == NewsletterSubscriberStatus.Subscribed)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(subscriber => subscriber.Status, NewsletterSubscriberStatus.Unsubscribed)
                        .SetProperty(subscriber => subscriber.UnsubscribedAtUtc, utcNow),
                    token);

            if (changed == 0)
            {
                var exists = await dbContext.NewsletterSubscribers
                    .AnyAsync(subscriber => subscriber.Id == subscriberId, token);

                await transaction.RollbackAsync(token);

                return exists ? StaffUnsubscribeOutcome.AlreadyUnsubscribed : StaffUnsubscribeOutcome.NotFound;
            }

            auditLog.Enlist(audit);
            await dbContext.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            return StaffUnsubscribeOutcome.Unsubscribed;
        }, cancellationToken);
    }
    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is SqlException sql
        && sql.Number is DuplicateKeyError or UniqueConstraintError;
}
