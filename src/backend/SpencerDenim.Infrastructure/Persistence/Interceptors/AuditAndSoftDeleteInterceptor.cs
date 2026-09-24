using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Doc 08 §1.5 and §10.2. Two rules, in one pass over the change tracker:
/// <list type="number">
/// <item><description>
/// Audit columns on <see cref="IAuditable"/> entities are stamped from <see cref="IClock"/>, so no
/// use case can forget them and no two rows in one transaction disagree about "now".
/// </description></item>
/// <item><description>
/// A tracked <see cref="ISoftDeletable"/> entry in state <c>Deleted</c> <b>throws</b> (DB5). The
/// interceptor deliberately does not convert the delete into a soft delete: by the time it runs, EF
/// has already cascaded deletes to tracked children, and silently persisting that would destroy
/// variants, media and category links. Callers use <c>SoftDelete()</c>; hard deletes use the
/// set-based <c>ExecuteDeleteAsync</c> (N12), which never reaches this interceptor.
/// </description></item>
/// </list>
/// The audit <i>log</i> table (<c>ops.AuditLogs</c>, ADM-03) arrives with the admin milestone; this
/// interceptor only maintains the per-row audit columns.
/// </summary>
public sealed class AuditAndSoftDeleteInterceptor(IClock clock) : SaveChangesInterceptor
{
    /// <summary>
    /// The user to stamp on a write. Storefront reads do not write, and the admin API supplies a real
    /// user once authentication lands (W3-01); until then the columns stay NULL, which doc 08 allows.
    /// </summary>
    public int? CurrentUserId { get; set; }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var utcNow = clock.UtcNow.UtcDateTime;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.DisplayName()} is soft-deletable (doc 08 §10.2, DB5). Call SoftDelete(...) " +
                    "instead of Remove(...), or use ExecuteDeleteAsync for a deliberate hard delete.");
            }

            if (entry.Entity is not IAuditable auditable)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    auditable.CreatedAtUtc = utcNow;
                    auditable.CreatedByUserId ??= CurrentUserId;
                    auditable.UpdatedAtUtc = null;
                    auditable.UpdatedByUserId = null;
                    break;

                case EntityState.Modified:
                    // CreatedAtUtc/CreatedByUserId are set once; an update must never move them.
                    entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedByUserId)).IsModified = false;
                    auditable.UpdatedAtUtc = utcNow;
                    auditable.UpdatedByUserId = CurrentUserId;
                    break;

                default:
                    break;
            }
        }
    }
}
