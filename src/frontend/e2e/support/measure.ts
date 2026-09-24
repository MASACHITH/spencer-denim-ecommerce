import { Locator, Page } from '@playwright/test';

/**
 * Measurements the browser suite makes, shared by the pages that make them.
 *
 * They were written for the product page (W2-05) and then wanted verbatim by the homepage
 * (W2-01). Copying them would have been the more dangerous option by far: `edgesWithoutRing` in
 * particular exists because an earlier assertion *could not fail*, and a second copy is a second
 * chance for one of them to drift back into reading a declaration instead of the screen.
 */

/** `scrollWidth` against `clientWidth`: `innerWidth` includes the scrollbar and hides overflow. */
export async function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => {
    const root = document.documentElement;
    return root.scrollWidth - root.clientWidth;
  });
}

/** One control that failed the 44 px touch minimum, with the numbers that failed it. */
export interface UndersizedBox {
  readonly label: string;
  readonly width: number;
  readonly height: number;
}

/** Every visible element matching `selector` whose box is under the 44 px touch minimum. */
export async function undersized(page: Page, selector: string): Promise<UndersizedBox[]> {
  return page.evaluate(
    (target) =>
      [...document.querySelectorAll<HTMLElement>(target)]
        .filter((element) => element.getClientRects().length > 0)
        .map((element) => ({
          label: `${element.tagName}.${element.className}`.slice(0, 60),
          width: Math.round(element.getBoundingClientRect().width),
          height: Math.round(element.getBoundingClientRect().height),
        }))
        .filter((box) => box.width < 44 || box.height < 44),
    selector,
  );
}

/**
 * How far outside a control's border box the focus ring is drawn: `outline-offset: 2px` plus
 * `--focus-width: 2.5px`, rounded up.
 */
export const RING_BAND = 6;

/**
 * The edges of a focused control where the focus ring does not actually reach the screen.
 *
 * **Why this takes pictures.** The obvious assertion — `getComputedStyle(element).outlineWidth` —
 * reads the outline the element *would* paint, and is structurally blind to an ancestor clipping
 * it away. It passed happily on a thumbnail rail that was cropping three of the four edges off
 * the ring: lesson L4 again, behind a test that could not fail. This screenshots the strip just
 * outside each edge with the control focused and again with it blurred, and reports the edges
 * whose pixels come back byte for byte identical. If the ring painted there, they cannot be.
 *
 * The control must be focused on the way in; it is left blurred on the way out.
 */
export async function edgesWithoutRing(page: Page, control: Locator): Promise<string[]> {
  // Centred, not merely "scrolled into view": a control resting against the bottom of the
  // viewport has no room below it for the strip, and the guard below would rightly refuse to
  // measure rather than quietly report the ring intact.
  await control.evaluate((element: HTMLElement) =>
    element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' }),
  );

  const box = await control.boundingBox();
  const viewport = page.viewportSize();
  if (!box || !viewport) {
    throw new Error('the control has no box to look around');
  }

  const strips: Record<string, Clip> = {
    top: {
      x: box.x - RING_BAND,
      y: box.y - RING_BAND,
      width: box.width + RING_BAND * 2,
      height: RING_BAND,
    },
    bottom: {
      x: box.x - RING_BAND,
      y: box.y + box.height,
      width: box.width + RING_BAND * 2,
      height: RING_BAND,
    },
    left: { x: box.x - RING_BAND, y: box.y, width: RING_BAND, height: box.height },
    right: { x: box.x + box.width, y: box.y, width: RING_BAND, height: box.height },
  };

  for (const [edge, clip] of Object.entries(strips)) {
    // A strip off the edge of the viewport would be unreadable, and a silent pass is the one
    // thing this helper exists to prevent.
    if (
      clip.x < 0 ||
      clip.y < 0 ||
      clip.x + clip.width > viewport.width ||
      clip.y + clip.height > viewport.height
    ) {
      throw new Error(`the ${edge} strip falls outside the viewport, so the ring cannot be seen`);
    }
  }

  const lit: Record<string, Buffer> = {};
  for (const [edge, clip] of Object.entries(strips)) {
    lit[edge] = await page.screenshot({ clip: round(clip) });
  }

  await control.evaluate((element: HTMLElement) => element.blur());

  const missing: string[] = [];
  for (const [edge, clip] of Object.entries(strips)) {
    if ((await page.screenshot({ clip: round(clip) })).equals(lit[edge])) {
      missing.push(edge);
    }
  }

  return missing;
}

export interface Clip {
  x: number;
  y: number;
  width: number;
  height: number;
}

function round(clip: Clip): Clip {
  return {
    x: Math.round(clip.x),
    y: Math.round(clip.y),
    width: Math.max(1, Math.round(clip.width)),
    height: Math.max(1, Math.round(clip.height)),
  };
}
