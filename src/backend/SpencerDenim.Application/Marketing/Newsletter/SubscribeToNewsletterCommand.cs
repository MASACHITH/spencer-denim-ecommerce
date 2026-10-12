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
/// The caller's address, recorded as proof of consent (doc 08 §3.6 <c>ConsentIpAddress</c>) and
/// used as the rate-limit partition. It is the socket peer, corrected from <c>X-Forwarded-For</c> only
/// when that peer is the trusted web gateway (W0-15); a header sent by anyone else is ignored.
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

    /// <summary>This caller has made too many signups in the window. Nothing was written.</summary>
    RateLimited = 4,
}

/// <summary>
/// Writes a newsletter signup (HOME-10, GLB-07).
/// <para>
/// <b>Single opt-in</b>, because that is the shape doc 08 §3.6 gives the table: <c>Status</c> has two
/// values and there is no "pending confirmation". A confirmation email would need the
/// <c>ops.EmailOutbox</c> of §3.8, which is not built; when it is, this handler gains a third status
/// and the endpoint's answer does not change, because it already never says whether the address was
/// new.
/// </para>
/// <para>
/// <b>The write outcomes are one answer to the caller.</b> Telling a stranger "you are already
/// subscribed" turns the form into a way to test whether an address shops here. The endpoint says
/// the same thing to all three; the distinction exists for the tests and for the admin report.
/// </para>
/// <para>
/// <b>Validate, then take a permit, then write.</b> In that order, so a mistyped address costs
/// nothing: see <see cref="INewsletterSignupThrottle"/> for why that matters more than it looks.
/// </para>
/// </summary>
public sealed class SubscribeToNewsletterCommandHandler(
    INewsletterSubscriptions subscriptions,
    INewsletterSignupThrottle throttle,
    IClock clock,
    TimeProvider timeProvider)
    : ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome>
{
    /// <summary>
    /// The floor every answer takes.
    ///
    /// <para>
    /// The body and the status are already identical for an address that is new and one that is
    /// known, which is the point — but the <i>work</i> behind them is not: a new address is a
    /// SELECT, an INSERT and a commit, and a known one returns after the SELECT. Measured at
    /// roughly 95 ms against 7.6 ms, which is a wide enough gap to read an address book through.
    /// Holding every answer to the same floor closes it without pretending the two code paths can
    /// be made to cost the same.
    /// </para>
    /// <para>
    /// It applies to the rejected address too. There is nothing secret about whether a string has
    /// an <c>@</c> in it, but branching the timing on <i>anything</i> is how the next leak gets in.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(250);

    public async Task<NewsletterSubscriptionOutcome> HandleAsync(
        SubscribeToNewsletterCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var started = timeProvider.GetTimestamp();

        try
        {
            return await SubscribeAsync(command, cancellationToken);
        }
        finally
        {
            await PadAsync(started, cancellationToken);
        }
    }

    private async Task<NewsletterSubscriptionOutcome> SubscribeAsync(
        SubscribeToNewsletterCommand command,
        CancellationToken cancellationToken)
    {
        if (!NewsletterEmail.TryNormalise(command.Email, out var email, out var normalised))
        {
            return NewsletterSubscriptionOutcome.InvalidEmail;
        }

        if (!throttle.TryAcquire(command.IpAddress))
        {
            return NewsletterSubscriptionOutcome.RateLimited;
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

    /// <summary>
    /// Waits out the rest of <see cref="MinimumDuration"/>.
    ///
    /// <para>
    /// Through <see cref="TimeProvider"/> so a test can drive it rather than sleep through it, and
    /// with the caller's cancellation swallowed: a visitor who closed the tab has already had
    /// their row written, and throwing here would turn a completed signup into a logged error.
    /// </para>
    /// </summary>
    private async Task PadAsync(long started, CancellationToken cancellationToken)
    {
        var remaining = MinimumDuration - timeProvider.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(remaining, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Nothing to undo: the work is done either way.
        }
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
