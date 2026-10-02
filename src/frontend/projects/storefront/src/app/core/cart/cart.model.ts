import { PriceDto } from '../catalog/catalog.model';

/**
 * Why a bag line can or cannot be bought right now.
 *
 * Mirrors `SpencerDenim.Application.Sales.Cart.Contracts.CartLineStatus`. The API sends the member
 * name, so this is a union of those names rather than numbers — the same contract the catalogue's
 * `StockStatus` travels under.
 *
 * The *wording* is this application's job, not the API's: see `cartLineNotice`.
 */
export type CartLineStatus = 'Buyable' | 'NotEnoughStock' | 'OutOfStock' | 'Unavailable';

/**
 * One line of the bag (CART-01).
 *
 * `price` and `lineSubtotal` are **null** when the shop no longer offers the item — unpublished,
 * deleted, or in a category the client has deactivated. A null price is not a zero price, and the
 * view must not print one: there is no offer to quote.
 *
 * `unitPriceAtAdd` is history. It exists so the page can say "the price has changed since you added
 * this" (`priceChanged`), and nothing in the bag is ever totalled from it.
 */
export interface CartLineDto {
  readonly variantId: number;
  readonly productId: number;
  readonly productName: string;
  readonly slug: string;
  readonly sku: string;
  readonly colourId: number;
  readonly colourName: string;
  readonly sizeId: number;
  readonly sizeLabel: string;
  readonly scaleName: string;
  readonly imageUrl: string | null;
  readonly imageAltText: string | null;
  readonly quantity: number;
  /** The largest value the quantity control may offer: min(10, stock), never below `quantity`. */
  readonly maxQuantity: number;
  readonly availableQuantity: number;
  readonly price: PriceDto | null;
  readonly lineSubtotal: number | null;
  readonly unitPriceAtAdd: number;
  readonly priceChanged: boolean;
  readonly status: CartLineStatus;
}

/**
 * The money and the counts for the whole bag (CART-01).
 *
 * There is deliberately **no promo discount, no delivery fee and no grand total**: promo codes need
 * ADM-13 and a delivery fee needs `sales.DeliveryMethodZoneRates`, and neither table exists. The bag
 * shows the cost of the items and says delivery is worked out at checkout, rather than printing a
 * total the shop has not agreed to.
 */
export interface CartTotalsDto {
  /** Σ quantity over every line, including the ones that cannot be bought. The header badge. */
  readonly itemCount: number;
  readonly buyableItemCount: number;
  readonly lineCount: number;
  readonly itemsOriginalTotal: number;
  /** Σ current price × quantity over buyable lines only. */
  readonly itemsSubtotal: number;
  readonly savedTotal: number;
  readonly currency: string;
}

/** The whole bag. The cart token is never in here — it lives only in the HttpOnly cookie. */
export interface CartDto {
  readonly lines: readonly CartLineDto[];
  readonly totals: CartTotalsDto;
}

/** A bag nobody has put anything in. The same shape the API sends for a visitor with no cart. */
export const EMPTY_CART: CartDto = {
  lines: [],
  totals: {
    itemCount: 0,
    buyableItemCount: 0,
    lineCount: 0,
    itemsOriginalTotal: 0,
    itemsSubtotal: 0,
    savedTotal: 0,
    currency: 'LKR',
  },
};

/** True when the line is counted in the subtotal. */
export function isBuyable(line: CartLineDto): boolean {
  return line.status === 'Buyable' || line.status === 'NotEnoughStock';
}

/**
 * What a line says about itself under the name and size, or null when there is nothing to say.
 *
 * The copy lives here rather than in the template so that there is one sentence per state and a
 * unit test can assert on it, and rather than on the server because it is wording, not a rule.
 */
export function cartLineNotice(line: CartLineDto): string | null {
  switch (line.status) {
    case 'Unavailable':
      return 'This item is no longer available. Remove it to carry on.';
    case 'OutOfStock':
      return 'Out of stock. It is not included in your subtotal.';
    case 'NotEnoughStock':
      return line.availableQuantity === 1
        ? 'Only 1 left — reduce the quantity to continue.'
        : `Only ${line.availableQuantity} left — reduce the quantity to continue.`;
    case 'Buyable':
      return line.priceChanged ? 'The price of this item has changed since you added it.' : null;
    default:
      return null;
  }
}
