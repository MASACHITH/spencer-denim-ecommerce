using System.Reflection;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Ops.Email;

namespace SpencerDenim.UnitTests.Infrastructure.Ops;

/// <summary>
/// The <c>ops.EmailOutbox</c> row an order email is queued as (doc 08 §3.8).
/// </summary>
public sealed class OrderEmailOutboxTests
{
    private static readonly Guid PublicId = Guid.Parse("7f3a1c90-2b44-4d6e-9f11-0c5e8a6d3b22");

    private static readonly DateTime Now = new(2026, 10, 9, 8, 33, 0, DateTimeKind.Utc);

    private static OrderEmailHeader Header() =>
        new(
            "SD-2026-000123",
            PublicId,
            new OrderEmailAddressee("buyer@example.com", "Nimal", "Perera"),
            Now);

    private static (IOrderEmailOutbox Outbox, CapturingWriter Writer) Build()
    {
        var writer = new CapturingWriter();

        return (
            new OrderEmailOutbox(
                writer,
                new OrderEmailComposer(new EmailOptions { StorefrontBaseUrl = "https://shop.example.com" })),
            writer);
    }

    /// <summary>
    /// The one state doc 08 §3.8 allows an insert to write: <c>Pending</c>, nothing attempted, due now.
    /// </summary>
    /// <remarks>
    /// <b><c>NextAttemptAtUtc == CreatedAtUtc == the transaction's own "now"</c> is the load-bearing
    /// part.</b> The column is <c>NOT NULL</c>, so "as soon as possible" has to be written as a time, and
    /// the claim's predicate is <c>NextAttemptAtUtc &lt;= @now</c>: a row stamped one second in the future
    /// would be invisible to the sweep that should have sent it, and a row stamped from a second clock
    /// reading would make two columns of one row disagree about when it was made.
    /// </remarks>
    [Fact]
    public void A_queued_message_is_pending_unattempted_and_due_now()
    {
        var (outbox, writer) = Build();

        outbox.EnlistOrderExpired(Header());

        var row = Assert.Single(writer.Enlisted);

        Assert.Equal(EmailOutboxStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal(EmailBackoff.MaxAttempts, row.MaxAttempts);
        Assert.Equal(Now, row.NextAttemptAtUtc);
        Assert.Equal(Now, row.CreatedAtUtc);
        Assert.Null(row.SentAtUtc);
        Assert.Null(row.LockedUntilUtc);
        Assert.Null(row.LastError);
    }

    /// <summary>
    /// The row names the order by its <b>public token</b>, which is what doc 11 §4's
    /// <c>EmailDeliveryFailed</c> alert and doc 11 §6.1 step 6 need in order to send staff to an order.
    /// </summary>
    [Fact]
    public void A_queued_message_names_the_order_by_its_public_token()
    {
        var (outbox, writer) = Build();

        outbox.EnlistOrderExpired(Header());

        var row = Assert.Single(writer.Enlisted);

        Assert.Equal("Order", row.RelatedEntityType);
        Assert.Equal("7f3a1c90-2b44-4d6e-9f11-0c5e8a6d3b22", row.RelatedEntityId);
    }

    /// <summary>
    /// No deduplication key, which is <see cref="EmailOutboxMessage.DeduplicationKey"/>'s own decision:
    /// every caller already sits behind a conditional claim that can succeed once, and a unique violation
    /// raised from inside an approval would roll back a payment to prevent a second copy of an email.
    /// </summary>
    [Fact]
    public void A_queued_message_carries_no_deduplication_key_and_no_transport_override()
    {
        var (outbox, writer) = Build();

        outbox.EnlistOrderExpired(Header());

        var row = Assert.Single(writer.Enlisted);

        Assert.Null(row.DeduplicationKey);
        Assert.Null(row.TransportOverrideProtected);
    }

    /// <summary>Both parts are stored, so the sender never has to re-render a promised message.</summary>
    [Fact]
    public void A_queued_message_stores_both_parts_and_the_recipient()
    {
        var (outbox, writer) = Build();

        outbox.EnlistOrderExpired(Header());

        var row = Assert.Single(writer.Enlisted);

        Assert.Equal("buyer@example.com", row.ToEmail);
        Assert.Equal("Nimal Perera", row.ToName);
        Assert.Equal("Order SD-2026-000123 has closed", row.Subject);
        Assert.Contains("<!doctype html>", row.BodyHtml!, StringComparison.Ordinal);
        Assert.Contains("SD-2026-000123", row.BodyText!, StringComparison.Ordinal);
    }

    /// <summary>Each method writes its own template code, and they are doc 08 §3.8's.</summary>
    [Fact]
    public void Each_email_carries_its_own_template_code()
    {
        var (outbox, writer) = Build();

        outbox.EnlistOrderExpired(Header());
        outbox.EnlistOrderCancelled(new CancelledOrderEmailFacts(Header(), true));
        outbox.EnlistPaymentConfirmed(new PaymentConfirmedEmailFacts(Header(), 1m, "INV-1", false));

        Assert.Equal(
            [
                EmailTemplateCodes.OrderExpired,
                EmailTemplateCodes.OrderCancelled,
                EmailTemplateCodes.PaymentConfirmed,
            ],
            writer.Enlisted.Select(row => row.TemplateCode));
    }

    /// <summary>
    /// Doc 08 §3.8 / G-03: the two templates whose link carries a single-use token have their body
    /// destroyed on send, and nothing else does.
    /// </summary>
    /// <remarks>
    /// <b>Both templates now exist</b> — W3-01 built them — so these cases are asserted against real
    /// producers rather than against values nothing could write. The negative half is equally
    /// load-bearing: a rule that cleared every body on send would destroy the record of what the shop
    /// told a customer about their order, thirty days early.
    /// <para>
    /// <b><c>Welcome</c> and <c>AccountExists</c> are account emails and are in the <i>false</i> half.</b>
    /// They carry no token, and that is why W3-01 split them off instead of sending one message with the
    /// link in it: the record of what the shop told a new customer survives the ordinary thirty days, and
    /// only the credential is destroyed on send.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("PasswordReset", true)]
    [InlineData("EmailConfirmation", true)]
    [InlineData("passwordreset", true)]
    [InlineData("OrderPlaced", false)]
    [InlineData("BankTransferInstructions", false)]
    [InlineData("PaymentConfirmed", false)]
    [InlineData("OrderShipped", false)]
    [InlineData("OrderExpired", false)]
    [InlineData("OrderCancelled", false)]
    [InlineData("Welcome", false)]
    [InlineData("AccountExists", false)]
    public void Only_the_token_bearing_templates_lose_their_body_on_send(string templateCode, bool expected)
    {
        Assert.Equal(expected, EmailOutboxMessage.BodyIsClearedOnSend(templateCode));
    }

    /// <summary>
    /// Every declared template code is on exactly one side of the G-03 rule, and which side is decided
    /// here rather than left to whoever declares the next code.
    /// </summary>
    /// <remarks>
    /// <b>This test was written by W3-06's tester as a hand-off, and W3-01 has taken it.</b> The rule was
    /// keyed on two string literals that nothing in the build could produce, so a W3-01 that declared
    /// <c>EmailTemplateCodes.PasswordReset = "PasswordResetLink"</c> — any value but the literal — would
    /// have taken doc 08 §3.8 G-03's body destruction off the one template it was written for while every
    /// case of the theory above still passed. W3-01 declared both codes and
    /// <c>EmailOutboxMessage.BodyClearedOnSend</c> now reads those constants, which is the fix that
    /// remark prescribed.
    /// <para>
    /// <b>The shape stays, because the hazard stays.</b> The declared set is still pinned name by name, so
    /// a task adding an eleventh code has to come here and read this remark; and each code is still
    /// classified <i>through the constant's value</i>, so renaming one is caught rather than silently
    /// reclassifying it. What changed is that the two token-bearing codes are now in the <c>true</c> list
    /// instead of in an <c>if (declared.TryGetValue(...))</c> that could never run.
    /// </para>
    /// <para>
    /// <b>The split is not "order emails versus account emails".</b> <c>Welcome</c> and
    /// <c>AccountExists</c> are account emails with no token in them, and they keep their bodies for the
    /// ordinary thirty days: the record of what the shop told a customer must survive, and only the
    /// credential must not.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_declared_template_code_is_accounted_for_by_the_body_destruction_rule()
    {
        var declared = typeof(EmailTemplateCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);

        // The codes whose body G-03 destroys on send, by name. Both carry a single-use token in a link.
        string[] tokenBearing =
        [
            nameof(EmailTemplateCodes.EmailConfirmation),
            nameof(EmailTemplateCodes.PasswordReset),
        ];

        // Doc 08 §3.8's five order codes, plus the cancellation, which is doc 11 §7 G-14's recommendation
        // and not in doc 08's list (see EmailTemplateCodes), plus W3-01's two tokenless account emails.
        // A task that adds a ninth of these fails here and has to decide which side it is on.
        string[] bodyKeptForThirtyDays =
        [
            nameof(EmailTemplateCodes.AccountExists),
            nameof(EmailTemplateCodes.BankTransferInstructions),
            nameof(EmailTemplateCodes.OrderCancelled),
            nameof(EmailTemplateCodes.OrderExpired),
            nameof(EmailTemplateCodes.OrderPlaced),
            nameof(EmailTemplateCodes.OrderShipped),
            nameof(EmailTemplateCodes.PaymentConfirmed),
            nameof(EmailTemplateCodes.Welcome),
        ];

        Assert.Equal(
            tokenBearing.Concat(bodyKeptForThirtyDays).Order(StringComparer.Ordinal),
            declared.Keys.Order(StringComparer.Ordinal));

        // Classified through the constant's *value*, which is what makes a rename fail rather than
        // silently move a template to the other side of the rule.
        foreach (var name in tokenBearing)
        {
            var code = declared[name];

            Assert.True(
                EmailOutboxMessage.BodyIsClearedOnSend(code),
                $"{name} is declared as \"{code}\", which EmailOutboxMessage.BodyClearedOnSend does "
                + "not match, so doc 08 §3.8 G-03's immediate body destruction would not apply to it. "
                + "BodyClearedOnSend must read EmailTemplateCodes rather than its own literals.");
        }

        foreach (var name in bodyKeptForThirtyDays)
        {
            var code = declared[name];

            Assert.False(
                EmailOutboxMessage.BodyIsClearedOnSend(code),
                $"{name} (\"{code}\") carries no token, so its body must survive until the thirty-day "
                + "rule: it is the record of what the shop told a customer.");
        }
    }

    private sealed class CapturingWriter : IEmailOutboxWriter
    {
        public List<EmailOutboxMessage> Enlisted { get; } = [];

        public void Enlist(EmailOutboxMessage message) => Enlisted.Add(message);
    }
}
