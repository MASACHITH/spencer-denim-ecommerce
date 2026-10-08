using System.Globalization;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// Composes each of doc 11 §4.1's order emails and enlists it into the caller's transaction.
/// </summary>
/// <remarks>
/// <b>It is six lines of plumbing and no decisions, which is why it is here rather than in the
/// stores.</b> The decisions are in two other places on purpose: what a message <i>says</i> is
/// <see cref="OrderEmailComposer"/>, which is pure and therefore testable; whether a message is queued at
/// all is the store's, because only the store knows whether its transaction committed.
/// </remarks>
internal sealed class OrderEmailOutbox(IEmailOutboxWriter writer, OrderEmailComposer composer)
    : IOrderEmailOutbox
{
    public void EnlistOrderPlaced(PlacedOrderEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Enlist(EmailTemplateCodes.OrderPlaced, facts.Header, composer.OrderPlaced(facts));
    }

    public void EnlistPaymentInstructions(OrderEmailHeader header, PaymentInstructions instructions)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(instructions);

        Enlist(
            EmailTemplateCodes.BankTransferInstructions,
            header,
            composer.BankTransferInstructions(header, instructions));
    }

    public void EnlistPaymentConfirmed(PaymentConfirmedEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Enlist(EmailTemplateCodes.PaymentConfirmed, facts.Header, composer.PaymentConfirmed(facts));
    }

    public void EnlistOrderShipped(ShippedOrderEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Enlist(EmailTemplateCodes.OrderShipped, facts.Header, composer.OrderShipped(facts));
    }

    public void EnlistOrderExpired(OrderEmailHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        Enlist(EmailTemplateCodes.OrderExpired, header, composer.OrderExpired(header));
    }

    public void EnlistOrderCancelled(CancelledOrderEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Enlist(EmailTemplateCodes.OrderCancelled, facts.Header, composer.OrderCancelled(facts));
    }

    /// <summary>
    /// The outbox row, in the one state doc 08 §3.8 allows an insert to write: <c>Pending</c>, nothing
    /// attempted, due now.
    /// </summary>
    /// <remarks>
    /// <b><c>NextAttemptAtUtc</c> is the transaction's own "now" and not a time in the future.</b> The
    /// column is <c>NOT NULL</c> (doc 08 §3.8), so "as soon as possible" has to be expressed as a time,
    /// and the claim's predicate is <c>NextAttemptAtUtc &lt;= @now</c> — a row stamped with the instant it
    /// was created is therefore due to the very next sweep. It is the same instant as
    /// <see cref="EmailOutboxMessage.CreatedAtUtc"/> rather than a second clock reading, because two
    /// columns of one row disagreeing about when the row was made is the sort of thing that costs an
    /// afternoon later.
    /// <para>
    /// <b>No <c>DeduplicationKey</c>.</b> Every caller sits behind a conditional claim that can succeed
    /// once (doc 08 §5.2), so there is no duplicate to prevent — and a unique violation raised from
    /// inside an approval would roll back a payment to stop a second copy of an email. See
    /// <see cref="EmailOutboxMessage.DeduplicationKey"/>.
    /// </para>
    /// </remarks>
    private void Enlist(string templateCode, OrderEmailHeader header, ComposedEmail composed) =>
        writer.Enlist(new EmailOutboxMessage
        {
            TemplateCode = templateCode,
            ToEmail = header.To.Email,
            ToName = header.To.DisplayName,
            Subject = composed.Subject,
            BodyHtml = composed.Html,
            BodyText = composed.Text,

            // Doc 11 §4's EmailDeliveryFailed alert has to be able to name "the order number and the
            // outbox row ID", and doc 11 §6.1 step 6 has staff open that order. The public token is what
            // the admin screen and the staff member's own link both carry — the same choice
            // ops.AuditLogs.EntityId makes — so a row can be matched to an order without a second lookup.
            RelatedEntityType = EmailOutboxMessage.OrderEntityType,
            RelatedEntityId = header.PublicId.ToString("D", CultureInfo.InvariantCulture),

            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            MaxAttempts = EmailBackoff.MaxAttempts,
            NextAttemptAtUtc = header.NowUtc,
            CreatedAtUtc = header.NowUtc,
        });
}
