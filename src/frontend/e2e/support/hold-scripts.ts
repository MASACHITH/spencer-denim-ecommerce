import { Page } from '@playwright/test';

/**
 * Putting a browser in the position a shopper is in **before the page has hydrated** (#123).
 *
 * Every storefront page is server rendered, so the document, its styles and its forms are on the screen
 * while the bundle is still downloading — and on a slow connection that is exactly when a shopper types
 * and presses Enter. A test that waits for the page to be interactive can never see what happens then,
 * which is why a suite that was green shipped a checkout whose Enter key put the shopper's name, email
 * address, phone number and street address into the URL.
 *
 * The scripts are **held**, not blocked, so a test can release them afterwards and watch the page come
 * alive: a fix that only worked by breaking the form for good would otherwise pass.
 */

/**
 * Holds every script request until the returned function is called.
 *
 * `Page.route` rather than `javaScriptEnabled: false`, because the latter also removes the small inline
 * script Angular's event replay puts in the document, and that is part of the real situation. It also
 * cannot be released, so it could not show the page working afterwards.
 *
 * The route stays in force for later navigations of the same page. That is deliberate: a form that
 * navigates on its own produces a *new* document whose scripts are held too, and releasing them lets the
 * test see where the shopper ended up.
 */
export async function holdScripts(page: Page): Promise<() => void> {
  let release!: () => void;
  const released = new Promise<void>((resolve) => {
    release = resolve;
  });

  await page.route('**/*.js', async (route) => {
    await released;
    // The page can be closed, or can have moved on, by the time the scripts are let go.
    await route.continue().catch(() => undefined);
  });

  return release;
}

/**
 * Every request address the page makes, in order, so a leak is caught even if the page then moves on and
 * the address is no longer the current one.
 */
export function recordRequests(page: Page): string[] {
  const urls: string[] = [];
  page.on('request', (request) => urls.push(request.url()));

  return urls;
}

/**
 * Marks the current document, so a test can tell "nothing happened" from "the page was replaced by a
 * copy of itself". A native form submission to the page's own address looks identical on screen and in
 * `page.url()` when the form has no fields, and a marker on `window` does not survive it.
 */
export async function markDocument(page: Page): Promise<void> {
  await page.evaluate(() => {
    (window as unknown as Record<string, unknown>)['__sameDocument'] = true;
  });
}

/** Whether the document marked by {@link markDocument} is still the one on the screen. */
export async function isSameDocument(page: Page): Promise<boolean> {
  return page.evaluate(
    () => (window as unknown as Record<string, unknown>)['__sameDocument'] === true,
  );
}

/** Whether this address carries any of the values, as typed, percent-encoded or `+`-encoded. */
export function carriesAny(url: string, values: readonly string[]): boolean {
  let decoded = url;
  try {
    decoded = decodeURIComponent(url.replace(/\+/g, ' '));
  } catch {
    // A malformed escape is not evidence of anything; the raw comparison below still runs.
  }

  return values.some((value) => url.includes(value) || decoded.includes(value));
}
