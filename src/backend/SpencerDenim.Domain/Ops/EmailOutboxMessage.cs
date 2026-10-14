namespace SpencerDenim.Domain.Ops;

/// <summary>
/// <c>ops.EmailOutbox</c> (doc 08 §3.8, ACC-03): one queued message.
/// <para>
/// <b>It is a transactional outbox, and that is the whole reason the table exists rather than an
/// <c>SmtpClient</c> call at the end of a use case.</b> Doc 08 §3.8: "Rows are inserted in the business
/// transaction". So the row and the change it is about commit together — a customer never gets "we have
/// your order" for an order that rolled back, and an order that committed always has its email queued.
/// Doc 08 §10.3 is the other half of the same rule: "Calls to PayPal and SMTP are never made inside a
/// retried transaction", which is why sending is a separate job and not the tail of the request.
/// </para>
/// <para>
/// <b>No foreign key to <c>sales.Orders</c>, deliberately</b> (doc 08 §3.8 gives
/// <see cref="RelatedEntityType"/> / <see cref="RelatedEntityId"/> as a loose pair with no FK). Account
/// emails have no order at all, the retention rules differ by an order of magnitude — rows here are kept
/// one year and an order seven (doc 08 §8.4) — and a purge of this table must never be able to touch an
/// order.
/// </para>
/// <para>
/// <b>Never audited</b> (doc 08 §3.8's "tables never audited", because a body may contain a reset link),
/// so this type is not <c>IAuditable</c>: it carries no <c>CreatedByUserId</c> / <c>UpdatedByUserId</c>
/// pair and the <c>SaveChanges</c> interceptor has nothing to stamp on it. <see cref="CreatedAtUtc"/> is
/// its own column rather than the audit group's.
/// </para>
/// </summary>
public sealed class EmailOutboxMessage
{
    /// <summary><c>varchar(150)</c>, doc 08 §3.8.</summary>
    public const int DeduplicationKeyMaxLength = 150;

    /// <summary><c>nvarchar(256)</c>, doc 08 §3.8 — the same width as <c>identity.Users.Email</c>.</summary>
    public const int ToEmailMaxLength = 256;

    /// <summary><c>nvarchar(120)</c>, doc 08 §3.8.</summary>
    public const int ToNameMaxLength = 120;

    /// <summary><c>nvarchar(200)</c>, doc 08 §3.8.</summary>
    public const int SubjectMaxLength = 200;

    /// <summary><c>varchar(40)</c>, doc 08 §3.8.</summary>
    public const int RelatedEntityTypeMaxLength = 40;

    /// <summary><c>varchar(100)</c>, doc 08 §3.8.</summary>
    public const int RelatedEntityIdMaxLength = 100;

    /// <summary><c>nvarchar(2000)</c>, doc 08 §3.8.</summary>
    public const int LastErrorMaxLength = 2000;

    /// <summary>
    /// <see cref="RelatedEntityType"/> for an order email, so the <c>EmailDeliveryFailed</c> alert G-37
    /// owes can name the order a staff member must open.
    /// </summary>
    public const string OrderEntityType = "Order";

    /// <summary>
    /// Template codes whose body is cleared the moment the message is sent, rather than after thirty days
    /// (doc 08 §3.8, G-03): "both carry a single-use token in the link, so the body must not sit in the
    /// outbox table or in a backup".
    /// </summary>
    /// <remarks>
    /// <b>The two codes are now <see cref="EmailTemplateCodes"/> constants rather than literals here, and
    /// that was the hand-off.</b> W3-06 wrote the rule before either template existed and keyed it on two
    /// string literals; <c>OrderEmailOutboxTests</c> carried the reason in full — "if W3-01 declares
    /// <c>EmailTemplateCodes.PasswordReset = "PasswordResetLink"</c>, any value but the literal, the
    /// privacy rule silently stops applying to the one template it was written for". W3-01 declared both
    /// codes, so the rule reads them.
    /// <para>
    /// <b>The list is deliberately not "every account email".</b> <c>Welcome</c> and
    /// <c>AccountExists</c> carry no token, so their bodies are kept for the ordinary thirty days: the
    /// record of what the shop told a customer must survive, and only the credential must not.
    /// </para>
    /// </remarks>
    private static readonly string[] BodyClearedOnSend =
        [EmailTemplateCodes.PasswordReset, EmailTemplateCodes.EmailConfirmation];

    public long Id { get; set; }

    /// <summary>One of <see cref="EmailTemplateCodes"/>. No CK: see that class.</summary>
    public string TemplateCode { get; set; } = string.Empty;

