using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// A page of the enquiry inbox (GLB-10, doc 11 P15 step 7: "newest first").
/// </summary>
/// <param name="Status">Show only this status, or <see langword="null"/> for all of them.</param>
/// <param name="Skip">How many to skip.</param>
/// <param name="Take">How many to return, already bounded by the endpoint.</param>
public sealed record GetContactSubmissionsQuery(ContactSubmissionStatus? Status, int Skip, int Take);

/// <summary>Reads a page of the inbox. The ordering and the projection live in the reader.</summary>
public sealed class GetContactSubmissionsQueryHandler(IAdminContentReader reader)
    : IQueryHandler<GetContactSubmissionsQuery, AdminContactSubmissionsDto>
{
    public Task<AdminContactSubmissionsDto> HandleAsync(
        GetContactSubmissionsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetSubmissionsAsync(query.Status, query.Skip, query.Take, cancellationToken);
    }
}

/// <summary>
/// Open one enquiry (GLB-10, doc 11 P15 step 7: "reads the enquiry (→ Read)").
/// <para>
/// <b>A read with a side effect, which is unusual enough to justify.</b> Doc 11 P15 makes opening the
/// enquiry the thing that marks it Read — not a button — and it is the right design: the point of the New
/// status is "nobody has looked at this", and a staff member who has it on screen has looked at it. The
/// alternative, a Mark-as-read button, adds a step whose only outcome is that the badge count stops being
/// true as soon as somebody forgets to press it.
/// </para>
/// <para>
/// <b>It is still a command, not a query.</b> It writes, so it is named and registered as one, and the
/// endpoint behind it is a <c>POST</c> rather than the <c>GET</c> the shape of the data suggests — a
/// <c>GET</c> that changes a row is a row a link checker, a prefetch or a crawler can change.
/// </para>
/// </summary>
/// <param name="SubmissionId">Which enquiry.</param>
/// <param name="Actor">Who is reading it, recorded as the handler.</param>
public sealed record OpenContactSubmissionCommand(int SubmissionId, AdminActor Actor);

/// <summary>
/// Move an enquiry's status by hand (GLB-10, doc 11 P15 step 7): Replied, Spam, or back to Read.
/// </summary>
/// <param name="SubmissionId">Which enquiry.</param>
/// <param name="Status">Where it should end up.</param>
/// <param name="Actor">Who is moving it, recorded with the time.</param>
public sealed record SetContactSubmissionStatusCommand(
    int SubmissionId,
    ContactSubmissionStatus Status,
    AdminActor Actor);

/// <summary>
/// Delete one enquiry for good (doc 08 §3.7, <c>Customers.Delete</c>).
/// <para>
/// The case this exists for is a customer having pasted <b>card or bank account details</b> into the
/// message, which "must be removed rather than merely hidden". So it is a hard delete, and the audit row
/// carries the reason and <b>no copy of the body</b>.
/// </para>
/// </summary>
/// <param name="SubmissionId">Which enquiry.</param>
/// <param name="Reason">
/// Why, in the staff member's own words, which doc 08 §3.7 requires the audit row to hold. Required:
/// a permanent deletion of customer data with no stated reason is not a trail.
/// </param>
/// <param name="Actor">Who is deleting it.</param>
public sealed record DeleteContactSubmissionCommand(int SubmissionId, string? Reason, AdminActor Actor);

/// <summary>What an enquiry write decided.</summary>
public enum ContactSubmissionWriteOutcome
{
    /// <summary>The status moved, or the row was deleted.</summary>
    Done = 0,

    /// <summary>No such enquiry.</summary>
    NotFound = 1,

    /// <summary>It was already in that status. Nothing was written and no audit row exists.</summary>
    Unchanged = 2,

    /// <summary>A required field was missing — today only the deletion reason.</summary>
    ReasonRequired = 3,
}

