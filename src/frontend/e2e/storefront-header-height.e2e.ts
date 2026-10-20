import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { brandingApiURL, brandingURL } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * #36: `--header-height` is the sticky header's real rendered height, whatever an admin writes in the
 * announcement strip (ADM-17).
 *
 * The token used to be a guess per breakpoint, correct for the three placeholder messages and wrong the moment
 * an admin typed a longer one (a 110-character line measured 206 px against a 176 px token at 320). The header
 * now publishes its own height from a ResizeObserver, so this measures the invariant directly, for every
 * message in the rotation rather than whichever one happened to be showing (the W2-05 tester's point on #36):
 * the strip is stepped with its own "Next" button and the header measured at each message.
 *
 * It switches the strip on the branding pair's stub (`/__test__/announcements`), because the storefront reads
 * the strip during the server render, so it runs serially on that pair and puts the default back at the end.
 * The token and the header are compared against each other, never against a figure, so the logo state the
 * branding spec may leave on the same stub changes nothing here.
 */
test.describe.configure({ mode: 'serial' });
test.use({ baseURL: brandingURL });

const WIDTHS = [320, 375, 390, 768, 1024, 1280, 1440, 1920];
const STATES = ['long', 'empty', 'single', 'default'] as const;

type Strip = (typeof STATES)[number] | 'hostile';

async function useStrip(page: Page, state: Strip): Promise<void> {
  const switched = await page.request.get(`${brandingApiURL}/__test__/announcements?state=${state}`);
  expect(switched.ok()).toBe(true);
}

/** The header's height and the token's value, once the browser has measured. */
async function measure(page: Page): Promise<{ header: number; token: number; scrollPadding: number }> {
  return page.evaluate(() => {
    const host = document.querySelector('sd-site-header') as HTMLElement;
    const root = document.documentElement;

    return {
      header: host.getBoundingClientRect().height,
      token: parseFloat(getComputedStyle(root).getPropertyValue('--header-height')),
      scrollPadding: parseFloat(getComputedStyle(root).scrollPaddingTop),
    };
  });
}

async function expectTokenMatchesHeader(page: Page, label: string): Promise<void> {
  await expect
    .poll(async () => {
      const { header, token } = await measure(page);
      return token >= header && token - header < 1;
    }, { message: `${label}: --header-height never matched the header`, timeout: 3_000 })
    .toBe(true);

  const { header, token, scrollPadding } = await measure(page);
  expect(token, `${label}: token ${token}px vs header ${header}px`).toBeGreaterThanOrEqual(header);
  expect(scrollPadding, `${label}: scroll-padding-top follows the token`).toBe(token);
}

test.afterAll(async ({ request }) => {
  await request.get(`${brandingApiURL}/__test__/announcements?state=default`);
});

for (const state of STATES) {
  for (const width of WIDTHS) {
    test(`${state} strip at ${width}px: --header-height is the header's real height for every message`, async ({ page }) => {
      await useStrip(page, state);
      await page.setViewportSize({ width, height: 800 });
      await page.goto('/');
      await expect(page.locator('sd-site-header')).toBeVisible();

      const announcements = page.getByTestId('announcement');
      if (state === 'empty') {
        await expect(announcements).toHaveCount(0);
      } else {
        await expect(announcements).toBeVisible();
      }

      await expectTokenMatchesHeader(page, `${state} @${width} first message`);

      // Every message in the rotation, stepped deliberately rather than waited for.
      const next = page.getByRole('button', { name: 'Next announcement' });
      if (await next.count()) {
        const pause = page.getByTestId('announcement-pause');
        if (await pause.count()) {
          await pause.click();
        }

        for (let step = 1; step < 3; step++) {
          await next.click();
          await expectTokenMatchesHeader(page, `${state} @${width} message ${step + 1}`);
        }
      }

      expect(await horizontalOverflow(page), `${state} @${width}: page scrolls sideways`).toBeLessThanOrEqual(0);
    });
  }
}

/**
 * The longest message an admin may save is the case the token could not hold: at 320 px it wraps onto several
 * lines, and a control focused below the header must still land clear of it.
 */
test('a 200-character message at 320px: a focused control is scrolled clear of the header', async ({ page }) => {
  await useStrip(page, 'long');
  await page.setViewportSize({ width: 320, height: 640 });
  await page.goto('/');
  await expectTokenMatchesHeader(page, 'long @320');

  const hidden = await page.evaluate(() => {
    const header = document.querySelector('sd-site-header') as HTMLElement;
    // A link from the middle of the page, so the browser can really scroll it to the top: one in the
    // footer cannot be, and would pass whatever the header did.
    const middle = document.documentElement.scrollHeight / 2;
    const target = [...document.querySelectorAll<HTMLElement>('main a[href]')].sort(
      (a, b) =>
        Math.abs(a.getBoundingClientRect().top + window.scrollY - middle) -
        Math.abs(b.getBoundingClientRect().top + window.scrollY - middle),
    )[0];
    if (!target) {
      return null;
    }

    target.focus();
    target.scrollIntoView({ block: 'start', behavior: 'instant' });
    return Math.round(header.getBoundingClientRect().bottom - target.getBoundingClientRect().top);
  });

  expect(hidden, 'the page has a focusable footer control').not.toBeNull();
  expect(hidden!).toBeLessThanOrEqual(0);
});

test('the long strip passes axe at 320px and 1280px', async ({ page }) => {
  await useStrip(page, 'long');

  for (const width of [320, 1280]) {
    await page.setViewportSize({ width, height: 800 });
    await page.goto('/');
    await expect(page.getByTestId('announcement')).toBeVisible();

    const results = await new AxeBuilder({ page }).include('sd-site-header').analyze();
    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  }
});

// ---- independent tester's additions (#192 / #36) -------------------------------------------------------

/** A phone turned on its side re-lays the strip; the token follows the header both ways. */
test('rotating a phone keeps the token on the header, both ways', async ({ page }) => {
  await useStrip(page, 'long');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expectTokenMatchesHeader(page, 'long portrait');

  await page.setViewportSize({ width: 844, height: 390 });
  await expectTokenMatchesHeader(page, 'long landscape');

  await page.setViewportSize({ width: 390, height: 844 });
  await expectTokenMatchesHeader(page, 'long portrait again');
});

/** Text enlarged to 200% (browser or OS font scaling) makes the strip wrap further; the token follows. */
test('enlarged text at 375px: the token follows the taller header', async ({ page }) => {
  await useStrip(page, 'long');
  await page.setViewportSize({ width: 375, height: 800 });
  await page.goto('/');
  await expectTokenMatchesHeader(page, 'long @375 100%');
  const before = (await measure(page)).header;

  await page.addStyleTag({ content: 'html { font-size: 200% !important; }' });
  await expectTokenMatchesHeader(page, 'long @375 200%');
  expect((await measure(page)).header).toBeGreaterThan(before);
});

/**
 * A ResizeObserver whose callback changes layout can loop, and the browser reports it as an error. Stepping
 * every message, resizing across the 1024 breakpoint and scrolling must leave the console clean.
 */
test('no ResizeObserver loop or script error while the strip rotates and the window resizes', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      errors.push(message.text());
    }
  });
  page.on('pageerror', (error) => errors.push(error.message));

  await useStrip(page, 'long');
  await page.setViewportSize({ width: 320, height: 700 });
  await page.goto('/');
  await expectTokenMatchesHeader(page, 'long @320');

  const pause = page.getByTestId('announcement-pause');
  if (await pause.count()) {
    await pause.click();
  }
  const next = page.getByRole('button', { name: 'Next announcement' });
  for (const width of [320, 1023, 1024, 1920, 375]) {
    await page.setViewportSize({ width, height: 700 });
    await next.click();
    await page.mouse.wheel(0, 600);
    await expectTokenMatchesHeader(page, `long @${width} after a step`);
  }

  expect(errors.filter((text) => /ResizeObserver/i.test(text))).toEqual([]);
  // The stub serves no artwork files, so a missing-image 404 is the stub's, not the header's.
  expect(errors.filter((text) => !text.startsWith('Failed to load resource'))).toEqual([]);
});

