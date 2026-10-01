import { expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * The ADM-11 departments-and-categories screen in a real browser (W1-12).
 *
 * **What is here rather than in a `.spec.ts`.** The unit suite runs in jsdom, which has no layout and no
 * media queries: it cannot see a row of buttons that overflows at 320 px, a 40 px tap target, or a focus
 * ring an ancestor is clipping. Everything that depends on real CSS belongs here, and everything that is
 * only about which request a click makes belongs in `category-tree.spec.ts`, where it is cheaper.
 *
 * The measurements come from `support/measure.ts` rather than from a copy: `edgesWithoutRing` in particular
 * exists because an earlier assertion about a focus ring could not fail, and a second copy is a second
 * chance for one of them to drift back into reading a declaration instead of the screen.
 */

/** Doc 04 §4's breakpoints — the list the whole project is measured at. */
const WIDTHS = [320, 375, 390, 768, 1024, 1280, 1440, 1920];

/**
 * Gives this test its own copy of the stub's tree and opens the screen.
 *
 * The cookie is what scopes the state: the stub is one process serving every worker's browser, and these
 * tests reorder and remove things, so without it one worker's reset wiped another's tree mid-test. It is
 * set through `page.request`, which shares the page's cookie jar, so the reset reaches the same state the
 * page will.
 */
/**
 * Before anything else: is this our own server?
 *
 * The port is derived from the worktree path, which makes a collision unlikely rather than impossible —
 * and with `reuseExistingServer` a collision means another worktree's server answering, serving another
 * worktree's `dist/admin` against these tests. Every assertion below would then be about somebody else's
 * build, which is how this branch's run of 113 failures looked before it was understood. One request makes
 * that impossible to mistake for a test failure.
 */
test.beforeAll(async ({ request }) => {
  const identity = await (await request.get(`${adminURL}/__worktree`)).json();

  expect(
    identity.root,
    `the server on ${adminURL} is serving ${identity.root}, not this worktree — another worktree's ` +
      'Playwright server is holding the port',
  ).toBe(adminWorktreeRoot);
});

test.beforeEach(async ({ page }, testInfo) => {
  await page.context().addCookies([
    { name: 'e2e_state', value: `w${testInfo.workerIndex}`, url: adminURL },
  ]);
  await page.request.get(`${adminURL}/__reset`);

  await page.goto(`${adminURL}/admin/catalog`);
  await expect(page.getByRole('heading', { name: 'Departments and categories' })).toBeVisible();
});

test.describe('the catalogue structure screen', () => {
  test('lists the departments, including one the shop is not showing', async ({ page }) => {
    await expect(page.getByTestId('department-1')).toContainText('Women');
    await expect(page.getByTestId('department-3')).toContainText('Kids');

    // The storefront's own reader would have dropped Kids entirely; this screen has to show it as hidden,
    // or there would be no way to switch it back on.
    await expect(page.getByTestId('department-3')).toContainText('Hidden');
    await expect(page.getByTestId('dept-toggle-3')).toHaveText(/show/i);
  });

  test('opens the first department’s tree with its levels and counts', async ({ page }) => {
    await expect(page.getByRole('heading', { name: 'Categories in Women' })).toBeVisible();

    await expect(page.getByTestId('category-10')).toContainText('Dresses');
    await expect(page.getByTestId('category-11')).toContainText('Midi dresses');
    await expect(page.getByTestId('category-11')).toContainText('level 2, under Dresses');
    await expect(page.getByTestId('category-10')).toContainText('6 products');

    // A deactivated category is shown, marked, with the button that would bring it back.
    await expect(page.getByTestId('category-30')).toContainText('Hidden');
  });

  test('switches to another department’s tree', async ({ page }) => {
    await page.getByTestId('select-2').click();

    await expect(page.getByRole('heading', { name: 'Categories in Men' })).toBeVisible();
    await expect(page.getByTestId('category-50')).toContainText('Shirts');
  });
});

test.describe('reordering', () => {
  /**
   * The interaction this screen was designed around. Not drag-and-drop: a pointer-only reorder has to be
   * built twice to be usable at all, so there is one mechanism that works for everybody.
   */
  test('moves a category with the buttons and saves the whole list once', async ({ page }) => {
    const order = () => page.locator('li.tree__item').evaluateAll((items) =>
      items.map((item) => item.getAttribute('data-testid')),
    );

    expect(await order()).toEqual([
      'category-10',
      'category-11',
      'category-12',
      'category-20',
      'category-30',
      'category-40',
    ]);

    await page.getByTestId('move-down-10').click();

    // Staged, not sent: the warning is the proof, and the subtree moved with its parent.
    await expect(page.getByTestId('order-pending')).toContainText('1 list has a new order');
    expect(await order()).toEqual([
      'category-20',
      'category-10',
      'category-11',
      'category-12',
      'category-30',
      'category-40',
    ]);

    const [saved] = await Promise.all([
      page.waitForResponse(
        (response) =>
          response.url().endsWith('/api/admin/catalog/categories/order') &&
          response.request().method() === 'PUT',
      ),
      page.getByTestId('save-order').click(),
    ]);

    expect(saved.status()).toBe(204);
    expect(saved.request().postDataJSON()).toEqual({
      departmentId: 1,
      parentCategoryId: null,
      orderedCategoryIds: [20, 10, 30, 40],
    });

    await expect(page.getByTestId('order-pending')).toHaveCount(0);

    // And the tree the server now holds is what is drawn, not the staged copy.
    expect(await order()).toEqual([
      'category-20',
      'category-10',
      'category-11',
      'category-12',
      'category-30',
      'category-40',
    ]);
  });

  /**
   * Keyboard-only, measured rather than claimed: Tab to the control, Enter to move, and Alt + arrow to keep
   * going without ever touching a pointer. The focus assertions are the point — a move that loses focus is
   * a move a keyboard user cannot repeat.
   */
  test('reorders with the keyboard alone, keeping focus on the row that moved', async ({ page }) => {
    await page.getByTestId('move-down-10').focus();
    await expect(page.getByTestId('move-down-10')).toBeFocused();

    await page.keyboard.press('Enter');
    await expect(page.getByTestId('order-pending')).toBeVisible();
    await expect(page.getByTestId('move-down-10')).toBeFocused();

    // Alt + ArrowDown from the same control, without re-Tabbing anywhere.
    await page.keyboard.press('Alt+ArrowDown');
    expect(
      await page
        .locator('li.tree__item')
        .evaluateAll((items) => items.map((item) => item.getAttribute('data-testid'))),
    ).toEqual([
      'category-20',
      'category-30',
      'category-10',
      'category-11',
      'category-12',
      'category-40',
    ]);
    await expect(page.getByTestId('move-down-10')).toBeFocused();

    // Alt + ArrowUp puts it back, from the same control.
    await page.keyboard.press('Alt+ArrowUp');
    await expect(page.getByTestId('move-down-10')).toBeFocused();

    // And the Save order button is reachable by Tab from here, so the whole job is keyboard-only.
    await page.keyboard.press('Enter');
    await page.getByTestId('save-order').focus();
    const [saved] = await Promise.all([
      page.waitForResponse((response) =>
        response.url().endsWith('/api/admin/catalog/categories/order'),
      ),
      page.keyboard.press('Enter'),
    ]);

    expect(saved.status()).toBe(204);
  });

  test('moves focus to the other direction when a button runs out of list', async ({ page }) => {
    await page.getByTestId('move-up-20').focus();
    await page.keyboard.press('Enter');

    await expect(page.getByTestId('move-up-20')).toBeDisabled();
    await expect(page.getByTestId('move-down-20')).toBeFocused();
  });

  test('a move button at the end of its list is disabled rather than silently doing nothing', async ({
    page,
  }) => {
    await expect(page.getByTestId('move-up-10')).toBeDisabled();
    await expect(page.getByTestId('move-down-40')).toBeDisabled();

    // Siblings only: "up" from the first child must not promote it a level.
    await expect(page.getByTestId('move-up-11')).toBeDisabled();
    await expect(page.getByTestId('move-down-12')).toBeDisabled();
  });

  /**
   * Discarding is right — a category reload can add or remove nodes, so reapplying a stale arrangement
   * would send a list the server refuses. Doing it in silence was not: the banner and the Save order button
   * vanished with no word, and the live region spoke only about the other change.
   */
  test('says so, visibly and in the live region, when a reload discards a staged order', async ({
    page,
  }) => {
    await page.getByTestId('move-down-10').click();
    await expect(page.getByTestId('order-pending')).toBeVisible();

    // An unrelated change, which reloads the tree underneath the staged arrangement.
    await page.getByTestId('toggle-30').click();
    await page.getByTestId('confirm-yes').click();

    const notice = page.getByTestId('order-discarded');
    await expect(notice).toBeVisible();
    await expect(notice).toHaveAttribute('role', 'status');
    await expect(notice).toContainText('order you had arranged was discarded');
    await expect(notice).toContainText('Nothing else was lost');

    // The staged banner and its Save order button are gone, which is what used to happen in silence.
    await expect(page.getByTestId('order-pending')).toHaveCount(0);

    // And the live region says the same thing rather than only naming the other change.
    await expect(page.getByTestId('tree-announcement')).toContainText(
      'order you had arranged was discarded',
    );

    // The tree is the server's, not the arrangement that was thrown away.
    expect(
      await page
        .locator('li.tree__item')
        .evaluateAll((items) => items.map((item) => item.getAttribute('data-testid'))),
    ).toEqual([
      'category-10',
      'category-11',
      'category-12',
      'category-20',
      'category-30',
      'category-40',
    ]);

    await page.getByTestId('dismiss-discarded').click();
    await expect(notice).toHaveCount(0);
  });

  test('discards a staged order without sending anything', async ({ page }) => {
    let sent = 0;
    page.on('request', (request) => {
      if (request.url().endsWith('/categories/order')) {
        sent++;
      }
    });

    await page.getByTestId('move-down-10').click();
    await expect(page.getByTestId('order-pending')).toBeVisible();

    await page.getByTestId('order-pending').getByRole('button', { name: 'Discard' }).click();

    await expect(page.getByTestId('order-pending')).toHaveCount(0);
    expect(sent).toBe(0);
  });

  test('reorders the departments with the keyboard, keeping focus on the row', async ({ page }) => {
    await page.getByTestId('dept-down-1').focus();
    await page.keyboard.press('Enter');

    await expect(page.getByTestId('dept-order-pending')).toBeVisible();
    await expect(page.getByTestId('dept-down-1')).toBeFocused();

    await page.keyboard.press('Alt+ArrowDown');
    await expect(page.getByTestId('dept-down-1')).toBeDisabled();
    await expect(page.getByTestId('dept-up-1')).toBeFocused();
  });

  test('reorders the departments too', async ({ page }) => {
    await page.getByTestId('dept-down-1').click();
    await expect(page.getByTestId('dept-order-pending')).toBeVisible();

    const [saved] = await Promise.all([
      page.waitForResponse((response) =>
        response.url().endsWith('/api/admin/catalog/departments/order'),
      ),
      page.getByTestId('save-dept-order').click(),
    ]);

    expect(saved.request().postDataJSON()).toEqual({ orderedDepartmentIds: [2, 1, 3] });
  });
});

test.describe('editing a department', () => {
  /**
   * The owner made the name and the URL read-only (2026-10-02, confirming doc 11 P1 step 1): a department
   * slug is the first segment of every listing URL in its department and redirects are deferred.
   */
  test('offers no name and no URL to type into', async ({ page }) => {
    await page.getByTestId('dept-edit-1').click();
    await expect(page.getByTestId('dept-field-description')).toBeVisible();

    await expect(page.getByTestId('dept-field-name')).toHaveCount(0);
    await expect(page.getByTestId('dept-field-slug')).toHaveCount(0);
    await expect(page.getByRole('form').or(page.locator('.depts__form'))).toContainText(
      'The name and the URL of a department are fixed',
    );
  });

  /**
   * The fix-round-1 blocker, in a browser: the form has no hero-image picker, so it has to carry the value
   * it loaded. A `PUT` replaces the row, so a body that omits the field tells the API to clear it — and the
   * audit row would record a member of staff wiping an image they never touched.
   */
  test('a save carries the hero image it has no picker for', async ({ page }) => {
    await page.getByTestId('dept-edit-1').click();
    await page.getByTestId('dept-field-description').fill('Edited by the browser suite.');

    const [saved] = await Promise.all([
      page.waitForResponse(
        (response) =>
          response.url().endsWith('/api/admin/catalog/departments/1') &&
          response.request().method() === 'PUT',
      ),
      page.getByTestId('dept-save').click(),
    ]);

    expect(saved.status()).toBe(204);

    const body = saved.request().postDataJSON();
    expect(body.heroImageAssetId).toBe(41);
    expect(body.description).toBe('Edited by the browser suite.');

    // And nothing in the body can move the two read-only columns.
    expect(body).not.toHaveProperty('name');
    expect(body).not.toHaveProperty('slug');
  });
});

test.describe('hiding and removing', () => {
  /**
   * Doc 08 §7.1: `IsActive` is the one switch that takes a section off the shop, so the numbers appear
   * before the button that does it — the brief's "do not let them discover it afterwards".
   */
  test('says what hiding a category will do before it is confirmed', async ({ page }) => {
    await page.getByTestId('toggle-10').click();

    const panel = page.getByTestId('confirm-panel');
    await expect(panel).toContainText('Hide “Dresses” from the shop?');
    await expect(panel).toContainText('6');
    await expect(panel).toContainText('will disappear from listings');
    await expect(panel).toContainText('2 categories');
    await expect(panel).toContainText('drop out of the mega menu');
    await expect(panel).toContainText('5');
    await expect(panel).toContainText('no menu route left to them');
    await expect(panel).toContainText('Linen shift dress');
  });

  test('hides a category only after the confirmation', async ({ page }) => {
    let sent = 0;
    page.on('request', (request) => {
      if (request.url().includes('/active')) {
        sent++;
      }
    });

    await page.getByTestId('toggle-10').click();
    await expect(page.getByTestId('confirm-panel')).toBeVisible();
    expect(sent).toBe(0);

    await page.getByTestId('confirm-panel').getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByTestId('confirm-panel')).toHaveCount(0);
    expect(sent).toBe(0);

    await page.getByTestId('toggle-10').click();
    await expect(page.getByTestId('confirm-panel')).toContainText('will disappear');
    await page.getByTestId('confirm-yes').click();

    await expect(page.getByTestId('category-10')).toContainText('Hidden');
    expect(sent).toBe(1);
  });

  /** A guard, never a cascade, and the refusal has to name what is in the way. */
  test('refuses to remove a category that still has products or children, and says why', async ({
    page,
  }) => {
    await page.getByTestId('remove-10').click();
    await page.getByTestId('confirm-yes').click();

    const error = page.getByTestId('tree-error');
    await expect(error).toContainText('2 categories underneath it');
    await expect(error).toContainText('6 products in it');

    // Still there.
    await expect(page.getByTestId('category-10')).toBeVisible();
  });

  test('removes a category nothing points at', async ({ page }) => {
    await page.getByTestId('remove-40').click();
    await expect(page.getByTestId('confirm-panel')).toContainText('Remove “Knitwear”?');
    await page.getByTestId('confirm-yes').click();

    await expect(page.getByTestId('category-40')).toHaveCount(0);
  });
});

