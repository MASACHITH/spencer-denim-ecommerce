/**
 * The values each PLP-02 filter offers.
 *
 * The catalogue API serves the *filters* (`GET /api/catalog/products` accepts every parameter
 * below) but it has no facet endpoint: nothing returns "the sizes, colours, materials, fits and
 * occasions this catalogue actually uses, with their counts". Until one exists, the option lists
 * are the seeded reference data of doc 08 §9
 * (`SpencerDenim.Infrastructure/Persistence/Seeding/CatalogReferenceData.cs`), copied slug for
 * slug so a chosen filter is always a value the API can resolve. An option that matches nothing
 * simply returns an empty page rather than an error, so a catalogue that drops a colour degrades
 * to "no products found" and not to a 400.
 *
 * Department and category are deliberately absent here: they are route segments, not query
 * parameters, and the filter panel builds them from the live navigation tree.
 */

/** One selectable value inside a filter group. */
export interface FilterOption {
  /** What goes in the URL and in the API request. */
  readonly value: string;
  readonly label: string;
  /** Colour families carry their swatch colour. */
  readonly hex?: string;
}

/**
 * One size scale — `catalog.Sizes.ScaleName` (doc 08 §3.2). Sizes are offered scale by scale
 * because a bare list of labels is unreadable and, worse, ambiguous: the same "36" is a women's
 * waist and a shoe. Only the scales a department can produce are offered, so a Baby size never
 * appears on a Women listing where it could only ever return nothing.
 */
export interface SizeScale {
  readonly name: string;
  /** Department slugs this scale belongs to; empty means every department. */
  readonly departments: readonly string[];
  readonly options: readonly FilterOption[];
}

function labels(values: readonly string[]): readonly FilterOption[] {
  return values.map((label) => ({ value: label, label }));
}

/** Age groups — `catalog.AgeGroups.Slug` (DEPT-01: Kids covers Baby, Toddler, Kids, Juniors). */
export const AGE_GROUP_OPTIONS: readonly FilterOption[] = [
  { value: 'baby', label: 'Baby (0–2)' },
  { value: 'toddler', label: 'Toddler (2–5)' },
  { value: 'kids', label: 'Kids (5–10)' },
  { value: 'juniors', label: 'Juniors (10–16)' },
];

/**
 * The seeded size scales of doc 08 §9. The API matches on the label, not on a slug or a scale, so
 * the scale is a label for the shopper rather than something it can filter by: ticking a "36"
 * still means every 36 in the catalogue. Narrowing a scale to its departments is as far as the
 * current contract goes — a `size` parameter that took a scale would be the real fix, and belongs
 * with the facets work.
 */
const CLOTHING_SIZES: SizeScale = {
  name: 'Clothing',
  departments: ['women', 'men'],
  options: labels(['XS', 'S', 'M', 'L', 'XL', 'XXL', 'XXXL']),
};

const WOMEN_WAIST: SizeScale = {
  name: 'Women waist',
  departments: ['women'],
  options: labels(['24', '26', '28', '30', '32', '34', '36']),
};

const MEN_WAIST: SizeScale = {
  name: 'Men waist',
  departments: ['men'],
  options: labels(['28', '30', '32', '34', '36', '38', '40']),
};

/**
 * One waist group for a listing with no department to tell the two apart.
 *
 * Women's and men's waists are the same numbers, so offering both scales at once put 28 to 36 on
 * screen twice, bound to the same value: two checkboxes with the same accessible name that ticked
 * each other. The API matches a bare label anyway, so one group is also the truth about what the
 * filter does.
 */
const MIXED_WAIST: SizeScale = {
  name: 'Waist',
  departments: [],
  options: labels(['24', '26', '28', '30', '32', '34', '36', '38', '40']),
};

const KIDS_AGE: SizeScale = {
  name: 'Kids age',
  departments: ['kids'],
  options: labels(['2-3Y', '4-5Y', '6-7Y', '8-9Y', '10-11Y', '12-13Y']),
};

const BABY_MONTHS: SizeScale = {
  name: 'Baby months',
  departments: ['kids'],
  options: labels(['0-3M', '3-6M', '6-9M', '9-12M', '12-18M', '18-24M']),
};

const FOOTWEAR: SizeScale = {
  name: 'Footwear (EU)',
  departments: [],
  options: labels(['35', '37', '39', '41', '43', '45']),
};

/**
 * The seeded size scales of doc 08 §9. The API matches on the label, not on a slug or a scale, so
 * the scale is a label for the shopper rather than something it can filter by: ticking a "36"
 * still means every 36 in the catalogue. Narrowing a scale to its departments is as far as the
 * current contract goes — a `size` parameter that took a scale would be the real fix, and belongs
 * with the facets work.
 */
export const SIZE_SCALES: readonly SizeScale[] = [
  CLOTHING_SIZES,
  WOMEN_WAIST,
  MEN_WAIST,
  KIDS_AGE,
  BABY_MONTHS,
  FOOTWEAR,
];

/** What New Arrivals and Sale offer: every scale, with the two waists folded into one. */
const MIXED_SCALES: readonly SizeScale[] = [
  CLOTHING_SIZES,
  MIXED_WAIST,
  KIDS_AGE,
  BABY_MONTHS,
  FOOTWEAR,
];