/// <summary>
/// The enquiry inbox's writes (GLB-10, doc 11 P15 step 7).
/// <para>
/// <b>Nothing in this class logs, audits or returns any part of the enquiry's text.</b> Doc 11 P8 records
/// that a customer may paste bank account details into the message; doc 08 §3.7 requires even the
/// deletion's audit row to hold no copy of the body. The audit rows these handlers write name the
/// enquiry's id and its old and new status, and for a deletion the staff member's reason — never the name,
/// the address, the telephone number or a character of the message.
/// </para>
/// <para>
/// <b>Replying is not modelled, on purpose.</b> Doc 11 P15: staff "repl[y] by email or phone outside the
/// system". Marking Replied records that somebody did it and who; there is no reply body here, because
/// there is no mail transport in this build and a field that stored a reply nothing sends would be the
/// promise checkout was marked Major for making.
/// </para>
/// </summary>
public sealed class ContactEnquiryWriteHandlers(
    IContactEnquiryStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<OpenContactSubmissionCommand, ContactSubmissionWriteOutcome>,
      ICommandHandler<SetContactSubmissionStatusCommand, ContactSubmissionWriteOutcome>,
      ICommandHandler<DeleteContactSubmissionCommand, ContactSubmissionWriteOutcome>
{
    /// <summary>How much of a reason doc 08 §3.7's audit row needs to be worth having.</summary>
    public const int ReasonMinLength = 3;

    /// <summary>What fits in <c>ops.AuditLogs.AfterJson</c> without turning the trail into prose.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>
    /// Marks a New enquiry Read. A no-op for one that is already Read, Replied or Spam — opening a Spam
    /// enquiry to check it was really spam must not quietly reclassify it, and opening a Replied one must
    /// not take the reply off it.
    /// </summary>
    public async Task<ContactSubmissionWriteOutcome> HandleAsync(
        OpenContactSubmissionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var submission = await store.FindAsync(command.SubmissionId, cancellationToken);
        if (submission is null)
        {
            return ContactSubmissionWriteOutcome.NotFound;
        }

        if (submission.Status != ContactSubmissionStatus.New)
        {
            return ContactSubmissionWriteOutcome.Unchanged;
        }

        return await MoveAsync(submission, ContactSubmissionStatus.Read, command.Actor, cancellationToken);
    }

    public async Task<ContactSubmissionWriteOutcome> HandleAsync(
        SetContactSubmissionStatusCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var submission = await store.FindAsync(command.SubmissionId, cancellationToken);
        if (submission is null)
        {
            return ContactSubmissionWriteOutcome.NotFound;
        }

        return await MoveAsync(submission, command.Status, command.Actor, cancellationToken);
    }

    public async Task<ContactSubmissionWriteOutcome> HandleAsync(
        DeleteContactSubmissionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = command.Reason?.Trim();
        if (reason is null || reason.Length < ReasonMinLength)
        {
            return ContactSubmissionWriteOutcome.ReasonRequired;
        }

        if (reason.Length > ReasonMaxLength)
        {
            reason = reason[..ReasonMaxLength];
        }

        var now = clock.UtcNow.UtcDateTime;

        // The audit row is written *before* the delete and committed by it, so the two are one
        // transaction: a deletion with no trail and a trail of a deletion that did not happen are both
        // states nobody could reason about afterwards (issue #59, IAuditLogWriter.Enlist).
        //
        // The before/after shape says what was removed without saying what it said: an id, the status it
        // was in, and when it arrived. Doc 08 §3.7 forbids a copy of the body; the name, the address and
        // the telephone number are left out on the same reasoning, since the reason this delete exists is
        // that the row held something that must stop existing.
        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Delete,
            ContentAuditTrail.ContactSubmissionEntityType,
            command.SubmissionId.ToString(CultureInfo.InvariantCulture),
            before: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = null,
            },
            after: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleted"] = true,
                ["reason"] = reason,
            },
            command.Actor,
            now));

        // The audit row and the delete are committed together: DeleteAsync is set-based
        // (ExecuteDeleteAsync, doc 08 §10.2) and the store commits the enlisted row in the same unit of
        // work. A row that had already gone answers NotFound, and the enlisted audit row goes with it
        // because nothing was saved.
        if (!await store.DeleteAsync(command.SubmissionId, cancellationToken))
        {
            return ContactSubmissionWriteOutcome.NotFound;
        }

        return ContactSubmissionWriteOutcome.Done;
    }

    private async Task<ContactSubmissionWriteOutcome> MoveAsync(
        ContactSubmission submission,
        ContactSubmissionStatus status,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        var before = submission.Status;
        var now = clock.UtcNow.UtcDateTime;

        // Idempotent: SetStatus answers false when it was already there, so two staff marking the same
        // enquiry Replied is a no-op rather than the second one's name replacing the first one's.
        if (!submission.SetStatus(status, actor.UserId, now))
        {
            return ContactSubmissionWriteOutcome.Unchanged;
        }

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.StatusChange,
            ContentAuditTrail.ContactSubmissionEntityType,
            submission.Id.ToString(CultureInfo.InvariantCulture),
            before: new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = before.ToString() },
            after: new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = status.ToString() },
            actor,
            now));

        await store.SaveAsync(cancellationToken);

        return ContactSubmissionWriteOutcome.Done;
    }
}
