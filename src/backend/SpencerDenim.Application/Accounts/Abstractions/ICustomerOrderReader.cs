using SpencerDenim.Application.Accounts.Contracts;

namespace SpencerDenim.Application.Accounts.Abstractions;

/// <summary>
/// A customer's own order history (ACC-02, doc 11 P11 step 12).
/// </summary>
/// <remarks>
/// <b>Separate from <c>ICheckoutReader</c>, which reads one order by its token, and from
/// <c>IAdminOrderReader</c>, which reads any order for staff.</b> The three answer different questions
/// about the same table and have different authority: this one is scoped to
/// <c>CustomerUserId = the session's</c> in the <c>WHERE</c> clause of every query, and there is no
/// overload that can be called without it. A method taking an order id and a "check the owner first"
/// comment would be the shape in which one careless endpoint reads somebody else's history.
/// <para>
/// <b>It deliberately does not answer for an order <i>detail</i>.</b> Doc 11 P11 step 12's detail — items,
/// totals, delivery, payment method, status timeline, tracking link, and the actions — is exactly what
/// <c>GET /api/orders/{token}</c> already answers for a guest, and that page is built and tested. A
/// second detail projection would be a second answer to "what does this order say", and the two would
/// drift. See <c>CustomerOrderSummaryDto.Token</c> for why handing the token to its own owner is safe.
/// </para>
/// </remarks>
public interface ICustomerOrderReader
{
    /// <summary>
    /// This customer's orders, newest first, one page at a time.
    /// </summary>
    /// <remarks>
    /// <b>Only orders whose <c>CustomerUserId</c> is this customer's.</b> An order placed as a guest with
    /// the same address and never confirmed is <i>not</i> here, and that is doc 08 §3.1 D5 working as
    /// specified rather than a gap: linking happens on confirmation and nowhere else. Doc 11 P11's own
    /// exception says so — "Customer never confirms → can sign in but guest orders are not linked" — and
    /// the storefront says why rather than showing an empty list with no explanation.
    /// </remarks>
    Task<CustomerOrderPageDto> ListAsync(
        int customerUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether this order belongs to this customer, by its public token.
    /// </summary>
    /// <remarks>
    /// <b>It exists so the account's order-detail route can be about the right order without duplicating
    /// the detail projection.</b> The storefront's account detail page reads the same
    /// <c>GET /api/orders/{token}</c> a guest does; this answers the one extra question an account page
    /// asks, which is whether to show it inside the account shell with a "back to my orders" link or to
    /// treat it as a stranger's link. False for a token that names no order, so it says nothing about
    /// whether one exists.
    /// </remarks>
    Task<bool> OwnsAsync(int customerUserId, Guid token, CancellationToken cancellationToken);
}