    /// <summary>
    /// A caller-chosen key under a filtered unique index, so "send this once" is the database's rule
    /// rather than a caller's intention.
    /// <para>
    /// <b>Null on every row this build writes.</b> Each of the six order emails is queued inside a
    /// transaction whose own conditional claim can succeed only once (doc 08 §5.2), so a duplicate is
    /// already impossible — and a unique violation raised from <i>inside</i> an approval or a placement
    /// would roll back a money transaction to prevent a second copy of an email, which is the wrong
    /// trade. It is left for the account emails, where a customer pressing "send me a new link" twice is
    /// an ordinary thing to do.
    /// </para>
    /// </summary>
    public string? DeduplicationKey { get; set; }

    /// <summary>
    /// Data Protection ciphertext of an SMTP configuration to use <i>instead of</i> the current one
    /// (doc 08 §3.8 N2), for the previous-configuration copy of <c>SensitiveSettingsChanged</c>.
    /// <para>
    /// <b>Always null in this build, and the column is here because the schema has it.</b> The dual-send
    /// belongs to the settings task that can change SMTP at all: it needs <c>ops.AdminAlerts</c>, the
    /// <c>Email.Smtp.*</c> settings group and <c>Settings.Security</c>, none of which exists. The sender
    /// does honour the one rule that outlives the dual-send — the column is NULLed on any terminal status
    /// (R5) — so a ciphertext cannot outlive the message it was for.
    /// </para>
    /// </summary>
    public string? TransportOverrideProtected { get; set; }

    public string ToEmail { get; set; } = string.Empty;

    public string? ToName { get; set; }

    public string Subject { get; set; } = string.Empty;

    /// <summary>The HTML body. NULLed after thirty days (doc 08 §8.4), or on send for two templates.</summary>
    public string? BodyHtml { get; set; }

    /// <summary>
    /// The plain-text body, sent as the alternative part.
    /// <para>
    /// <b>Both parts are stored, not one rendered from the other at send time.</b> The sender must be
    /// able to send a message whose composer has since changed, and a message in the queue is the message
    /// that was promised.
    /// </para>
    /// </summary>
    public string? BodyText { get; set; }

    /// <summary><c>"Order"</c> for the six order emails; null for a message about nothing in particular.</summary>
    public string? RelatedEntityType { get; set; }

    /// <summary>
    /// The order's <c>PublicId</c>, not its internal id — the same choice <c>ops.AuditLogs</c> makes, so
    /// a row can be matched to the link a shopper and a staff member both hold.
    /// </summary>
    public string? RelatedEntityId { get; set; }

    public EmailOutboxStatus Status { get; set; } = EmailOutboxStatus.Pending;

    public short AttemptCount { get; set; }

    /// <summary>
    /// How many attempts in all before the message is <see cref="EmailOutboxStatus.Dead"/>.
    /// <para>
    /// Defaulted from <see cref="EmailBackoff.MaxAttempts"/> so the ladder and the ceiling cannot
    /// disagree, and stored per row so a message queued under one ceiling is not re-judged under another.
    /// </para>
    /// </summary>
    public short MaxAttempts { get; set; } = EmailBackoff.MaxAttempts;

    /// <summary>
    /// When the next attempt is due. <b>Not nullable</b> (doc 08 §3.8), and a new row carries the time it
    /// was created, which means "as soon as a sweep sees it".
    /// </summary>
    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>The sender's lease. Null for a row nobody holds.</summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>
    /// Why the last attempt failed, clipped to <see cref="LastErrorMaxLength"/>.
    /// <para>
    /// <b>The exception's message, never the message body and never the recipient.</b> An SMTP failure's
    /// text is about a server; putting a rendered body in here would defeat the point of NULLing the body
    /// columns at all.
    /// </para>
    /// </summary>
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }

    /// <summary>
    /// Whether this message's body must be destroyed as soon as it is sent rather than after thirty days
    /// (doc 08 §3.8's G-03 rule).
    /// </summary>
    /// <remarks>
    /// Despite the name, <b>on every terminal status and not only on <c>Sent</c></b> (independent tester,
    /// W3-01): a reset or confirmation message that dies after its last attempt still holds a working
    /// link in its body, and "the body must not sit in the outbox table or in a backup" does not stop
    /// applying because the send failed. <c>EmailOutboxStore</c> clears it on <c>Sent</c> and on both
    /// paths to <c>Dead</c>.
    /// </remarks>
    public static bool BodyIsClearedOnSend(string templateCode) =>
        BodyClearedOnSend.Contains(templateCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The template codes whose body is destroyed on a terminal status, for a set-based statement that
    /// cannot call <see cref="BodyIsClearedOnSend"/> row by row.
    /// </summary>
    public static IReadOnlyList<string> TemplatesWithBodyClearedOnSend => BodyClearedOnSend;
}
