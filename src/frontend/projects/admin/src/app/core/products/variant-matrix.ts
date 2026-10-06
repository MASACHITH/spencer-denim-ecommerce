import {
  AdminProductColour,
  AdminProductVariant,
  ColourValues,
  ProductSizeOption,
  VariantValues,
} from './admin-product.model';

/**
 * The arithmetic behind the colour × size matrix (ADM-10, doc 11 P1 step 9), as pure functions.
 *
 * **Why the generation lives here and not in the component.** Doc 11 P1 step 9 is one sentence —
 * "**[System]** Generates one **variant per colour × size**" — and it is the step most likely to lose an
 * admin's work: it has to add the rows a newly ticked size needs, keep every row that already exists
 * (with its SKU, its price overrides and its id), and never silently drop a row for a variant that has
 * been ordered. Those are three rules about a list, which is exactly the shape that belongs in tested
 * functions rather than inside a template.
 */

/** One row of the matrix on screen. */
export interface MatrixRow extends VariantValues {
  /** For the label: "Indigo Rinse · M". */
  readonly sizeLabel: string;
  readonly sizeScaleName: string;
  /** An order line names it, so it can be switched off but never removed (doc 08 §3.2). */
  readonly hasOrders: boolean;
  /** Read-only: stock is ADM-12's. */
  readonly onHand: number;
}

/** Moves one item of a flat list by `delta`, or returns null when it cannot move that way. */
export function moveInArray<T>(items: readonly T[], index: number, delta: number): T[] | null {
  const target = index + delta;
  if (index < 0 || index >= items.length || target < 0 || target >= items.length) {
    return null;
  }

  const moved = [...items];
  [moved[index], moved[target]] = [moved[target], moved[index]];

  return moved;
}

/** Renumbers `sortOrder` to the list's own order, which is what the admin has just arranged. */
export function withSortOrder<T extends { sortOrder: number }>(items: readonly T[]): T[] {
  return items.map((item, index) => ({ ...item, sortOrder: index }));
}

/**
 * The matrix for the colours and sizes currently chosen, keeping every row that already exists.
 *
 * - A pair that is already in `existing` keeps its row untouched — its id, SKU, barcode, price overrides
 *   and on/off state. Rebuilding it would throw away what the admin typed and, worse, lose the id of a
 *   variant an order line points at.
 * - A pair that is new gets a row with a **suggested** SKU, which the admin can overwrite. A suggestion
 *   rather than a blank, because a 6 × 8 matrix is 48 SKUs to type and the convention
 *   (code-colour-size) is the one the demo catalogue uses.
 * - A row whose pair is no longer chosen is dropped — **unless it has been ordered**, in which case it is
 *   kept and switched off, because doc 08 §3.2 keeps an ordered variant for ever and the API would refuse
 *   the delete anyway. Keeping it visible is what lets the admin see why.
 */
export function buildMatrix(
  productCode: string,
  colours: readonly ColourValues[],
  sizeIds: readonly number[],
  sizesById: ReadonlyMap<number, ProductSizeOption>,
  existing: readonly MatrixRow[],
): MatrixRow[] {
  const byPair = new Map(existing.map((row) => [pairKey(row.colourName, row.sizeId), row]));
  const chosen = new Set<string>();
  const usedSkus = new Set(existing.map((row) => row.sku.toUpperCase()));

  const rows: MatrixRow[] = [];

  for (const colour of colours) {
    for (const sizeId of sizeIds) {
      const size = sizesById.get(sizeId);
      if (!size || !colour.name.trim()) {
        continue;
      }

      const key = pairKey(colour.name, sizeId);
      chosen.add(key);

      const kept = byPair.get(key);
      if (kept) {
        rows.push({ ...kept, sizeLabel: size.label, sizeScaleName: size.scaleName });
        continue;
      }

      const sku = uniqueSku(suggestSku(productCode, colour.name, size.label), usedSkus);
      usedSkus.add(sku.toUpperCase());

      rows.push({
        id: null,
        colourName: colour.name,
        sizeId,
        sku,
        barcode: null,
        priceOverride: null,
        compareAtPriceOverride: null,
        isActive: true,
        sortOrder: rows.length,
        sizeLabel: size.label,
        sizeScaleName: size.scaleName,
        hasOrders: false,
        onHand: 0,
      });
    }
  }

  // An ordered variant whose pair is no longer chosen: kept, switched off, and shown. Dropping it would
  // make the save a 409 the admin could not act on, because the row would not be on screen to explain it.
  for (const row of existing) {
    if (!chosen.has(pairKey(row.colourName, row.sizeId)) && row.hasOrders) {
      rows.push({ ...row, isActive: false });
    }
  }

  return withSortOrder(rows);
}

