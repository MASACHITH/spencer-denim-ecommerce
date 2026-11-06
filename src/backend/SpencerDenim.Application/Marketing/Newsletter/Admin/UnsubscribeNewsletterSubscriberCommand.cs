using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Marketing.Newsletter.Admin;

/// <summary>
/// A staff member marks a subscriber unsubscribed — someone who asked by phone, email or the Contact page (#262,
/// doc 11 §7 G-25: "staff action Unsubscribe on the list").
/// </summary>
/// <param name="SubscriberId"><c>marketing.NewsletterSubscribers.Id</c>.</param>
/// <param name="Actor">Who did it, for the audit row.</param>
public sealed record UnsubscribeNewsletterSubscriberCommand(int SubscriberId, AdminActor Actor);

/// <summary>
/// Withdraws the subscriber's consent and audits it in the same transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Guarded by <c>Newsletter.Export</c></b> at the endpoint. Doc 08 §9 has no newsletter write permission, and the
/// person who sends the list to a mail tool is the one who must keep it right; today the two permissions are held by
/// exactly the same roles (Store Manager, and every Super Admin). A <c>Newsletter.Edit</c> of its own would be a doc 08
/// change for the owner.
/// </para>
/// <para>
/// <b>The audit row names the row, not the address</b>: <c>EntityId</c> is the subscriber id, so doc 11 P17's
/// anonymisation can find and scrub it, and the email is not copied into the trail. Nothing is written — no audit row
/// either — when the subscriber is already unsubscribed or gone, because nothing changed.
/// </para>
/// </remarks>
public sealed class UnsubscribeNewsletterSubscriberCommandHandler(
    INewsletterSubscriptions subscriptions,
    IClock clock)
    : ICommandHandler<UnsubscribeNewsletterSubscriberCommand, StaffUnsubscribeOutcome>
{
    public Task<StaffUnsubscribeOutcome> HandleAsync(
        UnsubscribeNewsletterSubscriberCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.UtcNow.UtcDateTime;

        var audit = new AuditLog
        {
            OccurredAtUtc = now,
            UserId = command.Actor.UserId,
            UserEmail = command.Actor.Email,
            Action = AuditActions.StatusChange,
            EntityType = ExportNewsletterSubscribersCommandHandler.EntityType,
            EntityId = command.SubscriberId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = JsonSerializer.Serialize(new { status = "Subscribed" }),
            AfterJson = JsonSerializer.Serialize(new { status = "Unsubscribed", by = "staff" }),
            IpAddress = command.Actor.IpAddress,
            UserAgent = command.Actor.UserAgent,
            CorrelationId = command.Actor.CorrelationId,
        };

        return subscriptions.UnsubscribeByStaffAsync(command.SubscriberId, audit, now, cancellationToken);
    }
}
