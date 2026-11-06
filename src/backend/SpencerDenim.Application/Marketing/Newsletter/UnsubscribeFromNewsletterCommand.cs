using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;

namespace SpencerDenim.Application.Marketing.Newsletter;

/// <summary>
/// A subscriber following their own unsubscribe link (doc 11 P15 step 3; #262).
/// </summary>
/// <param name="Token">
/// The link's token as the request carried it — <c>marketing.NewsletterSubscribers.UnsubscribeToken</c>, a GUID only
/// that subscriber has been given. Anything that is not a GUID is simply a token that matches nobody.
/// </param>
public sealed record UnsubscribeFromNewsletterCommand(string? Token);

/// <summary>
/// Withdraws a subscriber's consent by their token.
/// </summary>
/// <remarks>
/// <para>
/// <b>One answer for everything.</b> The endpoint says the same thing whether the token named a subscribed row, an
/// already-unsubscribed one, nobody, or was not a GUID at all: a link checker must not be able to tell a live token
/// from a dead one. The outcome is returned for the tests and is never sent.
/// </para>
/// <para>
/// <b>Held to the signup's time floor</b> (<see cref="SubscribeToNewsletterCommandHandler.MinimumDuration"/>) for the
/// same reason the signup is: the write path costs an UPDATE and a commit and the miss costs a seek, and the code
/// base's rule is not to branch the timing on anything. A token is 122 random bits, so this is belt and braces, not a
/// fix for a reachable oracle.
/// </para>
/// <para>
/// <b>No throttle.</b> Unlike a signup, an unsubscribe cannot be used to write a stranger's address into the table or
/// to mail anyone; each request is at most one conditional UPDATE of a row the caller already holds the key to.
/// </para>
/// </remarks>
public sealed class UnsubscribeFromNewsletterCommandHandler(
    INewsletterSubscriptions subscriptions,
    IClock clock,
    TimeProvider timeProvider)
    : ICommandHandler<UnsubscribeFromNewsletterCommand, bool>
{
    public async Task<bool> HandleAsync(UnsubscribeFromNewsletterCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var started = timeProvider.GetTimestamp();

        try
        {
            return Guid.TryParse(command.Token?.Trim(), out var token)
                && token != Guid.Empty
                && await subscriptions.UnsubscribeByTokenAsync(token, clock.UtcNow.UtcDateTime, cancellationToken);
        }
        finally
        {
            var remaining = SubscribeToNewsletterCommandHandler.MinimumDuration - timeProvider.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, timeProvider, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The work is done either way; a closed tab is not an error.
                }
            }
        }
    }
}