/**
 * Publishing the token must not move anything on the server-rendered page: it only feeds scroll-padding, so
 * hydration with the long strip shifts no layout.
 */
test('hydration with the long strip causes no layout shift', async ({ page }) => {
  await useStrip(page, 'long');
  await page.setViewportSize({ width: 320, height: 700 });
  await page.addInitScript(() => {
    (window as unknown as { __cls: number }).__cls = 0;
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) {
        if (!entry.hadRecentInput) {
          (window as unknown as { __cls: number }).__cls += entry.value;
        }
      }
    }).observe({ type: 'layout-shift', buffered: true });
  });

  await page.goto('/');
  await expectTokenMatchesHeader(page, 'long @320 after hydration');
  await page.waitForTimeout(1_000);

  const cls = await page.evaluate(() => (window as unknown as { __cls: number }).__cls);
  expect(cls, `cumulative layout shift ${cls}`).toBeLessThan(0.1);
});

/**
 * Admin text is text. Markup in a message is shown escaped in the server render and never runs; a script or
 * protocol-relative link never reaches an href; and a path that only looks odd stays on this site.
 */
test('a hostile strip renders escaped on the server and in the browser, and no link leaves the site', async ({ page }) => {
  await useStrip(page, 'hostile');

  const html = await (await page.request.get('/')).text();
  expect(html).not.toContain('<img src=x onerror');
  expect(html).not.toContain('<script>window.__xss');
  expect(html).toContain('&lt;img src=x onerror');

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/');
  await expect(page.getByTestId('announcement')).toBeVisible();

  const pause = page.getByTestId('announcement-pause');
  if (await pause.count()) {
    await pause.click();
  }

  const next = page.getByRole('button', { name: 'Next announcement' });
  for (let step = 0; step < 3; step++) {
    const hrefs = await page
      .getByTestId('announcement')
      .locator('a[href]')
      .evaluateAll((links) => links.map((link) => (link as HTMLAnchorElement).href));

    for (const href of hrefs) {
      expect(href.toLowerCase()).not.toContain('javascript:');
      expect(new URL(href).origin, `href ${href}`).toBe(new URL(page.url()).origin);
    }

    await next.click();
  }

  expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
});
