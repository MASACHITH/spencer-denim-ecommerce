import { mkdir, open } from 'node:fs/promises';
import { join } from 'node:path';

import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * The ADM-10 product screens in a real browser (W1-03).
 *
 * **What is here rather than in a `.spec.ts`.** The unit suite runs in jsdom, which has no layout and no
 * media queries: it cannot see a variant matrix that overflows at 320 px, a 40 px tap target, or a focus
 * ring an ancestor is clipping. The brief names the matrix at 320 px as the hard case, and this is the only
 * place that can answer it. Everything that is only about which request a click makes lives in
 * `product-form-page.spec.ts`, where it is cheaper.
 *
 * The measurements come from `support/measure.ts` rather than from a copy, for the reason
 * `admin-catalog.e2e.ts` gives: a second copy is a second chance for one of them to drift back into
 * reading a declaration instead of the screen.
 */

/** Doc 04 §4's breakpoints — the list the whole project is measured at. */
const WIDTHS = [320, 375, 390, 768, 1024, 1280, 1440, 1920];

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
    { name: 'e2e_state', value: `p${testInfo.workerIndex}`, url: adminURL },
  ]);
  await page.request.get(`${adminURL}/__reset`);
});

async function openList(page: import('@playwright/test').Page): Promise<void> {
  await page.goto(`${adminURL}/admin/products`);
  await expect(page.getByRole('heading', { name: 'Products', level: 1 })).toBeVisible();
}

async function openProduct(page: import('@playwright/test').Page, id: number): Promise<void> {
  await page.goto(`${adminURL}/admin/products/${id}`);
  await expect(page.getByRole('heading', { name: 'Edit product' })).toBeVisible();
}

test.describe('the product list', () => {
  test('lists a product the shop is not showing, which the storefront listing could not', async ({ page }) => {
    await openList(page);

    await expect(page.getByTestId('product-301')).toContainText('Wide-Leg Rinse Jeans');
    await expect(page.getByTestId('state-301')).toHaveText(/on the shop/i);

    // The unpublished one. Its whole point: the storefront's own query drops it, so without this screen
    // there is no way to reach it and finish it.
    await expect(page.getByTestId('product-302')).toContainText('Linen Shirt Dress');
    await expect(page.getByTestId('state-302')).toHaveText(/not published/i);
  });

  test('searches by product code and by SKU, not only by name', async ({ page }) => {
    await openList(page);

    await page.getByTestId('filter-term').fill('SD-W-0301-IR-M');
    await page.getByTestId('apply-filters').click();

    await expect(page.getByTestId('product-301')).toBeVisible();
    await expect(page.getByTestId('product-302')).toHaveCount(0);
  });

  test('filters to the products that are not on the shop', async ({ page }) => {
    await openList(page);

    await page.getByTestId('filter-published').selectOption('unpublished');
    await page.getByTestId('apply-filters').click();

    await expect(page.getByTestId('product-302')).toBeVisible();
    await expect(page.getByTestId('product-301')).toHaveCount(0);
  });

  test('refuses to publish a product with no photograph and says why', async ({ page }) => {
    await openList(page);

    await page.getByTestId('toggle-302').click();

    await expect(page.getByTestId('page-error')).toContainText('gallery photograph');
    await expect(page.getByTestId('state-302')).toHaveText(/not published/i);
  });

  test('takes a published product off the shop and back on', async ({ page }) => {
    await openList(page);

    await page.getByTestId('toggle-301').click();
    await expect(page.getByTestId('state-301')).toHaveText(/not published/i);

    await page.getByTestId('toggle-301').click();
    await expect(page.getByTestId('state-301')).toHaveText(/on the shop/i);
  });

  /**
   * The ADM-22 defect in a real browser: `[disabled]` on a focused control makes the browser move focus to
   * `<body>`. jsdom will not reproduce that, so this is the only place the fix is actually proved.
   */
  test('keeps keyboard focus on the publish button across the write', async ({ page }) => {
    await openList(page);

    const toggle = page.getByTestId('toggle-301');
    await toggle.focus();
    await page.keyboard.press('Enter');

    await expect(page.getByTestId('state-301')).toHaveText(/not published/i);
    await expect(toggle).toBeFocused();
  });
});

