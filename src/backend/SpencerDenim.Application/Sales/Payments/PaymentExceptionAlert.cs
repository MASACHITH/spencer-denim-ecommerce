using System.Globalization;
using System.Text;
using System.Text.Json;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Sales.Payments;

/// <summary>
/// The facts about a payment that arrived and could not be applied, as the alert needs them.
/// </summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>, the exception payment.</param>
/// <param name="ProviderCode">Which provider took the money.</param>
/// <param name="OrderPublicId">The order's token — what the admin order route takes.</param>
/// <param name="OrderNumber">The number a customer quotes.</param>
/// <param name="Reason">One of <see cref="PaymentExceptionReasons"/>.</param>
/// <param name="ExpectedAmount">What the payment was supposed to be, in <paramref name="ExpectedCurrency"/>.</param>
/// <param name="ExpectedCurrency">The currency the provider was asked to charge in.</param>
/// <param name="ConfirmedAmount">What the provider says it took, if the caller knew.</param>
/// <param name="ConfirmedCurrency">In what currency, if known.</param>
public sealed record PaymentExceptionFacts(
    int PaymentId,
    string ProviderCode,
    Guid OrderPublicId,
    string OrderNumber,
    string Reason,
    decimal ExpectedAmount,
    string ExpectedCurrency,
    decimal? ConfirmedAmount,
    string? ConfirmedCurrency);

/// <summary>
/// Turns a payment routed to <c>CapturedRequiresRefund</c> into the <c>PaymentException</c> alert doc 08
/// §4.2 step 3.4 and doc 11 P8 specify.
/// <para>
/// <b>Pure</b>, like <see cref="Ops.Email.DeadEmailAlert"/>, so a unit test can assert the whole payload
/// — including what it must not contain.
/// </para>
/// <para>
/// <b>Entity ids, an order number and amounts, and nothing about the customer.</b> Doc 08 §3.8 limits
/// <c>DetailsJson</c> to "Keys, entity IDs and amounts", and §3.1 step 8's anonymisation test searches this
/// table for a customer's name, email and telephone number. Staff open the order to find out who to
/// contact. The provider's own transaction id is deliberately not here either: it is on the payment row,
/// which is where a refund reads it from.
/// </para>
/// <para>
/// <b>The related entity is the order</b> (<see cref="AdminAlert.OrderEntityType"/>), because the person
/// who sees the banner needs to open the order, accept the payment there or refund it. One alert per
/// payment: the exception path moves a payment to status 7 once and only once (the
/// <c>UPDATE</c> that does it is conditional), so a redelivered notification cannot raise a second.
/// </para>
/// <para>
/// <b>Doc 08 §4.2 also says to email Super Admins and the <c>Orders.Approve</c> and <c>Orders.Refund</c>
/// holders.</b> No staff email is built in this build (doc 11 §4.2 lists them; <c>IOrderEmailOutbox</c> has
/// only the customer's), so the banner is the whole notification today. Recorded in the journal as owed.
/// </para>
/// </summary>
public static class PaymentExceptionAlert
{
    /// <summary>Doc 11 §4.3: <c>PaymentException</c> is a Warning.</summary>
    public const AdminAlertSeverity Severity = AdminAlertSeverity.Warning;

    /// <summary>Builds the row. <paramref name="nowUtc"/> is passed in so the caller's clock is the only clock.</summary>
    public static AdminAlert For(PaymentExceptionFacts facts, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return new AdminAlert
        {
            AlertType = AdminAlertTypes.PaymentException,
            Severity = Severity,
            Title = Title(facts),
            DetailsJson = Details(facts),
            RelatedEntityType = AdminAlert.OrderEntityType,
            RelatedEntityId = facts.OrderPublicId.ToString("D", CultureInfo.InvariantCulture),
            AuditLogId = null,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>One sentence of facts, clipped to the column. The remedy is the screen's to say.</summary>
    private static string Title(PaymentExceptionFacts facts)
    {
        var title = string.Create(
            CultureInfo.InvariantCulture,
            $"Payment received but not applied to order {facts.OrderNumber}: {Describe(facts.Reason)} (payment {facts.PaymentId})");

        return title.Length <= AdminAlert.TitleMaxLength
            ? title
            : title[..AdminAlert.TitleMaxLength];
    }

    private static string Describe(string reason) =>
        reason switch
        {
            PaymentExceptionReasons.OrderExpired => "the order had expired",
            PaymentExceptionReasons.OrderCancelled => "the order had been cancelled",
            PaymentExceptionReasons.OrderAlreadyPaid => "the order was already paid",
            PaymentExceptionReasons.AmountMismatch => "the amount or currency did not match",
            _ => "the order could not take payment",
        };

    /// <summary>Fixed key order, so a test can assert the whole document.</summary>
    private static string Details(PaymentExceptionFacts facts)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("paymentId", facts.PaymentId);
            writer.WriteString("provider", facts.ProviderCode);
            writer.WriteString("reason", facts.Reason);
            writer.WriteString("orderNumber", facts.OrderNumber);
            writer.WriteString("orderPublicId", facts.OrderPublicId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteNumber("expectedAmount", facts.ExpectedAmount);
            writer.WriteString("expectedCurrency", facts.ExpectedCurrency);

            // Absent rather than null when unknown, as DeadEmailAlert does for the order: a null would
            // read as "the provider took nothing".
            if (facts.ConfirmedAmount is { } confirmed)
            {
                writer.WriteNumber("confirmedAmount", confirmed);
            }

            if (facts.ConfirmedCurrency is { Length: > 0 } currency)
            {
                writer.WriteString("confirmedCurrency", currency);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
