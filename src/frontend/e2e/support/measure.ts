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

/** Where a colour starts and stops down one column of an element, in device pixels. */
export interface VisibleBand {
  /** Height of the element as photographed, so a caller can say how close to an edge it got. */
  readonly height: number;
  /** First and last matching row, or -1 when the colour is not on screen at all. */
  readonly first: number;
  readonly last: number;
}

/**
 * Where the hero sample's magenta marker band appears down the middle of an element,
 * <b>read back from a picture of it</b>.
 *
 * **Why a picture.** The thing under test is which part of an over-sized video survives
 * `object-fit: cover` — and the obvious assertion, reading `object-position` off the computed
 * style, is exactly the kind of test MEMORY's lesson is about: it restates the stylesheet and is
 * blind to everything that actually decides the answer. The `aspect-ratio`, the `max-height` that
 * clamps the box past 1564 px and the intrinsic size of the file all move the visible band, and a
 * declaration of `center 30%` would read as correct with any of them changed underneath it.
 *
 * So this screenshots the element and looks at it. The screenshot is decoded in the page rather
 * than in Node, because nothing in this workspace decodes a PNG and a `data:` URL does not taint a
 * canvas.
 *
 * **The match is a relationship between channels, not a colour.** The marker is `#ff00ff` in the
 * file, the video is lossily encoded and the hero paints a dark gradient over the top of it, so
 * what comes back is anywhere between magenta and about a third of it. Red and blue together with
 * green far below them survives all of that; `#ff00ff` exactly survives none of it.
 *
 * **It refuses to measure an element that does not fit on the screen**, and the reason is worth
 * stating precisely because the obvious guard does not work. Part of an element hanging below the
 * viewport is not left out of the picture: the capture comes back the element's **full height**
 * with those rows **blank**, so comparing the capture's height against the box's can never fail.
 * Measured on the hero at 1920: viewport 940, box 1920x880 at y=118, capture 880 px tall, and the
 * marker's last row **821** instead of 879 — the 58 px that were off-screen read as "not the
 * marker", which is indistinguishable from "the band stops before the bottom edge". The first
 * version of the framing test ran at 940 and its lower assertion could not fail at the one width
 * that constrains the lower edge: `object-position: center 20%` cut the chin off at 1920 and all
 * five widths passed. So the check is the same one `edgesWithoutRing` makes — is the thing we are
 * about to photograph actually on the screen?
 *
 * **Its limit, stated rather than left to be discovered.** The check is purely geometric: it says
 * the element is on the screen, not that it is *unobscured*. Anything painted over the element —
 * a `position: fixed` overlay, a modal, a cookie bar — is photographed as element content and
 * measured as if it were. Forcing the storefront header to `fixed` moves the marker's first row to
 * 260 and would pass over a cut face. Not reachable today (the header is sticky, and the hero sits
 * at y=118 with no scroll at the framing test's 1100 px viewport), which is why it is written down
 * here instead of guarded against.
 */
export async function markerBand(page: Page, element: Locator): Promise<VisibleBand> {
  await element.scrollIntoViewIfNeeded();

  const box = await element.boundingBox();
  const viewport = page.viewportSize();
  if (!box || !viewport) {
    throw new Error('the element has no box to photograph');
  }

  if (box.y < 0 || box.y + box.height > viewport.height) {
    throw new Error(
      `the element is ${Math.round(box.height)} px tall at y=${Math.round(box.y)} in a ` +
        `${viewport.height} px viewport, so part of it cannot be photographed — the rows outside ` +
        'come back blank at the element\'s full height, which no size check can catch',
    );
  }

  const shot = (await element.screenshot()).toString('base64');

  const band = await page.evaluate(async (data) => {
    const bitmap = await createImageBitmap(
      await (await fetch(`data:image/png;base64,${data}`)).blob(),
    );
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const context = canvas.getContext('2d');
    if (!context) {
      throw new Error('no 2d context to read the screenshot back from');
    }

    context.drawImage(bitmap, 0, 0);
    const column = context.getImageData(Math.floor(bitmap.width / 2), 0, 1, bitmap.height).data;

    let first = -1;
    let last = -1;
    for (let y = 0; y < bitmap.height; y++) {
      const red = column[y * 4];
      const green = column[y * 4 + 1];
      const blue = column[y * 4 + 2];
      if (Math.abs(red - blue) < 60 && red - green > 30) {
        if (first < 0) {
          first = y;
        }
        last = y;
      }
    }

    return { height: bitmap.height, first, last };
  }, shot);

  return band;
}
