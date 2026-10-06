namespace SpencerDenim.Application.Sales.Checkout.Abstractions;

/// <summary>
/// Allocates from <c>sales.NumberSeries</c> (doc 08 §3.5).
/// </summary>
/// <remarks>
/// <b>Doc 08 §3.5 specifies this exactly, and the specification is the whole value of it:</b>
/// <list type="number">
/// <item><description>
/// <c>UPDATE … SET NextValue = NextValue + 1 OUTPUT deleted.NextValue WHERE SeriesKey = @k</c> — one
/// statement, so the read and the increment cannot be separated by another caller. A
/// <c>SELECT</c>-then-<c>UPDATE</c> would hand two checkouts the same number, and the unique index on
/// <c>OrderNumber</c> would then fail one of them *after* its stock was reserved.
/// </description></item>
/// <item><description>
/// The row is <b>inserted lazily</b>, with a retry on a primary-key violation: the first order of a new
/// Colombo year creates <c>ORDER-2027</c>, and two first orders at once mean one of them loses the insert
/// and re-runs the update.
/// </description></item>
/// <item><description>
/// <b>Order numbers are allocated in a short separate transaction</b> (<see cref="AllocateOrderNumberAsync"/>)
/// so that checkouts do not serialise on the series row — every concurrent placement would otherwise queue
/// behind whoever held it, for as long as their stock reservation took. The price is that
/// <b>gaps are allowed</b>: a checkout that fails after allocating has consumed a number, and doc 08 and
/// doc 11 P6 both say so in as many words.
/// </description></item>
/// <item><description>
/// Invoice numbers are the opposite trade and are <b>not allocated here</b>: doc 08 allocates them
/// <i>inside</i> the Paid transaction so the series is gapless, which is a legal requirement an order
/// number does not have. That transition is W3-03's step 3. <see cref="AllocateAsync"/> is the shared
/// primitive it should call, which is why this port is not named after orders.
/// </description></item>
/// <item><description>
/// A <c>SEQUENCE</c> is not used: it cannot reset per year safely and never rolls back.
/// </description></item>
/// </list>
/// </remarks>
public interface INumberSeriesAllocator
{
    /// <summary>
    /// The next value of <paramref name="seriesKey"/>, creating the series if it does not exist.
    /// <b>Enlists in the ambient transaction if there is one</b>, which is what makes it usable for a
    /// gapless invoice number inside the Paid transaction.
    /// </summary>
    /// <param name="seriesKey">
    /// e.g. <c>ORDER-2026</c> (<c>DocumentNumber.OrderSeriesKey</c>).
    /// </param>
    /// <param name="prefix">
    /// The series' prefix, used only when the row has to be created: <c>SD</c> or <c>INV</c>.
    /// </param>
    Task<int> AllocateAsync(string seriesKey, string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// A formatted order number (<c>SD-2026-000123</c>) for the Colombo year of
    /// <paramref name="placedAt"/>, allocated in a <b>transaction of its own</b> that is committed before
    /// it returns — deliberately outside any transaction the caller is in, so the series row's lock is held
    /// for one statement rather than for the length of a checkout.
    /// </summary>
    Task<string> AllocateOrderNumberAsync(DateTimeOffset placedAt, CancellationToken cancellationToken);
}
