using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Ops.Alerts;

/// <summary>
/// Acknowledges an alert (doc 08 §3.8 N2, R10).
/// <para>
/// <b>It records that a human saw the alert. It changes nothing about the problem</b> (doc 08 §3.8;
/// doc 11 §6.1's escalation rules: "Nothing is ever resolved by silencing it"). No email is resent, no
/// payment is retried, no stock is adjusted — and no string this handler or its screen produces may
/// suggest otherwise.
/// </para>
/// <para>
/// <b>Visibility is computed here and enforced in the statement.</b> The same two steps as the read:
/// effective permissions from the user's roles (doc 08 §3.1 R4), then <c>AdminAlertVisibility</c>'s R10
/// list, which goes into the acknowledging <c>UPDATE</c>'s <c>WHERE</c>. A user R10 gives no path to
/// cannot acknowledge anything, and gets the same answer as a wrong id.
/// </para>
/// </summary>
internal sealed class AcknowledgeAdminAlertCommandHandler(
    IEffectivePermissionsProvider permissionsProvider,
    IAdminAlertAcknowledger acknowledger)
    : ICommandHandler<AcknowledgeAdminAlertCommand, AcknowledgeAdminAlertResult>
{
    public async Task<AcknowledgeAdminAlertResult> HandleAsync(
        AcknowledgeAdminAlertCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Actor.UserId is not { } userId)
        {
            // Doc 08 §3.8 makes AcknowledgedByUserId nullable, but an acknowledgement with nobody in it
            // is a row that says the problem was seen and cannot say by whom — which is the one fact the
            // column exists to carry. Refused rather than stored.
            return new AcknowledgeAdminAlertResult.NoActor();
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();

        if (note is not null && note.Length > AdminAlert.AcknowledgementNoteMaxLength)
        {
            // Refused rather than clipped: a note is somebody's sentence about a live problem, and
            // silently losing its last half is worse than asking for a shorter one. (The outbox's
            // LastError is clipped, for the opposite reason — nobody writes it and losing it would cost
            // the row's whole state.)
            return new AcknowledgeAdminAlertResult.NoteTooLong(AdminAlert.AcknowledgementNoteMaxLength);
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken);

        var visibleTypes = AdminAlertVisibility.VisibleTypes(
            permissions.GrantsAllPermissions,
            permissions.Permissions);

        if (visibleTypes.Count == 0)
        {
            return new AcknowledgeAdminAlertResult.NotFound();
        }

        var (outcome, alert) = await acknowledger.AcknowledgeAsync(
            command.AlertId,
            visibleTypes,
            note,
            command.Actor,
            cancellationToken);

        return outcome switch
        {
            AcknowledgeAlertOutcome.Acknowledged => new AcknowledgeAdminAlertResult.Acknowledged(
                alert!.Id, alert.AlertType, alert.AcknowledgedAtUtc),

            AcknowledgeAlertOutcome.AlreadyAcknowledged => new AcknowledgeAdminAlertResult.AlreadyAcknowledged(
                alert!.Id, alert.AlertType, alert.AcknowledgedAtUtc),

            _ => new AcknowledgeAdminAlertResult.NotFound(),
        };
    }
}