test.describe('creating and editing', () => {
  test('creates a category and shows it in the tree', async ({ page }) => {
    await page.getByTestId('add-top-level').click();

    await page.getByTestId('field-name').fill('Linen shirting');
    await page.getByTestId('field-slug').fill('linen-shirting');
    await page.getByTestId('save-category').click();

    await expect(page.getByText('Linen shirting')).toBeVisible();
  });

  /** A rejected slug has to land on the slug box, not in a page-wide banner. */
  test('shows a rejected slug against the slug box', async ({ page }) => {
    await page.getByTestId('add-top-level').click();

    await page.getByTestId('field-name').fill('Linen shirting');
    await page.getByTestId('field-slug').fill('Linen Shirting');
    await page.getByTestId('save-category').click();

    await expect(page.getByTestId('slug-error')).toContainText('lower-case');
    await expect(page.getByTestId('field-slug')).toHaveAttribute('aria-invalid', 'true');
  });

  /**
   * The largest thing this screen does, and until fix round 1 it had no browser coverage at all: the stub's
   * `PUT /categories/{id}` wrote only the name and the slug and ignored `parentCategoryId`.
   */
  test('moves a category to another parent and re-paths its subtree', async ({ page }) => {
    const levels = () =>
      page
        .locator('li.tree__item')
        .evaluateAll((items) =>
          items.map((item) => `${item.getAttribute('data-testid')}@${item.getAttribute('aria-level')}`),
        );

    expect(await levels()).toEqual([
      'category-10@1',
      'category-11@2',
      'category-12@2',
      'category-20@1',
      'category-30@1',
      'category-40@1',
    ]);

    await page.getByTestId('edit-11').click();
    await page.getByTestId('field-parent').selectOption({ label: 'Jeans' });

    const [saved] = await Promise.all([
      page.waitForResponse(
        (response) =>
          response.url().endsWith('/api/admin/catalog/categories/11') &&
          response.request().method() === 'PUT',
      ),
      page.getByTestId('save-category').click(),
    ]);

    expect(saved.status()).toBe(204);
    expect(saved.request().postDataJSON().parentCategoryId).toBe(20);

    // Midi dresses is now under Jeans, at level 2, and Dresses has one child left.
    await expect(page.getByTestId('category-11')).toContainText('under Jeans');
    expect(await levels()).toEqual([
      'category-10@1',
      'category-12@2',
      'category-20@1',
      'category-11@2',
      'category-30@1',
      'category-40@1',
    ]);
  });

  /**
   * The screen's own writes carry the antiforgery header and the row version, and these two prove it by
   * taking one away from the request the screen really makes.
   *
   * An earlier version issued a raw `fetch` from inside the page, which proved only that the stub refuses —
   * it would have passed unchanged if the client had stopped sending either header, which is exactly what
   * its own comment claimed it prevented. Intercepting the screen's request instead keeps the whole chain
   * in the test: the client builds it, the server refuses it, and the screen has to say so.
   */
  test('the screen surfaces the refusal when its own antiforgery header is stripped', async ({ page }) => {
    await page.route('**/api/admin/catalog/categories/order', async (route) => {
      const headers = { ...route.request().headers() };
      expect(headers['x-xsrf-token']).toBeTruthy();
      delete headers['x-xsrf-token'];

      await route.continue({ headers });
    });

    await page.getByTestId('move-down-10').click();
    await page.getByTestId('save-order').click();

    await expect(page.getByTestId('tree-error')).toContainText('antiforgery token');

    // And nothing was applied: the staged warning is still there to try again from.
    await expect(page.getByTestId('order-pending')).toBeVisible();
  });

  test('the screen surfaces the refusal when its own row version is stale', async ({ page }) => {
    await page.route('**/api/admin/catalog/categories/10/active', async (route) => {
      const body = route.request().postDataJSON();
      expect(body.rowVersion).toBeTruthy();

      await route.continue({ postData: JSON.stringify({ ...body, rowVersion: 'AAAAAAAAAAA=' }) });
    });

    await page.getByTestId('toggle-10').click();
    await expect(page.getByTestId('confirm-panel')).toContainText('will disappear from listings');
    await page.getByTestId('confirm-yes').click();

    const error = page.getByTestId('tree-error');
    await expect(error).toContainText('Somebody else changed this');
    await expect(error.getByRole('button', { name: /reload/i })).toBeVisible();

    // The category is still live: a refused write changed nothing.
    await expect(page.getByTestId('category-10')).toContainText('Live');
  });

  test('the parent list never offers a category its own descendant', async ({ page }) => {
    await page.getByTestId('edit-10').click();

    const options = await page
      .getByTestId('field-parent')
      .locator('option')
      .evaluateAll((items) => items.map((item) => item.textContent?.trim()));

    expect(options).toContain('Top level');
    expect(options).not.toContain('— Midi dresses');
    expect(options).not.toContain('Dresses');
  });
});