test.describe('the product form', () => {
  test('fills every section of a saved product', async ({ page }) => {
    await openProduct(page, 301);

    await expect(page.getByTestId('field-name')).toHaveValue('Wide-Leg Rinse Jeans');
    await expect(page.getByTestId('field-slug')).toHaveValue('wide-leg-rinse-jeans');
    await expect(page.getByTestId('colour-list').locator('> li')).toHaveCount(2);
    await expect(page.getByTestId('variant-list').locator('> li')).toHaveCount(3);
    await expect(page.getByTestId('media-list').locator('> li')).toHaveCount(4);
    await expect(page.getByTestId('related-list').locator('> li')).toHaveCount(1);
  });

  /**
   * **The W1-12 defect, end to end.** A save that changes only the name must leave the four media rows
   * exactly where they were. The stub only rebuilds its gallery when the body carries a `media` field, so
   * a screen that always sent one would be caught here as well as in the unit suite.
   */
  test('a save that only renames the product keeps every photograph and the video', async ({ page }) => {
    await openProduct(page, 301);

    await page.getByTestId('field-name').fill('Wide-Leg Rinse Jeans (2026)');
    await page.getByTestId('save').click();

    await expect(page.getByTestId('announcement')).toHaveText(/changes saved/i);
    await expect(page.getByTestId('media-list').locator('> li')).toHaveCount(4);

    // And still four after a reload, which is the version the database would have.
    await page.reload();
    await expect(page.getByTestId('field-name')).toHaveValue('Wide-Leg Rinse Jeans (2026)');
    await expect(page.getByTestId('media-list').locator('> li')).toHaveCount(4);
  });

  test('removing a photograph and saving keeps the rest', async ({ page }) => {
    await openProduct(page, 301);

    await page.getByTestId('media-remove-0').click();
    await page.getByTestId('save').click();

    await expect(page.getByTestId('announcement')).toHaveText(/changes saved/i);

    await page.reload();
    await expect(page.getByTestId('media-list').locator('> li')).toHaveCount(3);
  });

  test('warns before moving a published product’s web address, then moves it', async ({ page }) => {
    await openProduct(page, 301);

    await page.getByTestId('field-slug').fill('wide-leg-rinse-jeans-2026');
    await page.getByTestId('save').click();

    await expect(page.getByTestId('page-error')).toContainText('page not found');
    await expect(page.getByTestId('confirm-slug')).toBeVisible();

    await page.getByTestId('confirm-slug').click();

    await expect(page.getByTestId('announcement')).toHaveText(/changes saved/i);
    await expect(page.getByTestId('field-slug')).toHaveValue('wide-leg-rinse-jeans-2026');
  });

  /**
   * Doc 08 §3.2 keeps an ordered variant for ever, and the API refuses the delete with 409
   * `variant_not_deletable`. **The form's answer is to make that refusal unreachable**: unticking the size
   * keeps the ordered row, switched off and still on screen, so the admin can see what happened and the
   * save goes through. A row that simply vanished would produce a 409 about a row that was no longer
   * there to explain it.
   *
   * The 409 itself is still covered — it is reachable from any client that does not do this, an older
   * build among them — by `ProductWriteHandlerTests` and by the real-SQL `AdminProductEndpointTests`.
   */
  test('unticking a size an order names switches that row off instead of deleting it', async ({ page }) => {
    await openProduct(page, 301);

    await expect(page.getByTestId('variant-list').locator('> li')).toHaveCount(3);
    await expect(page.getByTestId('variant-ordered-1')).toBeVisible();

    // The M size is the one the seeded order line points at.
    await page.getByTestId('size-82').uncheck();

    // Still three rows: the ordered one is kept, and it is now off.
    await expect(page.getByTestId('variant-list').locator('> li')).toHaveCount(3);
    const ordered = page.locator('[data-testid^="variant-active-"]').nth(2);
    await expect(ordered).not.toBeChecked();

    await page.getByTestId('save').click();

    await expect(page.getByTestId('announcement')).toHaveText(/changes saved/i);
    await expect(page.getByTestId('page-error')).toHaveCount(0);
  });

  /**
   * The second reason a size cannot be deleted: it holds stock. The seeded Jet Black S has none, so this
   * uses the Indigo Rinse S row, which the stub seeds with six on hand.
   */
  test('unticking a size that holds stock also switches it off rather than deleting it', async ({ page }) => {
    await openProduct(page, 301);

    await expect(page.getByTestId('variant-stocked-0')).toBeVisible();

    await page.getByTestId('size-81').uncheck();

    const skus = () =>
      page
        .getByTestId('variant-list')
        .locator('[data-testid^="variant-sku-"]')
        .evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));

    // Indigo Rinse S holds six, so it is kept and switched off. Jet Black S holds nothing and has never
    // been ordered, so it goes. (A Jet Black M row appears, because M is now the only ticked size and the
    // matrix is one row per colour and size — that is the generation working, not a leftover.)
    await expect.poll(skus).toContain('SD-W-0301-IR-S');
    expect(await skus()).not.toContain('SD-W-0301-JB-S');

    await page.getByTestId('save').click();

    await expect(page.getByTestId('announcement')).toHaveText(/changes saved/i);
    await expect(page.getByTestId('page-error')).toHaveCount(0);
  });

  test('generates the matrix from a colour and a tick, with a suggested SKU', async ({ page }) => {
    await openProduct(page, 302);

    await expect(page.getByTestId('variant-list').locator('> li')).toHaveCount(1);

    await page.getByTestId('size-82').check();

    await expect(page.getByTestId('variant-list').locator('> li')).toHaveCount(2);
    await expect(page.getByTestId('variant-sku-1')).toHaveValue('SD-W-0302-CHALK-M');
  });

  test('asks for an age group once the department is Kids', async ({ page }) => {
    await openProduct(page, 302);

    await expect(page.getByTestId('field-age-group')).toHaveCount(0);

    await page.getByTestId('field-department').selectOption({ label: 'Kids' });

    await expect(page.getByTestId('field-age-group')).toBeVisible();
  });

  test('creates a product, which arrives unpublished', async ({ page }) => {
    await page.goto(`${adminURL}/admin/products/new`);
    await expect(page.getByRole('heading', { name: 'Add a product' })).toBeVisible();

    await page.getByTestId('field-product-code').fill('SD-W-0999');
    await page.getByTestId('field-name').fill('E2E Denim Skirt');
    await page.getByTestId('field-base-price').fill('3990');
    await page.getByTestId('field-department').selectOption({ label: 'Women' });
    await page.getByTestId('field-primary-category').selectOption({ label: 'Jeans' });

    await expect(page.getByTestId('field-slug')).toHaveValue('e2e-denim-skirt');

    await page.getByTestId('save').click();

    await expect(page.getByRole('heading', { name: 'Edit product' })).toBeVisible();
    await expect(page.getByTestId('publish-blockers')).toContainText('gallery photograph');
  });

  test('uploads a photograph and a video through the media library', async ({ page }) => {
    await openProduct(page, 302);

    await page.getByTestId('upload-image').setInputFiles({
      name: 'front.png',
      mimeType: 'image/png',
      buffer: Buffer.from('not-a-real-png'),
    });

    await expect(page.getByTestId('media-list').locator('> li')).toHaveCount(1);

    await page.getByTestId('upload-video').setInputFiles({
      name: 'clip.mp4',
      mimeType: 'video/mp4',
      buffer: Buffer.from('not-a-real-mp4'),
    });

    await expect(page.getByTestId('upload-poster')).toBeVisible();

    await page.getByTestId('upload-poster').setInputFiles({
      name: 'poster.png',
      mimeType: 'image/png',
      buffer: Buffer.from('not-a-real-png'),
    });

    await expect(page.getByTestId('media-list').locator('> li')).toHaveCount(2);
    await expect(page.getByTestId('announcement')).toHaveText(/video added/i);
  });

  /**
   * The file is written to disk rather than passed as a buffer: Playwright caps an in-memory
   * `setInputFiles` payload at 50 MB, which is the very boundary this test is about, so a buffer would
   * fail in the harness instead of in the screen.
   */
  test('refuses a video over the library’s 50 MB ceiling before uploading it', async ({ page }, testInfo) => {
    const huge = join(testInfo.outputDir, 'huge.mp4');
    await mkdir(testInfo.outputDir, { recursive: true });
    const handle = await open(huge, 'w');
    await handle.truncate(51 * 1024 * 1024);
    await handle.close();

    await openProduct(page, 302);

    await page.getByTestId('upload-video').setInputFiles(huge);

    await expect(page.getByTestId('media-error')).toContainText('50 MB');
    await expect(page.getByTestId('upload-poster')).toHaveCount(0);
  });
});

