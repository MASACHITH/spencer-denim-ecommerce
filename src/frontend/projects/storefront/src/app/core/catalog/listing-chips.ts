import {
  AGE_GROUP_OPTIONS,
  COLOUR_OPTIONS,
  FILTER_GROUP_LABEL,
  FIT_OPTIONS,
  FilterOption,
  MATERIAL_OPTIONS,
  OCCASION_OPTIONS,
  PRICE_BANDS,
} from './filter-options';
import {
  LIST_FILTER_KEYS,
  ListFilterKey,
  ListingState,
  clearFilters,
  hasDiscountFilter,
  toggleListValue,
  withFilter,
} from './listing-state';
import { formatLkr } from './money';

/**
 * The active-filter chips (PLP-03).
 *
 * Department and category are not chips: they are the page itself, they are already in the
 * breadcrumb, and removing them has nowhere to go — there is no listing route without a
 * department. Every other filter in the URL gets a chip, including one set by a link from
 * elsewhere in the site, so a shopper who arrived on a pre-filtered listing can always see what
 * is narrowing it and take it off.
 */

/** What a chip removes. `list` chips also carry the value; the others are whole-filter chips. */
export type ChipKind = ListFilterKey | 'price' | 'rating' | 'inStock' | 'discount' | 'newArrivals';

export interface FilterChip {
  /** Stable across renders, so `@for` can track it. */
  readonly id: string;
  readonly kind: ChipKind;
  /** The value a repeatable filter chip removes; empty for whole-filter chips. */
  readonly value: string;
  /** What the shopper reads, e.g. "Colour: Navy". */
  readonly label: string;
  /** What a screen reader hears on the remove button. */
  readonly removeLabel: string;
}

const LIST_OPTIONS: Readonly<Record<ListFilterKey, readonly FilterOption[]>> = {
  ageGroups: AGE_GROUP_OPTIONS,
  sizes: [],
  colours: COLOUR_OPTIONS,
  materials: MATERIAL_OPTIONS,
  fits: FIT_OPTIONS,
  occasions: OCCASION_OPTIONS,
};

/** Every chip the current state should show, in the order the filter panel lists the groups. */
export function activeChips(state: ListingState): readonly FilterChip[] {
  const chips: FilterChip[] = [];

  for (const key of LIST_FILTER_KEYS) {
    for (const value of state[key]) {
      chips.push(chip(key, value, `${FILTER_GROUP_LABEL[key]}: ${optionLabel(key, value)}`));
    }
  }

  const price = priceLabel(state);
  if (price) {
    chips.push(chip('price', '', price));
  }

  if (state.minRating !== null) {
    chips.push(chip('rating', '', `${formatRating(state.minRating)} stars & up`));
  }

  if (state.inStock) {
    chips.push(chip('inStock', '', 'In stock'));
  }

  if (hasDiscountFilter(state)) {
    chips.push(chip('discount', '', `${state.minDiscountPercent}% off or more`));
  } else if (state.onSale) {
    chips.push(chip('discount', '', 'On sale'));
  }

  if (state.newArrivals) {
    chips.push(chip('newArrivals', '', 'New arrivals'));
  }

  return chips;
}

/** The state with that one chip's filter taken off, back at the first page. */
export function removeChip(state: ListingState, target: FilterChip): ListingState {
  switch (target.kind) {
    case 'price':
      return withFilter(state, { minPrice: null, maxPrice: null });
    case 'rating':
      return withFilter(state, { minRating: null });
    case 'inStock':
      return withFilter(state, { inStock: false });
    case 'discount':
      return withFilter(state, { onSale: false, minDiscountPercent: null });
    case 'newArrivals':
      return withFilter(state, { newArrivals: false });
    default:
      return toggleListValue(state, target.kind, target.value);
  }
}

/** "Clear filters" (PLP-03): every chip at once. */
export function clearAllChips(state: ListingState): ListingState {
  return clearFilters(state);
}

function chip(kind: ChipKind, value: string, label: string): FilterChip {
  return {
    id: value ? `${kind}:${value}` : kind,
    kind,
    value,
    label,
    removeLabel: `Remove filter: ${label}`,
  };
}

/** A size label is its own name; everything else is looked up so the chip reads like a word. */
function optionLabel(key: ListFilterKey, value: string): string {
  return LIST_OPTIONS[key].find((option) => option.value === value)?.label ?? value;
}

/**
 * A price chip reads as its band when the bounds match one, and as the bounds themselves when a
 * link set something else. `null` when no price filter is on.
 */
function priceLabel(state: ListingState): string | null {
  if (state.minPrice === null && state.maxPrice === null) {
    return null;
  }

  const band = PRICE_BANDS.find(
    (candidate) => candidate.min === state.minPrice && candidate.max === state.maxPrice,
  );
  if (band) {
    return band.label;
  }

  if (state.minPrice === null) {
    return `Under ${formatLkr(state.maxPrice ?? 0)}`;
  }
  if (state.maxPrice === null) {
    return `Over ${formatLkr(state.minPrice)}`;
  }

  return `${formatLkr(state.minPrice)} – ${formatLkr(state.maxPrice)}`;
}

/** 4 rather than 4.0, but 4.5 when a link asked for half a star. */
function formatRating(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}
