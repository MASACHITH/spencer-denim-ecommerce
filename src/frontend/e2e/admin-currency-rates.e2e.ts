import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { adminURL } from '../playwright.config';
import { BREAKPOINTS, ownState, stubSwitch, trustOnlyThisWorktree } from './support/admin-content-e2e';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * Issue #248: the "Display currencies" panel on the admin settings screen, in a browser.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, whose display-currency save follows
 * `UpdateCurrencyRatesCommandHandler` (and is held to it by `admin-currency-stub-parity.e2e.ts` and the shared table
 * `support/currency-rate-cases.json`). The endpoint itself is proved against a real SQL Server in
 * `AdminCurrencyRatesTests`; what this file proves is the screen.
 */
trustOnlyThisWorktree();

interface Case {
  readonly value: string;
  readonly problem: 'none' | 'notANumber' | 'tooManyDecimals' | 'outOfRange';
}

const cases = (
  JSON.parse(readFileSync(resolve(__dirname, 'support', 'currency-rate-cases.json'), 'utf8')) as { cases: Case[] }
).cases;

const SELECTORS = {
  panel: '[data-testid="currency-panel"]',
  save: '[data-testid="save-currency"]',
  saved: '[data-testid="currency-saved"]',
  offered: '[data-testid="currency-offered"]',
  alert: '[data-testid="currency-error"]',
} as const;

const field = (page: Page, code: string) => page.locator(`[data-testid="currency-field-${code}"]`);
const error = (page: Page, code: string) => page.locator(`[data-testid="currency-error-${code}"]`);
const line = (page: Page, code: string) => page.locator(`[data-testid="currency-line-${code}"]`);
const stale = (page: Page, code: string) => page.locator(`[data-testid="currency-stale-${code}"]`);

/** Opens the settings screen with a state of this test's own, and waits for the panel. */
async function open(page: Page, width = 1280): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await ownState(page, 'currency');
  await reopen(page);
}

/** Opens it again keeping the state this test has written: a fresh state cookie would read every box blank. */
async function reopen(page: Page): Promise<void> {
  await page.goto(`${adminURL}/settings`);
  await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'Display currencies' })).toBeVisible();
}

async function enter(page: Page, code: string, value: string): Promise<void> {
  await field(page, code).fill(value);
}

/** Counts the saves the screen sends, so "nothing was sent" is a measurement and not an absence of evidence. */
function countSaves(page: Page): { readonly count: () => number } {
  let sent = 0;

  page.on('request', (request) => {
    if (request.method() === 'PUT' && request.url().endsWith('/api/admin/settings/currency-rates')) {
      sent += 1;
    }
  });

  return { count: () => sent };
}

for (const width of [390, 1280]) {
  test.describe(`a staff member keeps the rates current, at ${width}px`, () => {
    test('enters a rate, sees it saved on the screen, and finds it again, dated, after a reload', async ({ page }) => {
      await open(page, width);

      await expect(page.locator(SELECTORS.offered)).toContainText('No currency is offered to shoppers yet');
      await expect(line(page, 'gbp')).toHaveText('Not shown to shoppers until a rate is entered.');

      await enter(page, 'gbp', '399.50');
      await expect(line(page, 'gbp')).toContainText('Unsaved. GBP will be offered to shoppers once you save.');
      await page.locator(SELECTORS.save).click();

      // On the screen as well as announced: a sighted admin must not have to guess that the button worked.
      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
      await expect(page.locator(SELECTORS.saved)).toBeVisible();

      await reopen(page);

      await expect(field(page, 'gbp')).toHaveValue('399.5');
      await expect(line(page, 'gbp')).toContainText('Last changed today');
      await expect(page.locator(SELECTORS.offered)).toHaveText('Shoppers can choose to see prices in: GBP.');
      await expect(field(page, 'usd')).toHaveValue('');
    });

    test('clears a rate with an empty box, and the currency stops being offered', async ({ page }) => {
      await open(page, width);

      await enter(page, 'usd', '321.5');
      await enter(page, 'gbp', '399.5');
      await page.locator(SELECTORS.save).click();
      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

      await reopen(page);
      await expect(page.locator(SELECTORS.offered)).toHaveText('Shoppers can choose to see prices in: USD, GBP.');

      await field(page, 'gbp').fill('');
      await expect(line(page, 'gbp')).toHaveText('Saving with this box empty stops offering GBP to shoppers.');
      await page.locator(SELECTORS.save).click();
      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

      await reopen(page);
      await expect(field(page, 'gbp')).toHaveValue('');
      await expect(field(page, 'usd')).toHaveValue('321.5');
      await expect(page.locator(SELECTORS.offered)).toHaveText('Shoppers can choose to see prices in: USD.');
    });

    for (const bad of ['1e3', '0', '1,000', '1.00001']) {
      test(`refuses ${bad} on the screen: nothing is sent, the box is named and focused`, async ({ page }) => {
        await open(page, width);
        const saves = countSaves(page);

        await enter(page, 'eur', bad);
        await page.locator(SELECTORS.save).click();

        await expect(error(page, 'eur')).toBeVisible();
        await expect(field(page, 'eur')).toHaveAttribute('aria-invalid', 'true');
        await expect(field(page, 'eur')).toBeFocused();
        await expect(field(page, 'eur')).toHaveValue(bad);

        // The describedby chain reaches the sentence, so a screen reader reads the reason with the box.
        await expect(field(page, 'eur')).toHaveAttribute('aria-describedby', /currency-error-eur/);
        expect(saves.count()).toBe(0);
      });
    }
  });
}

