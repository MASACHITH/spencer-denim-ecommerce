import { randomUUID } from 'node:crypto';

import { BrowserContext, Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';

/**
 * One visit, one page view (W4-03, ADM-06, ADM-20) — on the wire, with the real SSR server and a real
 * browser, which are the two halves that have to agree.
 *
 * The storefront's server records the page it rendered; the browser records each page reached by clicking
 * and skips the first page because the server left a flag in it (`src/server/page-view.ts`). Every test
 * here owns a visit session of its own (`sd_vs`) and asks the stub only about that session, because the
 * stub is shared with every other spec running at the same time.
 *
 * The stub stands in for the API, so what is counted here is **recording requests**; that one request is
 * exactly one row is the API's integration tests' job (`PageViewRecordingTests`).
 */

interface RecordedView {
  readonly path: string;
  readonly referrer: string | null;
  readonly source: 'server' | 'browser';
}

/** The browser's own `/api` calls go to the stub, cookie and all, as the reverse proxy sends them to the API. */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

async function startVisit(context: BrowserContext): Promise<string> {
  const session = randomUUID();
  await context.addCookies([{ name: 'sd_vs', value: session, domain: 'localhost', path: '/' }]);
  return session;
}

async function recorded(page: Page, session: string): Promise<RecordedView[]> {
  const response = await page.request.get(`${apiURL}/__test__/page-views?session=${session}`);
  return (await response.json()) as RecordedView[];
}

/** Waits for `count` views, then gives a duplicate a second to arrive — a test of "exactly", not "at least". */
async function expectExactly(page: Page, session: string, count: number): Promise<RecordedView[]> {
  await expect.poll(async () => (await recorded(page, session)).length, { timeout: 10_000 }).toBeGreaterThanOrEqual(count);
  await page.waitForTimeout(1000);

  const views = await recorded(page, session);
  expect(views).toHaveLength(count);
  return views;
}

async function hydrated(page: Page): Promise<void> {
  await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
}

test.describe('traffic recording', () => {
  test('a server-rendered page is one view, recorded by the server and not again by the browser', async ({ page, context }) => {
    const session = await startVisit(context);
    await useStubApi(page);

    await page.goto('/');
    await hydrated(page);

    const views = await expectExactly(page, session, 1);
    expect(views[0]).toMatchObject({ path: '/', source: 'server' });
  });

  test('each page reached by clicking is one more view, recorded by the browser', async ({ page, context }) => {
    const session = await startVisit(context);
    await useStubApi(page);

    await page.goto('/');
    await hydrated(page);
    await expectExactly(page, session, 1);

    await page.locator('header a[href="/women"]').first().click();
    await expect(page).toHaveURL(/\/women$/);

    const views = await expectExactly(page, session, 2);
    expect(views[1]).toMatchObject({ path: '/women', source: 'browser', referrer: null });
  });

  test('changing the sort on a listing is not a new page view; reloading it is', async ({ page, context }) => {
    const session = await startVisit(context);
    await useStubApi(page);

    await page.goto('/women');
    await hydrated(page);
    await expectExactly(page, session, 1);

    await page.locator('#listing-sort').selectOption('newest');
    await expect(page).toHaveURL(/sort=newest/);
    await expectExactly(page, session, 1);

    await page.reload();
    await hydrated(page);

    const views = await expectExactly(page, session, 2);
    expect(views.map((view) => view.source)).toEqual(['server', 'server']);
    expect(views[1].path).toBe('/women?sort=newest');
  });

  test('a page that does not exist is not a page view', async ({ page, context }) => {
    const session = await startVisit(context);
    await useStubApi(page);

    const response = await page.goto('/no-such-page-w403');
    expect(response?.status()).toBe(404);
    await hydrated(page);

    await page.waitForTimeout(1500);
    expect(await recorded(page, session)).toEqual([]);
  });

  test('the server sets the thirty-minute visit cookie the browser’s later views carry', async ({ page, context }) => {
    await useStubApi(page);

    const response = await page.goto('/');
    await hydrated(page);

    const cookie = (await response?.headerValue('set-cookie')) ?? '';
    expect(cookie).toMatch(/sd_vs=[0-9a-f-]{36}; Max-Age=1800; Path=\/; HttpOnly; Secure; SameSite=Lax/);

    const session = (await context.cookies()).find((each) => each.name === 'sd_vs')?.value ?? '';
    expect(session).toMatch(/^[0-9a-f-]{36}$/);

    await page.locator('header a[href="/women"]').first().click();
    await expect(page).toHaveURL(/\/women$/);

    const views = await expectExactly(page, session, 2);
    expect(views.map((view) => view.source)).toEqual(['server', 'browser']);
  });
});
