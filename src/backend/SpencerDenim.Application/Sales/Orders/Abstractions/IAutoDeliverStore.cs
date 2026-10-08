namespace SpencerDenim.Application.Sales.Orders.Abstractions;

/// <summary>
/// <c>AutoDeliverShipped</c> (doc 11 P9 step 8, doc 08 §4.1's <c>Shipped → Delivered</c> System path):
/// once a day, mark orders delivered that shipped more than <c>Orders.AutoDeliverAfterDays</c> ago.
/// <para>
/// <b>Never a store-pickup order</b> (doc 08 §4.1 G-18, doc 11 P9). For a pickup, <c>Shipped</c> only
/// means <i>ready for collection</i>: an order nobody has collected has not been delivered, and marking
/// it so would also hand out a "Verified buyer" badge for goods still on the shelf. The exclusion is in
/// <b>both</b> statements below — the one that finds candidates and the one that moves each order — so a
/// future caller of either cannot lose it.
/// </para>
/// <para>
/// <b>It is a System transition and carries no staff permission</b> (doc 08 §4): the history row's source
/// is <c>SystemJob</c> and its <c>ChangedByUserId</c> is null, which is exactly why that column is
/// nullable.
/// </para>
/// </summary>
public interface IAutoDeliverStore
{
    /// <summary>
    /// The orders due to be auto-delivered: <c>Shipped</c>, not pickup, shipped before
    /// <paramref name="shippedBeforeUtc"/>.
    /// </summary>
    /// <param name="batchSize">A ceiling per pass, so one backlog cannot hold locks for minutes.</param>
    /// <param name="shippedBeforeUtc">
    /// <c>now − Orders.AutoDeliverAfterDays</c>, computed by the caller from the setting. Passed in rather
    /// than read here so that the job's clock and the statement's cut-off are provably the same value, and
    /// so a test can choose both.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<int>> FindDueOrdersAsync(
        int batchSize,
        DateTime shippedBeforeUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves one order <c>Shipped → Delivered</c> as the System, answering whether it won the claim.
    /// </summary>
    /// <remarks>
    /// <b><see langword="false"/> is an ordinary outcome, not a failure.</b> A staff member marking the
    /// same order delivered a second before the job does is the expected race, and the conditional UPDATE
    /// is what makes exactly one of them the one that happened. The job logs nothing for a lost claim.
    /// <para>
    /// The pickup exclusion and the age cut-off are both inside this statement as well as in
    /// <see cref="FindDueOrdersAsync"/>. That is not belt and braces: the candidate list is read outside
    /// the transaction, so an order could be re-shipped — or, once ADM-12 exists, edited — between the two,
    /// and a guard that only lives in the finder is a guard a concurrent change can walk past.
    /// </para>
    /// </remarks>
    Task<bool> AutoDeliverAsync(
        int orderId,
        DateTime shippedBeforeUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
