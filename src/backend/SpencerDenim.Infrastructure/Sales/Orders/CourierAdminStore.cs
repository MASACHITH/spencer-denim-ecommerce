using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// Adding and editing couriers (<c>sales.Couriers</c>, ADM-09 / ADM-22).
/// <para>
/// <b>EF rather than raw SQL, unlike the fulfilment transitions, and the difference is what the row
/// is.</b> An order's status is money-adjacent state whose concurrency control is a conditional UPDATE's
/// row count; a courier is reference data a staff member edits on a form, and <c>sales.Couriers</c> is
/// <c>[A]</c> in doc 08 §3.5 — audited by the <c>SaveChanges</c> interceptor, which raw SQL would bypass.
/// So this store tracks the entity and lets the interceptor do its job, and the only thing it writes by
/// hand is nothing at all.
/// </para>
/// </summary>
internal sealed class CourierAdminStore(AppDbContext dbContext, IAuditLogWriter auditLog)
    : ICourierAdminStore
{
    /// <summary>
    /// SQL Server's "violation of unique index" errors.
    /// </summary>
    /// <remarks>
    /// 2627 is a constraint violation and 2601 a unique-index violation; <c>UX_Couriers_Name</c> is a
    /// unique index, so 2601 is the one that actually arrives — and both are listed because which of them
    /// SQL Server raises depends on how the uniqueness was declared, and a future migration that made it a
    /// constraint would otherwise turn a tidy 400 into a 500.
    /// </remarks>
    private static readonly int[] UniqueViolations = [2601, 2627];

    public async Task<CourierWriteResult> CreateAsync(
        CourierDraft draft,
        AdminActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(actor);

        var courier = new Courier
        {
            Name = draft.Name,
            TrackingUrlTemplate = draft.TrackingUrlTemplate,
            Phone = draft.Phone,
            IsActive = draft.IsActive,
            SortOrder = draft.SortOrder,
        };

        dbContext.Couriers.Add(courier);

        // The audit row the interceptor writes covers the column values; this one records the act and its
        // actor's address and correlation id, which is what doc 08 §3.8 asks of a settings-level change.
        // Enlisted, so it commits with the insert (issue #59).
        auditLog.Enlist(Audit(actor, nowUtc, AuditActions.Create, draft, null));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateName(exception))
        {
            // **Caught rather than pre-checked.** A read-then-insert would be a race: two staff members
            // adding the same carrier at once would both find nothing and one would get a 500 out of the
            // index. Catching the index's own answer is the same outcome without the window — and the index
            // is the thing that actually enforces it.
            //
            // The tracker is cleared so a caller reusing this scope does not carry the failed insert into
            // its next SaveChanges.
            dbContext.ChangeTracker.Clear();
            return new CourierWriteResult.Duplicate(draft.Name);
        }

        return new CourierWriteResult.Applied(courier.Id);
    }

    public async Task<CourierWriteResult> UpdateAsync(
        int courierId,
        CourierDraft draft,
        AdminActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(actor);

        var courier = await dbContext.Couriers
            .FirstOrDefaultAsync(candidate => candidate.Id == courierId, cancellationToken);

        if (courier is null)
        {
            return new CourierWriteResult.NotFound();
        }

        // Every field is assigned, because the command carries every field — see ICourierAdminStore.
        // Assigning only what changed would buy nothing (the interceptor diffs anyway) and would make this
        // method's contract ambiguous about what an unmentioned field means, which is the ambiguity four
        // defects in this project came out of.
        courier.Name = draft.Name;
        courier.TrackingUrlTemplate = draft.TrackingUrlTemplate;
        courier.Phone = draft.Phone;
        courier.IsActive = draft.IsActive;
        courier.SortOrder = draft.SortOrder;

        auditLog.Enlist(Audit(actor, nowUtc, AuditActions.Update, draft, courierId));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateName(exception))
        {
            dbContext.ChangeTracker.Clear();
            return new CourierWriteResult.Duplicate(draft.Name);
        }

        return new CourierWriteResult.Applied(courier.Id);
    }

    /// <summary>Whether <paramref name="exception"/> is <c>UX_Couriers_Name</c> refusing a repeat.</summary>
    /// <remarks>
    /// <b>The index name is checked as well as the error number</b>, so a different unique violation — one
    /// a future column adds — is not reported to a staff member as "that courier name is taken". It would
    /// otherwise be a message that sent them looking for a row that does not exist.
    /// </remarks>
    private static bool IsDuplicateName(DbUpdateException exception) =>
        exception.InnerException is SqlException sql
        && UniqueViolations.Contains(sql.Number)
        && sql.Message.Contains("UX_Couriers_Name", StringComparison.Ordinal);

    private static AuditLog Audit(
        AdminActor actor,
        DateTime nowUtc,
        string action,
        CourierDraft draft,
        int? courierId) =>
        new()
        {
            OccurredAtUtc = nowUtc,
            UserId = actor.UserId,
            UserEmail = actor.Email,
            Action = action,
            EntityType = nameof(Courier),

            // The courier's own id, or null for a create — which has not been allocated one yet. The
            // interceptor's row for the same SaveChanges carries the id, so nothing is lost; inventing a
            // value here would be worse than an honest null.
            EntityId = courierId?.ToString(CultureInfo.InvariantCulture),
            AfterJson = $$"""
                {"name":{{Json(draft.Name)}},"trackingUrlTemplate":{{Json(draft.TrackingUrlTemplate)}},"phone":{{Json(draft.Phone)}},"isActive":{{(draft.IsActive ? "true" : "false")}},"sortOrder":{{draft.SortOrder.ToString(CultureInfo.InvariantCulture)}}}
                """,
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CorrelationId = actor.CorrelationId,
        };

    /// <summary>A JSON string literal, or <c>null</c>, for the audit row's <c>AfterJson</c> (CK ISJSON).</summary>
    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);
}
