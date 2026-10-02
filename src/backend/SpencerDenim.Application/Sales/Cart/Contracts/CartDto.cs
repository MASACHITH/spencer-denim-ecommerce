using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Sales.Cart.Contracts;

/// <summary>
/// Why a bag line can or cannot be bought right now (CART-01).
/// <para>
/// The <b>reason</b> travels, not the sentence: the wording is the storefront's, and a status it can
/// switch on is also a status a test can assert on without matching prose.
/// </para>
/// </summary>
public enum CartLineStatus : byte
{
    /// <summary>On sale, in stock, and the whole quantity is available.</summary>
    Buyable = 1,

    /// <summary>
    /// Still for sale, but fewer are left than the bag asks for. The line keeps its quantity — the
    /// shopper chose it — and says how many there are.
    /// </summary>
    NotEnoughStock = 2,

    /// <summary>For sale, none left. Not in the subtotal.</summary>
    OutOfStock = 3,

    /// <summary>
    /// Not for sale any more: the product was unpublished or deleted, its department or primary
    /// category was deactivated (ADM-11 lets the client do that), or the variant was deactivated.
    /// Not in the subtotal.
    /// </summary>
    Unavailable = 4,
}

/// <summary>
/// One line of the bag (CART-01): what it is, how many, and what it costs <b>today</b>.
/// </summary>
/// <param name="Price">
/// Recomputed from the catalogue on every read (doc 08 §6 steps 1–4), never read back from
/// <paramref name="UnitPriceAtAdd"/>. <see langword="null"/> only when the catalogue no longer holds a
/// price for the variant at all, which is also <see cref="CartLineStatus.Unavailable"/>.
/// </param>
/// <param name="LineSubtotal">
/// <c>Price.Current × Quantity</c>, or <see langword="null"/> when there is no current price. It is the
/// line's own total and is included in <see cref="CartTotalsDto.ItemsSubtotal"/> only when the line is
/// buyable — see the remarks there.
/// </param>
/// <param name="UnitPriceAtAdd">
/// What one cost when it first went in (<c>sales.CartItems.UnitPriceAtAdd</c>). Sent so the storefront
/// can say "the price changed", which is the only thing doc 08 §3.5 keeps it for. <b>Nothing prices the
/// bag from it.</b>
/// </param>
/// <param name="PriceChanged">
/// True when today's price differs from <paramref name="UnitPriceAtAdd"/>. Computed here so the
/// comparison is made in one place and in <c>decimal</c>.
/// </param>
/// <param name="AvailableQuantity">
/// <c>inventory.StockLevels.Available</c> for the variant, floored at 0. Nothing is reserved in a cart
/// (doc 08 §3.5), so this is a fact about the moment of the read.
/// </param>
/// <param name="MaxQuantity">
/// The largest quantity this line may be set to: the lower of the per-line ceiling of 10 (doc 08 §3.5)
/// and what is in stock, and never below the quantity already in the bag — a selector that could not
/// re-select its own current value would be unusable.
/// </param>
public sealed record CartLineDto(
    int VariantId,
    int ProductId,
    string ProductName,
    string Slug,
    string Sku,
    int ColourId,
    string ColourName,
    int SizeId,
    string SizeLabel,
    string ScaleName,
    string? ImageUrl,
    string? ImageAltText,
    short Quantity,
    short MaxQuantity,
    int AvailableQuantity,
    PriceDto? Price,
    decimal? LineSubtotal,
    decimal UnitPriceAtAdd,
    bool PriceChanged,
    CartLineStatus Status);

/// <summary>
/// The money and the counts for the whole bag (CART-01).
/// </summary>
/// <remarks>
/// <b>What is deliberately not in here: the promo discount, the delivery fee and a grand total.</b>
/// <list type="bullet">
/// <item><description>
/// A promo code needs <c>marketing.PromoCodes</c> (ADM-13, week 3). There is no table, so there is no
/// code to validate and no discount to apply.
/// </description></item>
/// <item><description>
/// A delivery fee needs <c>sales.DeliveryMethodZoneRates</c> and <c>ref.Districts</c> (#53, #33).
/// Neither exists. Doc 08 §6 step 10 computes the fee from a zone rate row that "must exist and be
/// available"; with no rows, every fee would be invented.
/// </description></item>
/// <item><description>
/// A grand total is <c>subtotal − promo + delivery − delivery discount</c> (§6 step 11). With two of
/// those four unknowable, a number labelled "total" would be a number the shop has not agreed to.
/// </description></item>
/// </list>
/// So the bag shows the cost of the items and says delivery is worked out at checkout. See the task
/// report; the fields arrive with the features that can fill them.
/// </remarks>
/// <param name="ItemCount">
/// Σ quantity over <b>every</b> line, which is what the header badge counts (GLB-04): an item that has
/// gone out of stock is still in the bag, and a badge that stopped counting it would look like the bag
/// had quietly emptied itself.
/// </param>
/// <param name="BuyableItemCount">Σ quantity over the lines that can be bought now.</param>
/// <param name="ItemsOriginalTotal">
/// Σ <c>Price.Original × Quantity</c> over buyable lines (doc 08 §6 step 2's "was" price).
/// </param>
/// <param name="ItemsSubtotal">
/// Σ <c>Price.Current × Quantity</c> over <b>buyable lines only</b>. A line that cannot be sold is not
/// priced into a total the shopper is being asked to pay.
/// </param>
/// <param name="SavedTotal">
/// <paramref name="ItemsOriginalTotal"/> − <paramref name="ItemsSubtotal"/>: the markdown the shopper
/// is getting. Campaign discounts (§6 step 3) are week 3; until then a campaign cannot move a price, so
/// this is the whole saving rather than part of it.
/// </param>
public sealed record CartTotalsDto(
    int ItemCount,
    int BuyableItemCount,
    int LineCount,
    decimal ItemsOriginalTotal,
    decimal ItemsSubtotal,
    decimal SavedTotal,
    string Currency)
{
    public static CartTotalsDto Empty { get; } = new(0, 0, 0, 0m, 0m, 0m, PriceDto.Lkr);
}

/// <summary>
/// The whole bag, as the bag page (CART-01) and the mini bag (CART-02) draw it.
/// <para>
/// <b><c>Carts.Token</c> is not in here and must never be.</b> The token is the bearer of a guest's
/// bag and it travels in an <c>HttpOnly</c> cookie precisely so that a script on the page cannot read
/// it; repeating it in the JSON body would hand it to any script and make the flag decorative.
/// </para>
/// </summary>
public sealed record CartDto(IReadOnlyList<CartLineDto> Lines, CartTotalsDto Totals)
{
    /// <summary>A visitor who has never added anything. The same shape as a bag whose last line was removed.</summary>
    public static CartDto Empty { get; } = new([], CartTotalsDto.Empty);
}
