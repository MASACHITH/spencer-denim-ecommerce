using SpencerDenim.Application.Sales.Cart.Contracts;

namespace SpencerDenim.Application.Sales.Cart;

/// <summary>
/// A bag plus what the transport must now do about the guest cookie — the two things every cart read
/// and write produces.
/// <para>
/// The cookie decision is made here rather than in the endpoint because it follows from which cart was
/// resolved, which only the use case knows. The endpoint owns the cookie's <i>attributes</i>
/// (<c>HttpOnly</c>, <c>Secure</c>, <c>SameSite</c>) because those are HTTP, not business rules.
/// </para>
/// </summary>
public sealed record CartView(CartDto Cart, CartCookieAction Cookie, Guid? GuestToken)
{
    /// <summary>A visitor with no cart at all. Nothing is created and no cookie is written.</summary>
    public static CartView Empty { get; } = new(CartDto.Empty, CartCookieAction.Leave, null);

    /// <summary>A guest's cart: the token is reissued so the 30-day window slides.</summary>
    public static CartView ForGuest(CartDto cart, Guid token) => new(cart, CartCookieAction.Issue, token);

    /// <summary>
    /// An account cart. Any guest token the request still carried is cleared: the bag it named has
    /// either been merged or is not this customer's, and either way the browser should stop sending it.
    /// </summary>
    public static CartView ForCustomer(CartDto cart, bool hadGuestToken) =>
        new(cart, hadGuestToken ? CartCookieAction.Clear : CartCookieAction.Leave, null);

    /// <summary>
    /// A guest whose cookie named a cart that is not there any more (purged, or never existed). The
    /// stale token is cleared rather than reissued.
    /// </summary>
    public static CartView StaleGuestToken { get; } = new(CartDto.Empty, CartCookieAction.Clear, null);
}
