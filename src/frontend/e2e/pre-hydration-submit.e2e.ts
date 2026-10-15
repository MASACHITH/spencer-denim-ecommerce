import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import {
  carriesAny,
  holdScripts,
  isSameDocument,
  markDocument,
  recordRequests,
} from './support/hold-scripts';

/**
 * A form submitted **before the page has hydrated** (#123).
 *
 * Every storefront page is server rendered, so a shopper sees a working-looking form while the bundle is
 * still downloading. At that moment no `(submit)` handler exists, so nothing calls `preventDefault` and
 * the browser performs its **own** submit: a `GET` to the page's own address with every field in the
 * query string. On checkout that was a name, an email address and a phone number written into the browser
 * history, the next request's `Referer` and every access log between the shopper and the server (doc 11
 * P17: keep personal data out of places it cannot be deleted from) — and the shopper came back to an
 * empty form with no message.
 *
 * **Why the rest of the suite cannot see it.** Every other test waits for the page to be interactive
 * before it touches a form, and an interactive page has a handler. These tests hold the scripts back
 * instead (`support/hold-scripts.ts`): the document and its styles arrive, the form is on the screen and
 * focusable, and no JavaScript has run — the shopper's situation, reproduced on purpose.
 *
 * **How "nothing happened" is proved without waiting for it.** The scripts are released afterwards and the
 * test waits for the page's own handler to answer — a positive condition — and only then asks where the
 * shopper is. A form that navigated has replaced the document by then, so the URL, a marker on `window`
 * and the log of every request made all say so; a form that did not is still the document the test marked.
 *
 * The contact form needs the content-pages server pair and so is tested in `content-pages.e2e.ts`.
 */

/** The values every test types. Distinctive enough that finding one in an address is never a coincidence. */
const FIRST_NAME = 'Nimali';
const LAST_NAME = 'Perera';
const EMAIL = 'nimali@example.com';
const PHONE = '0771234567';
const PERSONAL = [FIRST_NAME, LAST_NAME, EMAIL, PHONE];

/** Sends the browser's own `/api` calls to the stub, as `checkout.e2e.ts` does and for the same reason. */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * Puts one item in the bag, on a page that is allowed to hydrate. Same gate as `checkout.e2e.ts`'s
 * `addToBag`: the visit being recorded is hydration having happened.
 *
 * Product 81 is this file's own: the stub's stock overrides are one shared map and the suite runs in
 * parallel, so a product another spec overrides would make this one fail for a reason that is not its own.
 */
async function putSomethingInTheBag(page: Page): Promise<void> {
  const slug = 'test-product-81';
  await page.goto(`/product/${slug}?colour=1&size=10`);
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(slug);

  const add = page.locator('[data-testid="add-to-bag"]');
  await expect(add).toBeEnabled();
  await add.click();
  await expect(page.locator('sd-mini-bag [role="dialog"] .mini-bag__line')).toHaveCount(1);
}

/** What a test needs to say about where the shopper ended up. */
async function expectStillHere(page: Page, path: string, requests: readonly string[]): Promise<void> {
  // The log first: it is the one that names the leak, where the URL alone would show only the *last*
  // address the page was sent to.
  expect(
    requests.filter((url) => carriesAny(url, PERSONAL)),
    'the shopper\'s details reached an address',
  ).toEqual([]);

  const here = new URL(page.url());
  expect(here.pathname + here.search, 'the page moved on its own').toBe(path);
  expect(await isSameDocument(page), 'the document was replaced by a native form submission').toBe(true);
}

test.describe('checkout, submitted before hydration (#123)', () => {
  test('Enter and the Place order button keep the details out of the URL, and still work afterwards', async ({
    page,
  }) => {
    await useStubApi(page);
    await putSomethingInTheBag(page);

    const release = await holdScripts(page);
    const requests = recordRequests(page);

    // `commit`, because `load` waits for the scripts that are being held.
    await page.goto('/checkout', { waitUntil: 'commit' });
    await expect(page.locator('#co-firstName')).toBeVisible();
    await markDocument(page);

    await page.locator('#co-firstName').fill(FIRST_NAME);
    await page.locator('#co-lastName').fill(LAST_NAME);
    await page.locator('#co-email').fill(EMAIL);
    await page.locator('#co-phone').fill(PHONE);

    // **Both native routes into the form's submission.** Enter inside a field is implicit submission; the
    // Place order button is a sibling of the form, associated with it by `form=`, and is the second.
    await page.locator('#co-phone').press('Enter');
    await page.locator('.checkout__place').focus();
    await page.keyboard.press('Enter');

    // The page's own read of the checkout, armed before the scripts are let go: it is the first thing the
    // component does once it is alive, so its arrival means the handlers exist.
    const alive = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/checkout'
        && response.request().method() === 'GET',
    );
    release();
    await alive;

    await expectStillHere(page, '/checkout', requests);

    // **Enter-to-submit still works once the page is alive.** The form is incomplete — no district — so
    // the page refuses and says so; that it answers at all is the point, because a fix that removed the
    // native submit without replacing it would leave this press doing nothing. The complete round trip
    // (district, method, details, Enter, order placed) is `checkout.e2e.ts`'s keyboard test.
    await expect(page.locator('#checkout-refused')).toHaveCount(0);
    await page.locator('#co-firstName').fill(FIRST_NAME);
    await page.locator('#co-firstName').press('Enter');

    const refused = page.locator('#checkout-refused');
    await expect(refused).toBeVisible();
    await expect(refused).toHaveAttribute('role', 'alert');
    await expect(refused).toContainText('Choose the district');
  });
});

