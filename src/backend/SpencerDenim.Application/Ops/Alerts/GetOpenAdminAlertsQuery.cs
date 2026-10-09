namespace SpencerDenim.Application.Ops.Alerts;

/// <summary>
/// The open alerts the signed-in staff user may see (doc 08 §3.8 R10), for the admin shell's banner.
/// </summary>
/// <param name="UserId">
/// Whose permissions decide what comes back. Read from the cookie's subject at the endpoint, never from a
/// client-supplied field — the whole point of R10 is that the answer differs per user.
/// </param>
public sealed record GetOpenAdminAlertsQuery(int UserId)
{
    /// <summary>
    /// How many alerts one banner carries.
    /// <para>
    /// <b>A cap, not a page.</b> A banner is not a list screen and there is nowhere to page to (the
    /// dashboard is ADM-04 and the outbox screen is doc 11 §7 G-42), so the payload is bounded and the
    /// total is sent beside it: a shop with sixty open alerts gets a banner that says so instead of one
    /// that quietly shows five. Ten is the number a person will actually read before acting on one.
    /// </para>
    /// </summary>
    public const int MaxAlerts = 10;
}
