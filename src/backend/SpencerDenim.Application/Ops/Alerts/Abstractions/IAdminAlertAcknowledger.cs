using SpencerDenim.Application.Abstractions.Auditing;

namespace SpencerDenim.Application.Ops.Alerts.Abstractions;

/// <summary>What an acknowledgement did to the row.</summary>
public enum AcknowledgeAlertOutcome
{
    /// <summary>This user's acknowledgement was recorded, with its audit row in the same transaction.</summary>
    Acknowledged = 1,

    /// <summary>There is no alert with that id, or none of a type this user may see (R10).</summary>
    NotFound = 2,

    /// <summary>
    /// Somebody acknowledged it first. <b>Not an error.</b> Doc 08 §3.8 shows the banner "to every
    /// GrantsAll holder until <i>one of them</i> acknowledges it", so a second Super Admin pressing the
    /// button has the outcome they wanted; the screen tells them when, rather than refusing.
    /// </summary>
    AlreadyAcknowledged = 3,
}

/// <summary>One alert's state after an acknowledgement attempt.</summary>
/// <param name="AcknowledgedAtUtc">
/// When it was acknowledged — this attempt's time, or the earlier one's for
/// <see cref="AcknowledgeAlertOutcome.AlreadyAcknowledged"/>.
/// </param>
public sealed record AcknowledgedAlert(int Id, string AlertType, DateTime AcknowledgedAtUtc);

/// <summary>
/// Acknowledges one <c>ops.AdminAlerts</c> row and writes its <c>AlertAcknowledged</c> audit row in the
/// same transaction (doc 08 §3.8: "Acknowledging is audited and doesn't resolve the underlying issue").
/// Implemented in Infrastructure.
/// </summary>
/// <remarks>
/// <b>R10 is enforced in the statement and not before it.</b> <paramref name="visibleTypes"/> goes into
/// the <c>WHERE</c>, so an alert of a type this user has no path to is simply not there to acknowledge —
/// which is the same answer as a wrong id, deliberately: an endpoint that distinguished them would tell a
/// caller that an alert they may not see exists.
/// </remarks>
public interface IAdminAlertAcknowledger
{
    Task<(AcknowledgeAlertOutcome Outcome, AcknowledgedAlert? Alert)> AcknowledgeAsync(
        int alertId,
        IReadOnlyList<string> visibleTypes,
        AdminActor actor,
        CancellationToken cancellationToken);
}
