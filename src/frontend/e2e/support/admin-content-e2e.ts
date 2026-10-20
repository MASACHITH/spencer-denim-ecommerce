import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './measure';

/**
 * Shared steps for the #192 content screens' browser tests (homepage, announcement bar, size guides),
 * which run against `admin-content-stub.mjs` through `admin-catalog-stub.mjs`.
 */

/** docs/04 §4 and the project's non-negotiable list. */
export const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** The server has to be this worktree's own before a single answer from it is trusted (doc 14). */
export function trustOnlyThisWorktree(): void {
  test.beforeAll(async ({ request }) => {
    const response = await request.get(`${adminURL}/__worktree`);
    expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

    const { root } = (await response.json()) as { root: string };
    expect(root.replace(/\\/g, '/').toLowerCase()).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
  });
}

/** Gives this test its own stub state, so parallel tests cannot see each other's writes. */
export async function ownState(page: Page, prefix: string): Promise<void> {
  const state = `${prefix}-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
}

/** Calls a stub switch with this test's state cookie. */
export async function stubSwitch(page: Page, pathAndQuery: string): Promise<void> {
  const response = await page.request.get(`${adminURL}${pathAndQuery}`);
  expect(response.ok(), `${pathAndQuery} answered ${response.status()}`).toBe(true);
}

export async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations).toEqual([]);
}

/** One h1, first, and no heading level skipped on the way down (axe does not check this). */
export async function expectHeadingOrder(page: Page): Promise<void> {
  const levels = await page
    .locator('h1, h2, h3, h4, h5, h6')
    .evaluateAll((nodes) => nodes.map((node) => Number(node.tagName.slice(1))));

  expect(levels[0]).toBe(1);
  expect(levels.filter((level) => level === 1)).toHaveLength(1);
  for (let index = 1; index < levels.length; index += 1) {
    expect(levels[index] - levels[index - 1]).toBeLessThan(2);
  }
}

/** No sideways page scroll at any width, and every named control present, 44 px and reachable. */
export async function expectFitsEveryWidth(page: Page, controls: readonly string[]): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });

    expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

    for (const control of controls) {
      await expect(page.locator(control), `${control} at ${width}px`).toHaveCount(1);
      expect(await undersized(page, control), `tap target ${control} at ${width}px`).toEqual([]);
      expect(await unreachable(page, control), `reachability of ${control} at ${width}px`).toEqual([]);
    }
  }
}

/** A tiny file for an upload; the stub does not parse it. */
export function fakeFile(name: string, mimeType: string): { name: string; mimeType: string; buffer: Buffer } {
  return { name, mimeType, buffer: Buffer.from('UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==', 'base64') };
}
