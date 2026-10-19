/**
 * The chart palette, read from the admin theme's tokens (`styles.css`, "Charts and dashboard cards").
 *
 * **Why it reads computed styles instead of writing `var(--chart-1)`:** a chart is drawn on a canvas, and a
 * canvas `fillStyle` cannot resolve a custom property. So the colours stay in the stylesheet, where the rest
 * of the theme is, and this turns them into strings at draw time. Nothing here names a colour.
 */

/** The categorical tokens, in order: a chart with more slices than this cycles round. */
export const CATEGORICAL_TOKENS = ['--chart-1', '--chart-2', '--chart-3', '--chart-4', '--chart-5', '--chart-6', '--chart-7', '--chart-8'] as const;

/**
 * Each order status's token. Fixed per status, so a status keeps its colour from one chart to the next and
 * matches its badge in Recent orders. The API's eight statuses (`OrderStatus` in `SalesEnums.cs`); a status
 * this table does not know falls back to the categorical order.
 */
export const STATUS_TOKENS: Readonly<Record<string, string>> = {
  PendingPayment: '--status-awaiting',
  Paid: '--status-paid',
  Processing: '--status-processing',
  Shipped: '--status-dispatched',
  Delivered: '--status-delivered',
  Cancelled: '--status-cancelled',
  Expired: '--status-expired',
  Refunded: '--status-refunded',
};

export interface ChartPalette {
  /** Resolves one token, `--chart-3`, to the colour the theme gives it. */
  readonly token: (name: string) => string;
  /** The categorical colour for position `index`, cycling. */
  readonly series: (index: number) => string;
  readonly accent: string;
  readonly grid: string;
  readonly ink: string;
  readonly surface: string;
}

/** Reads the palette as `element` sees it, so a themed subtree gets its own values. */
export function readPalette(element: Element): ChartPalette {
  const view = element.ownerDocument.defaultView;
  const styles = view ? view.getComputedStyle(element) : null;
  const token = (name: string) => styles?.getPropertyValue(name).trim() ?? '';

  return {
    token,
    series: (index) => token(CATEGORICAL_TOKENS[((index % CATEGORICAL_TOKENS.length) + CATEGORICAL_TOKENS.length) % CATEGORICAL_TOKENS.length]),
    accent: token('--chart-accent'),
    grid: token('--chart-grid'),
    ink: token('--chart-ink'),
    surface: token('--color-surface'),
  };
}

/**
 * `#rrggbb` (or `#rgb`) at `alpha`, as `rgb(r g b / a)` — the demo's "same colour at 55%" for the bars after
 * the first, and the area fill under the revenue line. Anything that is not a hex colour is returned as it
 * came: a theme that writes its tokens another way loses the transparency, not the colour.
 */
export function withAlpha(colour: string, alpha: number): string {
  const hex = colour.trim().replace(/^#/, '');
  const full = hex.length === 3 ? [...hex].map((digit) => digit + digit).join('') : hex;

  if (!/^[0-9a-f]{6}$/i.test(full)) {
    return colour;
  }

  const [r, g, b] = [0, 2, 4].map((offset) => parseInt(full.slice(offset, offset + 2), 16));

  return `rgb(${r} ${g} ${b} / ${alpha})`;
}
