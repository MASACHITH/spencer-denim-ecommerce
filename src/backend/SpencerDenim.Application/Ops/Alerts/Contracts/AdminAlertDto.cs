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
/// That order's number, the one the customer quotes, so the link can name where it goes.
/// <para>
/// <b>Null means the order was not found when the alert was raised</b>, not merely that nobody looked.
/// <c>DeadEmailAlert</c> writes the number only when <c>DeadEmailAlerts</c> resolved it, and orders are
/// never deleted (doc 08 §1.6) — so an order that could not be found then cannot appear later, and the
/// screen may safely treat the pair "<paramref name="OrderPublicId"/> set, this null" as permanent.
/// </para>
/// </param>
/// <param name="TemplateCode">
/// The <c>ops.EmailOutbox.TemplateCode</c> of the message that died, lifted out of the details.
/// <para>
/// <b>The third of G-37's three facts, carried as a field and not only inside
/// <paramref name="Title"/>.</b> The screen's advice depends on it: doc 11 §4 gives a dead
/// <c>StaffInvite</c> a different remedy from a dead <c>PasswordReset</c>, and a sentence cannot be
/// branched on. Null for an alert type that is not about an email — the other six have no producer yet.
/// </para>
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
    string? OrderNumber,
    string? TemplateCode);

/// <summary>
/// What the banner needs for one load: the open alerts this user may see, newest first, and whether there
/// are more than it is showing.
/// </summary>
/// <param name="MoreThanShown">
/// True when this user has more open alerts than <paramref name="Alerts"/> holds — so a banner showing
/// the newest few says so instead of quietly hiding the rest.
/// <para>
/// <b>A flag and not a total, and the reason is measured.</b> A <c>COUNT(*)</c> over the same predicate
/// cannot use <c>IX_AdminAlerts_Open</c>: 5,020 logical reads against the list's 38, on 20,000 alerts,
/// on every admin page load. See <c>IAdminAlertReader</c>. What is lost is a precise number nobody acts
/// on; doc 11 §6.1 step 6 says to open every alert, and this says whether they have finished.
/// </para>
/// </param>
/// <param name="Alerts">At most <c>GetOpenAdminAlertsQuery.MaxAlerts</c> of them.</param>
public sealed record AdminAlertBannerDto(bool MoreThanShown, IReadOnlyList<AdminAlertDto> Alerts);
