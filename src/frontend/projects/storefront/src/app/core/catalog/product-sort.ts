/**
 * The seven sorts of PLP-04.
 *
 * The values are the ones `ProductSortOptions` accepts on the `sort` query parameter; the API
 * rejects anything else with a 400, so the list is copied from it rather than invented here.
 */
export const SORT_OPTIONS = [
  { value: 'recommended', label: 'Recommended' },
  { value: 'newest', label: 'Newest' },
  { value: 'best-selling', label: 'Best Selling' },
  { value: 'price-asc', label: 'Price: low to high' },
  { value: 'price-desc', label: 'Price: high to low' },
  { value: 'rating', label: 'Highest Rated' },
  { value: 'discount', label: 'Biggest Discount' },
] as const satisfies readonly { value: string; label: string }[];

export type SortValue = (typeof SORT_OPTIONS)[number]['value'];

/** What the API falls back to when `sort` is absent, so the URL never has to spell it out. */
export const DEFAULT_SORT: SortValue = 'recommended';

/** The sort named by the URL, or {@link DEFAULT_SORT} when it names nothing we recognise. */
export function readSort(raw: string | null | undefined): SortValue {
  const value = raw?.trim().toLowerCase();
  return SORT_OPTIONS.some((option) => option.value === value) ? (value as SortValue) : DEFAULT_SORT;
}

export function sortLabel(value: SortValue): string {
  return SORT_OPTIONS.find((option) => option.value === value)?.label ?? '';
}
