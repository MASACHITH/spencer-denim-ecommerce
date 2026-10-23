import AxeBuilder from '@axe-core/playwright';
import { Page, Response, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * ADM-22 and GLB-02 in the admin application: the settings screen.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, which serves `dist/admin` and answers
 * `/api/admin/*` from the same origin — the session and antiforgery cookies are both same-origin, and in a
 * deployment Caddy puts the app and the API on one host. The endpoint behaviour itself is proved against a
 * real SQL Server in `AdminSettingsEndpointTests`; what this file proves is the screen.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * A 1x1 PNG, as a file the upload control can be given.
 *
 * A real PNG rather than arbitrary bytes, because the production endpoint checks the file *signature* — so a
 * fixture that would be refused in production is a fixture that proves nothing about the happy path.
 */
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==',
  'base64',
);

/**
 * **The server has to be this worktree's own before a single answer from it is trusted.**
 *
 * `reuseExistingServer` is on outside CI and the admin port is derived from the worktree path — which makes a
 * collision unlikely, not impossible. When one happens, another worktree's server serves another worktree's
 * `dist/admin` against these tests, which is how a run of 113 failures looked the first time it happened.
 * `admin-catalog.e2e.ts` makes the same check for the same reason.
 */
test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(
    root.replace(/\\/g, '/').toLowerCase(),
    `the server on ${adminURL} is serving ${root}, not this worktree — a port that answers is not proof that `
      + 'it is yours (doc 14)',
  ).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

/**
 * Opens the settings screen with a state of this test's own.
 *
 * The stub keys its state on an `e2e_state` cookie, so parallel workers do not edit each other's settings —
 * `admin-catalog.e2e.ts` learned that the expensive way.
 */
async function openSettings(page: Page): Promise<void> {
  await page.context().addCookies([
    { name: 'e2e_state', value: `settings-${Math.random().toString(36).slice(2)}`, url: adminURL },
  ]);

  await page.goto(`${adminURL}/settings`);

  // The page fetches its settings on construction, so "the heading is there" is not enough: a panel asserted
  // before the response has arrived is a panel that is legitimately absent.
  await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'Logo' })).toBeVisible();
}

/**
 * Opens the settings screen **again**, keeping the state this test has already written.
 *
 * <b>Not <c>openSettings</c>.</b> That helper seeds a fresh random `e2e_state` cookie, which is exactly
 * right for starting a test and exactly wrong for checking that a save persisted: it hands the stub a new,
 * empty state, so every field reads back blank and the test fails for a reason that has nothing to do with
 * the screen. This was a real mistake in the first version of the invoice round-trip test, and the browser
 * run is what found it.
 */
async function reopenSettings(page: Page): Promise<void> {
  await page.goto(`${adminURL}/settings`);

  await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'Logo' })).toBeVisible();
}

test.describe('the logo panel (GLB-02)', () => {
  test('says there is no logo, and the storefront is showing the name as text', async ({ page }) => {
    await openSettings(page);

    await expect(page.locator('[data-testid="logo-empty"]')).toContainText('No logo yet');
    await expect(page.locator('[data-testid="logo-preview"]')).toHaveCount(0);

    // No Remove button when there is nothing to remove.
    await expect(page.locator('[data-testid="logo-remove"]')).toHaveCount(0);
  });

  /**
   * The whole flow the owner asked for, in a browser: upload, see it, then remove it and get the wordmark
   * back. Two requests per upload — the file to the media library, then the setting — so a failed upload
   * changes nothing.
   */
  test('uploads a logo, shows it, and removes it again', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="logo-file"]').setInputFiles({
      name: 'logo.png',
      mimeType: 'image/png',
      buffer: PNG,
    });

    const preview = page.locator('[data-testid="logo-preview"] img');
    await expect(preview).toBeVisible();

    // The preview really decoded an image. `toBeVisible` passes for a broken one — it has a box.
    expect(
      await preview.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0),
      'the preview did not decode an image',
    ).toBe(true);

    await expect(page.locator('[data-testid="logo-empty"]')).toHaveCount(0);

    // The live region said what happened, which is the only confirmation a screen-reader user gets.
    await expect(page.locator('[data-testid="announcement"]')).toContainText('logo was updated');

    // The file box is emptied, so the next upload of the same file still fires a change event.
    expect(await page.locator('[data-testid="logo-file"]').inputValue()).toBe('');

    await page.locator('[data-testid="logo-remove"]').click();

    await expect(page.locator('[data-testid="logo-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="logo-preview"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="announcement"]')).toContainText('shows the shop');
  });

  /**
   * **Replacing works twice in a row**, which is the assertion that catches the stale-token bug: every save
   * moves the group's row version, so a screen that kept the token it loaded with would fail its second save
   * with a 409 the admin did nothing to cause.
   */
  test('a second upload replaces the first without a conflict', async ({ page }) => {
    await openSettings(page);

    for (const name of ['first.png', 'second.png']) {
      await page.locator('[data-testid="logo-file"]').setInputFiles({
        name,
        mimeType: 'image/png',
        buffer: PNG,
      });

      await expect(page.locator('[data-testid="logo-preview"] img')).toBeVisible();
      await expect(page.locator('[data-testid="page-error"]')).toHaveCount(0);
    }
  });

  test('a file over the size limit is refused before it is uploaded', async ({ page }) => {
    await openSettings(page);

    let uploaded = false;
    page.on('request', (request) => {
      if (request.url().includes('/api/admin/media/images')) {
        uploaded = true;
      }
    });

    await page.locator('[data-testid="logo-file"]').setInputFiles({
      name: 'huge.png',
      mimeType: 'image/png',
      buffer: Buffer.alloc(3 * 1024 * 1024),
    });

    await expect(page.locator('[data-testid="logo-error"]')).toContainText('larger than 2 MB');

    // Refused locally, so nothing was sent: a 2 MB refusal after a slow upload on a mobile connection is a
    // worse experience than a sentence before it starts.
    expect(uploaded, 'the file was uploaded despite being over the limit').toBe(false);
  });

  /**
   * An asset the library does not have — a stale screen re-sending a deleted one. The screen has to say so
   * rather than leave the admin looking at a preview of something that is not the logo.
   */
  test('an asset the library refuses is reported, and the file box is cleared', async ({ page }) => {
    await openSettings(page);

    await page.route('**/api/admin/settings/store-logo', (route) =>
      route.fulfill({
        status: 400,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          title: 'One or more fields are invalid.',
          status: 400,
          errors: { logoAssetPublicId: ['That image is not in the media library.'] },
        }),
      }),
    );

    await page.locator('[data-testid="logo-file"]').setInputFiles({
      name: 'logo.png',
      mimeType: 'image/png',
      buffer: PNG,
    });

    await expect(page.locator('[data-testid="page-error"]')).toBeVisible();
    expect(await page.locator('[data-testid="logo-file"]').inputValue()).toBe('');
    await expect(page.locator('[data-testid="logo-empty"]')).toBeVisible();
  });
});

