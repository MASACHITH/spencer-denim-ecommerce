namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// Whether the "the shop's bank details are not set" warning has already been written in this process.
/// </summary>
/// <remarks>
/// <b>A one-line singleton rather than a static field, so the throttle can be tested.</b>
/// <see cref="BankTransferProvider"/> is resolved per request and its warning has to fire once for the
/// process, not once per request: its only caller is <c>GET /api/orders/{token}</c>, which is
/// unauthenticated, unthrottled (doc 11 §7 G-41 is owed) and re-read on every refresh — so the first
/// version logged one Warning per page view and a shop live without bank details would have buried its own
/// log. That is the failure <c>ExpireUnpaidOrdersService</c> guards against one file away: a signal that
/// fires every cycle stops being a signal.
/// <para>
/// A static field would do the same job and could not be reset between tests, which is how a throttle ends
/// up either untested or order-dependent. One instance per process is registered; a test constructs its
/// own.
/// </para>
/// <para>
/// <b>Once per process, not once per order</b>, and the message says so. Per order is the better signal and
/// needs somewhere bounded to keep it — <c>ops.AdminAlerts</c> is where this belongs (doc 08 §3.8 DB2) and
/// is not this task's table.
/// </para>
/// </remarks>
internal sealed class BankDetailsWarningOnce
{
    private int _taken;

    /// <summary>
    /// <see langword="true"/> for the first caller only. Safe for two requests arriving at once, which is
    /// the whole reason it is not a plain <see cref="bool"/>.
    /// </summary>
    public bool ShouldWarn() => Interlocked.Exchange(ref _taken, 1) == 0;
}
