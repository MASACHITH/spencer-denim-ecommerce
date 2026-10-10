using System.Globalization;
using System.Text;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The three admin alerts PayHere's flow raises that the shared <see cref="PaymentExceptionAlert"/> does not
/// already cover, as pure functions so a unit test can assert the whole payload — including what it must not
/// contain (doc 08 §3.8: "Keys, entity IDs and amounts; never secret values", and never personal data).
/// </summary>
public static class PayHereAlerts
{
    /// <summary>The reason recorded for a chargeback, beside <see cref="Abstractions.PaymentExceptionReasons"/>.</summary>
    public const string ChargebackReason = "chargeback";

    /// <summary>
    /// Status code −3: the cardholder's bank took the money back (doc 11 P8b step 4). The order and the
    /// payment are left as they are — a person decides — and this is the whole notification.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="PaymentExceptionAlert"/> so the payload has the same fixed shape a staff screen
    /// already reads, with the reason <c>chargeback</c>; only the sentence is replaced, because that helper's
    /// wording is about money that arrived and could not be applied, which is the opposite of this.
    /// </remarks>
    public static AdminAlert Chargeback(PayHerePayment payment, decimal? amount, string? currency, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var alert = PaymentExceptionAlert.For(
            new PaymentExceptionFacts(
                payment.PaymentId,
                Domain.Sales.PaymentProviderCodes.PayHere,
                payment.OrderPublicId,
                payment.OrderNumber,
                ChargebackReason,
                payment.Amount,
                PayHereHash.Currency,
                amount,
                currency),
            nowUtc);

        alert.Title = Clip(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Chargeback reported on order {payment.OrderNumber} (payment {payment.PaymentId}): the money was taken back"),
            AdminAlert.TitleMaxLength);

        return alert;
    }

    /// <summary>
    /// A notification that could not be applied after every attempt the retry ladder allows. The money may
    /// well have arrived: a person has to look at the event, because nothing will try again.
    /// </summary>
    public static AdminAlert NotificationNotApplied(long eventId, int attempts, DateTime nowUtc) =>
        new()
        {
            AlertType = AdminAlertTypes.PaymentException,
            Severity = PaymentExceptionAlert.Severity,
            Title = Clip(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A PayHere notification could not be applied after {attempts} attempts (payment event {eventId})"),
                AdminAlert.TitleMaxLength),
            DetailsJson = Json(writer =>
            {
                writer.WriteString("reason", "notification_not_applied");
                writer.WriteNumber("paymentEventId", eventId);
                writer.WriteNumber("attempts", attempts);
            }),
            RelatedEntityType = null,
            RelatedEntityId = null,
            AuditLogId = null,
            CreatedAtUtc = nowUtc,
        };

    /// <summary>
    /// A change to the PayHere settings (doc 08 §3.8 DB2 and N2): which keys, and who — and never a value.
    /// One alert per save, listing every key that moved; the audit log has one row per key.
    /// </summary>
    public static AdminAlert SettingsChanged(IReadOnlyList<string> changedKeys, AdminActor actor, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(changedKeys);
        ArgumentNullException.ThrowIfNull(actor);

        return new AdminAlert
        {
            AlertType = AdminAlertTypes.SensitiveSettingsChanged,
            Severity = AdminAlertSeverity.Critical,
            Title = Clip(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"PayHere payment settings were changed ({changedKeys.Count} setting(s))"),
                AdminAlert.TitleMaxLength),
            DetailsJson = Json(writer =>
            {
                writer.WriteStartArray("keys");

                foreach (var key in changedKeys)
                {
                    writer.WriteStringValue(key);
                }

                writer.WriteEndArray();

                if (actor.UserId is { } userId)
                {
                    writer.WriteNumber("actorUserId", userId);
                }
            }),
            RelatedEntityType = null,
            RelatedEntityId = null,
            AuditLogId = null,
            CreatedAtUtc = nowUtc,
        };
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Clip(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];
}
