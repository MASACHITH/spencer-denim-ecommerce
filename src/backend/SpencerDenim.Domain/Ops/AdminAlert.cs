namespace SpencerDenim.Domain.Ops;

/// <summary>
/// <c>ops.AdminAlerts</c> (doc 08 §3.8 N2): one thing a human has to look at, shown as a banner until
/// somebody acknowledges it.
/// <para>
/// <b>It exists because a log line is not a signal.</b> Doc 08 §3.8 calls these "persistent dashboard
/// alerts that must be acknowledged", and the difference from an <c>Error</c> in the application log is
/// that a row here survives a restart, is addressed to a named set of people (R10), and stays in their
/// faces until one of them says they have seen it. Doc 11 §6.1 step 1 is the routine built on that.
/// </para>
/// <para>
/// <b>Acknowledging records that a human saw it. It does not fix anything</b> (doc 08 §3.8: "Acknowledging
/// is audited and doesn't resolve the underlying issue"; doc 11 §6.1's escalation rules: "Nothing is ever
/// resolved by silencing it"). Every string this project puts near the button has to mean that.
/// </para>
/// <para>
/// <b>Not <c>IAuditable</c>, and not for the outbox's reason.</b> The rows are written by jobs and
/// background paths that have no acting user, so a <c>CreatedByUserId</c> would be null on every one of
/// them; what is audited is the <i>acknowledgement</i>, through <c>AuditActions.AlertAcknowledged</c>
/// (doc 08 §3.8, §1258), which the acknowledge use case writes in the same transaction as the update.
/// </para>
/// <para>
/// <b>Personal data never comes in here.</b> Doc 08 §3.8 limits <see cref="DetailsJson"/> to "Keys,
/// entity IDs and amounts", and §3.1 step 8's PDPA acceptance test searches <i>every</i> table —
/// <c>ops.AdminAlerts</c> named among them — for a customer's original email, telephone number and name.
/// Nothing anonymises these rows, so an email address written into <see cref="Title"/> or
/// <see cref="DetailsJson"/> would be a PDPA defect that no later routine can clean up. Entity ids and
/// an order number are the way to name a customer here; their contact details are read from the order.
/// </para>
/// </summary>
public sealed class AdminAlert
{
    /// <summary><c>nvarchar(200)</c>, doc 08 §3.8.</summary>
    public const int TitleMaxLength = 200;

    /// <summary><c>varchar(40)</c>, doc 08 §3.8 — the same width as the outbox's own pair.</summary>
    public const int RelatedEntityTypeMaxLength = 40;

    /// <summary><c>varchar(100)</c>, doc 08 §3.8.</summary>
    public const int RelatedEntityIdMaxLength = 100;

    /// <summary><c>nvarchar(500)</c>, doc 08 §3.8.</summary>
    public const int AcknowledgementNoteMaxLength = 500;

    /// <summary>
    /// <see cref="RelatedEntityType"/> for an alert whose subject is an order a staff member must open —
    /// the same spelling <c>EmailOutboxMessage.OrderEntityType</c> uses, so the two tables name the same
    /// entity the same way.
    /// </summary>
    public const string OrderEntityType = "Order";

    /// <summary>
    /// <see cref="RelatedEntityType"/> for an alert about a queued message itself, used when a dead
    /// message had no order behind it (doc 11 §4: the account emails "have no order to open", so "the
    /// alert gives staff the outbox row and nothing to resend from").
    /// </summary>
    public const string EmailOutboxEntityType = "EmailOutbox";

    public int Id { get; set; }

    /// <summary>One of <see cref="AdminAlertTypes"/>. No CK: see that class.</summary>
    public string AlertType { get; set; } = string.Empty;

    /// <summary>Doc 11 §4.3's column, per type. CK 1–3.</summary>
    public AdminAlertSeverity Severity { get; set; }

    /// <summary>
    /// What the banner says, in one sentence a staff member can act on.
    /// <para>
    /// <b>It carries the facts and not the advice.</b> What to do about an alert changes as the admin
    /// panel grows — the day doc 11 §7 G-42's "Resend email" action lands, every dead-email alert ever
    /// written would be telling its reader to do something slower — so a row holds the facts and the
    /// screen holds the sentence about what can be done with them today.
    /// </para>
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Doc 08 §3.8: "Keys, entity IDs and amounts; never secret values. CK ISJSON".
    /// <para>
    /// And never personal data either — see this class's own remarks and doc 08 §3.1 step 8.
    /// </para>
    /// </summary>
    public string? DetailsJson { get; set; }

    /// <summary>
    /// What a staff member should open, as <see cref="OrderEntityType"/> /
    /// <see cref="EmailOutboxEntityType"/>. Null for an alert about the shop as a whole.
    /// </summary>
    public string? RelatedEntityType { get; set; }

    /// <summary>
    /// The related entity's own key — an order's <c>PublicId</c>, which is what the admin order route
    /// takes and what <c>ops.AuditLogs</c> and <c>ops.EmailOutbox</c> both record for an order.
    /// </summary>
    public string? RelatedEntityId { get; set; }

    /// <summary>
    /// The <c>ops.AuditLogs</c> row of the change that <i>caused</i> this alert, where there was one
    /// (doc 08 §3.8's ERD: "source change (no FK)") — a <c>SettingChange</c> for
    /// <c>SensitiveSettingsChanged</c>, a <c>BreakGlass</c> for <c>BreakGlassUsed</c>.
    /// <para>
    /// <b>No foreign key, by doc 08's own design</b>, and <b>null for every row this build writes</b>:
    /// <c>ops.EmailOutbox</c> is in doc 08 §3.8's "tables never audited" list, so a message that died has
    /// no audit row to point at. It is not the acknowledgement's audit row — that one is written after
    /// this column can no longer be set, and is found by searching the trail for
    /// <c>AlertAcknowledged</c> on this id.
    /// </para>
    /// </summary>
    public long? AuditLogId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Who acknowledged it. FK to <c>identity.Users</c> (doc 08 §3.8) — unlike
    /// <see cref="AuditLogId"/>, which has none.
    /// </summary>
    public int? AcknowledgedByUserId { get; set; }

    /// <summary>
    /// When it was acknowledged, and the <b>only</b> column that decides whether the banner shows it:
    /// <c>IX_AdminAlerts_Open</c> is filtered on <c>AcknowledgedAtUtc IS NULL</c>, so this is the
    /// open/closed flag as well as the timestamp.
    /// </summary>
    public DateTime? AcknowledgedAtUtc { get; set; }

    /// <summary>What the person who acknowledged it wanted the next reader to know. Optional.</summary>
    public string? AcknowledgementNote { get; set; }
}