/**
 * A SKU suggestion: the product code, the colour and the size, upper-cased, with anything that is not a
 * letter or a digit collapsed to a single hyphen.
 *
 * `varchar(64)` is the column, so the result is clipped — at a hyphen where there is one, so a clipped SKU
 * does not end mid-word.
 */
export function suggestSku(productCode: string, colourName: string, sizeLabel: string): string {
  const parts = [productCode, colourName, sizeLabel]
    .map((part) =>
      part
        .toUpperCase()
        .replace(/[^A-Z0-9]+/g, '-')
        .replace(/^-+|-+$/g, ''),
    )
    .filter((part) => part.length > 0);

  const joined = parts.join('-') || 'SKU';

  if (joined.length <= 64) {
    return joined;
  }

  const clipped = joined.slice(0, 64);
  const lastHyphen = clipped.lastIndexOf('-');

  return lastHyphen > 40 ? clipped.slice(0, lastHyphen) : clipped;
}

/** Appends `-2`, `-3`… until the SKU is not one of `used`, keeping inside `varchar(64)`. */
function uniqueSku(candidate: string, used: ReadonlySet<string>): string {
  if (!used.has(candidate.toUpperCase())) {
    return candidate;
  }

  for (let suffix = 2; suffix < 1000; suffix++) {
    const tail = `-${suffix}`;
    const trimmed = candidate.slice(0, Math.min(candidate.length, 64 - tail.length)) + tail;

    if (!used.has(trimmed.toUpperCase())) {
      return trimmed;
    }
  }

  return candidate;
}

/**
 * A slug suggestion for a product name: lower case, letters and digits, single hyphens between them.
 *
 * It is the same grammar `CatalogSlug` enforces in the domain and `CK_Products_Slug` enforces in the
 * database, so a suggestion is never a value the API will refuse. Non-ASCII letters are stripped rather
 * than transliterated: the column is `varchar`, and guessing at a romanisation would produce a URL nobody
 * chose.
 */
export function suggestSlug(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 220)
    .replace(/-+$/g, '');
}

/** The rows a product's saved variants start as. */
export function toMatrixRows(
  variants: readonly AdminProductVariant[],
  sizesById: ReadonlyMap<number, ProductSizeOption>,
): MatrixRow[] {
  return variants.map((variant, index) => ({
    id: variant.id,
    colourName: variant.colourName,
    sizeId: variant.sizeId,
    sku: variant.sku,
    barcode: variant.barcode,
    priceOverride: variant.priceOverride,
    compareAtPriceOverride: variant.compareAtPriceOverride,
    isActive: variant.isActive,
    sortOrder: index,
    sizeLabel: variant.sizeLabel ?? sizesById.get(variant.sizeId)?.label ?? String(variant.sizeId),
    sizeScaleName: variant.sizeScaleName ?? sizesById.get(variant.sizeId)?.scaleName ?? '',
    hasOrders: variant.hasOrders,
    onHand: variant.onHand,
  }));
}

/** The form values a product's saved colourways start as. */
export function toColourValues(colours: readonly AdminProductColour[]): ColourValues[] {
  return colours.map((colour, index) => ({
    id: colour.id,
    name: colour.name,
    colourFamilyId: colour.colourFamilyId,
    swatchHex: colour.swatchHex,
    swatchAssetPublicId: colour.swatchAssetPublicId,
    sortOrder: index,
  }));
}

/**
 * The sizes a product already uses, so re-opening the form shows the scale and the ticks that built the
 * matrix rather than an empty picker beside a full matrix.
 */
export function sizeIdsIn(rows: readonly MatrixRow[]): number[] {
  const seen = new Set<number>();

  for (const row of rows) {
    seen.add(row.sizeId);
  }

  return [...seen];
}

/** The size scale a product is on, or null when it has no variants yet. */
export function scaleIn(rows: readonly MatrixRow[]): string | null {
  return rows.find((row) => row.sizeScaleName)?.sizeScaleName ?? null;
}

function pairKey(colourName: string, sizeId: number): string {
  return `${colourName.trim().toLowerCase()}\u0000${sizeId}`;
}
