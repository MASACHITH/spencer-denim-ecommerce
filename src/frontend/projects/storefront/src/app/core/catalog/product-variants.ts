import { Params } from '@angular/router';

import {
  ColourSwatchDto,
  PriceDto,
  ProductDetailDto,
  ProductImageDto,
  ProductSizeDto,
  ProductVariantDto,
  StockStatus,
} from './catalog.model';
import { RawParams } from './listing-state';

/**
 * Which colourway and size the product page is showing, and the mapping between that and the URL
 * (PDP-01, PDP-02, PDP-03).
 *
 * Nothing here touches the DOM or the network. It is pure for the same reason `listing-state.ts`
 * is: the same mapping has to run while the server renders and be testable without a browser.
 *
 * **Why the selection is in the query string.** The listing's rule — the URL is the state —
 * applies here for a reason stronger than consistency: both halves of the selection change what
 * the page *shows*, not merely what it would put in a bag.
 *
 * - A colour changes the photographs. `catalog.ProductMedia.ProductColourId` ties each gallery
 *   image to one colourway and the demo seeder gives every colour its own shot, so
 *   `?colour=7` and `?colour=8` are different pictures of the same product. A shopper who sends
 *   a friend "this one, in the black" has to be able to.
 * - A size changes the money. `catalog.ProductVariants.PriceOverride` is a real, seeded column —
 *   a 32 and a 38 waist of the same jean are priced apart — and the stock line changes with it.
 *
 * Both are therefore read from the URL before the first render, so the server renders the
 * selection the link asked for, and written back into it on every change, so whatever is on
 * screen is always what the address bar says. The write replaces the history entry rather than
 * adding one: trying four sizes should not cost four presses of Back to leave the page.
 *
 * The **canonical** address carries neither (see the product page): the colourways are one
 * product, not several pages, and a crawler must not be offered one URL per colour.
 */

/** The query-string names. Singular, matching the listing's filter parameters. */
export const COLOUR_PARAM = 'colour';
export const SIZE_PARAM = 'size';

/**
 * A selection as the URL asks for it: ids that may name nothing. {@link resolveSelection} is what
 * turns this into a selection the product really has.
 */
export interface RequestedSelection {
  readonly colourId: number | null;
  readonly sizeId: number | null;
}

/** A selection this product really offers. `sizeId` is null until the shopper picks one. */
export interface VariantSelection {
  readonly colourId: number | null;
  readonly sizeId: number | null;
}

/** One colour swatch, with whether anything in it can be bought. */
export interface ColourOption {
  readonly colour: ColourSwatchDto;
  readonly isInStock: boolean;
}

/**
 * One size button for the chosen colour. `offered` is false when this colourway is not made in
 * that size at all, which is a different thing from being made and sold out.
 */
export interface SizeOption {
  readonly size: ProductSizeDto;
  readonly offered: boolean;
  readonly isInStock: boolean;
  readonly availableQuantity: number;
}

/** What the page prints as stock, for whatever is selected. */
export interface StockView {
  readonly status: StockStatus;
  readonly available: number;
}

/**
 * Reads the two ids out of the query string. Anything that is not a positive whole number is
 * dropped: a URL arrives from outside, and a stale or hand-edited one must still render a product
 * page rather than an error.
 */
export function readRequestedSelection(query: RawParams): RequestedSelection {
  return {
    colourId: readId(query[COLOUR_PARAM]),
    sizeId: readId(query[SIZE_PARAM]),
  };
}

/**
 * Turns what the URL asked for into what the product has.
 *
 * - A colour the product does not have falls back to the default: the first colourway with
 *   something in stock, so the page does not open on a sold-out colour while a live one exists.
 * - A size is kept only when the chosen colour is actually made in it. Switching to a colour that
 *   does not come in the selected size clears the size rather than leaving a button lit that
 *   names no variant. A size that is made but sold out stays selected — a shopper is allowed to
 *   look at it, and the page says it is out of stock.
 * - Size has no default. PDP-03 wants a deliberate choice, and guessing one would misprice a
 *   product whose variants carry price overrides.
 */
export function resolveSelection(
  product: ProductDetailDto,
  requested: RequestedSelection,
): VariantSelection {
  const colourId = resolveColour(product, requested.colourId);
  const sizeId =
    requested.sizeId !== null && isOffered(product, colourId, requested.sizeId)
      ? requested.sizeId
      : null;

  return { colourId, sizeId };
}

/** The variant a complete selection names, or null while the size is still open. */
export function variantFor(
  product: ProductDetailDto,
  selection: VariantSelection,
): ProductVariantDto | null {
  if (selection.colourId === null || selection.sizeId === null) {
    return null;
  }

  return (
    product.variants.find(
      (variant) => variant.colourId === selection.colourId && variant.sizeId === selection.sizeId,
    ) ?? null
  );
}

/** Every colourway, with whether any size of it is in stock (PDP-03). */
export function colourOptions(product: ProductDetailDto): readonly ColourOption[] {
  return product.colours.map((colour) => ({
    colour,
    isInStock: product.variants.some(
      (variant) => variant.colourId === colour.id && variant.isInStock,
    ),
  }));
}

/**
 * Every size the product is made in, answered for one colourway (PDP-03).
 *
 * The full size list is always shown — a shopper needs to see that the product runs XS to XXL
 * even when this colour stops at L — with the sizes this colour does not come in marked as such.
 * With no colour chosen, the aggregate the API already computed is used.
 */