test.describe('a slow click on Save', () => {
  /**
   * Found by an intermittent failure under load, then reproduced on purpose. Checking a box when it loses focus puts
   * its error under it, which pushes Save down; if that happens between the mouse going down on Save and coming up, the
   * pointer is no longer over the button, no click is made, and the first press on Save does nothing. A real hand takes
   * a tenth of a second between down and up; Playwright's own click takes a few milliseconds, so only a slow one shows it.
   */
  for (const width of [390, 1280]) {
    test(`is not lost to the error that appears when the box loses focus, at ${width}px`, async ({ page }) => {
      await open(page, width);
      const saves = countSaves(page);

      await enter(page, 'eur', '1,000');

      // On the screen first: a pointer below the fold is pressing the page, not the button.
      await page.locator(SELECTORS.save).scrollIntoViewIfNeeded();
      const button = await page.locator(SELECTORS.save).boundingBox();
      await page.mouse.move(button!.x + button!.width / 2, button!.y + button!.height / 2);
      await page.mouse.down();
      await page.waitForTimeout(400);
      await page.mouse.up();

      // The click arrived: Save ran its own check and moved focus to the wrong box.
      await expect(error(page, 'eur')).toBeVisible();
      await expect(field(page, 'eur')).toBeFocused();
      expect(saves.count()).toBe(0);

      // And a good rate typed the same slow way is saved by that one press.
      await enter(page, 'eur', '345');
      await page.locator(SELECTORS.save).scrollIntoViewIfNeeded();
      const again = await page.locator(SELECTORS.save).boundingBox();
      await page.mouse.move(again!.x + again!.width / 2, again!.y + again!.height / 2);
      await page.mouse.down();
      await page.waitForTimeout(400);
      await page.mouse.up();

      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
      expect(saves.count()).toBe(1);
    });
  }
});

test.describe('the screen and the API agree about what a rate is', () => {
  /**
   * The whole shared table through the real screen: the pre-check in the browser must call exactly the cases the API
   * refuses, and no others, or an admin is told "wrong" about a rate the API would take (or sent a rate it refuses).
   */
  test('shows an error for every case the table says is not a rate, and none for a rate', async ({ page }) => {
    await open(page);
    const tested = cases.filter((entry) => entry.value === entry.value.trim() && entry.value !== '');

    expect(tested.length).toBeGreaterThanOrEqual(40);

    for (const entry of tested) {
      await field(page, 'gbp').fill(entry.value);
      await field(page, 'gbp').blur();

      if (entry.problem === 'none') {
        await expect(error(page, 'gbp'), `'${entry.value}' is a rate`).toHaveCount(0);
      } else {
        await expect(error(page, 'gbp'), `'${entry.value}' is ${entry.problem}`).toBeVisible();
      }
    }
  });
});