test.describe('moving rows with the buttons', () => {
  /**
   * Not drag and drop: the owner's decision of 2026-10-02 for ADM-11, carried over. A pointer-only
   * reorder has to be built twice to be usable at all, so there is one mechanism that works for everybody.
   */
  test('moves a colour with the buttons and keeps focus on the one pressed', async ({ page }) => {
    await openProduct(page, 301);

    const names = () =>
      page
        .getByTestId('colour-list')
        .locator('[data-testid^="colour-name-"]')
        .evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));

    expect(await names()).toEqual(['Indigo Rinse', 'Jet Black']);

    const down = page.getByTestId('colour-down-0');
    await down.focus();
    await page.keyboard.press('Enter');

    expect(await names()).toEqual(['Jet Black', 'Indigo Rinse']);
    await expect(page.getByTestId('colour-down-1')).toBeFocused();
    await expect(page.getByTestId('announcement')).toContainText('position 2 of 2');
  });

  test('Alt + arrow moves a row from either button of the pair', async ({ page }) => {
    await openProduct(page, 301);

    await page.getByTestId('colour-up-1').focus();
    await page.keyboard.press('Alt+ArrowUp');

    const names = await page
      .getByTestId('colour-list')
      .locator('[data-testid^="colour-name-"]')
      .evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value));

    expect(names).toEqual(['Jet Black', 'Indigo Rinse']);
  });

  /**
   * The control says `aria-disabled`, not `disabled`, and the difference is the ADM-22 defect: the native
   * attribute takes the control out of the tab order and makes the browser move focus to `<body>` when the
   * element that had focus is the one being disabled.
   *
   * The assertion is on the DOM property rather than on Playwright's `toBeDisabled`, which treats
   * `aria-disabled="true"` as disabled too — correctly, for actionability. What has to be true here is
   * narrower and is exactly what `disabled` would break: the button is still focusable.
   */
  test('marks a move button unavailable at the end of its list without disabling it', async ({ page }) => {
    await openProduct(page, 301);

    const up = page.getByTestId('colour-up-0');

    await expect(up).toHaveAttribute('aria-disabled', 'true');
    expect(await up.evaluate((element) => (element as HTMLButtonElement).disabled)).toBe(false);

    await up.focus();
    await expect(up).toBeFocused();
  });
});

