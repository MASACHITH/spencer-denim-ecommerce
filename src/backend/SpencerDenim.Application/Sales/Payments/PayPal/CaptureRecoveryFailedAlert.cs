using System.Globalization;
using System.Text;
using System.Text.Json;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>The facts about a capture the recovery job gave up on.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="OrderPublicId">The order's token — what the admin order route takes.</param>
/// <param name="OrderNumber">The number a customer quotes.</param>
/// <param name="RecoveryHours">How long PayPal was asked about it (<c>CaptureRecoveryMaxHours</c>).</param>
public sealed record CaptureRecoveryFailedFacts(
    int PaymentId,
    Guid OrderPublicId,
    string OrderNumber,
    int RecoveryHours);

/// <summary>
/// The <c>CaptureRecoveryFailed</c> alert (doc 08 §4.2 step 5, R10; doc 11 P8 step 8): a PayPal capture could
/// not be confirmed within <c>CaptureRecoveryMaxHours</c>, so the payment was failed with
/// <c>RECOVERY_TIMEOUT</c> and a person must look at PayPal by hand. A later verified COMPLETED webhook still
/// applies, so this is "check", not "lost".
/// <para>
/// Pure, like <see cref="PaymentExceptionAlert"/>, and for the same reasons: ids, the order number and a count
/// of hours — nothing about the customer, nothing secret. Visible to Super Admins and to the
/// <c>Orders.Approve</c> / <c>Orders.Refund</c> holders (R10). As with the payment-exception alert, the staff
/// email doc 08 asks for is not built in this build (<c>IOrderEmailOutbox</c> is customer-only); the banner is
/// the whole notification today.
/// </para>
/// </summary>
public static class CaptureRecoveryFailedAlert
{
    /// <summary>Doc 11 §4.3: Warning.</summary>
    public const AdminAlertSeverity Severity = AdminAlertSeverity.Warning;

    /// <summary>Builds the row.</summary>
    public static AdminAlert For(CaptureRecoveryFailedFacts facts, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var title = string.Create(
            CultureInfo.InvariantCulture,
            $"PayPal payment for order {facts.OrderNumber} could not be confirmed for {facts.RecoveryHours} hours "
            + $"(payment {facts.PaymentId}); check PayPal");

        return new AdminAlert
        {
            AlertType = AdminAlertTypes.CaptureRecoveryFailed,
            Severity = Severity,
            Title = title.Length <= AdminAlert.TitleMaxLength ? title : title[..AdminAlert.TitleMaxLength],
            DetailsJson = Details(facts),
            RelatedEntityType = AdminAlert.OrderEntityType,
            RelatedEntityId = facts.OrderPublicId.ToString("D", CultureInfo.InvariantCulture),
            AuditLogId = null,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Fixed key order, so a test can assert the whole document.</summary>
    private static string Details(CaptureRecoveryFailedFacts facts)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("paymentId", facts.PaymentId);
            writer.WriteString("provider", PaymentProviderCodes.PayPal);
            writer.WriteString("reason", "RECOVERY_TIMEOUT");
            writer.WriteString("orderNumber", facts.OrderNumber);
            writer.WriteString("orderPublicId", facts.OrderPublicId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteNumber("recoveryHours", facts.RecoveryHours);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
