/**
 * Colour contrast, by the WCAG 2.1 definition (relative luminance, sRGB gamma).
 *
 * This exists because a mark drawn in one fixed colour over admin-chosen colours is a mark that
 * is mostly not there. The sold-out slash was painted in `--color-text-muted` whatever the swatch
 * under it was, and measured **1.08:1 on Olive Green** against WCAG 2.2 AA's 3:1 minimum for
 * non-text contrast (1.4.11) — twelve of the sixteen seeded colourways failed.
 *
 * Note the difference from `isLight()` on the product page and the product card. That one answers
 * "does this swatch need a stronger border to be seen against white?" with a *naive* luminance
 * and a 0.8 threshold, which is fine for its question. This one answers "what can be drawn *on
 * top of* this colour and still be read?", which needs the real curve: under the naive 0.8 rule
 * Mustard and Blush Pink look dark and would take a white mark, and both fail at 2.17:1 and
 * 2.06:1. The two are deliberately not merged.
 */

/** Which of the two inks to draw on a colour. */
export type Ink = 'dark' | 'light';

/**
 * The relative luminance of an `#rrggbb` colour, 0 (black) to 1 (white).
 *
 * An unparseable or missing colour is treated as white, which is the surface a swatch with no hex
 * shows through to.
 */
export function relativeLuminance(hex: string | null | undefined): number {
  const rgb = parseHex(hex);
  return 0.2126 * channel(rgb[0]) + 0.7152 * channel(rgb[1]) + 0.0722 * channel(rgb[2]);
}

/** The WCAG contrast ratio between two colours, from 1 (identical) to 21 (black on white). */
export function contrastRatio(a: string | null | undefined, b: string | null | undefined): number {
  const first = relativeLuminance(a);
  const second = relativeLuminance(b);
  const lighter = Math.max(first, second);
  const darker = Math.min(first, second);

  return (lighter + 0.05) / (darker + 0.05);
}

/**
 * Which ink reads better on `background`: the dark one or the light one.
 *
 * Chosen by measuring both rather than by a lightness threshold, so there is no cut-off to tune
 * and no colour that falls the wrong side of one. Picking the better of two inks at opposite ends
 * of the scale has a floor: the worst any colour can do is the crossover where both inks score
 * alike, which for these two is **4.17:1**, at a relative luminance of 0.2017. (Pure black and
 * white would give 4.58; the dark ink is `#1a1a1a`, not black, which costs the difference.) The
 * worst across the sixteen seeded colourways is 4.75:1, on Terracotta. All well clear of 3:1,
 * and `contrast.spec.ts` walks the whole colour cube rather than taking my word for it.
 */
export function readableInk(
  background: string | null | undefined,
  dark: string = DARK_INK,
  light: string = LIGHT_INK,
): Ink {
  return contrastRatio(background, dark) >= contrastRatio(background, light) ? 'dark' : 'light';
}

/**
 * The two inks, as `--color-text` and `--color-on-primary` are set in `tokens.css`.
 *
 * They are written twice — here to choose with, and as the tokens themselves to paint with — so
 * that a theme change moves the paint. If one of them moved far enough for the choice to be
 * wrong, the contrast is measured on the rendered page in `e2e/product-page.e2e.ts` over all
 * sixteen colourways, and that test would fail rather than the fault shipping.
 */
const DARK_INK = '#1a1a1a';
const LIGHT_INK = '#ffffff';

function channel(value: number): number {
  const part = value / 255;
  return part <= 0.04045 ? part / 12.92 : Math.pow((part + 0.055) / 1.055, 2.4);
}

/** `#rgb` and `#rrggbb`, with anything else read as white. */
function parseHex(hex: string | null | undefined): [number, number, number] {
  const value = hex?.trim() ?? '';

  if (/^#[0-9a-f]{3}$/i.test(value)) {
    const [, r, g, b] = value;
    return [byte(r + r), byte(g + g), byte(b + b)];
  }

  if (/^#[0-9a-f]{6}$/i.test(value)) {
    return [byte(value.slice(1, 3)), byte(value.slice(3, 5)), byte(value.slice(5, 7))];
  }

  return [255, 255, 255];
}

function byte(pair: string): number {
  return parseInt(pair, 16);
}
