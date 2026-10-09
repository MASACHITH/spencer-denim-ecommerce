using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// Raises the <c>EmailDeliveryFailed</c> alert for an outbox row that has reached <c>Dead</c>
/// (doc 11 §4, §7 G-37).
/// <para>
/// <b>Everything it writes comes from the database, and the caller only names the row.</b> Both paths to
/// <c>Dead</c> can produce an id and the second of them (a recovered lease) has nothing else, so taking
/// the id and reading the rest here is what lets one implementation serve both. It also means the three
/// facts in the alert are the three facts on the row, rather than three values that travelled through a
/// sweep and could have drifted.
/// </para>
/// <para>
/// <b>It refuses to raise an alert for a row that is not Dead.</b> Doc 11 §4 ties the alert to status 4,
/// and a banner telling a Super Admin that a message failed while the ladder was still retrying it would
/// be worse than no banner — they would telephone a customer who is about to get the email anyway.
/// </para>
/// <para>
/// <b>And it refuses to raise a second alert for the same message.</b> Neither path can kill one row
/// twice (both transitions are conditional <c>UPDATE</c>s with one winner), so this is not load-bearing
/// today; it is what makes the port safe to call again, which is the difference between a sweep that can
/// be retried and one that cannot.
/// </para>
/// </summary>
internal sealed class DeadEmailAlerts(
    AppDbContext dbContext,
    IAdminAlertWriter alertWriter,
    IClock clock)
    : IDeadEmailAlerts
{
    public async Task<bool> RaiseAsync(long outboxMessageId, CancellationToken cancellationToken)
    {
        var message = await dbContext.EmailOutbox
            .AsNoTracking()
            .Where(candidate => candidate.Id == outboxMessageId)
            .Select(candidate => new DeadRow(
                candidate.Status,
                candidate.TemplateCode,
                candidate.RelatedEntityType,
                candidate.RelatedEntityId))
            .FirstOrDefaultAsync(cancellationToken);

        if (message is null || message.Status != EmailOutboxStatus.Dead)
        {
            return false;
        }

        if (await AlreadyRaisedAsync(outboxMessageId, cancellationToken))
        {
            return false;
        }

        var (orderPublicId, orderNumber) = await ReadOrderAsync(message, cancellationToken);

        var alert = DeadEmailAlert.For(
            new DeadEmailFacts(outboxMessageId, message.TemplateCode, orderPublicId, orderNumber),
            clock.UtcNow.UtcDateTime);

        await alertWriter.RaiseAsync(alert, cancellationToken);

        return true;
    }

    /// <summary>
    /// Whether this message already has an alert, open or acknowledged.
    /// </summary>
    /// <remarks>
    /// <b>Keyed on the related-entity pair, which for this alert type is the outbox row</b> (see
    /// <c>DeadEmailAlert</c>): one message is one alert, and two messages about the same order must each
    /// get their own. An acknowledged one counts — a Super Admin who has already dealt with this message
    /// must not get the same banner back.
    /// <para>
    /// <b>No index serves this, and it does not need one.</b> <c>ops.AdminAlerts</c> holds a handful of
    /// rows a month (doc 08 §8.4 keeps acknowledged ones for a year) and this statement runs only on the
    /// rare occasion a message dies, so a scan of it costs less than an index paid for on every insert.
    /// Doc 08 §3.8 lists one index on this table and this does not add a second.
    /// </para>
    /// </remarks>
    private Task<bool> AlreadyRaisedAsync(long outboxMessageId, CancellationToken cancellationToken)
    {
        var relatedId = outboxMessageId.ToString(CultureInfo.InvariantCulture);

        return dbContext.AdminAlerts
            .AsNoTracking()
            .AnyAsync(
                alert => alert.AlertType == AdminAlertTypes.EmailDeliveryFailed
                    && alert.RelatedEntityType == AdminAlert.EmailOutboxEntityType
                    && alert.RelatedEntityId == relatedId,
                cancellationToken);
    }

    /// <summary>
    /// The order the dead message was about, and its number.
    /// </summary>
    /// <remarks>
    /// <b>The number is read here and written into the alert, rather than joined at read time.</b> Doc 11
    /// §4 requires the alert itself to name the order number, and <c>ops.EmailOutbox</c> stores an order's
    /// <c>PublicId</c> and not its number — so somebody has to resolve it, and doing it once when a
    /// message dies is cheaper and more honest than doing it on every banner load against a column whose
    /// order may by then have been removed.
    /// <para>
    /// <b>A missing order is not a reason to skip the alert.</b> The number comes back null, the title
    /// says the order can no longer be found, and the template code and row id are still named: an alert
    /// withheld because one of its three facts was unavailable would restore the silence G-37 exists to
    /// end.
    /// </para>
    /// </remarks>
    private async Task<(Guid? PublicId, string? OrderNumber)> ReadOrderAsync(
        DeadRow message,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(message.RelatedEntityType, EmailOutboxMessage.OrderEntityType, StringComparison.Ordinal)
            || !Guid.TryParse(message.RelatedEntityId, CultureInfo.InvariantCulture, out var publicId))
        {
            return (null, null);
        }

        var orderNumber = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.PublicId == publicId)
            .Select(order => order.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        return (publicId, orderNumber);
    }

    /// <summary>
    /// What the outbox row says about itself. A record class, not a <c>record struct</c>: a projection to
    /// a value type cannot answer null, so a missing row would come back as <c>Pending</c> with an empty
    /// template code and be mistaken for a live message.
    /// </summary>
    private sealed record DeadRow(
        EmailOutboxStatus Status,
        string TemplateCode,
        string? RelatedEntityType,
        string? RelatedEntityId);
}
