using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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
public sealed class AuditAndSoftDeleteInterceptor(IClock clock, IHttpContextAccessor? httpContextAccessor = null) : SaveChangesInterceptor
{
    /// <summary>
    /// The user to stamp on a write, when explicitly set. A hosted service or seeder running outside any HTTP
    /// request has no principal to read, so it sets this directly; an admin write endpoint does not need to —
    /// see <see cref="ResolveCurrentUserId"/>.
    /// </summary>
    public int? CurrentUserId { get; set; }

    /// <summary>
    /// Issue #8 follow-up flagged in the 2026-09-29 journal: nothing wired the signed-in admin's user ID into
    /// this interceptor for any write endpoint, including this feature's own (the first authenticated admin
    /// *write* endpoints in the codebase). Resolved here, once, from the ambient <see cref="HttpContext"/>'s
    /// authenticated principal, rather than requiring every command handler to set <see cref="CurrentUserId"/>
    /// by hand and risk forgetting it. <see cref="CurrentUserId"/> still wins when a caller (a seeder, a test)
    /// sets it explicitly, since there is no HTTP request for this to read in that case anyway.
    /// Internal rather than private for <c>StaffAccountLockout</c> (issue #46), whose atomic counter <c>UPDATE</c>
    /// never passes through the change tracker and stamps the same <c>UpdatedByUserId</c> by calling this.
    /// </summary>
    internal int? ResolveCurrentUserId()
    {
        if (CurrentUserId is { } explicitUserId)
        {
            return explicitUserId;
        }

        var claim = httpContextAccessor?.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier);
        return claim is not null && int.TryParse(claim.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }

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
                    auditable.CreatedByUserId ??= ResolveCurrentUserId();
                    auditable.UpdatedAtUtc = null;
                    auditable.UpdatedByUserId = null;
                    break;

                case EntityState.Modified:
                    // CreatedAtUtc/CreatedByUserId are set once; an update must never move them.
                    entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedByUserId)).IsModified = false;
                    auditable.UpdatedAtUtc = utcNow;
                    auditable.UpdatedByUserId = ResolveCurrentUserId();
                    break;

                default:
                    break;
            }
        }
    }
}
