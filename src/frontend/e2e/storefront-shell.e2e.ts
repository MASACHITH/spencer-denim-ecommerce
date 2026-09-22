import { Page, expect, test } from '@playwright/test';

/**
 * Browser checks for the storefront shell (GLB-03..GLB-09).
 *
 * These are the checks jsdom cannot make: they need real CSS, real media queries and a real
 * focus model.
 */

const DIALOGS = [
  {
    name: 'mobile navigation drawer',
    viewport: { width: 390, height: 844 },
    trigger: '[data-testid="menu-toggle"]',
    panel: '#storefront-menu',
  },
] as const;

/** `scrollWidth` against `clientWidth`: `innerWidth` includes the scrollbar and hides overflow. */
async function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => {
    const root = document.documentElement;
    return root.scrollWidth - root.clientWidth;
  });
}

test.describe('reduced motion', () => {
  /**
   * `page.emulateMedia` rather than `test.use({ reducedMotion })`: the fixture form is silently
   * ignored under this config (verified — `matchMedia('(prefers-reduced-motion: reduce)')` still
   * reported `false`), which would have made every test in this block pass without ever
   * exercising the reduced-motion path. `emulateMedia` before the first navigation means the
   * app boots with the setting already on.
   */
  test.beforeEach(async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
  });

  test('the setting really is on', async ({ page }) => {
    await page.goto('/');

    expect(
      await page.evaluate(
        () => window.matchMedia('(prefers-reduced-motion: reduce)').matches,
      ),
    ).toBe(true);
  });

  for (const dialog of DIALOGS) {
    // Lesson L3. Reduced motion shortens every transition, and `transition-property` defaults to
    // `all`, so the `visibility` that reveals a dialog can end up mid-transition at the moment
    // focus is moved into it. A browser will not focus a hidden element, and the dialog then
    // opens with focus left behind on the page underneath.
    test(`${dialog.name} takes focus when it opens`, async ({ page }) => {
      await page.setViewportSize(dialog.viewport);
      await page.goto('/');

      const trigger = page.locator(dialog.trigger);
      await trigger.focus();
      await trigger.click();

      const panel = page.locator(dialog.panel);
      await expect(panel).toBeVisible();
      await expect(trigger).toHaveAttribute('aria-expanded', 'true');

      // The focused element must be inside the panel, not the trigger behind it.
      await expect
        .poll(() => page.evaluate((sel) => {
          const target = document.querySelector(sel);
          return !!target && !!document.activeElement && target.contains(document.activeElement);
        }, dialog.panel))
        .toBe(true);
    });

    test(`${dialog.name} still closes on Escape and hands focus back`, async ({ page }) => {
      await page.setViewportSize(dialog.viewport);
      await page.goto('/');

      const trigger = page.locator(dialog.trigger);
      await trigger.focus();
      await trigger.click();
      await expect(page.locator(dialog.panel)).toBeVisible();

      await page.keyboard.press('Escape');

      await expect(trigger).toHaveAttribute('aria-expanded', 'false');
      await expect(trigger).toBeFocused();
    });
  }

  test('the announcement bar does not rotate by itself', async ({ page }) => {
    await page.goto('/');

    const message = page.locator('[data-testid="announcement"]');
    const first = await message.textContent();

    await page.waitForTimeout(7000);

    expect(await message.textContent()).toBe(first);
    // Nothing moves, so there is nothing to pause.
    await expect(page.locator('[data-testid="announcement-pause"]')).toHaveCount(0);
  });
});

test.describe('announcement bar (WCAG 2.2.2)', () => {
  test('a phone can stop the rotation with a tap, without hover or focus', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/');

    const pause = page.locator('[data-testid="announcement-pause"]');
    await expect(pause).toBeVisible();
    await expect(pause).toHaveAttribute('aria-pressed', 'false');

    await pause.click();
    await expect(pause).toHaveAttribute('aria-pressed', 'true');

    // Move the pointer and focus away, so only the pressed state can be keeping it still.
    await page.mouse.move(5, 500);
    await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());

    const message = page.locator('[data-testid="announcement"]');
    const held = await message.textContent();
    await page.waitForTimeout(7000);

    expect(await message.textContent()).toBe(held);
  });

  test('the rotating message is not a live region', async ({ page }) => {
    await page.goto('/');

    await expect(page.locator('[data-testid="announcement"]')).not.toHaveAttribute('aria-live');
    await expect(page.locator('[data-testid="announcement-status"]')).toHaveAttribute(
      'role',
      'status',
    );
  });
});

test.describe('layout', () => {
  for (const width of [320, 375, 390, 768, 1024, 1280, 1440, 1920]) {
    test(`no horizontal scroll at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    });
  }

  test('every visible control is at least 44px on both axes at 320px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto('/');

    const tooSmall = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLElement>('a[href], button, select, input')]
        .filter((el) => el.getClientRects().length > 0)
        .map((el) => ({
          label: `${el.tagName}.${el.className}`.slice(0, 60),
          width: Math.round(el.getBoundingClientRect().width),
          height: Math.round(el.getBoundingClientRect().height),
        }))
        .filter((box) => box.width < 44 || box.height < 44),
    );

    expect(tooSmall).toEqual([]);
  });
});