/**
 * Clicks Save on the contact and social form and waits for **that save's own response**.
 *
 * **Why this exists, and why a wait would not do.** `page.reload()` aborts whatever the page still has in
 * flight. A reload issued straight after the click therefore races the `PUT` off the socket, and when the
 * navigation wins the save never reaches the server at all — so the reloaded form shows the *old* value,
 * settled, pristine and completely stable. No amount of polling can recover it, because the value on the
 * server is already the wrong one; this is why the failure presented as a confident wrong value rather than
 * as a timeout.
 *
 * **The screen offers nothing in the DOM to gate on instead.** `announcement` is set to the same sentence by
 * every save and never clears, so after a *second* save nothing in the markup transitions — which is how the
 * first version of these tests came to gate on `page-error` having count 0. That assertion is already true
 * before the request is even sent, so it gated nothing and the reload went out on top of the live `PUT`.
 *
 * Returning the response lets the caller assert the save was *accepted*. A 409 or a 400 here used to leave no
 * trace but a stale value several lines later.
 */
async function saveContactAndSocial(page: Page): Promise<Response> {
  const settled = contactSaveResponse(page);

  await page.locator('[data-testid="save-contact-social"]').click();

  return settled;
}

/** <inheritdoc cref="saveContactAndSocial" /> — for the tests that have to press the button themselves. */
function contactSaveResponse(page: Page): Promise<Response> {
  return page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/admin/settings/contact-social'
      && response.request().method() === 'PUT',
  );
}

/**
 * The re-read the screen performs after every successful write, so a test can wait for the screen to have
 * *settled* rather than merely for the write to have been accepted.
 */
function settingsReloadResponse(page: Page): Promise<Response> {
  return page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/admin/settings'
      && response.request().method() === 'GET',
  );
}

/**
 * Holds the contact and social `PUT` open for `delayMs`, so the screen's *in-progress* state can be
 * observed instead of raced for.
 *
 * This does not change what the screen does — it only widens a window that is genuinely there. The same
 * technique is what established the cause of the CI failure these tests were fixed for: the race was not
 * reachable on a developer machine, so it was reproduced by holding the request rather than by hoping.
 */
async function holdContactSave(page: Page, delayMs: number): Promise<void> {
  await page.route('**/api/admin/settings/contact-social', async (route) => {
    if (route.request().method() !== 'PUT') {
      return route.continue();
    }

    await new Promise((resolve) => setTimeout(resolve, delayMs));
    await route.continue();
  });
}

