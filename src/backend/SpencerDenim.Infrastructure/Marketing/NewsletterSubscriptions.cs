using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// Writes newsletter signups to <c>marketing.NewsletterSubscribers</c> (HOME-10, doc 08 §3.6).
/// </summary>
internal sealed class NewsletterSubscriptions(AppDbContext dbContext) : INewsletterSubscriptions
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

    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is SqlException sql
        && sql.Number is DuplicateKeyError or UniqueConstraintError;
}