test.describe('age and the thirty-day warning', () => {
  test('warns after thirty days and not at thirty, in words an admin can act on', async ({ page }) => {
    await open(page);

    await enter(page, 'gbp', '399.5');
    await enter(page, 'usd', '321.5');
    await page.locator(SELECTORS.save).click();
    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

    await stubSwitch(page, '/__test__/currency-age?field=gbp&days=45');
    await stubSwitch(page, '/__test__/currency-age?field=usd&days=30');
    await reopen(page);

    await expect(stale(page, 'gbp')).toHaveText('This rate has not changed for 45 days. Check it is still right.');
    await expect(stale(page, 'gbp')).toBeVisible();
    await expect(line(page, 'gbp')).toContainText('Last changed 45 days ago');
    await expect(stale(page, 'usd')).toHaveCount(0);
    await expect(line(page, 'usd')).toContainText('Last changed 30 days ago');

    // Changing the number re-dates it, and the warning goes.
    await enter(page, 'gbp', '401');
    await page.locator(SELECTORS.save).click();
    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
    await reopen(page);
    await expect(stale(page, 'gbp')).toHaveCount(0);
    await expect(line(page, 'gbp')).toContainText('Last changed today');
  });
});

test.describe('two people edit the rates at once', () => {
  test('the second save is told so, keeps its typing, loads the latest, and then succeeds', async ({ page }) => {
    await open(page);

    // Another staff member, in the same state, saves a rate while this page is open.
    const other = await page.context().newPage();
    await reopen(other);
    await enter(other, 'usd', '321');
    await other.locator(SELECTORS.save).click();
    await expect(other.locator(SELECTORS.saved)).toHaveText('Rates saved.');
    await other.close();

    await enter(page, 'gbp', '399');
    await page.locator(SELECTORS.save).click();

    await expect(page.locator(SELECTORS.alert)).toContainText('Somebody else changed the display currency rates');
    await expect(field(page, 'gbp')).toHaveValue('399');
    await expect(page.locator(SELECTORS.saved)).toHaveCount(0);

    await page.locator('[data-testid="currency-reload"]').click();

    // The latest rates arrive; the typing the admin had not saved is kept, not thrown away.
    await expect(field(page, 'usd')).toHaveValue('321');
    await expect(field(page, 'gbp')).toHaveValue('399');

    await page.locator(SELECTORS.save).click();
    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

    await reopen(page);
    await expect(field(page, 'usd')).toHaveValue('321');
    await expect(field(page, 'gbp')).toHaveValue('399');
  });
});

test.describe('who sees the panel', () => {
  test('is not on the screen at all when the server does not send the group', async ({ page }) => {
    await ownState(page, 'currency-hidden');
    await stubSwitch(page, '/__test__/currency-hidden?hidden=yes');
    await page.goto(`${adminURL}/settings`);

    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 2, name: 'Logo' })).toBeVisible();
    await expect(page.locator(SELECTORS.panel)).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Display currencies' })).toHaveCount(0);
  });
});

test.describe('the panel is usable and accessible', () => {
  /** A busy state: saved and stale rates, one box wrong and focused, so the layout is measured at its fullest. */
  async function busy(page: Page, width: number): Promise<void> {
    await open(page, width);
    await enter(page, 'usd', '321.5');
    await enter(page, 'gbp', '399.5');
    await enter(page, 'aed', '87.5');
    await page.locator(SELECTORS.save).click();
    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

    await stubSwitch(page, '/__test__/currency-age?field=gbp&days=60');
    await stubSwitch(page, '/__test__/currency-age?field=aed&days=90');
    await reopen(page);

    await enter(page, 'eur', '1,000');
    await field(page, 'eur').blur();
    await expect(error(page, 'eur')).toBeVisible();
  }

  test('does not scroll sideways and keeps every control a 44 px target at all eight widths', async ({ page }) => {
    await busy(page, 1280);

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.locator(SELECTORS.panel)).toBeVisible();

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);

      for (const selector of ['[data-testid^="currency-field-"]', SELECTORS.save]) {
        expect(await undersized(page, selector), `${selector} at ${width}px`).toEqual([]);
      }
    }
  });

  test('has no serious or critical axe violation in the panel, in its busiest state', async ({ page }) => {
    await busy(page, 1280);

    const results = await new AxeBuilder({ page })
      .include(SELECTORS.panel)
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
    expect(results.passes.length).toBeGreaterThan(0);
  });

  test('is reachable by keyboard in reading order, five boxes and then Save', async ({ page }) => {
    await open(page);

    await field(page, 'usd').focus();
    for (const code of ['gbp', 'eur', 'aud', 'aed']) {
      await page.keyboard.press('Tab');
      await expect(field(page, code)).toBeFocused();
    }

    await page.keyboard.press('Tab');
    await expect(page.locator(SELECTORS.save)).toBeFocused();

    // Enter in a box saves, as a form should.
    await field(page, 'aud').fill('205');
    await field(page, 'aud').press('Enter');
    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
  });
});
