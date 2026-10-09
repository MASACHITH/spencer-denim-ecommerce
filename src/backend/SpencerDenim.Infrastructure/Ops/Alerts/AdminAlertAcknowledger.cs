using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Alerts;

/// <summary>
/// Records that a human saw an alert, with its audit row (doc 08 §3.8 N2: "Acknowledging is audited and
/// doesn't resolve the underlying issue").
/// <para>
/// <b>One transaction, because an acknowledgement with no trail is the thing doc 11 §6.1 warns about.</b>
/// "Nothing is ever resolved by silencing it. Acknowledging an alert records that a human saw it" — and a
/// banner that disappeared with no <c>AlertAcknowledged</c> row behind it would be exactly a silencing
/// with nobody's name on it. The row and the trail commit together or neither does; issue #59's argument,
/// applied to the one write this feature has.
/// </para>
/// <para>
/// <b>The update is conditional on the alert still being open</b> (doc 08 §5.2), so of two Super Admins
/// pressing the button in the same second exactly one acknowledgement and exactly one audit row are
/// recorded — and the second is told who got there first rather than being refused, because doc 08 §3.8
/// shows the banner "until one of them acknowledges it" and the second person's intention is already
/// satisfied.
/// </para>
/// </summary>
internal sealed class AdminAlertAcknowledger(
    AppDbContext dbContext,
    IAuditLogWriter auditLogWriter,
    IClock clock)
    : IAdminAlertAcknowledger
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for an alert.</summary>
    internal const string EntityType = "AdminAlert";

    public async Task<(AcknowledgeAlertOutcome Outcome, AcknowledgedAlert? Alert)> AcknowledgeAsync(
        int alertId,
        IReadOnlyList<string> visibleTypes,
        string? note,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visibleTypes);
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.UserId is not { } userId || visibleTypes.Count == 0)
        {
            return (AcknowledgeAlertOutcome.NotFound, null);
        }

        dbContext.ChangeTracker.Clear();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // Cleared again inside the retry: a retried attempt must not inherit the audit row the failed
            // one added, or the trail would carry two acknowledgements of one alert
            // (OrderFulfilmentStore's lesson).
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            var result = await AcknowledgeOnceAsync(alertId, visibleTypes, note, actor, userId, cancellationToken);

            if (result.Outcome == AcknowledgeAlertOutcome.Acknowledged)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        });
    }

    private async Task<(AcknowledgeAlertOutcome Outcome, AcknowledgedAlert? Alert)> AcknowledgeOnceAsync(
        int alertId,
        IReadOnlyList<string> visibleTypes,
        string? note,
        AdminActor actor,
        int userId,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(alertId, cancellationToken);

        if (existing is null || !visibleTypes.Contains(existing.AlertType, StringComparer.Ordinal))
        {
            // **One answer for "no such alert" and "not a type you may see" (R10).** Two answers would
            // let anyone with a session walk the id space and learn how many security alerts the shop
            // has had.
            return (AcknowledgeAlertOutcome.NotFound, null);
        }

        if (existing.AcknowledgedAtUtc is { } already)
        {
            return (
                AcknowledgeAlertOutcome.AlreadyAcknowledged,
                new AcknowledgedAlert(existing.Id, existing.AlertType, already));
        }

        var nowUtc = clock.UtcNow.UtcDateTime;

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE ops.AdminAlerts
               SET AcknowledgedByUserId = {userId},
                   AcknowledgedAtUtc = {nowUtc},
                   AcknowledgementNote = {note}
             WHERE Id = {alertId}
               AND AcknowledgedAtUtc IS NULL;
            """,
            cancellationToken);

        if (updated != 1)
        {
            // Somebody committed between the read and the update. Their time is the answer, so it is read
            // again rather than guessed at.
            var winner = await ReadAsync(alertId, cancellationToken);

            return winner?.AcknowledgedAtUtc is { } winnerTime
                ? (AcknowledgeAlertOutcome.AlreadyAcknowledged,
                    new AcknowledgedAlert(winner.Id, winner.AlertType, winnerTime))
                : (AcknowledgeAlertOutcome.NotFound, null);
        }

        auditLogWriter.Enlist(new AuditLog
        {
            OccurredAtUtc = nowUtc,
            UserId = userId,
            UserEmail = actor.Email,
            Action = AuditActions.AlertAcknowledged,
            EntityType = EntityType,
            EntityId = alertId.ToString(CultureInfo.InvariantCulture),

            // **No BeforeJson.** The before state of an acknowledgement is "open", which the action name
            // already says; a JSON document repeating it would be noise in a table doc 08 §8.4 keeps for
            // three years.
            AfterJson = JsonSerializer.Serialize(new AcknowledgementTrail(
                existing.AlertType,
                nowUtc,
                note is not null)),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CorrelationId = actor.CorrelationId,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return (
            AcknowledgeAlertOutcome.Acknowledged,
            new AcknowledgedAlert(alertId, existing.AlertType, nowUtc));
    }

    /// <summary>The three things an acknowledgement needs to know about the row.</summary>
    /// <remarks>
    /// A projection rather than a tracked entity: the write is a conditional <c>UPDATE</c> and a tracked
    /// row would tempt a later change into "set the property and save", which cannot express "only if it
    /// is still open".
    /// </remarks>
    private async Task<AlertState?> ReadAsync(int alertId, CancellationToken cancellationToken) =>
        await dbContext.AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.Id == alertId)
            .Select(alert => new AlertState(alert.Id, alert.AlertType, alert.AcknowledgedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A record class and deliberately not a <c>record struct</c>: <c>FirstOrDefaultAsync</c> over a
    /// projection to a value type returns <c>default</c> rather than null, so a missing alert would come
    /// back as id 0 with an empty type and look like an unacknowledged alert
    /// (<c>OrderFulfilmentStore</c> records what that cost in the bank-transfer path).
    /// </summary>
    private sealed record AlertState(int Id, string AlertType, DateTime? AcknowledgedAtUtc);

    /// <summary>
    /// What the audit row says about the acknowledgement.
    /// </summary>
    /// <param name="HasNote">
    /// Whether a note was left, rather than the note itself. The note is on the alert row the audit row
    /// points at, and copying free text a staff member typed into a second table — one that is also not
    /// scrubbed by the PDPA routine (doc 08 §3.1 step 8) — adds a place for a telephone number somebody
    /// pasted to survive, for no reader's benefit.
    /// </param>
    private sealed record AcknowledgementTrail(string AlertType, DateTime AcknowledgedAtUtc, bool HasNote);
}