test.describe('the contact and social panel (GLB-07, GLB-08)', () => {
  test('saves the number and the three addresses together', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-whatsApp"]').fill('94771234567');
    await page.locator('[data-testid="field-facebookUrl"]').fill('https://www.facebook.com/colombowardrobe');
    await page.locator('[data-testid="field-instagramUrl"]').fill('https://www.instagram.com/colombowardrobe');
    await page.locator('[data-testid="field-tikTokUrl"]').fill('https://www.tiktok.com/@colombowardrobe');

    const saved = await saveContactAndSocial(page);
    expect(saved.ok(), `the save answered ${saved.status()}`).toBe(true);

    await expect(page.locator('[data-testid="announcement"]')).toContainText('saved');
    await expect(page.locator('[data-testid="page-error"]')).toHaveCount(0);

    // Reloaded from the server, so these values came back rather than being the ones still typed in.
    await page.reload();
    await expect(page.locator('[data-testid="field-whatsApp"]')).toHaveValue('94771234567');
    await expect(page.locator('[data-testid="field-tikTokUrl"]')).toHaveValue(
      'https://www.tiktok.com/@colombowardrobe',
    );
  });

  /**
   * **Every field, every save** — the W1-12 blocker's exact shape. Fill all four, save; then clear one and
   * save again, and the other three have to survive. A request model that omitted a field would clear it.
   */
  test('saving one field does not clear the others', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-whatsApp"]').fill('94771234567');
    await page.locator('[data-testid="field-facebookUrl"]').fill('https://www.facebook.com/one');
    await page.locator('[data-testid="field-instagramUrl"]').fill('https://www.instagram.com/one');
    await page.locator('[data-testid="field-tikTokUrl"]').fill('https://www.tiktok.com/@one');

    const first = await saveContactAndSocial(page);
    expect(first.ok(), `the first save answered ${first.status()}`).toBe(true);
    await expect(page.locator('[data-testid="announcement"]')).toContainText('saved');

    await page.locator('[data-testid="field-whatsApp"]').fill('94779999999');

    // The second save needs a gate of its own. The announcement already says "saved" from the first one, so
    // it transitions on nothing here — and the reload below would otherwise abort this `PUT` before it left
    // the browser, which is exactly how this test failed on Linux CI while passing on Windows.
    const second = await saveContactAndSocial(page);
    expect(second.ok(), `the second save answered ${second.status()}`).toBe(true);
    await expect(page.locator('[data-testid="page-error"]')).toHaveCount(0);

    await page.reload();
    await expect(page.locator('[data-testid="field-whatsApp"]')).toHaveValue('94779999999');
    await expect(page.locator('[data-testid="field-facebookUrl"]')).toHaveValue('https://www.facebook.com/one');
    await expect(page.locator('[data-testid="field-instagramUrl"]')).toHaveValue('https://www.instagram.com/one');
    await expect(page.locator('[data-testid="field-tikTokUrl"]')).toHaveValue('https://www.tiktok.com/@one');
  });

  test('clearing a box clears the setting, which hides that icon on the shop', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-instagramUrl"]').fill('https://www.instagram.com/one');

    const set = await saveContactAndSocial(page);
    expect(set.ok(), `the save answered ${set.status()}`).toBe(true);
    await expect(page.locator('[data-testid="announcement"]')).toContainText('saved');

    await page.locator('[data-testid="field-instagramUrl"]').fill('');

    // As above: the clearing save had no gate at all, so the reload raced it.
    const cleared = await saveContactAndSocial(page);
    expect(cleared.ok(), `the clearing save answered ${cleared.status()}`).toBe(true);

    await page.reload();
    await expect(page.locator('[data-testid="field-instagramUrl"]')).toHaveValue('');
  });

  /**
   * **GLB-12: the second save has to be announced too.**
   *
   * `role="status"` announces a *change*. Setting the region to the same sentence it already holds is not a
   * change, so an admin who saves twice — which is most of what this screen is for — is told once and then
   * never again. The screen knew it was saving and knew it had finished; it simply never said so.
   *
   * The assertion is on the transition, not on any particular wording: the region must leave the old
   * sentence while the save is in flight and come back to a completion message. That is a real mutation for
   * assistive technology to voice, and it is information rather than a differentiator bolted on to force a
   * re-announcement — a counter or a timestamp appended to an unchanged sentence would satisfy a naive
   * version of this test while telling the listener nothing.
   */
  test('a second save is announced too, not silently', async ({ page }) => {
    await openSettings(page);

    const status = page.locator('[data-testid="announcement"]');

    await page.locator('[data-testid="field-whatsApp"]').fill('94771234567');
    const first = await saveContactAndSocial(page);
    expect(first.ok(), `the first save answered ${first.status()}`).toBe(true);
    await expect(status).toContainText('saved');

    const announcedFirst = (await status.textContent())?.trim() ?? '';

    // Held open so the in-progress state is observable rather than raced for.
    await holdContactSave(page, 600);

    await page.locator('[data-testid="field-whatsApp"]').fill('94779999999');
    const settled = contactSaveResponse(page);
    await page.locator('[data-testid="save-contact-social"]').click();

    // The region has to *leave* the old sentence, or a screen reader hears nothing for this save at all.
    await expect(status).not.toHaveText(announcedFirst);
    await expect(status).toContainText('Saving');

    const second = await settled;
    expect(second.ok(), `the second save answered ${second.status()}`).toBe(true);

    await expect(status).toContainText('saved');
  });

  test('a rejected address is reported against its own box', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-facebookUrl"]').fill('javascript:alert(1)');
    await page.locator('[data-testid="field-instagramUrl"]').fill('https://www.instagram.com/fine');
    await page.locator('[data-testid="save-contact-social"]').click();

    await expect(page.locator('[data-testid="error-facebookUrl"]')).toBeVisible();
    await expect(page.locator('[data-testid="field-facebookUrl"]')).toHaveAttribute('aria-invalid', 'true');

    // The box that was fine is not marked.
    await expect(page.locator('[data-testid="error-instagramUrl"]')).toHaveCount(0);

    // Typing in the bad box clears its message, so it does not sit there contradicting the value.
    await page.locator('[data-testid="field-facebookUrl"]').fill('https://www.facebook.com/fine');
    await expect(page.locator('[data-testid="error-facebookUrl"]')).toHaveCount(0);
  });

  test('a rejected WhatsApp number is reported against its own box', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-whatsApp"]').fill('0771234567');
    await page.locator('[data-testid="save-contact-social"]').click();

    await expect(page.locator('[data-testid="error-whatsApp"]')).toContainText('international form');
  });

  /** The screen sends the antiforgery header; without this the whole suite would pass if it stopped. */
  test('the save carries an antiforgery token', async ({ page }) => {
    await openSettings(page);

    await page.route('**/api/admin/settings/contact-social', async (route) => {
      const headers = { ...route.request().headers() };
      delete headers['x-xsrf-token'];
      await route.continue({ headers });
    });

    await page.locator('[data-testid="field-whatsApp"]').fill('94771234567');
    await page.locator('[data-testid="save-contact-social"]').click();

    // The stub refuses a mutating request without the header, exactly as all three real write routes do, so
    // taking it away has to produce a visible refusal rather than a silent success.
    await expect(page.locator('[data-testid="page-error"]')).toBeVisible();
  });

  /** And the row version, the other precondition a screen can quietly stop sending. */
  test('the save carries the row version it was handed', async ({ page }) => {
    await openSettings(page);

    await page.route('**/api/admin/settings/contact-social', async (route) => {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;

      expect(typeof body['rowVersion'], 'the screen sent no rowVersion').toBe('string');
      delete body['rowVersion'];

      await route.continue({ postData: JSON.stringify(body) });
    });

    await page.locator('[data-testid="field-whatsApp"]').fill('94771234567');
    await page.locator('[data-testid="save-contact-social"]').click();

    await expect(page.locator('[data-testid="page-error"]')).toBeVisible();
  });
});