/** The scales a department offers; the mixed set when no department narrows it. */
export function sizeScalesFor(department: string | null): readonly SizeScale[] {
  if (department === null) {
    return MIXED_SCALES;
  }

  return SIZE_SCALES.filter(
    (scale) => scale.departments.length === 0 || scale.departments.includes(department),
  );
}

/** Colour families — `catalog.ColourFamilies.Slug`, with the seeded swatch colour. */
export const COLOUR_OPTIONS: readonly FilterOption[] = [
  { value: 'white', label: 'White', hex: '#FFFFFF' },
  { value: 'cream', label: 'Cream', hex: '#F3EADA' },
  { value: 'beige', label: 'Beige', hex: '#D8CBB6' },
  { value: 'brown', label: 'Brown', hex: '#6B4F3A' },
  { value: 'black', label: 'Black', hex: '#101010' },
  { value: 'grey', label: 'Grey', hex: '#8A8D91' },
  { value: 'navy', label: 'Navy', hex: '#1F2A44' },
  { value: 'blue', label: 'Blue', hex: '#2E6DB4' },
  { value: 'green', label: 'Green', hex: '#2E7D5B' },
  { value: 'red', label: 'Red', hex: '#C0392B' },
  { value: 'pink', label: 'Pink', hex: '#E8A0BF' },
  { value: 'purple', label: 'Purple', hex: '#6C4A8F' },
  { value: 'yellow', label: 'Yellow', hex: '#E8C55F' },
  { value: 'orange', label: 'Orange', hex: '#D98032' },
];

/** Materials, fits and occasions — `catalog.AttributeOptions.Slug`, by type. */
export const MATERIAL_OPTIONS: readonly FilterOption[] = [
  { value: 'cotton', label: 'Cotton' },
  { value: '100-cotton', label: '100% Cotton' },
  { value: 'cotton-blend', label: 'Cotton blend' },
  { value: 'linen', label: 'Linen' },
  { value: 'linen-blend', label: 'Linen blend' },
  { value: 'denim', label: 'Denim' },
  { value: 'viscose', label: 'Viscose' },
  { value: 'polyester-blend', label: 'Polyester blend' },
];

export const FIT_OPTIONS: readonly FilterOption[] = [
  { value: 'regular', label: 'Regular' },
  { value: 'slim', label: 'Slim' },
  { value: 'relaxed', label: 'Relaxed' },
  { value: 'wide-leg', label: 'Wide Leg' },
  { value: 'oversized', label: 'Oversized' },
];

export const OCCASION_OPTIONS: readonly FilterOption[] = [
  { value: 'casual', label: 'Casual' },
  { value: 'smart-casual', label: 'Smart Casual' },
  { value: 'formal', label: 'Formal' },
  { value: 'party', label: 'Party' },
  { value: 'traditional', label: 'Traditional' },
  { value: 'active', label: 'Active' },
];

/**
 * A price band, in LKR. The state keeps `minPrice`/`maxPrice` as numbers rather than a band id,
 * so a hand-written or campaign-built URL with any bounds still works and still shows a chip.
 */
export interface PriceBand {
  readonly id: string;
  readonly label: string;
  readonly min: number | null;
  readonly max: number | null;
}

export const PRICE_BANDS: readonly PriceBand[] = [
  { id: 'under-2500', label: 'Under Rs. 2,500', min: null, max: 2500 },
  { id: '2500-5000', label: 'Rs. 2,500 – Rs. 5,000', min: 2500, max: 5000 },
  { id: '5000-10000', label: 'Rs. 5,000 – Rs. 10,000', min: 5000, max: 10000 },
  { id: '10000-20000', label: 'Rs. 10,000 – Rs. 20,000', min: 10000, max: 20000 },
  { id: 'over-20000', label: 'Over Rs. 20,000', min: 20000, max: null },
];

/** Minimum star rating. The API takes any 0–5 value; these are the ones worth a control. */
export const RATING_OPTIONS: readonly FilterOption[] = [
  { value: '4', label: '4 stars & up' },
  { value: '3', label: '3 stars & up' },
  { value: '2', label: '2 stars & up' },
];

/**
 * Minimum discount. `minDiscountPercent` implies `onSale` on the API side, so a discount choice
 * and the "On sale" flag are offered in one group and never contradict each other.
 */
export const DISCOUNT_OPTIONS: readonly FilterOption[] = [
  { value: '0', label: 'On sale' },
  { value: '10', label: '10% or more' },
  { value: '25', label: '25% or more' },
  { value: '50', label: '50% or more' },
];

/**
 * The heading each repeatable filter group carries, in the filter panel and on its chips. Keyed
 * by the {@link ListingState} field rather than the query-string name, so a chip and the control
 * that set it can never drift apart.
 */
export const FILTER_GROUP_LABEL = {
  ageGroups: 'Age group',
  sizes: 'Size',
  colours: 'Colour',
  materials: 'Material',
  fits: 'Fit',
  occasions: 'Occasion',
} as const satisfies Readonly<Record<string, string>>;
