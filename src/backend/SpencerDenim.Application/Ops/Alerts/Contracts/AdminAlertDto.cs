namespace SpencerDenim.Application.Ops.Alerts.Contracts;

/// <summary>
/// One open alert, as the admin shell's banner reads it (doc 08 §3.8 N2; ADM-04).
/// </summary>
/// <param name="Id">The row id, which is what the acknowledge route takes.</param>
/// <param name="AlertType">One of <c>AdminAlertTypes</c>. The screen groups and styles by it.</param>
/// <param name="Severity">
/// 1 Info, 2 Warning, 3 Critical (doc 08 §3.8). Sent as the number rather than a word because the number
/// is the database's and a translated word would make the payload the place a severity is defined.
/// </param>
/// <param name="Title">
/// The sentence the banner shows. It carries the facts; what to do about them is the screen's, because
/// that changes as the admin panel grows (see <c>AdminAlert.Title</c>).
/// </param>
/// <param name="CreatedAtUtc">When it was raised. The banner shows newest first, as the index is ordered.</param>
/// <param name="RelatedEntityType">
/// What the alert is about — <c>Order</c>, <c>EmailOutbox</c> — or null for one about the shop as a whole.
/// </param>
/// <param name="RelatedEntityId">That entity's key.</param>
/// <param name="OrderPublicId">
/// The order a staff member should open, when the alert's details name one: the token the admin order
/// route takes (<c>/orders/{orderPublicId}</c>).
/// <para>
/// <b>Lifted out of <c>DetailsJson</c> rather than being a second copy of
/// <paramref name="RelatedEntityId"/>.</b> A dead-email alert is <i>about</i> the message that died — that
/// is what makes it one alert per message rather than one per order, and two messages for the same order
/// can both die — while the order is the thing a human opens. Any later alert type whose details name an
/// order gets this link for free.
/// </para>
/// </param>
/// <param name="OrderNumber">
/// That order's number, the one the customer quotes, so the link can name where it goes. Null when the
/// alert names no order, or when the order has since been removed.
/// </param>
public sealed record AdminAlertDto(
    int Id,
    string AlertType,
    byte Severity,
    string Title,
    DateTime CreatedAtUtc,
    string? RelatedEntityType,
    string? RelatedEntityId,
    string? OrderPublicId,
    string? OrderNumber);

/// <summary>
/// What the banner needs for one load: the open alerts this user may see, newest first, and how many
/// there are in all.
/// </summary>
/// <param name="TotalOpen">
/// Every open alert this user may see (R10), not just the ones in <paramref name="Alerts"/> — so a banner
/// showing the first few can say so instead of quietly hiding the rest.
/// </param>
/// <param name="Alerts">At most <c>GetOpenAdminAlertsQuery.MaxAlerts</c> of them.</param>
public sealed record AdminAlertBannerDto(int TotalOpen, IReadOnlyList<AdminAlertDto> Alerts);