test.describe('the settings screen is usable and accessible', () => {
  test('has no serious or critical axe violation', async ({ page }) => {
    await openSettings(page);

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();

    const serious = results.violations
      .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
      .map((violation) => ({
        id: violation.id,
        impact: violation.impact,
        help: violation.help,
        nodes: violation.nodes.map((node) => node.target.join(' ')),
      }));

    expect(serious, JSON.stringify(serious, null, 2)).toEqual([]);
  });

  /**
   * Everything axe found, at any impact, printed rather than asserted.
   *
   * The gate is serious and critical (docs/06 §3) and `heading-order` is a *moderate* — the exact rule class of
   * #76 — so a new moderate is invisible to the gate. This is where it shows up at all.
   */
  test('reports whatever else axe found, for the record', async ({ page }) => {
    await openSettings(page);

    const results = await new AxeBuilder({ page }).analyze();

    test.info().annotations.push({
      type: 'axe',
      description: JSON.stringify(
        results.violations.map((violation) => ({
          id: violation.id,
          impact: violation.impact,
          targets: violation.nodes.map((node) => node.target.join(' ')),
        })),
        null,
        2,
      ),
    });
  });

  /**
   * Heading order, by hand, because axe will not catch it: `heading-order` is a moderate and the gate is
   * serious and critical. The page is one `h1` followed by its panels' `h2`s, in document order.
   */
  test('announces one h1 and then its panels', async ({ page }) => {
    await openSettings(page);

    // The Payment methods panel (owner decision 2026-10-21) fetches for itself, so it is waited for before the
    // headings are read, or the list would be read before it arrived.
    await expect(page.getByRole('heading', { level: 2, name: 'Payment methods' })).toBeVisible();

    const headings = await page.locator('h1, h2').evaluateAll((nodes) =>
      nodes.map((node) => `${node.tagName.toLowerCase()}:${node.textContent?.trim()}`),
    );

    // W3-03 (PAY-01) added the third panel: the bank details a shopper is told to transfer to. W3-04 added
    // the fourth: the company's registered details as an invoice prints them, and the auto-deliver window.
    // The list is exhaustive on purpose — a panel appearing without a heading, or out of order, is the
    // defect this test exists for — so a new panel is expected to come back here and say so. It did, and
    // this test is how the new panel was noticed in a browser at all.
    expect(headings).toEqual([
      'h1:Settings',
      'h2:Logo',
      'h2:Contact and social',

      // Owner decision 2026-10-21: which payment methods checkout offers, beside the bank details.
      'h2:Payment methods',
      'h2:Bank transfer',
      'h2:Invoice and fulfilment',

      // Issue #193: how long the nightly purge keeps raw page views.
      'h2:Traffic',

      // Issue #33: the product page's delivery and returns texts.
      'h2:Product page: delivery and returns',

      // Issue #189: whether customers' reviews wait for approval.
      'h2:Reviews',

      // ADM-22 (2026-10-21): the mail account, for a Super Admin (the stub's user holds GrantsAll).
      'h2:Email account',
    ]);
  });

  test('every control is reachable and labelled', async ({ page }) => {
    await openSettings(page);

    // A name on every form control, which is what makes the screen usable without sight.
    for (const testId of [
      'logo-alt-input',
      'logo-file',
      'field-whatsApp',
      'field-facebookUrl',
      'field-instagramUrl',
      'field-tikTokUrl',
      'field-youTubeUrl',
      'field-rawRetentionDays',
      'field-botRetentionDays',
    ]) {
      const name = await page
        .locator(`[data-testid="${testId}"]`)
        .evaluate((input: HTMLInputElement) => input.labels?.[0]?.textContent?.trim() ?? '');

      expect(name, `${testId} has no label`).not.toBe('');
    }
  });

  test('the save button is reachable by keyboard from the last field', async ({ page }) => {
    await openSettings(page);

    // The YouTube box is the last field since issue #193 (it was TikTok before).
    await page.locator('[data-testid="field-youTubeUrl"]').focus();
    await page.keyboard.press('Tab');

    await expect(page.locator('[data-testid="save-contact-social"]')).toBeFocused();
  });

  /**
   * **The save button is reachable by keyboard — and has to still be there afterwards.**
   *
   * The test above proves a keyboard user can *get* to Save. This one proves the operation does not throw
   * them off the screen: `afterWrite` re-reads the settings, and while that read is in flight the panel is
   * replaced by "Loading settings…", so the button the user just pressed is destroyed under their focus and
   * focus falls to `<body>`. From there Tab starts again at the top of the document, which on a settings
   * screen the client uses repeatedly is the difference between usable and not.
   *
   * Driven with real keys rather than by calling the handler: `[disabled]` on a focused button moves focus
   * on its own, so a programmatic `.click()` would not exercise the same path a keyboard user takes.
   */
  test('keyboard focus survives a save', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-whatsApp"]').fill('94771234567');

    const save = page.locator('[data-testid="save-contact-social"]');
    await page.locator('[data-testid="field-youTubeUrl"]').focus();
    await page.keyboard.press('Tab');
    await expect(save, 'Tab from the last field did not reach Save').toBeFocused();

    const settled = contactSaveResponse(page);
    const reloaded = settingsReloadResponse(page);
    await page.keyboard.press('Enter');

    const response = await settled;
    expect(response.ok(), `the save answered ${response.status()}`).toBe(true);
    await reloaded;

    // Reported rather than merely asserted: "focus is not on Save" is far less useful than knowing it
    // landed on `<body>`, which is what a destroyed control does to it.
    const landedOn = await page.evaluate(() => {
      const active = document.activeElement as HTMLElement | null;
      return active
        ? `${active.tagName.toLowerCase()}${active.dataset['testid'] ? `[${active.dataset['testid']}]` : ''}`
        : 'nothing';
    });

    expect(landedOn, 'focus after a save').toBe('button[save-contact-social]');
    await expect(save).toBeFocused();
  });

  /**
   * The one write whose own control does not survive it.
   *
   * Removing the logo takes the Remove button off the screen — the logo is what put it there — so focus
   * cannot simply stay put the way it does for Save, and "wherever the browser leaves it" means `<body>`.
   * The upload box is the deliberate choice: same panel, and the next thing an admin who just removed a
   * logo is likely to want.
   */
  test('removing the logo hands focus to the upload box, not to nothing', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="logo-file"]').setInputFiles({
      name: 'logo.png',
      mimeType: 'image/png',
      buffer: PNG,
    });

    const remove = page.locator('[data-testid="logo-remove"]');
    await expect(remove).toBeVisible();

    await remove.focus();
    await page.keyboard.press('Enter');

    // Gone by design, so this is not a race: the button exists only while a logo does.
    await expect(page.locator('[data-testid="logo-empty"]')).toBeVisible();
    await expect(remove).toHaveCount(0);

    const landedOn = await page.evaluate(() => {
      const active = document.activeElement as HTMLElement | null;
      return active
        ? `${active.tagName.toLowerCase()}${active.dataset['testid'] ? `[${active.dataset['testid']}]` : ''}`
        : 'nothing';
    });

    expect(landedOn, 'focus after removing the logo').toBe('input[logo-file]');
  });

  /** Lesson L4: a ring an ancestor clips away computes as present, so it is photographed instead. */
  test('the save button keeps a visible focus ring', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    const save = page.locator('[data-testid="save-contact-social"]');
    await save.focus();

    expect(await edgesWithoutRing(page, save)).toEqual([]);
  });

  test('fits every breakpoint with no horizontal scroll and real tap targets', async ({ page }) => {
    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await openSettings(page);

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);

      // #80: guarded per clause, because a compound selector's guard is satisfied by its first clause while a
      // later one silently matches nothing — and `undersized` returns [] for "nothing matched" too.
      const selector = '[data-testid="save-contact-social"], [data-testid="field-whatsApp"]';
      for (const clause of selector.split(',').map((part) => part.trim())) {
        const rendered = await page.evaluate(
          (one) =>
            [...document.querySelectorAll<HTMLElement>(one)].filter(
              (element) => element.getClientRects().length > 0,
            ).length,
          clause,
        );

        expect(
          rendered,
          `nothing rendered matches "${clause}" at ${width}px, so measuring it could not fail`,
        ).toBeGreaterThan(0);
      }

      expect(await undersized(page, selector), `tap targets at ${width}px`).toEqual([]);
    }
  });

  test('the preview and its panel fit the narrowest breakpoint once a logo is set', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="logo-file"]').setInputFiles({
      name: 'logo.png',
      mimeType: 'image/png',
      buffer: PNG,
    });

    await expect(page.locator('[data-testid="logo-preview"] img')).toBeVisible();

    expect(await horizontalOverflow(page), 'horizontal overflow at 320px with a logo set').toBeLessThanOrEqual(0);

    // The Remove button is a destructive action, so it has to be a real target rather than a thin link.
    expect(await undersized(page, '[data-testid="logo-remove"]')).toEqual([]);
  });

  // ---- W3-04: the invoice and fulfilment form ------------------------------------------------

  /**
   * **The form ADM-09 needed, measured at every breakpoint.**
   *
   * It arrived in W3-04's second round with six Vitest specs and ten integration tests, and no browser
   * measurement at all - so the project's non-negotiable responsive rule had never been applied to it. A
   * sweep is the only thing that can catch a field that renders off-screen or a Save button under the
   * floor, because neither the specs (no layout) nor the integration tests (no browser) can see it.
   */
  test('the invoice form fits every breakpoint with real tap targets', async ({ page }) => {
    const fields = [
      '[data-testid="field-legalName"]',
      '[data-testid="field-registeredAddress"]',
      '[data-testid="field-brNumber"]',
      '[data-testid="field-vatNumber"]',
      '[data-testid="field-autoDeliverAfterDays"]',
      '[data-testid="save-invoice"]',
    ];

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await openSettings(page);

      expect(
        await horizontalOverflow(page),
        `horizontal overflow at ${width}px with the invoice form on screen`,
      ).toBeLessThanOrEqual(0);

      // #80: each selector is guarded on its own. A compound selector's guard is satisfied by its first
      // clause while a later one silently matches nothing, and `undersized` answers [] for "nothing
      // matched" as well as for "everything is big enough" - so an unguarded sweep over six fields is a
      // sweep that cannot fail.
      for (const selector of fields) {
        const rendered = await page.evaluate(
          (one) =>
            [...document.querySelectorAll<HTMLElement>(one)].filter(
              (element) => element.getClientRects().length > 0,
            ).length,
          selector,
        );

        expect(
          rendered,
          `nothing rendered matches "${selector}" at ${width}px, so measuring it could not fail`,
        ).toBeGreaterThan(0);

        expect(await undersized(page, selector), `${selector} at ${width}px`).toEqual([]);
      }
    }
  });

  /**
   * The round trip, through the screen: type, save, re-read, and the values are the ones the server now
   * holds rather than the ones left in the boxes.
   */
  test('saving the invoice details sends them and shows what came back', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-legalName"]').fill('A Registered Company (Pvt) Ltd');
    await page.locator('[data-testid="field-registeredAddress"]').fill('No. 1, Example Road\nColombo 00100');
    await page.locator('[data-testid="field-brNumber"]').fill('PV-00000');
    await page.locator('[data-testid="field-autoDeliverAfterDays"]').fill('10');

    const saved = page.waitForResponse(
      (response: Response) =>
        response.url().includes('/api/admin/settings/invoice-and-fulfilment')
        && response.request().method() === 'PUT',
    );

    await page.locator('[data-testid="save-invoice"]').click();
    expect((await saved).status()).toBe(204);

    // Re-opened rather than re-read from the boxes: the screen re-reads after a save, and a form that kept
    // showing what was typed would hide a field the server never stored. `reopenSettings` keeps this
    // test's own stub state — see its remarks for what using `openSettings` here cost.
    await reopenSettings(page);

    await expect(page.locator('[data-testid="field-legalName"]')).toHaveValue(
      'A Registered Company (Pvt) Ltd',
    );
    await expect(page.locator('[data-testid="field-brNumber"]')).toHaveValue('PV-00000');
    await expect(page.locator('[data-testid="field-autoDeliverAfterDays"]')).toHaveValue('10');
  });

  /**
   * The window's refusal reaches the field rather than a banner, because a figure a staff member cannot
   * see refused beside the box is a figure they retype unchanged.
   */
  test('a window outside the range is refused against its own field', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-autoDeliverAfterDays"]').fill('0');
    await page.locator('[data-testid="save-invoice"]').click();

    const error = page.locator('[data-testid="error-autoDeliverAfterDays"]');
    await expect(error).toBeVisible();

    // And the refusal itself does not push the narrowest viewport sideways.
    expect(await horizontalOverflow(page), 'horizontal overflow at 320px with a field error').toBeLessThanOrEqual(0);
  });

  /**
   * The seeded window is on screen when the page opens, which is what tells a staff member the system is
   * already closing orders after seven days rather than not at all.
   */
  test('the seeded auto-deliver window is shown rather than an empty box', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await expect(page.locator('[data-testid="field-autoDeliverAfterDays"]')).toHaveValue('7');
  });
});

