import { ListingState } from '../../core/catalog/listing-state';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

/**
 * A listing whose filter is the page itself.
 *
 * `/new-arrivals` and `/sale` are two of the five permanent navigation entries (GLB-05). They are
 * the category listing with one filter fixed and no department, so the shopper can still narrow
 * by size, colour, price and the rest without the page's own filter being something they can take
 * off — it is the address, not a choice.
 */
export type ListingMode = 'new-arrivals' | 'sale';

interface ModeDefinition {
  readonly path: string;
  readonly heading: string;
  readonly description: string;
  readonly title: string;
  readonly metaDescription: string;
  /** The chip this listing never shows, because removing it would leave the page meaningless. */
  readonly fixedFilter: 'newArrivals' | 'discount';
  readonly apply: (state: ListingState) => ListingState;
}

export const LISTING_MODES: Readonly<Record<ListingMode, ModeDefinition>> = {
  'new-arrivals': {
    path: '/new-arrivals',
    heading: 'New Arrivals',
    description: 'The latest pieces across Women, Men and Kids.',
    title: brandPageTitle('New Arrivals'),
    metaDescription:
      `The newest ${BRAND.name} pieces across Women, Men and Kids, updated as they land.`,
    fixedFilter: 'newArrivals',
    apply: (state) => ({ ...state, department: null, category: null, newArrivals: true }),
  },
  sale: {
    path: '/sale',
    heading: 'Sale',
    description: 'Reduced pieces across Women, Men and Kids, while stocks last.',
    title: brandPageTitle('Sale'),
    metaDescription:
      `Reduced ${BRAND.name} pieces across Women, Men and Kids. Shop the sale while stocks last.`,
    fixedFilter: 'discount',
    apply: (state) => ({ ...state, department: null, category: null, onSale: true }),
  },
};

/** The mode a route's `data.listing` names, or `null` for an ordinary department listing. */
export function readListingMode(value: unknown): ListingMode | null {
  return value === 'new-arrivals' || value === 'sale' ? value : null;
}
