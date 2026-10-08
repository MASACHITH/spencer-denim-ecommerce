using SpencerDenim.Application.Sales.Orders.Contracts;

namespace SpencerDenim.Application.Sales.Orders.Abstractions;

/// <summary>
/// Read port for the two <c>ops.Settings</c> groups the orders screen depends on: the <c>Store.*</c> legal
/// details an invoice prints, and <c>Orders.AutoDeliverAfterDays</c> (doc 08 §3.8).
/// <para>
/// <b>Its own port rather than <c>ISettingsStore</c></b>, for the reason
/// <c>IBankTransferSettingsReader</c> records: that store hands back tracked rows carrying their
/// <c>RequiredPermission</c> column, which is what an admin write path needs and what a read on a hot path
/// must not have. This port answers two questions and returns no rows.
/// </para>
/// </summary>
public interface IOrderSettingsReader
{
    /// <summary>
    /// The seller's legal details for an invoice. <b>Never <see langword="null"/></b>: an unset key comes
    /// back as a <see langword="null"/> field inside <see cref="InvoiceSellerDetails"/> and prints as
    /// nothing, which is the rule an invented address would break.
    /// </summary>
    Task<InvoiceSellerDetails> ReadInvoiceSellerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// How many days after dispatch the auto-deliver job may mark a non-pickup order delivered, or
    /// <see langword="null"/> when the setting is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// <b>Null means "do not run", and that is deliberate rather than defensive.</b> The key is seeded
    /// with doc 08 §3.8's 7, so null can only mean the row was deleted or holds something that is not a
    /// number — and a job that silently fell back to a hard-coded 7 in that case would be moving money's
    /// worth of orders to Delivered on a figure nobody could see on any screen. The job logs and skips the
    /// pass instead, and the order screen says the setting needs fixing.
    /// </remarks>
    Task<int?> ReadAutoDeliverAfterDaysAsync(CancellationToken cancellationToken);
}
