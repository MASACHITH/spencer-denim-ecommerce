using System.Text.Json;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Application.Ops;

/// <summary>
/// The <c>EmailDeliveryFailed</c> payload (doc 11 §4, §7 G-37).
/// <para>
/// <b>Most of this file is about what the alert does not contain.</b> G-37: the alert carries "the
/// template code, the order number and the outbox row ID only, <i>so the anonymisation acceptance test in
/// 08 §3.1 still passes</i>". That test (§3.1 step 8) searches every table — <c>ops.AdminAlerts</c> named
/// among them — for a customer's original email, telephone number and name, and doc 11 §4 records that
/// nothing anonymises these rows. So an email address copied into the title would be a PDPA defect that
/// no later routine can clean up, and it would be invisible until somebody ran that acceptance test.
/// </para>
/// </summary>
public sealed class DeadEmailAlertTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 3, 11, 0, DateTimeKind.Utc);

    private static readonly Guid OrderPublicId = new("44444444-4444-4444-8444-444444444444");

    /// <summary>The three things a real customer's row would carry and this alert must not.</summary>
    private const string CustomerEmail = "nimali.perera@example.com";

    private const string CustomerName = "Nimali Perera";

    private const string CustomerPhone = "0771234567";

    [Fact]
    public void The_title_names_the_template_the_order_number_and_the_row()
    {
        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(4821, "BankTransferInstructions", OrderPublicId, "SD-2026-000123"),
            Now);

        Assert.Contains("BankTransferInstructions", alert.Title, StringComparison.Ordinal);
        Assert.Contains("SD-2026-000123", alert.Title, StringComparison.Ordinal);
        Assert.Contains("4821", alert.Title, StringComparison.Ordinal);
        Assert.Contains("not delivered", alert.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The one that matters: no personal data, anywhere in the row.</b>
    /// </summary>
    /// <remarks>
    /// The facts record has no field that could carry an address, a name or a number — which is the real
    /// guard — so this asserts the whole row's text against the three values doc 08 §3.1 step 8 searches
    /// for, in case a later edit widens the facts and helpfully interpolates one.
    /// </remarks>
    [Fact]
    public void Nothing_in_the_row_carries_an_email_address_a_name_or_a_telephone_number()
    {
        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(4821, "BankTransferInstructions", OrderPublicId, "SD-2026-000123"),
            Now);

        var everything = string.Join(
            '\n',
            alert.Title,
            alert.DetailsJson,
            alert.RelatedEntityType,
            alert.RelatedEntityId,
            alert.AcknowledgementNote);

        Assert.DoesNotContain(CustomerEmail, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CustomerName, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CustomerPhone, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("@", everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// The details are G-37's three facts as data, and nothing else.
    /// </summary>
    [Fact]
    public void The_details_are_the_three_facts_and_a_valid_json_document()
    {
        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(4821, "OrderShipped", OrderPublicId, "SD-2026-000123"),
            Now);

        using var document = JsonDocument.Parse(alert.DetailsJson!);
        var root = document.RootElement;

        Assert.Equal("OrderShipped", root.GetProperty("templateCode").GetString());
        Assert.Equal(4821, root.GetProperty("outboxMessageId").GetInt64());
        Assert.Equal("SD-2026-000123", root.GetProperty("orderNumber").GetString());
        Assert.Equal(OrderPublicId.ToString("D"), root.GetProperty("orderPublicId").GetString());

        Assert.Equal(4, root.EnumerateObject().Count());
    }

    /// <summary>
    /// A message about no order has <b>no order keys at all</b> rather than null ones.
    /// </summary>
    /// <remarks>
    /// Doc 11 §4: <c>Welcome</c>, <c>EmailConfirmation</c>, <c>AccountExists</c>, <c>PasswordReset</c> and
    /// <c>StaffInvite</c> "have no order to open". A <c>null</c> in the details would read as "the order is
    /// unknown", which is a different fact and would send a Super Admin looking for one.
    /// </remarks>
    [Fact]
    public void A_message_about_no_order_says_so_and_carries_no_order_keys()
    {
        var alert = DeadEmailAlert.For(new DeadEmailFacts(99, "PasswordReset", null, null), Now);

        using var document = JsonDocument.Parse(alert.DetailsJson!);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("orderNumber", out _));
        Assert.False(root.TryGetProperty("orderPublicId", out _));
        Assert.Equal(2, root.EnumerateObject().Count());

        Assert.Contains("PasswordReset", alert.Title, StringComparison.Ordinal);
        Assert.Contains("99", alert.Title, StringComparison.Ordinal);
        Assert.Contains("not about an order", alert.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// An order that has gone still gets an alert, and the title says which fact is missing.
    /// </summary>
    /// <remarks>
    /// Withholding the alert because one of G-37's three facts could not be resolved would restore exactly
    /// the silence it exists to end — and the template code and row id are still enough to find the
    /// message.
    /// </remarks>
    [Fact]
    public void An_order_that_can_no_longer_be_found_still_raises_an_alert()
    {
        var alert = DeadEmailAlert.For(new DeadEmailFacts(4821, "OrderPlaced", OrderPublicId, null), Now);

        Assert.Contains("can no longer be found", alert.Title, StringComparison.Ordinal);
        Assert.Contains("OrderPlaced", alert.Title, StringComparison.Ordinal);
        Assert.Contains("4821", alert.Title, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(alert.DetailsJson!);
        Assert.False(document.RootElement.TryGetProperty("orderNumber", out _));
        Assert.Equal(
            OrderPublicId.ToString("D"),
            document.RootElement.GetProperty("orderPublicId").GetString());
    }

    /// <summary>
    /// The row's own columns: Warning (doc 11 §4.3), the message as the related entity, and no audit row.
    /// </summary>
    [Fact]
    public void The_row_is_a_warning_about_the_message_with_no_source_audit_row()
    {
        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(4821, "OrderPlaced", OrderPublicId, "SD-2026-000123"),
            Now);

        Assert.Equal(AdminAlertTypes.EmailDeliveryFailed, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
        Assert.Equal(Now, alert.CreatedAtUtc);

        // The alert is about the dead message, not about the order: one message is one alert, and two
        // messages for the same order must each get their own.
        Assert.Equal(AdminAlert.EmailOutboxEntityType, alert.RelatedEntityType);
        Assert.Equal("4821", alert.RelatedEntityId);

        // ops.EmailOutbox is in doc 08 §3.8's "tables never audited" list, so there is no source row.
        Assert.Null(alert.AuditLogId);

        // Nobody has seen it yet, and the check constraint requires all three to be null together.
        Assert.Null(alert.AcknowledgedAtUtc);
        Assert.Null(alert.AcknowledgedByUserId);
        Assert.Null(alert.AcknowledgementNote);
    }

    /// <summary>
    /// The title fits <c>nvarchar(200)</c> even for the widest template code the column allows.
    /// </summary>
    /// <remarks>
    /// <c>TemplateCode</c> is <c>varchar(50)</c> and no code in <c>EmailTemplateCodes</c> comes close, so
    /// this is unreachable from the real queue. It is pinned because the alternative failure is the worst
    /// one available: a string-truncation error on the insert, which would leave a dead message with no
    /// alert — silence, for a reason nobody would look for.
    /// </remarks>
    [Fact]
    public void A_title_that_would_overflow_the_column_is_clipped_rather_than_refused()
    {
        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(long.MaxValue, new string('X', 50), OrderPublicId, new string('N', 120)),
            Now);

        Assert.Equal(AdminAlert.TitleMaxLength, alert.Title.Length);
    }

    /// <summary>
    /// Doc 11 §4: the alert is the trigger and <b>nothing in the row tells anybody to resend</b>.
    /// </summary>
    /// <remarks>
    /// There is no resend action anywhere in this build (doc 11 §7 G-42 is unspecified), so advice in the
    /// row would be a promise it cannot keep — and the row outlives the advice: the day G-42 lands, every
    /// alert already written would still be giving the old instruction. The screen owns the sentence
    /// (<c>alertAdvice</c> in <c>admin-alert.model.ts</c>), which is one place to change.
    /// </remarks>
    [Fact]
    public void The_row_gives_facts_and_not_instructions()
    {
        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(4821, "BankTransferInstructions", OrderPublicId, "SD-2026-000123"),
            Now);

        var everything = $"{alert.Title}\n{alert.DetailsJson}";

        Assert.DoesNotContain("resend", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telephone", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retry", everything, StringComparison.OrdinalIgnoreCase);
    }
}