test.describe('layout', () => {
  /** The brief's hard case: a colour × size matrix on a phone. */
  for (const width of WIDTHS) {
    test(`the product form has no horizontal overflow at ${width} px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await openProduct(page, 301);

      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  for (const width of WIDTHS) {
    test(`the product list has no horizontal overflow at ${width} px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await openList(page);

      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  test('every control on the form is at least 44 px at 320 px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openProduct(page, 301);

    // The sweep is guarded: a selector that matched nothing would pass, which is the failure mode of
    // every measurement test that enumerates by selector.
    const buttons = await page.locator('.form button').count();
    expect(buttons).toBeGreaterThan(20);

    expect(await undersized(page, '.form button')).toEqual([]);
    expect(
      await undersized(page, '.form input:not([type="checkbox"]), .form select, .form textarea'),
    ).toEqual([]);

    // A checkbox is measured through the label that wraps it, because that is what a tap lands on.
    expect(await undersized(page, '.form .adm-check')).toEqual([]);
  });

  test('every control on the list is at least 44 px at 320 px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openList(page);

    expect(await page.locator('.products button, .products a.adm-btn').count()).toBeGreaterThan(5);

    expect(await undersized(page, '.products button')).toEqual([]);
    expect(await undersized(page, '.products input, .products select')).toEqual([]);
  });

  test('a focused move button shows a focus ring nothing is clipping', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openProduct(page, 301);

    const control = page.getByTestId('colour-down-0');
    await control.focus();

    expect(await edgesWithoutRing(page, control)).toEqual([]);
  });
});

test.describe('accessibility', () => {
  /**
   * The gate `docs/06` §3 states: serious and critical only. The same filter
   * `accessibility.e2e.ts` applies, kept here rather than imported so this feature's screens are scanned
   * from this feature's file — and so adding them does not mean editing a file another branch is also
   * editing.
   *
   * **What axe cannot see, and is therefore measured above instead:** a tap target under 44 px is not an
   * axe rule at this level (the size ticks were 36 px and axe said nothing), and a focus ring an ancestor
   * clips away computes as present. Both are in the `layout` block.
   */
  async function violations(page: import('@playwright/test').Page) {
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();

    return results.violations
      .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
      .map((violation) => ({
        id: violation.id,
        impact: violation.impact,
        help: violation.help,
        nodes: violation.nodes.map((node) => node.target.join(' ')),
      }));
  }

  test('the product list has no serious or critical violation', async ({ page }) => {
    await openList(page);

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  test('the product form has no serious or critical violation', async ({ page }) => {
    await openProduct(page, 301);

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  test('the product form has no serious or critical violation at 320 px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await openProduct(page, 301);

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /** The states a shopper never sees but an admin does: the refusal panels and the confirmations. */
  test('the slug confirmation and the remove confirmation have no serious or critical violation', async ({
    page,
  }) => {
    await openProduct(page, 301);

    await page.getByTestId('field-slug').fill('wide-leg-rinse-jeans-2026');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('confirm-slug')).toBeVisible();

    await page.getByTestId('cancel-slug').click();
    await page.getByTestId('remove').click();
    await expect(page.getByTestId('confirm-remove')).toBeVisible();

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });
});
