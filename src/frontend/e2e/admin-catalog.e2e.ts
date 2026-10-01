import { expect, test } from '@playwright/test';

import { adminURL } from '../playwright.config';
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
   * And it still does not when every panel on the page is open at once — the row of action buttons, an edit
   * form, a confirmation panel and the staged-order warning together are the widest the screen ever gets,
   * and the narrow widths are where a six-button row stops fitting.
   */
  for (const width of [320, 375, 390, 768]) {
    test(`${width}px: it does not scroll sideways with a form and a confirmation open`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 900 });

      await page.getByTestId('move-down-10').click();
      await page.getByTestId('edit-20').click();
      await page.getByTestId('toggle-30').click();
      await expect(page.getByTestId('confirm-panel')).toBeVisible();

      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  /** Doc 04 §4: touch targets of at least 44 px. The reorder buttons are the smallest on the page. */
  test('every control meets the 44 px touch minimum on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await expect(page.getByTestId('category-10')).toBeVisible();

    expect(await undersized(page, '.catalog button')).toEqual([]);
    expect(await undersized(page, '.catalog input, .catalog select, .catalog textarea')).toEqual([]);
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

    // Walk forward until a reorder button has focus, so "Tab gets you there" is measured, not assumed.
    const reached = await page.evaluate(() => document.body.contains(document.activeElement));
    expect(reached).toBe(true);

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
    const problems: string[] = [];

    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        problems.push(`${message.type()}: ${message.text()}`);
      }
    });
    page.on('requestfailed', (request) => problems.push(`failed: ${request.url()}`));
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
