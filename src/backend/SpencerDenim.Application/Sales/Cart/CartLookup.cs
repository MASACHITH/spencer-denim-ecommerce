namespace SpencerDenim.Application.Sales.Cart;

/// <summary>
/// Who is asking for a bag (doc 08 §3.5: "server-side for everyone").
/// </summary>
/// <param name="CustomerUserId">
/// The signed-in customer, or <see langword="null"/> for a guest. When it is set it is the <b>only</b>
/// thing that decides which cart is returned — see <see cref="ByCustomer"/>.
/// </param>
/// <param name="GuestToken">
/// The <c>Carts.Token</c> from the guest's cookie, or <see langword="null"/> when the request carried
/// no usable one (absent, empty, or not a GUID).
/// </param>
public readonly record struct CartLookup(int? CustomerUserId, Guid? GuestToken)
{
    public static CartLookup ForGuest(Guid? token) => new(null, token);

    public static CartLookup ForCustomer(int customerUserId, Guid? token) => new(customerUserId, token);

    /// <summary>True when a signed-in customer's own cart is the one being asked for.</summary>
    public bool ByCustomer => CustomerUserId is not null;
}

/// <summary>A cart that exists, as the store found or created it.</summary>
/// <param name="Id">The clustered key, used for every subsequent write. Never leaves the server.</param>
/// <param name="Token">The public token. Only ever put into the guest cookie, never into a response body.</param>
/// <param name="CustomerUserId">Set when this is an account cart.</param>
public readonly record struct CartIdentity(int Id, Guid Token, int? CustomerUserId)
{
    public bool IsCustomerCart => CustomerUserId is not null;
}

/// <summary>What the guest cookie must say once a request has been answered.</summary>
public enum CartCookieAction : byte
{
    /// <summary>Nothing to do: no cart was found or created, and nothing was invalidated.</summary>
    Leave = 1,

    /// <summary>
    /// Write the guest token with a fresh 30-day expiry (doc 08 §3.5: sliding). Issued on every answer
    /// that resolved a guest cart, not only on the one that created it, which is what makes the window
    /// slide.
    /// </summary>
    Issue = 2,

    /// <summary>
    /// Delete it. Either the cart it named is gone, or a signed-in customer is using their account cart
    /// and the guest token must not be left lying in the browser.
    /// </summary>
    Clear = 3,
}