export function sizeOptions(
  product: ProductDetailDto,
  colourId: number | null,
): readonly SizeOption[] {
  return product.sizes.map((size) => {
    if (colourId === null) {
      return {
        size,
        offered: true,
        isInStock: size.isInStock,
        availableQuantity: sum(
          product.variants.filter((variant) => variant.sizeId === size.id),
        ),
      };
    }

    const variant = product.variants.find(
      (candidate) => candidate.colourId === colourId && candidate.sizeId === size.id,
    );

    return {
      size,
      offered: variant !== undefined,
      isInStock: variant?.isInStock ?? false,
      availableQuantity: variant?.availableQuantity ?? 0,
    };
  });
}

/**
 * The price to show (PDP-02).
 *
 * A chosen variant prices itself; otherwise the product's own band is used, which the API builds
 * from the cheapest variant and the dearest compare-at price.
 */
export function priceFor(product: ProductDetailDto, selection: VariantSelection): PriceDto {
  return variantFor(product, selection)?.price ?? product.price;
}

/**
 * True when the product sells at more than one price, so the page can say "from Rs. X" rather
 * than quoting the cheapest size's price as if it were the product's.
 */
export function hasPriceRange(product: ProductDetailDto): boolean {
  return product.maxPrice > product.minPrice;
}

/** The stock line (PDP-02), narrowed as far as the selection allows. */
export function stockFor(product: ProductDetailDto, selection: VariantSelection): StockView {
  const variant = variantFor(product, selection);
  if (variant) {
    return { status: variant.stockStatus, available: variant.availableQuantity };
  }

  if (selection.colourId !== null) {
    const forColour = product.variants.filter(
      (candidate) => candidate.colourId === selection.colourId,
    );

    if (forColour.length > 0) {
      return { status: aggregate(forColour), available: sum(forColour) };
    }
  }

  // No colour, or a colour with no variants at all: the product's own status, which the API
  // computed from every variant's stock against every variant's threshold.
  return { status: product.stockStatus, available: sum(product.variants) };
}

/**
 * The stills the gallery shows for a colourway (PDP-01).
 *
 * A photo tied to a colour belongs to that colour; a photo tied to none belongs to every colour
 * (the seeded fabric-detail shot is one). Videos are kept apart, because they are a different
 * kind of slide and a product may have one with no stills at all.
 */
export function galleryFor(
  product: ProductDetailDto,
  colourId: number | null,
): readonly ProductImageDto[] {
  const stills = product.images.filter((image) => image.role !== 'Video');
  const forColour = stills.filter(
    (image) => image.colourId === null || colourId === null || image.colourId === colourId,
  );

  // A colourway with no photographs of its own still gets the product's photographs rather than
  // an empty stage: media is admin-managed and may simply not have been uploaded yet.
  return sortMedia(forColour.length > 0 ? forColour : stills);
}

/** The product video(s), which most products do not have (PDP-01). */
export function videosFor(
  product: ProductDetailDto,
  colourId: number | null,
): readonly ProductImageDto[] {
  const videos = product.images.filter((image) => image.role === 'Video');

  return sortMedia(
    videos.filter(
      (video) => video.colourId === null || colourId === null || video.colourId === colourId,
    ),
  );
}

/**
 * The query string for a selection. A null removes the parameter, which is how the router is told
 * to drop it, so an unselected size leaves no `?size=` behind.
 */
export function toVariantParams(selection: VariantSelection): Params {
  return {
    [COLOUR_PARAM]: selection.colourId ?? null,
    [SIZE_PARAM]: selection.sizeId ?? null,
  };
}

/** The default colourway: the first one with stock, else the first one, else none. */
function resolveColour(product: ProductDetailDto, requested: number | null): number | null {
  if (requested !== null && product.colours.some((colour) => colour.id === requested)) {
    return requested;
  }

  const inStock = product.colours.find((colour) =>
    product.variants.some((variant) => variant.colourId === colour.id && variant.isInStock),
  );

  return inStock?.id ?? product.colours[0]?.id ?? null;
}

/** Whether a colourway is made in a size at all, sold out or not. */
function isOffered(product: ProductDetailDto, colourId: number | null, sizeId: number): boolean {
  if (colourId === null) {
    return product.sizes.some((size) => size.id === sizeId);
  }

  return product.variants.some(
    (variant) => variant.colourId === colourId && variant.sizeId === sizeId,
  );
}

/** Rolls a set of variants up into one status, the way the API rolls a product up. */
function aggregate(variants: readonly ProductVariantDto[]): StockStatus {
  if (sum(variants) === 0) {
    return 'OutOfStock';
  }

  return variants.some((variant) => variant.stockStatus === 'InStock') ? 'InStock' : 'LowStock';
}

function sum(variants: readonly ProductVariantDto[]): number {
  return variants.reduce((total, variant) => total + Math.max(variant.availableQuantity, 0), 0);
}

/** `sortOrder` then `id`, which is the order the API already sends and the one admin controls. */
function sortMedia(images: readonly ProductImageDto[]): readonly ProductImageDto[] {
  return [...images].sort((left, right) => left.sortOrder - right.sortOrder || left.id - right.id);
}

/** A positive whole number, or null. Ids are the only stable handle the API gives us. */
function readId(raw: string | readonly string[] | undefined): number | null {
  const value = Array.isArray(raw) ? raw[0] : raw;
  if (typeof value !== 'string' || value.trim() === '') {
    return null;
  }

  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}