// ---- issue #193: the YouTube channel and the Traffic form ------------------------------------------------

test.describe('the YouTube channel (issue #193)', () => {
  test('is saved with the other addresses and comes back from the server', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-youTubeUrl"]').fill('https://www.youtube.com/@colombowardrobe');

    const saved = page.waitForResponse(
      (response: Response) =>
        response.url().includes('/api/admin/settings/contact-social') && response.request().method() === 'PUT',
    );
    await page.locator('[data-testid="save-contact-social"]').click();
    expect((await saved).status()).toBe(204);

    await reopenSettings(page);

    await expect(page.locator('[data-testid="field-youTubeUrl"]')).toHaveValue(
      'https://www.youtube.com/@colombowardrobe',
    );
  });

  test('an address a browser should not follow is refused against its own box', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-youTubeUrl"]').fill('javascript:alert(1)');
    await page.locator('[data-testid="save-contact-social"]').click();

    await expect(page.locator('[data-testid="error-youTubeUrl"]')).toBeVisible();
    expect(await horizontalOverflow(page), 'horizontal overflow at 320px with a field error').toBeLessThanOrEqual(0);
  });
});

test.describe('the Traffic form (issue #193)', () => {
  const trafficFields = [
    '[data-testid="field-rawRetentionDays"]',
    '[data-testid="field-botRetentionDays"]',
    '[data-testid="save-traffic"]',
  ];

  async function saveTraffic(page: Page): Promise<Response> {
    const saved = page.waitForResponse(
      (response: Response) =>
        response.url().includes('/api/admin/settings/traffic') && response.request().method() === 'PUT',
    );

    await page.locator('[data-testid="save-traffic"]').click();

    return saved;
  }

  /** The seeded periods are on screen, which tells a staff member what the purge is doing today. */
  test('opens on the seeded 90 and 7 days', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await expect(page.locator('[data-testid="field-rawRetentionDays"]')).toHaveValue('90');
    await expect(page.locator('[data-testid="field-botRetentionDays"]')).toHaveValue('7');
    await expect(page.locator('[data-testid="traffic-panel"]')).toContainText('kept for ever');
  });

  test('saving sends both periods and shows what the server now holds', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-rawRetentionDays"]').fill('180');
    await page.locator('[data-testid="field-botRetentionDays"]').fill('21');

    const saved = await saveTraffic(page);
    expect(saved.status()).toBe(204);
    expect(saved.request().postDataJSON()).toMatchObject({ rawRetentionDays: '180', botRetentionDays: '21' });

    await expect(page.locator('[data-testid="announcement"]')).toContainText('Traffic settings saved');

    await reopenSettings(page);

    await expect(page.locator('[data-testid="field-rawRetentionDays"]')).toHaveValue('180');
    await expect(page.locator('[data-testid="field-botRetentionDays"]')).toHaveValue('21');
  });

  test('a period outside its bounds is refused against its own box, and nothing is stored', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-rawRetentionDays"]').fill('10');
    expect((await saveTraffic(page)).status()).toBe(400);

    await expect(page.locator('[data-testid="error-rawRetentionDays"]')).toContainText('between 30 and 365');
    await expect(page.locator('[data-testid="field-rawRetentionDays"]')).toHaveAttribute('aria-invalid', 'true');
    expect(await horizontalOverflow(page), 'horizontal overflow at 320px with a field error').toBeLessThanOrEqual(0);

    await reopenSettings(page);
    await expect(page.locator('[data-testid="field-rawRetentionDays"]')).toHaveValue('90');
  });

  test('an emptied box is refused rather than cleared', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-botRetentionDays"]').fill('');
    expect((await saveTraffic(page)).status()).toBe(400);

    await expect(page.locator('[data-testid="error-botRetentionDays"]')).toContainText('cannot be left empty');
  });

  test('bots kept longer than people is refused against the bot box', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSettings(page);

    await page.locator('[data-testid="field-rawRetentionDays"]').fill('40');
    await page.locator('[data-testid="field-botRetentionDays"]').fill('60');
    expect((await saveTraffic(page)).status()).toBe(400);

    await expect(page.locator('[data-testid="error-botRetentionDays"]')).toContainText('cannot be kept longer');
  });

  /** The project's responsive rule, applied to the new panel: every breakpoint, no sideways scroll, real targets. */
  test('fits every breakpoint with no horizontal scroll and real tap targets', async ({ page }) => {
    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await openSettings(page);

      expect(
        await horizontalOverflow(page),
        `horizontal overflow at ${width}px with the Traffic form on screen`,
      ).toBeLessThanOrEqual(0);

      // #80: each selector guarded on its own, so the sweep cannot pass by matching nothing.
      for (const selector of [...trafficFields, '[data-testid="field-youTubeUrl"]']) {
        const rendered = await page.evaluate(
          (one) =>
            [...document.querySelectorAll<HTMLElement>(one)].filter((element) => element.getClientRects().length > 0)
              .length,
          selector,
        );

        expect(rendered, `nothing rendered matches "${selector}" at ${width}px`).toBeGreaterThan(0);
        expect(await undersized(page, selector), `${selector} at ${width}px`).toEqual([]);
      }
    }
  });
});

