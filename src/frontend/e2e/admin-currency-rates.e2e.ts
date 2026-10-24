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

interface Currency {
  readonly field: string;
  readonly code: string;
  readonly name: string;
}

const table = JSON.parse(readFileSync(resolve(__dirname, 'support', 'currency-rate-cases.json'), 'utf8')) as {
  cases: Case[];
  currencies: Currency[];
  messages: Record<'tooManyDecimals' | 'outOfRange' | 'notANumber' | 'staleRowVersion' | 'missingRowVersion', string>;
};
const cases = table.cases;

/** The exact sentence the API gives a currency for a kind of fault; the screen must say the same one. */
const sentence = (kind: 'tooManyDecimals' | 'outOfRange' | 'notANumber', currency: Currency): string =>
  table.messages[kind].replace('{name}', currency.name).replace('{code}', currency.code);
const gbp = table.currencies.find((currency) => currency.field === 'gbp')!;

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

      await expect(page.locator(SELECTORS.offered)).toHaveText('No rates set yet.');
      await expect(line(page, 'gbp')).toHaveText('No rate set. GBP is left out until you type one.');

      await enter(page, 'gbp', '399.50');
      await expect(line(page, 'gbp')).toContainText('Unsaved. Save to set a rate for GBP.');
      await page.locator(SELECTORS.save).click();

      // On the screen as well as announced: a sighted admin must not have to guess that the button worked.
      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
      await expect(page.locator(SELECTORS.saved)).toBeVisible();

      await reopen(page);

      await expect(field(page, 'gbp')).toHaveValue('399.5');
      await expect(line(page, 'gbp')).toContainText('Last changed today');
      await expect(page.locator(SELECTORS.offered)).toHaveText('Rates set: GBP.');
      await expect(field(page, 'usd')).toHaveValue('');
    });

    test('clears a rate with an empty box, and the currency is left out', async ({ page }) => {
      await open(page, width);

      await enter(page, 'usd', '321.5');
      await enter(page, 'gbp', '399.5');
      await page.locator(SELECTORS.save).click();
      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

      await reopen(page);
      await expect(page.locator(SELECTORS.offered)).toHaveText('Rates set: USD, GBP.');

      await field(page, 'gbp').fill('');
      await expect(line(page, 'gbp')).toHaveText('Saving with this box empty leaves GBP out.');
      await page.locator(SELECTORS.save).click();
      await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

      await reopen(page);
      await expect(field(page, 'gbp')).toHaveValue('');
      await expect(field(page, 'usd')).toHaveValue('321.5');
      await expect(page.locator(SELECTORS.offered)).toHaveText('Rates set: USD.');
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
        await expect(error(page, 'gbp'), `'${entry.value}' is told as the API tells it`).toHaveText(
          sentence(entry.problem, gbp),
        );
      }
    }
  });

  /**
   * The screen speaks before the request and the server after it, and they must be one voice: the same sentence for the
   * same fault, for every currency. The server's sentence is the shared table's, held there by the API's own test.
   */
  test('says the sentence the API gives, for each of the five currencies and each kind of fault', async ({ page }) => {
    await open(page);

    for (const currency of table.currencies) {
      for (const [kind, value] of [
        ['notANumber', '1e3'],
        ['tooManyDecimals', '1.00001'],
        ['outOfRange', '0'],
      ] as const) {
        await field(page, currency.field).fill(value);
        await field(page, currency.field).blur();

        await expect(error(page, currency.field), `${currency.code} ${kind}`).toHaveText(sentence(kind, currency));
      }

      await field(page, currency.field).fill('');
      await field(page, currency.field).blur();
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

    await expect(page.locator(SELECTORS.alert)).toContainText(table.messages.staleRowVersion);
    await expect(field(page, 'gbp')).toHaveValue('399');
    await expect(page.locator(SELECTORS.saved)).toHaveCount(0);

    await page.locator('[data-testid="currency-reload"]').click();

    // The conflict has been dealt with: neither the alert nor its button stays on the screen.
    await expect(page.locator(SELECTORS.alert)).toHaveCount(0);
    await expect(page.locator('[data-testid="currency-reload"]')).toHaveCount(0);

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

/**
 * Tester round, MAJOR 1. After pressing Save the result has to be where the admin is looking. It used to be drawn
 * ABOVE the form, so on a phone and on 1280 and 1440 px desktops its bottom edge was 60 to 160 px above the top of
 * the viewport; `toBeVisible()` cannot see that (an element scrolled out of view is still "visible"), which is why the
 * earlier tests were green. These measure the box against the window.
 *
 * The worst case is staged on purpose: Save is scrolled to the very bottom edge of the window before it is pressed,
 * so anything under it is out of sight until the panel brings it in.
 */
test.describe('the outcome of Save is in the window (tester round, major 1)', () => {
  const SIZES = [
    { width: 390, height: 844 },
    { width: 768, height: 1024 },
    { width: 1280, height: 900 },
    { width: 1440, height: 900 },
  ] as const;

  const OUTCOMES = ['saved', 'nothing to save', 'rejected rate', 'conflict', 'server error', 'offline'] as const;

  /** Where an element is relative to the window right now. */
  async function placement(page: Page, selector: string) {
    return page.evaluate((target) => {
      const element = document.querySelector(target);
      if (!element) {
        return null;
      }

      const box = element.getBoundingClientRect();
      return { top: box.top, bottom: box.bottom, height: box.height, window: window.innerHeight };
    }, selector);
  }

  async function expectInWindow(page: Page, selector: string, what: string): Promise<void> {
    // The scroll is smooth, so the answer is polled rather than read once. The failure says where the box really was.
    await expect
      .poll(
        async () => {
          const box = await placement(page, selector);

          if (box === null) {
            return 'is not on the page';
          }

          return box.height > 0 && box.top >= 0 && box.bottom <= box.window
            ? 'inside the window'
            : `outside the window: top ${Math.round(box.top)}, bottom ${Math.round(box.bottom)}, window ${box.window}`;
        },
        { message: `${what}`, timeout: 8000 },
      )
      .toBe('inside the window');
  }

  async function pressSaveFromTheBottomEdge(page: Page): Promise<void> {
    await page.locator(SELECTORS.save).evaluate((button) => button.scrollIntoView({ block: 'end', behavior: 'instant' }));
    await page.locator(SELECTORS.save).click();
  }

  for (const size of SIZES) {
    test.describe(`at ${size.width}x${size.height}`, () => {
      for (const outcome of OUTCOMES) {
        test(`shows the ${outcome} outcome inside the window`, async ({ page }) => {
          await page.setViewportSize(size);
          await ownState(page, 'currency-outcome');
          await reopen(page);

          const refuse = (status: number, body: object) =>
            page.route('**/api/admin/settings/currency-rates', (route) =>
              route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }),
            );

          if (outcome === 'saved') {
            await enter(page, 'gbp', '399.5');
            await pressSaveFromTheBottomEdge(page);
            await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
            await expectInWindow(page, SELECTORS.saved, 'the saved message');
          }

          if (outcome === 'nothing to save') {
            await pressSaveFromTheBottomEdge(page);
            await expect(page.locator(SELECTORS.saved)).toHaveText('There is nothing to save: no rate has changed.');
            await expectInWindow(page, SELECTORS.saved, 'the nothing-to-save message');
          }

          if (outcome === 'rejected rate') {
            // A rate the screen accepts and the server does not: its message is beside its box, and the box has focus.
            await refuse(400, { title: 'One or more fields are invalid.', errors: { gbp: ['Use at most 4 decimal places.'] } });
            await enter(page, 'gbp', '399.5');
            await pressSaveFromTheBottomEdge(page);
            await expect(error(page, 'gbp')).toHaveText('Use at most 4 decimal places.');
            await expect(field(page, 'gbp')).toBeFocused();
            await expectInWindow(page, '[data-testid="currency-error-gbp"]', 'the rejected rate\'s message');
            await expectInWindow(page, '[data-testid="currency-field-gbp"]', 'the rejected rate\'s box');
          }

          if (outcome === 'conflict') {
            await refuse(409, { detail: table.messages.staleRowVersion, code: 'stale_row_version' });
            await enter(page, 'gbp', '399.5');
            await pressSaveFromTheBottomEdge(page);
            await expect(page.locator(SELECTORS.alert)).toContainText(table.messages.staleRowVersion);
            await expectInWindow(page, SELECTORS.alert, 'the conflict alert');
            await expectInWindow(page, '[data-testid="currency-reload"]', 'its Load the latest rates button');
          }

          if (outcome === 'server error') {
            await refuse(500, { title: 'Server error' });
            await enter(page, 'gbp', '399.5');
            await pressSaveFromTheBottomEdge(page);
            await expect(page.locator(SELECTORS.alert)).toBeVisible();
            await expectInWindow(page, SELECTORS.alert, 'the server-error alert');
            // Focus was not moved to the message.
            await expect(page.locator(SELECTORS.save)).toBeFocused();
          }

          if (outcome === 'offline') {
            await page.route('**/api/admin/settings/currency-rates', (route) => route.abort('connectionrefused'));
            await enter(page, 'gbp', '399.5');
            await pressSaveFromTheBottomEdge(page);
            await expect(page.locator(SELECTORS.alert)).toContainText('could not reach the server');
            await expectInWindow(page, SELECTORS.alert, 'the offline alert');
          }
        });
      }
    });
  }

  test('is just as visible for a visitor who asked for less motion', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width: 390, height: 844 });
    await ownState(page, 'currency-reduced-motion');
    await reopen(page);

    await enter(page, 'gbp', '399.5');
    await pressSaveFromTheBottomEdge(page);

    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');
    await expectInWindow(page, SELECTORS.saved, 'the saved message');
  });

  test('draws the message directly under the Save button, in the same container, never above the form', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await ownState(page, 'currency-placement');
    await reopen(page);

    await enter(page, 'gbp', '399.5');
    await page.locator(SELECTORS.save).click();
    await expect(page.locator(SELECTORS.saved)).toHaveText('Rates saved.');

    const geometry = await page.evaluate(() => {
      const save = document.querySelector('[data-testid="save-currency"]')!.getBoundingClientRect();
      const message = document.querySelector('[data-testid="currency-saved"]')!.getBoundingClientRect();
      const lastBox = document.querySelector('[data-testid="currency-field-aed"]')!.getBoundingClientRect();
      const sameContainer =
        document.querySelector('[data-testid="save-currency"]')!.closest('.cur-save') ===
        document.querySelector('[data-testid="currency-saved"]')!.closest('.cur-save');

      return { gapBelowSave: message.top - save.bottom, belowLastBox: message.top - lastBox.bottom, sameContainer };
    });

    expect(geometry.sameContainer).toBe(true);
    expect(geometry.belowLastBox).toBeGreaterThan(0);
    expect(geometry.gapBelowSave).toBeGreaterThanOrEqual(0);
    expect(geometry.gapBelowSave).toBeLessThan(48);
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
