using SpencerDenim.Application.Abstractions.Auditing;

namespace SpencerDenim.Application.Ops.Alerts;

/// <summary>
/// "I have seen this alert" (doc 08 §3.8: "Acknowledging is audited and doesn't resolve the underlying
/// issue"; doc 11 §6.1 step 1).
/// </summary>
/// <param name="AlertId">The row to acknowledge.</param>
/// <param name="Note">
/// Optional, <c>nvarchar(500)</c>: what the person who cleared it wants the next reader to know. Trimmed,
/// and an empty note is stored as null rather than as a blank string.
/// </param>
/// <param name="Actor">
/// Who is acknowledging and from where (<c>ops.AuditLogs</c>'s actor columns, doc 08 §3.8). The user id
/// is both the audit actor and the value written into <c>AcknowledgedByUserId</c>, so an alert cannot be
/// acknowledged by a request with no identified user.
/// </param>
public sealed record AcknowledgeAdminAlertCommand(int AlertId, string? Note, AdminActor Actor);
