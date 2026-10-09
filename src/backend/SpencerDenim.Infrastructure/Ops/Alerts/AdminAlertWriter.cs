using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Alerts;

/// <summary>
/// Inserts one <c>ops.AdminAlerts</c> row (doc 08 §3.8 N2). <c>IAuditLogWriter</c>'s shape, for issue
/// #59's reason — see <see cref="IAdminAlertWriter"/>.
/// </summary>
public sealed class AdminAlertWriter(AppDbContext dbContext) : IAdminAlertWriter
{
    public async Task RaiseAsync(AdminAlert alert, CancellationToken cancellationToken)
    {
        Enlist(alert);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds the row to the same scoped <see cref="AppDbContext"/> the caller's use case writes through,
    /// so their own <c>SaveChangesAsync</c> commits the alert in the transaction that caused it.
    /// </summary>
    public void Enlist(AdminAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        dbContext.AdminAlerts.Add(alert);
    }
}
