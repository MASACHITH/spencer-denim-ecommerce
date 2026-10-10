namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// Lets a "PayHere is switched on but cannot take orders" warning through at most once per window, per
/// process.
/// <para>
/// <b>A singleton rather than a static field, for <c>BankDetailsWarningOnce</c>'s reason, and a window rather
/// than "once"</b> for a reason of its own: the provider is asked whether it is available on every view of
/// the checkout page, so an unthrottled warning is one line per shopper per page view and buries the log it
/// is meant to be seen in; but "once per process" would say it at start-up and never again, and a shop that
/// has been misconfigured for a week should still be saying so. Half an hour is a line every half hour for as
/// long as the fault lasts.
/// </para>
/// </summary>
internal sealed class PayHereWarningThrottle(TimeSpan window)
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastWarned;

    /// <summary>The window the registered instance uses.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    public PayHereWarningThrottle()
        : this(DefaultWindow)
    {
    }

    /// <summary>
    /// <see langword="true"/> for the first caller in each window. Safe for two requests arriving at once.
    /// </summary>
    public bool ShouldWarn(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastWarned is { } last && now - last < window)
            {
                return false;
            }

            _lastWarned = now;

            return true;
        }
    }
}