test.describe('layout', () => {
  /**
   * No horizontal page scroll at any of doc 04 §4's widths, measured with `scrollWidth` against
   * `clientWidth` — `innerWidth` includes the scrollbar and hides exactly the overflow this is looking for.
   */
  for (const width of WIDTHS) {
    test(`${width}px: the page does not scroll sideways`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.getByTestId('category-10')).toBeVisible();

      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  /**
   * And it still does not with the widest arrangement the screen can actually reach.
   *
   * The first version of this claimed "every panel open at once", which is not reachable: opening a
   * confirmation closes the edit form and opening the edit form closes the confirmation — `startEdit` and
   * `startToggle` each clear the other. So there are two reachable worst cases, and both are measured: the
   * staged-order warning with the edit form open (the widest form row plus the warning), and the
   * staged-order warning with a deactivation panel open (the longest prose on the page).
   */
  for (const width of [320, 375, 390, 768]) {
    test(`${width}px: it does not scroll sideways with the staged warning and the edit form open`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 900 });

      await page.getByTestId('move-down-10').click();
      await page.getByTestId('edit-20').click();
      await expect(page.getByTestId('field-slug')).toBeVisible();

      expect(await horizontalOverflow(page)).toBe(0);
    });

    test(`${width}px: it does not scroll sideways with the staged warning and a deactivation panel open`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 900 });

      await page.getByTestId('move-down-10').click();
      await page.getByTestId('toggle-10').click();
      await expect(page.getByTestId('confirm-panel')).toContainText('will disappear from listings');

      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  /**
   * Doc 04 §4: touch targets of at least 44 px. The reorder buttons are the smallest on the page.
   *
   * The counts are asserted first, and that is not padding: `undersized` returns an empty list both when
   * every control passes and when the selector matched nothing at all, so without them a renamed class
   * would turn this into a test that cannot fail — the project's own lesson L4.
   */
  test('every control meets the 44 px touch minimum on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await expect(page.getByTestId('category-10')).toBeVisible();

    // Open a form as well, so the inputs are on the page at all.
    await page.getByTestId('edit-10').click();
    await expect(page.getByTestId('field-slug')).toBeVisible();

    const buttons = await page.locator('.catalog button').count();
    const fields = await page
      .locator('.catalog input:not([type="checkbox"]), .catalog select, .catalog textarea')
      .count();
    const checkboxLabels = await page.locator('.catalog .adm-check').count();

    expect(buttons).toBeGreaterThan(30);
    expect(fields).toBeGreaterThan(5);
    expect(checkboxLabels).toBeGreaterThan(0);

    expect(await undersized(page, '.catalog button')).toEqual([]);
    expect(
      await undersized(page, '.catalog input:not([type="checkbox"]), .catalog select, .catalog textarea'),
    ).toEqual([]);

    // A checkbox is measured through the label that wraps it, because that is what a tap lands on: the box
    // itself is 20 px and the label carrying it is the target. Measuring the bare input would report a
    // failure the screen does not have; leaving checkboxes out silently would hide one it might.
    expect(await undersized(page, '.catalog .adm-check')).toEqual([]);
  });

  /**
   * The focus ring has to reach the screen, which is a question about pixels rather than about the
   * stylesheet: `getComputedStyle(...).outlineWidth` reads the outline an element *would* paint and is
   * structurally blind to an ancestor clipping it away.
   */
  test('the reorder buttons really paint a focus ring on all four edges', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(page.getByTestId('category-10')).toBeVisible();

    const control = page.getByTestId('move-down-10');
    await control.focus();

    expect(await edgesWithoutRing(page, control)).toEqual([]);
  });

  test('the whole screen is reachable with the keyboard from the top of the page', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(page.getByTestId('category-10')).toBeVisible();

    // Start from the top of the document, so what is measured is a walk from the beginning of the page and
    // not from wherever a previous action happened to leave focus.
    //
    // There was an assertion here that `document.body.contains(document.activeElement)` — which is true on
    // any page, including a blank one, because `document.body` contains `document.body`. It is gone; the
    // loop below is the measurement, and the throw at the end is what makes it able to fail.
    await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());

    for (let step = 0; step < 60; step++) {
      await page.keyboard.press('Tab');
      const id = await page.evaluate(() => document.activeElement?.getAttribute('data-testid') ?? null);
      if (id === 'move-down-10') {
        return;
      }
    }

    throw new Error('the move button was not reachable by Tab within 60 steps');
  });

  test('nothing is logged to the console and no request fails', async ({ page }) => {
    // The page `beforeEach` opened has to be finished with before this one navigates again, or its own
    // in-flight requests are cancelled by the navigation and arrive here as failures. That is what made
    // this test fail about one full-suite run in five while passing every time on its own.
    await expect(page.getByTestId('category-10')).toBeVisible();
    await page.waitForLoadState('networkidle');

    const problems: string[] = [];

    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        problems.push(`${message.type()}: ${message.text()}`);
      }
    });

    page.on('requestfailed', (request) => {
      // A request a navigation cancelled is not a defect, and it is the one failure this test can provoke
      // itself. Anything else — a refused connection, a DNS failure, a reset — still counts.
      const reason = request.failure()?.errorText ?? 'unknown';
      if (reason !== 'net::ERR_ABORTED') {
        problems.push(`failed: ${request.url()} — ${reason}`);
      }
    });

    page.on('response', (response) => {
      if (response.status() >= 400) {
        problems.push(`${response.status()}: ${response.url()}`);
      }
    });

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`${adminURL}/admin/catalog`);
    await expect(page.getByTestId('category-10')).toBeVisible();

    await page.getByTestId('select-2').click();
    await expect(page.getByTestId('category-50')).toBeVisible();

    expect(problems).toEqual([]);
  });
});
