using System.Security.Cryptography;
using System.Text;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// PayHere's server-to-server notification, as the endpoint received it (doc 11 P8b step 4).
/// </summary>
/// <param name="Fields">
/// The posted form, one value per field. The endpoint has already refused a request that repeats a field, so
/// there is no ambiguity about which value of <c>status_code</c> the signature was computed over.
/// </param>
public sealed record PayHereNotifyCommand(IReadOnlyDictionary<string, string> Fields);

/// <summary>What the notify endpoint should answer. Never carries the payload back out.</summary>
public abstract record PayHereNotifyResult
{
    private PayHereNotifyResult()
    {
    }

    /// <summary>
    /// The notification was verified and stored (or had been stored already): answer 200.
    /// </summary>
    /// <param name="IsDuplicate">A redelivery of an event that was already stored; nothing was done with it.</param>
    /// <param name="Outcome">What applying it did. Null for a duplicate.</param>
    public sealed record Accepted(bool IsDuplicate, PayHereProcessingOutcome? Outcome) : PayHereNotifyResult;

    /// <summary>Not shaped like a PayHere notification: answer 400. Nothing was stored.</summary>
    public sealed record Malformed : PayHereNotifyResult;

    /// <summary>
    /// The merchant ID is not ours, or <c>md5sig</c> is not the signature of what was posted: answer 400.
    /// Nothing was stored. The reason is for the log only; it is never sent back.
    /// </summary>
    public sealed record Forged(PayHereRefusal Reason) : PayHereNotifyResult;

    /// <summary>
    /// PayHere is not configured here, so no signature can be checked: answer 503, so that PayHere tries
    /// again once someone has fixed the settings. Nothing was stored.
    /// </summary>
    public sealed record NotConfigured : PayHereNotifyResult;
}

/// <summary>Why a well-formed notification was refused. For the log; never returned to the caller.</summary>
public enum PayHereRefusal
{
    /// <summary><c>merchant_id</c> is not the configured one.</summary>
    MerchantMismatch,

    /// <summary><c>md5sig</c> is not the signature of the posted values.</summary>
    BadSignature,
}

/// <summary>
/// Verifies a PayHere notification, stores it once, and applies it (doc 11 P8b step 4; doc 08 DB13).
/// <para>
/// <b>The order is the security of the endpoint, and each step is here because skipping it has a name.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Shape.</b> Anything that is not a PayHere notification is refused before the database or the secret is
/// touched.
/// </description></item>
/// <item><description>
/// <b>Configured.</b> Without a secret there is nothing to verify against, so the answer is 503 and not 400:
/// a genuine notification must be retried, not discarded.
/// </description></item>
/// <item><description>
/// <b>Merchant, then signature, both in constant time, both before anything is stored.</b> An unverified
/// request must never consume an idempotency key: an attacker who can guess a real <c>payment_id</c> could
/// otherwise get the genuine delivery ignored as a duplicate. The signature covers the order ID, the amount,
/// the currency and the status code, so none of them can be changed — which is also what makes "another
/// order's payment" and "a smaller amount" fail here and not later.
/// </description></item>
/// <item><description>
/// <b>Store once</b>, keyed by PayHere's payment ID and the status code, with the allow-listed fields as the
/// payload — never the card fields, never the signature. A redelivery answers 200 and stops.
/// </description></item>
/// <item><description>
/// <b>Apply</b> (<see cref="PayHereNotificationProcessor"/>), which records its outcome on the event. A
/// failure to apply does not become a failure to answer: the event is stored, PayHere is told so, and the
/// retry sweep finishes the job.
/// </description></item>
/// </list>
/// </summary>
public sealed class PayHereNotifyCommandHandler(
    IPayHereSettingsReader settings,
    IPaymentEventStore events,
    PayHereNotificationProcessor processor,
    IClock clock)
    : ICommandHandler<PayHereNotifyCommand, PayHereNotifyResult>
{
    public async Task<PayHereNotifyResult> HandleAsync(
        PayHereNotifyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!PayHereNotification.TryRead(command.Fields, out var form))
        {
            return new PayHereNotifyResult.Malformed();
        }

        var notification = form!.Notification;
        var current = await settings.ReadAsync(cancellationToken);

        if (!current.IsConfigured)
        {
            return new PayHereNotifyResult.NotConfigured();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(notification.MerchantId),
                Encoding.UTF8.GetBytes(current.MerchantId!.Trim())))
        {
            return new PayHereNotifyResult.Forged(PayHereRefusal.MerchantMismatch);
        }

        if (!PayHereHash.IsValidNotificationSignature(
                notification.MerchantId,
                notification.OrderId,
                notification.PayhereAmount,
                notification.PayhereCurrency,
                notification.StatusCode,
                current.MerchantSecret!,
                form.Signature))
        {
            return new PayHereNotifyResult.Forged(PayHereRefusal.BadSignature);
        }

        var receipt = await events.RecordAsync(
            new PaymentEventRecord(
                PaymentProviderCodes.PayHere,
                PaymentEventSource.VerifiedWebhook,
                notification.EventType,
                notification.PaymentId,
                PaymentEventKey.ForEvent(
                    PaymentProviderCodes.PayHere,
                    $"{notification.PaymentId}:{notification.StatusCode}"),
                notification.ToStoredJson(),
                clock.UtcNow.UtcDateTime),
            cancellationToken);

        if (!receipt.IsNew)
        {
            // A redelivery. If the first delivery's processing died, the sweep owns the event; this request
            // does not process it a second time on PayHere's say-so.
            return new PayHereNotifyResult.Accepted(IsDuplicate: true, Outcome: null);
        }

        var outcome = await processor.ProcessAsync(receipt.EventId, notification, 0, cancellationToken);

        return new PayHereNotifyResult.Accepted(IsDuplicate: false, outcome);
    }
}
