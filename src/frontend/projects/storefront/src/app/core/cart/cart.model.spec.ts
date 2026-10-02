import { CartLineDto, cartLineNotice, isBuyable } from './cart.model';
import { PriceDto } from '../catalog/catalog.model';

function price(current: number, original = current): PriceDto {
  const discount = original > current ? Math.floor(((original - current) / original) * 100) : 0;
  return { current, original, discountPercent: discount, hasDiscount: discount > 0, currency: 'LKR' };
}

function line(overrides: Partial<CartLineDto> = {}): CartLineDto {
  return {
    variantId: 1,
    productId: 10,
    productName: 'Relaxed Linen Blend Shirt',
    slug: 'relaxed-linen-blend-shirt',
    sku: 'SD-1-M',
    colourId: 2,
    colourName: 'Beige',
    sizeId: 3,
    sizeLabel: 'M',
    scaleName: 'Apparel alpha',
    imageUrl: '/media/public/renditions/x/thumb.webp',
    imageAltText: 'A beige linen shirt',
    quantity: 2,
    maxQuantity: 10,
    availableQuantity: 12,
    price: price(4392, 6990),
    lineSubtotal: 8784,
    unitPriceAtAdd: 4392,
    priceChanged: false,
    status: 'Buyable',
    ...overrides,
  };
}

describe('cart line rules (CART-01)', () => {
  describe('which lines count towards the subtotal', () => {
    it('counts a buyable line and one that is merely short of stock', () => {
      expect(isBuyable(line({ status: 'Buyable' }))).toBe(true);
      expect(isBuyable(line({ status: 'NotEnoughStock' }))).toBe(true);
    });

    it('does not count a line that cannot be sold at all', () => {
      expect(isBuyable(line({ status: 'OutOfStock' }))).toBe(false);
      expect(isBuyable(line({ status: 'Unavailable' }))).toBe(false);
    });
  });

  describe('what a line says about itself', () => {
    it('says nothing when everything about the line is fine', () => {
      expect(cartLineNotice(line())).toBeNull();
    });

    /**
     * The "price changed" notice is the only reason `unitPriceAtAdd` travels at all (doc 08 §3.5).
     * Nothing prices the bag from it, so this sentence is its entire job.
     */
    it('says the price has changed when it has', () => {
      expect(cartLineNotice(line({ priceChanged: true }))).toContain('price');
    });

    it('names how many are left, and gets the singular right', () => {
      expect(cartLineNotice(line({ status: 'NotEnoughStock', availableQuantity: 1 }))).toBe(
        'Only 1 left — reduce the quantity to continue.',
      );
      expect(cartLineNotice(line({ status: 'NotEnoughStock', availableQuantity: 4 }))).toBe(
        'Only 4 left — reduce the quantity to continue.',
      );
    });

    it('says an out-of-stock line is not in the subtotal, because it is not', () => {
      const notice = cartLineNotice(line({ status: 'OutOfStock', availableQuantity: 0 }));

      expect(notice).toContain('Out of stock');
      expect(notice).toContain('subtotal');
    });

    /**
     * An unavailable line is the one a shopper cannot do anything about except take it out, so the
     * sentence has to say that rather than only state the fact.
     */
    it('tells the shopper to remove an item the shop no longer offers', () => {
      const notice = cartLineNotice(line({ status: 'Unavailable', price: null, lineSubtotal: null }));

      expect(notice).toContain('no longer available');
      expect(notice).toContain('Remove');
    });

    /**
     * A withdrawn line's price change is beside the point: there is no price. The status wins, which
     * is what stops the page claiming "the price changed" about something with no price to show.
     */
    it('prefers the unavailable sentence over the price-changed one', () => {
      const notice = cartLineNotice(
        line({ status: 'Unavailable', price: null, lineSubtotal: null, priceChanged: true }),
      );

      expect(notice).toContain('no longer available');
      expect(notice).not.toContain('price of this item has changed');
    });
  });
});
