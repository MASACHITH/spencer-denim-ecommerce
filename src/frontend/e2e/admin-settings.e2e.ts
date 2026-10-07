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

    const headings = await page.locator('h1, h2').evaluateAll((nodes) =>
      nodes.map((node) => `${node.tagName.toLowerCase()}:${node.textContent?.trim()}`),
    );

    // W3-03 (PAY-01) adds the third panel: the bank details a shopper is told to transfer to. The list is
    // exhaustive on purpose — a panel appearing without a heading, or out of order, is the defect this
    // test exists for — so a new panel is expected to come back here and say so.
    expect(headings).toEqual([
      'h1:Settings',
      'h2:Logo',
      'h2:Contact and social',
      'h2:Bank transfer',
    ]);
  });

  test('every control is reachable and labelled', async ({ page }) => {
    await openSettings(page);

    // A name on every form control, which is what makes the screen usable without sight.
    for (const testId of ['logo-alt-input', 'logo-file', 'field-whatsApp', 'field-facebookUrl', 'field-instagramUrl', 'field-tikTokUrl']) {
      const name = await page
        .locator(`[data-testid="${testId}"]`)
        .evaluate((input: HTMLInputElement) => input.labels?.[0]?.textContent?.trim() ?? '');

      expect(name, `${testId} has no label`).not.toBe('');
    }
  });

  test('the save button is reachable by keyboard from the last field', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-tikTokUrl"]').focus();
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
    await page.locator('[data-testid="field-tikTokUrl"]').focus();
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
});
