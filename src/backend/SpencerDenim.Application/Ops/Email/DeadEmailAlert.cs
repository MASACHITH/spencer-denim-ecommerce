using System.Globalization;
using System.Text.Json;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// The facts about a message that reached <c>Dead</c>, as the outbox row holds them.
/// </summary>
/// <param name="OutboxMessageId">
/// <c>ops.EmailOutbox.Id</c> — one of the three things doc 11 §4 (G-37) requires the alert to name.
/// </param>
/// <param name="TemplateCode">The second. Which message it was.</param>
/// <param name="OrderPublicId">
/// The order it was about, or null for an account email. The token the admin order route takes.
/// </param>
/// <param name="OrderNumber">
/// The third: the number the customer quotes. Null when the message was about no order, and also null if
/// the order could not be found — in which case the alert still names the row and the template, because
/// an alert that is not raised because one of its three facts is missing is the silence G-37 exists to end.
/// </param>
public sealed record DeadEmailFacts(
    long OutboxMessageId,
    string TemplateCode,
    Guid? OrderPublicId,
    string? OrderNumber);

/// <summary>
/// Turns a dead outbox row into the <c>EmailDeliveryFailed</c> alert doc 11 §4 (§7 G-37) specifies.
/// <para>
/// <b>Pure.</b> Nothing here reads a database, a clock or a request, which is what lets the unit suite
/// assert the payload itself — including the thing that most matters about it, which is what it does
/// <i>not</i> contain.
/// </para>
/// <para>
/// <b>Three facts and no fourth.</b> G-37: the alert carries "the template code, the order number and the
/// outbox row ID only, so the anonymisation acceptance test in 08 §3.1 still passes". That test searches
/// every table — <c>ops.AdminAlerts</c> named among them — for a customer's original email, telephone
/// number and name, and nothing anonymises these rows (doc 11 §4). The recipient's address is on the
/// outbox row, which <i>is</i> anonymised (doc 08 §3.1 step 2); it must never be copied here. Staff open
/// the named order to find who to contact.
/// </para>
/// <para>
/// <b>The title states the facts and not the remedy.</b> What a Super Admin can do about a dead message
/// changes the day doc 11 §7 G-42's per-order "Resend email" action is specified and built — and until
/// then there is <b>no resend path in this build at all</b>, so an alert row that said "resend it from the
/// order" would be telling its reader to press a button that does not exist. That sentence belongs to the
/// screen, which can be changed in one place; see the admin banner.
/// </para>
/// </summary>
public static class DeadEmailAlert
{
    /// <summary>Doc 11 §4.3: <c>EmailDeliveryFailed</c> is a Warning.</summary>
    public const AdminAlertSeverity Severity = AdminAlertSeverity.Warning;

    /// <summary>
    /// Builds the row. <paramref name="nowUtc"/> is passed in rather than read, so the caller's clock is
    /// the only clock.
    /// </summary>
    public static AdminAlert For(DeadEmailFacts facts, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return new AdminAlert
        {
            AlertType = AdminAlertTypes.EmailDeliveryFailed,
            Severity = Severity,
            Title = Title(facts),
            DetailsJson = Details(facts),

            // **The alert is about the message, not about the order.** One message dying is one alert, and
            // two messages for the same order can both die — so the row id is what identifies this alert,
            // and it is what makes raising one twice for the same message detectable. The order is in the
            // details, which is where doc 08 §3.8 puts "entity IDs".
            RelatedEntityType = AdminAlert.EmailOutboxEntityType,
            RelatedEntityId = facts.OutboxMessageId.ToString(CultureInfo.InvariantCulture),

            // Doc 08 §3.8's "source change (no FK)" column. Null, and not because nobody got round to it:
            // ops.EmailOutbox is in §3.8's "tables never audited" list, so a message that died has no
            // audit row to point at.
            AuditLogId = null,

            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>
    /// One sentence naming G-37's three facts, clipped to the column.
    /// </summary>
    /// <remarks>
    /// "Not delivered" and not "failed to send": the shop handed it over or could not, and either way the
    /// only thing anybody can say for certain is that it did not arrive. The reader is a Super Admin on a
    /// Colombo morning (doc 11 §6.1), so the template code is printed raw — it is the string they will
    /// search the queue for — and the order number is printed in the form the customer quotes.
    /// </remarks>
    private static string Title(DeadEmailFacts facts)
    {
        var title = facts.OrderNumber is { Length: > 0 } orderNumber
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Email not delivered: {facts.TemplateCode} for order {orderNumber} (outbox row {facts.OutboxMessageId})")
            : facts.OrderPublicId is not null
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Email not delivered: {facts.TemplateCode} for an order that can no longer be found (outbox row {facts.OutboxMessageId})")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Email not delivered: {facts.TemplateCode} (outbox row {facts.OutboxMessageId}); it is not about an order");

        // TemplateCode is varchar(50) and the title is nvarchar(200), so this cannot fire for any code in
        // EmailTemplateCodes. Clipped rather than left to the database, because a message that died is the
        // one case where failing to record the alert would restore exactly the silence the alert ends.
        return title.Length <= AdminAlert.TitleMaxLength
            ? title
            : title[..AdminAlert.TitleMaxLength];
    }

    /// <summary>
    /// G-37's three facts as data, in a fixed key order so a test can assert the whole document.
    /// </summary>
    /// <remarks>
    /// Written with a <see cref="Utf8JsonWriter"/> rather than an anonymous object so that the keys are
    /// in one place and an order key is <i>absent</i> rather than <c>null</c> when there is no order —
    /// a null in here would read as "the order is unknown" where the truth is "there is no order".
    /// </remarks>
    private static string Details(DeadEmailFacts facts)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("templateCode", facts.TemplateCode);
            writer.WriteNumber("outboxMessageId", facts.OutboxMessageId);

            if (facts.OrderPublicId is { } publicId)
            {
                writer.WriteString("orderPublicId", publicId.ToString("D", CultureInfo.InvariantCulture));
            }

            if (facts.OrderNumber is { Length: > 0 } orderNumber)
            {
                writer.WriteString("orderNumber", orderNumber);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
