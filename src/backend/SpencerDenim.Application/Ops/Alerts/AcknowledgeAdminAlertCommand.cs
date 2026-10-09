using SpencerDenim.Application.Abstractions.Auditing;

namespace SpencerDenim.Application.Ops.Alerts;

/// <summary>
/// "I have seen this alert" (doc 08 §3.8: "Acknowledging is audited and doesn't resolve the underlying
/// issue"; doc 11 §6.1 step 1).
/// </summary>
/// <param name="AlertId">The row to acknowledge.</param>
/// <param name="Actor">
/// Who is acknowledging and from where (<c>ops.AuditLogs</c>'s actor columns, doc 08 §3.8). The user id
/// is both the audit actor and the value written into <c>AcknowledgedByUserId</c>, so an alert cannot be
/// acknowledged by a request with no identified user.
/// </param>
/// <remarks>
/// <b>There is no note.</b> Doc 08 §3.8 gives the table an <c>AcknowledgementNote</c> column and the
/// migration creates it; nothing in this build writes it, by the owner's decision (2026-10-09) — see
/// <c>AdminAlertAcknowledger</c> for the reasoning and the journal's "What is owed" for what brings it
/// back.
/// </remarks>
public sealed record AcknowledgeAdminAlertCommand(int AlertId, AdminActor Actor);
