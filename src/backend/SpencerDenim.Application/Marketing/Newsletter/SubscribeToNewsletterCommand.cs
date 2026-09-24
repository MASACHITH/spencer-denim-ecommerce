using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Newsletter;

/// <summary>
/// A visitor asking to receive the newsletter (HOME-10, GLB-07).
/// </summary>
/// <param name="Email">The address as typed. Validated and normalised by the handler.</param>
/// <param name="Source">Where the form was (<see cref="NewsletterSource"/>).</param>
/// <param name="IpAddress">
/// The caller's address, recorded as proof of consent (doc 08 §3.6 <c>ConsentIpAddress</c>). It is
/// the real socket peer: <c>X-Forwarded-For</c> processing is deliberately off (W1-01 round 3).
/// </param>
public sealed record SubscribeToNewsletterCommand(string? Email, string Source, string? IpAddress);

/// <summary>What happened to a signup, as the endpoint has to answer it.</summary>
public enum NewsletterSubscriptionOutcome
{
    /// <summary>The address is not one this store will store.</summary>
    InvalidEmail = 0,

    /// <summary>A new row was written.</summary>
    Subscribed = 1,

    /// <summary>The address was already subscribed. Nothing changed, and the answer is the same one.</summary>
    AlreadySubscribed = 2,

    /// <summary>A previously unsubscribed address consented again.</summary>
    Resubscribed = 3,
}

/// <summary>
/// Writes a newsletter signup (HOME-10).
/// <para>
/// <b>Single opt-in</b>, because that is the shape doc 08 §3.6 gives the table: <c>Status</c> has two
/// values and there is no "pending confirmation". A confirmation email would need the
/// <c>ops.EmailOutbox</c> of §3.8, which is not built; when it is, this handler gains a third status
/// and the endpoint's answer does not change, because it already never says whether the address was
/// new.
/// </para>
/// <para>
/// <b>The three outcomes are one answer to the caller.</b> Telling a stranger "you are already
/// subscribed" turns the form into a way to test whether an address shops here. The endpoint says
/// the same thing to all three; the distinction exists for the tests and for the admin report.
/// </para>
/// </summary>
public sealed class SubscribeToNewsletterCommandHandler(INewsletterSubscriptions subscriptions, IClock clock)
    : ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome>
{
    public async Task<NewsletterSubscriptionOutcome> HandleAsync(
        SubscribeToNewsletterCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!NewsletterEmail.TryNormalise(command.Email, out var email, out var normalised))
        {
            return NewsletterSubscriptionOutcome.InvalidEmail;
        }

        var source = string.IsNullOrWhiteSpace(command.Source) ? NewsletterSource.Homepage : command.Source;

        return await subscriptions.SubscribeAsync(
            email,
            normalised,
            source,
            clock.UtcNow.UtcDateTime,
            command.IpAddress,
            cancellationToken);
    }
}

/// <summary>
/// Write port for <c>marketing.NewsletterSubscribers</c>.
/// <para>
/// The whole "insert or update" is one method rather than a read followed by a write, because two
/// people can submit the same address at the same moment and the unique index on
/// <c>EmailNormalized</c> has to be the arbiter, not a check that has already gone stale.
/// </para>
/// </summary>
public interface INewsletterSubscriptions
{
    /// <summary>Records a consent for an address that has already been validated and normalised.</summary>
    Task<NewsletterSubscriptionOutcome> SubscribeAsync(
        string email,
        string emailNormalized,
        string source,
        DateTime utcNow,
        string? ipAddress,
        CancellationToken cancellationToken);
}