test.describe('the Email account panel (ADM-22, 2026-10-21)', () => {
  const smtpFields = ['host', 'port', 'username', 'password', 'fromAddress', 'fromName', 'useStartTls'].map(
    (name) => `[data-testid="smtp-field-${name}"]`,
  );

  async function fillAccount(page: Page): Promise<void> {
    const panel = page.getByTestId('smtp-panel');
    await panel.getByLabel('Mail server').fill('smtp.example.test');
    await panel.getByLabel('Port').fill('587');
    await panel.getByLabel('User name').fill('shop@example.test');
    await panel.getByLabel('Password').fill('an app password');
    await panel.getByLabel('Send from address').fill('shop@example.test');
    await panel.getByLabel('Send from name').fill('Colombo Wardrobe');
  }

  test('is shown to a Super Admin with every box labelled and the password empty', async ({ page }) => {
    await openSettings(page);

    const panel = page.getByTestId('smtp-panel');
    await expect(panel.getByRole('heading', { level: 2, name: 'Email account' })).toBeVisible();
    await expect(panel.getByLabel('Password')).toHaveValue('');
    await expect(panel.getByLabel('Password')).toHaveAttribute('type', 'password');
    await expect(panel.getByTestId('smtp-password-state')).toContainText('No password is saved yet.');
    await expect(panel.getByLabel('Encrypt the connection (STARTTLS)')).toBeChecked();
  });

  test('is absent for anybody who is not a Super Admin', async ({ page }) => {
    await openSettings(page);

    // page.request shares the context's cookies, so the switch lands in this test's own stub state.
    await page.request.get(`${adminURL}/__test__/smtp-hidden?hidden=yes`);
    await reopenSettings(page);

    await expect(page.getByTestId('smtp-panel')).toHaveCount(0);
    await expect(page.getByRole('heading', { level: 2, name: 'Reviews' })).toBeVisible();
  });

  test('saving stores the account, shows it after a reload, and keeps the password write-only', async ({ page }) => {
    await openSettings(page);
    await fillAccount(page);

    const saved = page.waitForResponse(
      (response) => response.url().endsWith('/api/admin/settings/email-smtp') && response.request().method() === 'PUT',
    );
    await page.getByTestId('save-smtp').click();
    const response = await saved;

    expect(response.status()).toBe(204);
    expect(response.request().postDataJSON()).toMatchObject({
      host: 'smtp.example.test',
      port: '587',
      username: 'shop@example.test',
      password: 'an app password',
      fromAddress: 'shop@example.test',
      fromName: 'Colombo Wardrobe',
      useStartTls: true,
    });

    await expect(page.getByTestId('smtp-announcement')).toContainText('Email account saved');
    await expect(page.getByTestId('smtp-field-password')).toHaveValue('');

    await reopenSettings(page);
    const panel = page.getByTestId('smtp-panel');
    await expect(panel.getByLabel('Mail server')).toHaveValue('smtp.example.test');
    await expect(panel.getByLabel('Password')).toHaveValue('');
    await expect(panel.getByTestId('smtp-password-state')).toContainText('A password is saved.');

    // A second save with the password box empty does not send a password at all.
    const second = page.waitForResponse(
      (reply) => reply.url().endsWith('/api/admin/settings/email-smtp') && reply.request().method() === 'PUT',
    );
    await panel.getByLabel('Send from name').fill('Renamed');
    await page.getByTestId('save-smtp').click();
    expect('password' in ((await second).request().postDataJSON() as object)).toBe(false);
  });

  test('a server without a port and a sender is refused against those boxes', async ({ page }) => {
    await openSettings(page);

    await page.getByTestId('smtp-panel').getByLabel('Mail server').fill('smtp.example.test');
    await page.getByTestId('save-smtp').click();

    await expect(page.getByTestId('smtp-error-port')).toBeVisible();
    await expect(page.getByTestId('smtp-error-fromAddress')).toBeVisible();
    await expect(page.getByTestId('smtp-field-port')).toHaveAttribute('aria-invalid', 'true');
  });

  test('a user name with encryption switched off is refused against the switch', async ({ page }) => {
    await openSettings(page);
    await fillAccount(page);

    await page.getByTestId('smtp-panel').getByLabel('Encrypt the connection (STARTTLS)').uncheck();
    await page.getByTestId('save-smtp').click();

    await expect(page.getByTestId('smtp-error-useStartTls')).toContainText('Keep encryption on');
  });

  test('a server name with a scheme is refused against its box', async ({ page }) => {
    await openSettings(page);
    await fillAccount(page);

    await page.getByTestId('smtp-panel').getByLabel('Mail server').fill('https://smtp.example.test');
    await page.getByTestId('save-smtp').click();

    await expect(page.getByTestId('smtp-error-host')).toBeVisible();
  });

  test('send test email reports where it went, and a refusal in a plain sentence', async ({ page }) => {
    await openSettings(page);
    await fillAccount(page);

    await page.getByTestId('test-smtp').click();
    await expect(page.getByTestId('smtp-test-sent')).toContainText('catalogue@example.com');

    await page.request.get(`${adminURL}/__test__/smtp-test-outcome?outcome=refused`);

    await page.getByTestId('test-smtp').click();
    await expect(page.getByTestId('smtp-error')).toContainText('refused the user name or password');
    await expect(page.getByTestId('smtp-test-sent')).toHaveCount(0);
  });

  test('fits every breakpoint with no horizontal scroll and real tap targets', async ({ page }) => {
    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await openSettings(page);
      await page.getByTestId('smtp-panel').scrollIntoViewIfNeeded();

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px with the Email account panel`).toBeLessThanOrEqual(0);

      for (const selector of [...smtpFields, '[data-testid="save-smtp"]', '[data-testid="test-smtp"]']) {
        const rendered = await page.evaluate(
          (one) =>
            [...document.querySelectorAll<HTMLElement>(one)].filter((element) => element.getClientRects().length > 0)
              .length,
          selector,
        );

        expect(rendered, `nothing rendered matches "${selector}" at ${width}px`).toBeGreaterThan(0);

        if (!selector.includes('useStartTls')) {
          expect(await undersized(page, selector), `${selector} at ${width}px`).toEqual([]);
        }
      }
    }
  });

  test('has no serious or critical axe violation with an error and a test result on screen', async ({ page }) => {
    await openSettings(page);
    await page.getByTestId('smtp-panel').getByLabel('Mail server').fill('smtp.example.test');
    await page.getByTestId('save-smtp').click();
    await expect(page.getByTestId('smtp-error-port')).toBeVisible();

    const results = await new AxeBuilder({ page })
      .include('[data-testid="smtp-panel"]')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();

    const serious = results.violations.filter(
      (violation) => violation.impact === 'serious' || violation.impact === 'critical',
    );

    expect(serious.map((violation) => violation.id)).toEqual([]);
  });
});

/**
 * Issue #33: the product page's delivery and returns texts. The stub mirrors the endpoint — text or null, a
 * sanitiser no more permissive than the server's, the 4,000-character cap, the Content token — so the screen's
 * labels, its removed-markup notice and its field errors are exercised against what production answers.
 */
test.describe('product page texts (#33)', () => {
  test('both texts are on screen as seeded, labelled, and a save says what was removed', async ({ page }) => {
    await openSettings(page);

    const panel = page.locator('[data-testid="pdp-texts-panel"]');
    await expect(panel.getByRole('heading', { level: 2, name: 'Product page: delivery and returns' })).toBeVisible();

    const delivery = panel.getByLabel('Delivery text');
    const returns = panel.getByLabel('Returns text');
    await expect(delivery).toHaveValue(/We also deliver worldwide/);
    await expect(returns).toHaveValue(/within 14 days of delivery/);

    await delivery.fill('<p onclick="steal()">Two days in Colombo.</p><script>alert(1)</script>');
    await panel.locator('[data-testid="save-pdp-texts"]').click();

    await expect(page.locator('[data-testid="pdp-removed"]')).toContainText('<script>');
    await expect(page.locator('[data-testid="pdp-removed"]')).toContainText('onclick');
    await expect(delivery).toHaveValue('<p>Two days in Colombo.</p>');

    await reopenSettings(page);
    await expect(page.locator('[data-testid="pdp-texts-panel"]').getByLabel('Delivery text')).toHaveValue('<p>Two days in Colombo.</p>');
  });

  test('a text over the cap is refused on its own box', async ({ page }) => {
    await openSettings(page);

    const panel = page.locator('[data-testid="pdp-texts-panel"]');
    await panel.getByLabel('Returns text').fill(`<p>${'a'.repeat(4_000)}</p>`);
    await panel.locator('[data-testid="save-pdp-texts"]').click();

    await expect(page.locator('[data-testid="error-returnsInfoHtml"]')).toContainText('at most 4,000');
    await expect(panel.getByLabel('Returns text')).toHaveAttribute('aria-invalid', 'true');
  });

  test('the panel has no serious axe violations and fits every width', async ({ page }) => {
    await openSettings(page);

    const results = await new AxeBuilder({ page })
      .include('[data-testid="pdp-texts-panel"]')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await openSettings(page);
      await expect(page.locator('[data-testid="pdp-texts-panel"]')).toBeVisible();
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
      expect(await undersized(page, '[data-testid="save-pdp-texts"]')).toEqual([]);
    }
  });
});
