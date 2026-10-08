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
    /// <b>Neither template exists yet</b> — both are W3-01's — so this is the outbox being <i>ready</i>
    /// for them, which is the only part of W3-01 this task was asked to prepare. The negative half is
    /// equally load-bearing: a rule that cleared every body on send would destroy the record of what the
    /// shop told a customer about their order, thirty days early.
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
    public void Only_the_token_bearing_templates_lose_their_body_on_send(string templateCode, bool expected)
    {
        Assert.Equal(expected, EmailOutboxMessage.BodyIsClearedOnSend(templateCode));
    }

    private sealed class CapturingWriter : IEmailOutboxWriter
    {
        public List<EmailOutboxMessage> Enlisted { get; } = [];

        public void Enlist(EmailOutboxMessage message) => Enlisted.Add(message);
    }
}
