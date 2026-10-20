import { Page, expect, test } from '@playwright/test';

import { adminURL } from '../playwright.config';
import { ownState, trustOnlyThisWorktree } from './support/admin-content-e2e';

/**
 * Independent tester's additions for #192: the content stub must be no more permissive than the API's
 * validators (`ContentFieldRules`), or a screen can pass here with a value the real API refuses.
 */
trustOnlyThisWorktree();

async function poster(page: Page): Promise<(path: string, data: unknown) => Promise<{ status: number; fields: string[] }>> {
  await ownState(page, 'parity');
  const cookies = await page.context().cookies(adminURL);
  const state = cookies.find((cookie) => cookie.name === 'e2e_state')!.value;

  return async (path, data) => {
    const response = await page.request.post(`${adminURL}${path}`, {
      headers: { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token' },
      data,
    });
    const body = response.status() === 400 ? ((await response.json()) as { errors?: Record<string, unknown> }) : {};
    return { status: response.status(), fields: Object.keys(body.errors ?? {}) };
  };
}

function announcement(overrides: Record<string, unknown>): Record<string, unknown> {
  return { message: 'Hello', linkUrl: null, sortOrder: 5, isActive: true, startsAt: null, endsAt: null, ...overrides };
}

test.describe('the content stub is no more permissive than the API (#192)', () => {
  test('refuses the links and text the API refuses, and the stub agrees on what it already checks', async ({ page }) => {
    const post = await poster(page);

    const cases: readonly [Record<string, unknown>, string][] = [
      [{ linkUrl: 'javascript:alert(1)' }, 'linkUrl'],
      [{ linkUrl: '//evil.example' }, 'linkUrl'],
      [{ linkUrl: 'data:text/html,x' }, 'linkUrl'],
      [{ message: 'x'.repeat(201) }, 'message'],
      [{ message: ' ' }, 'message'],
    ];

    for (const [overrides, field] of cases) {
      const answer = await post('/api/admin/content/announcements', announcement(overrides));
      expect(answer.status, JSON.stringify(overrides)).toBe(400);
      expect(answer.fields, JSON.stringify(overrides)).toContain(field);
    }
  });

  /**
   * DEFECT (tester, #192): the stub accepts each of these and the API refuses each one by name — a line
   * break or tab in plain text, a reverse solidus or whitespace in a site path, credentials in a full
   * address. Remove `fixme` once `admin-content-stub.mjs` checks them as `ContentFieldRules` does.
   */
  test.fixme('refuses control characters, a reverse solidus, whitespace and credentials as the API does', async ({ page }) => {
    const post = await poster(page);

    const cases: readonly [Record<string, unknown>, string][] = [
      [{ message: 'Line one\nline two' }, 'message'],
      [{ message: 'Tab\there' }, 'message'],
      [{ linkUrl: '/\\evil.example' }, 'linkUrl'],
      [{ linkUrl: '/women dresses' }, 'linkUrl'],
      [{ linkUrl: '/women\tx' }, 'linkUrl'],
      [{ linkUrl: 'https://user:pass@evil.example/x' }, 'linkUrl'],
    ];

    for (const [overrides, field] of cases) {
      const answer = await post('/api/admin/content/announcements', announcement(overrides));
      expect(answer.status, JSON.stringify(overrides)).toBe(400);
      expect(answer.fields, JSON.stringify(overrides)).toContain(field);
    }
  });
});