test.describe('the search box, submitted before hydration (#123)', () => {
  /**
   * A search term in the address is what a search **is**, so this form is allowed to navigate. What it must
   * do is navigate to the results. With no `action` it submitted to the page the shopper was on, so a search
   * from the listing reloaded the listing with a `?q=` the page ignores.
   */
  test('lands on the results page rather than reloading the page it was typed on', async ({
    page,
  }) => {
    await useStubApi(page);
    const release = await holdScripts(page);

    await page.goto('/women', { waitUntil: 'commit' });

    const box = page.locator('form[role="search"] input[name="q"]').locator('visible=true').first();
    await expect(box).toBeVisible();
    await box.fill('linen shirt');
    await box.press('Enter');

    await expect(page).toHaveURL(/\/search\?q=linen(\+|%20)shirt$/);
    await expect(page.locator('h1')).toContainText('linen shirt');

    release();
  });
});

test.describe('the newsletter forms, submitted before hydration (#123)', () => {
  test('the footer form keeps the address out of the URL', async ({ page }) => {
    await useStubApi(page);
    const release = await holdScripts(page);
    const requests = recordRequests(page);

    await page.goto('/women', { waitUntil: 'commit' });

    const field = page.locator('#footer-newsletter-email');
    await expect(field).toBeVisible();
    await markDocument(page);

    await field.fill(EMAIL);
    await field.press('Enter');
    await page.locator('.newsletter__submit').click();

    release();

    // The page's own answer to an empty submission, retried until the component is alive. Pressing Enter
    // before then is the very thing under test, so it is safe to repeat.
    await expect(async () => {
      await field.fill('');
      await field.press('Enter');
      await expect(page.locator('#footer-newsletter-status')).toContainText(
        'Enter your email address',
        { timeout: 1_000 },
      );
    }).toPass();

    await expectStillHere(page, '/women', requests);
  });

  test('the homepage form keeps the address out of the URL', async ({ page }) => {
    await useStubApi(page);
    const release = await holdScripts(page);
    const requests = recordRequests(page);

    await page.goto('/', { waitUntil: 'commit' });

    const field = page.locator('[data-testid="newsletter-email"]');
    await expect(field).toBeVisible();
    await markDocument(page);

    await field.fill(EMAIL);
    await field.press('Enter');
    await page.locator('[data-testid="newsletter-submit"]').click();

    release();

    await expect(async () => {
      await field.fill('');
      await field.press('Enter');
      await expect(page.locator('[data-testid="newsletter-error"]')).toContainText(
        'Enter your email address',
        { timeout: 1_000 },
      );
    }).toPass();

    await expectStillHere(page, '/', requests);
  });
});

test.describe('every server-rendered form says what its native submit does (#123)', () => {
  /**
   * The root cause of #123 was a belief — "there is no other form in the storefront" — that nobody
   * checked, so the structure is checked here instead of remembered. A form that can submit natively must
   * either do nothing (`method="dialog"`) or go somewhere on purpose (the search box, to the results). A
   * form with neither submits to the page it is on, with every field in the query string, which is the
   * defect. A future form that forgets fails here, naming the page it was found on.
   */
  const PAGES = ['/', '/women', '/product/test-product-81', '/bag', '/wishlist', '/search?q=test'];

  for (const path of PAGES) {
    test(`${path}`, async ({ request }) => {
      const html = await (await request.get(path)).text();
      const forms = html.match(/<form\b[^>]*>/g) ?? [];

      expect(forms.length, `no <form> was found on ${path}; is the page rendering?`).toBeGreaterThan(0);

      for (const tag of forms) {
        const method = /\smethod="([^"]*)"/.exec(tag)?.[1]?.toLowerCase();
        const action = /\saction="([^"]*)"/.exec(tag)?.[1];
        const harmless = method === 'dialog' || (method === 'get' && action === '/search');

        expect(harmless, `${tag} can submit to the page it is on with its fields in the address`).toBe(true);
      }
    });
  }
});
