namespace SpencerDenim.Application.Sales.Orders.Contracts;

/// <summary>
/// One courier, as the admin screen sees it (<c>sales.Couriers</c>, ADM-09 / ADM-22, doc 11 P9).
/// </summary>
/// <remarks>
/// <b>There is no seeded courier and there is deliberately no default one.</b> Which carriers the client
/// uses, and what their tracking URLs look like, is the client's information — the same class of fact as
/// their bank account. This project has twice shipped an invented client value as a Major finding, so
/// nothing in this build names a courier: not a seeder row, not a fallback, and not a form placeholder
/// that renders. The consequence is that a brand-new installation cannot mark a delivery order shipped,
/// and that is correct; what the screen owes the staff member is the sentence "add a courier first", not a
/// disabled button.
/// </remarks>
/// <param name="Id">
/// The courier's own id.
/// <para>
/// <b>An internal id in a payload, which GLB-11 keeps out of <i>URLs</i> and not out of bodies.</b>
/// <c>sales.Couriers</c> has no public token column and inventing one would need a migration for no gain:
/// a courier is reference data a staff member picks from a list, not a resource a stranger can guess at —
/// every route that takes one is behind <c>Orders.Edit</c> or <c>Settings.Edit</c>, and the id leaks
/// nothing about anybody. The <i>order</i> keeps its token, because an order link is a credential.
/// </para>
/// </param>
/// <param name="Name">What staff and customers call them. Unique, so an order's courier is unambiguous.</param>
/// <param name="TrackingUrlTemplate">
/// A URL containing <c>{trackingNumber}</c>, or <see langword="null"/> for a courier with no online
/// tracking — then the number is shown as text. The placeholder is checked in the database
/// (<c>CK_Couriers_TrackingUrlTemplate</c>), because a template without it would send every customer to the
/// courier's home page.
/// </param>
/// <param name="Phone">Who to ring about a parcel.</param>
/// <param name="IsActive">
/// Whether a new dispatch may choose it. <b>Deactivating never hides it from an order that already
/// shipped with it</b>: the order holds a snapshot of the name (doc 11 P9), so a past dispatch note keeps
/// reading the same.
/// </param>
/// <param name="SortOrder">The order the list is drawn in, lowest first.</param>
/// <param name="OrdersShipped">
/// How many orders were dispatched with this courier.
/// <para>
/// It is here to answer the one question a staff member has before deactivating one — "is anything of ours
/// out with them?" — and it counts <i>every</i> order ever shipped with it rather than only the ones still
/// in transit, because the figure is also the reason not to rename a courier carelessly.
/// </para>
/// </param>
public sealed record AdminCourierDto(
    int Id,
    string Name,
    string? TrackingUrlTemplate,
    string? Phone,
    bool IsActive,
    short SortOrder,
    int OrdersShipped);
