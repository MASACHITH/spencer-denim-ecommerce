import AxeBuilder from '@axe-core/playwright';
import { Locator, Page, expect, test } from '@playwright/test';

import { adminURL, apiURL, liveOnlyApiURL, liveOnlyCatalogueURL } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * Accessibility (GLB-12, WCAG 2.2 AA), and the axe harness `docs/06` §4 has claimed to have since
 * September (#65).
 *
 * **Why this file exists at all.** `docs/06-engineering-process.md` lists "axe-core in Playwright" in
 * the test strategy. It was not a dependency and no test ran it, so every accessibility result on this
 * project so far came from a tester installing axe in a scratch directory and running it by hand. That
 * worked, and it meant the check happened when somebody remembered, never in CI, and a regression
 * merged in silence — a gate written down and not built is worse than no gate, because people believe
 * it. The harness is now a dependency and runs here, which means it runs in CI.
 *
 * **What axe cannot see, and is therefore tested by hand below.** Heading order is a best-practice
 * rule rather than a WCAG failure, so axe reported nothing for #76 while every page announced three
 * department headings before it said what the page was. A focus ring that an ancestor clips away
 * computes as present (#38, lesson L4). A tap target under 44 px is not an axe rule at this level. And
 * a menu that re-opens itself when focus returns to it (#20 SH8) is a behaviour, not a snapshot. Each
 * of those is measured, not inferred.
 *
 * **The gate's own limit, stated so that nobody relies on it by accident.** The gate is serious and
 * critical, as `docs/06` §3 says. **Nothing here fails on the next *moderate* violation** except the
 * one bespoke `region` assertion below - and `heading-order` is a moderate, which is the exact rule
 * class of #76 and #20's SH11, the defects this task has just finished fixing by hand. So if you are
 * changing headings: **axe will not catch you.** The heading-order block further down is what will, and
 * the any-impact annotation test is where a new moderate shows up at all. Raising the gate to every
 * impact was considered and not done, because an axe upgrade adds rules and a suite that goes red on
 * `npm ci` teaches people to ignore it - the same reasoning as #84's warning about an `npm audit` gate.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * The browser's own `/api` calls go to the storefront's origin, which in production is a reverse proxy
 * in front of the API. Here it is the stub, fetched from the test process.
 */
async function useStubApi(page: Page, origin = apiURL): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${origin}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * **A `count() > 0` guard per clause, not per selector (#80).**
 *
 * `undersized` returns `[]` both when every control passes and when the selector matched nothing, so
 * `expect(await undersized(page, sel)).toEqual([])` passes on a page with no such element — a dead
 * assertion of exactly that shape was found in the admin screens, and another in a 503 page where five
 * 20 px links sat outside the selector being measured. #80 fixes the helper and is deliberately not
 * this task's to fix, so every call site here is guarded instead.
 *
 * Per clause, because a compound selector's guard is satisfied by its first clause while a later one
 * silently matches nothing. And `getClientRects()` rather than `count()`, because that is what
 * `undersized` itself filters on — `count()` counts matches regardless of visibility, so a selector can
 * satisfy a `count()` guard and still measure nothing at all.
 */
async function tapTargets(page: Page, selector: string): Promise<unknown[]> {
  for (const clause of selector.split(',').map((part) => part.trim()).filter(Boolean)) {
    const rendered = await page.evaluate(
      (one) =>
        [...document.querySelectorAll<HTMLElement>(one)].filter(
          (element) => element.getClientRects().length > 0,
        ).length,
      clause,
    );

    expect(rendered, `nothing rendered matches "${clause}", so measuring it could not fail`).toBeGreaterThan(0);
  }

  return undersized(page, selector);
}

/**
 * Puts one thing in the bag the way a shopper does (W2-09): the product page, with a colourway and
 * size settled so Add to Bag has a variant to send. Leaves the mini bag open, which is what the add
 * does.
 *
 * A product of its own per caller, because the stub's stock switch is one shared map and this suite
 * runs in parallel — `bag.e2e.ts` learned that the expensive way.
 */
async function addToBag(page: Page, product = 1): Promise<void> {
  const slug = `test-product-${product}`;
  await page.goto(`/product/${slug}?colour=1&size=10`);

  // **Hydration, and `toBeEnabled()` is not it.** `goto` resolves on `load`, before Angular has claimed the
  // server's DOM, and Add to Bag is *server-rendered enabled* when the URL settles a variant — so an
  // actionability check passes on a button whose handler does not exist yet, the click goes nowhere, and the
  // wait below times out. The signal is the one the product page's own suite uses and documents — recording
  // the visit (PDP-07) is the last thing that page does.
  //
  // **This gates the add, and nothing more.** The callers that then navigate to `/bag` do so with a bare
  // `page.goto` and no gate of their own, so an axe scan can still read a document the browser has not
  // finished claiming. `bag.e2e.ts`'s `openBagPage` — which arms `waitForResponse` for the page's own `GET
  // /api/cart` before navigating — is what those five navigations and the click below want. That is **filed
  // rather than fixed here**, and this comment now says so instead of implying otherwise: the version of it
  // that stood here claimed the unhydrated-scan problem was dealt with, and was contradicted by the very next
  // line of every caller.
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(slug);

  const add = page.locator('[data-testid="add-to-bag"]');
  await expect(add).toBeEnabled();
  await add.click();
  await expect(page.locator('sd-mini-bag .mini-bag__line')).toHaveCount(1);
}

/** Serious and critical only, which is the gate `docs/06` §3 states. */
async function violations(page: Page, within?: string) {
  let builder = new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']);
  if (within) {
    builder = builder.include(within);
  }

  const results = await builder.analyze();

  return results.violations
    .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
    .map((violation) => ({
      id: violation.id,
      impact: violation.impact,
      help: violation.help,
      nodes: violation.nodes.map((node) => node.target.join(' ')),
    }));
}

/** Everything axe found, serious and critical or not, for the report rather than the gate. */
async function everything(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();

  return results.violations.map((violation) => ({
    id: violation.id,
    impact: violation.impact,
    targets: violation.nodes.map((node) => node.target.join(' ')),
  }));
}

test.describe('axe over the storefront (#65)', () => {
  const PAGES = [
    { label: 'the home page', path: '/' },
    { label: 'a department listing', path: '/women' },
    { label: 'a category listing', path: '/women/jeans' },
    { label: 'a product page', path: '/product/test-product-1' },
    { label: 'the not-found page', path: '/not-a-dept' },
    // W2-09. An *empty* bag, which is a real state and the one a crawler and a first-time visitor both
    // meet; a bag with something in it, the open mini bag, and a line the shop has withdrawn are
    // scanned separately below, because none of them is reachable by navigating to an address.
    { label: 'the bag page', path: '/bag' },
  ] as const;

  for (const subject of PAGES) {
    test(`${subject.label} has no serious or critical violation`, async ({ page }) => {
      await useStubApi(page);
      await page.goto(subject.path);
      await expect(page.getByRole('heading', { level: 1 })).toBeAttached();

      const found = await violations(page);

      // Printed as well as asserted: a failure should say what to fix without a rerun.
      expect(found, JSON.stringify(found, null, 2)).toEqual([]);
    });

    /**
     * **The one finding #65 names, which is below the gate and is now asserted anyway.**
     *
     * axe reported one moderate on every storefront page — `region`, on the floating WhatsApp
     * button's visually hidden label, which `app.ts` renders outside banner, main and contentinfo
     * alike. #65 asked for it to be fixed or explicitly allowed rather than silently tolerated; it
     * is fixed (the button is its own named `complementary` landmark), so this holds it fixed. The
     * gate above stays at serious and critical, as `docs/06` §3 states, rather than at every impact
     * an axe upgrade might add.
     */
    test(`${subject.label} leaves nothing outside a landmark`, async ({ page }) => {
      await useStubApi(page);
      await page.goto(subject.path);
      await expect(page.getByRole('heading', { level: 1 })).toBeAttached();

      const found = (await everything(page)).filter((violation) => violation.id === 'region');

      expect(found, JSON.stringify(found, null, 2)).toEqual([]);
    });
  }

  /**
   * The mega menu is a third of the storefront's markup and is `hidden` until something opens it, so a
   * scan of a page at rest never looks inside it. Opened, it is the biggest single block of
   * interactive markup on the site.
   */
  test('an open mega menu has no serious or critical violation', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await useStubApi(page);
    await page.goto('/');

    await page.locator('header').getByRole('link', { name: 'Women', exact: true }).first().hover();
    await expect(page.locator('#mega-women')).toBeVisible();

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /** The drawer is the phone's whole navigation, and it is a dialog, so it is scanned open too. */
  test('the open mobile drawer has no serious or critical violation', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await useStubApi(page);
    await page.goto('/');

    await page.locator('[data-testid="menu-toggle"]').click();
    await expect(page.locator('#storefront-menu')).toBeVisible();

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /**
   * The bag with something in it (W2-09, CART-01), which the loop above cannot reach: an address alone
   * gives an empty bag, and every control that matters — the quantity stepper, Remove, Move to
   * wishlist, the attribute list — only exists on a line.
   */
  test('the bag page with an item in it has no serious or critical violation', async ({ page }) => {
    await useStubApi(page);
    await addToBag(page);
    await page.goto('/bag');
    await expect(page.locator('.bag__line')).toHaveCount(1);

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /**
   * A line the shop has withdrawn. Its row is a different shape — no quantity control, and a notice in
   * `--color-sale` rather than the muted grey — so it is the one bag state with its own colours, which
   * is a rule class (`color-contrast`) axe does gate at serious.
   */
  test('a bag holding a withdrawn line has no serious or critical violation', async ({ page }) => {
    await useStubApi(page);
    await addToBag(page, 5);

    try {
      await page.request.get(
        `${apiURL}/__test__/cart-variant?variantId=${5 * 1000 + 1}&sellable=0`,
      );
      await page.goto('/bag');
      await expect(page.locator('.bag__line.is-unavailable')).toHaveCount(1);

      const found = await violations(page);
      expect(found, JSON.stringify(found, null, 2)).toEqual([]);
    } finally {
      await page.request.get(`${apiURL}/__test__/cart-variant?reset=variant&variantId=${5 * 1000 + 1}`);
    }
  });

  /**
   * The mini bag (CART-02), scanned open for the same reason the mobile drawer is: it is a dialog, its
   * panel is `hidden` until something opens it, and its contents are not even rendered until then.
   */
  test('the open mini bag has no serious or critical violation', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await useStubApi(page);
    await addToBag(page);

    await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeVisible();
    await expect(page.locator('sd-mini-bag .mini-bag__line')).toHaveCount(1);

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /**
   * **The review axe asks for and will not do, done in code.**
   *
   * axe reports `aria-valid-attr-value` as **incomplete at critical impact** for the header's bag
   * control on every page of the site — and `incomplete` is not a violation, so the gate above is
   * silent about it and reading the exit code would miss it entirely. Measured cause: axe refuses to
   * decide about `aria-controls` whenever the element also carries `aria-haspopup`, because a popup may
   * legitimately be created on demand. The bag control has `aria-haspopup="dialog"`; the mobile menu
   * toggle next to it does not and axe passes it, and `.pdp__guide` (PDP-04, shipped in W2-05) does and
   * gets the same incomplete.
   *
   * Probed directly: `#mini-bag` is in the DOM on every page, it is the drawer's own `role="dialog"`
   * panel, and it is `visibility: hidden` while closed — which is exactly what it should be. So the
   * answer to axe's question is yes, and `aria-controls` stays, because dropping it to quieten a
   * needs-review would throw away a real relationship. This test is the answer written down: every
   * `aria-controls` on the page names an element that is really there.
   */
  test('every aria-controls on the bag page names an element that exists', async ({ page }) => {
    await useStubApi(page);
    await addToBag(page, 2);
    await page.goto('/bag');
    await expect(page.locator('.bag__line')).toHaveCount(1);

    const dangling = await page.evaluate(() =>
      [...document.querySelectorAll('[aria-controls]')]
        .map((element) => ({
          control: `${element.tagName}.${element.className}`.slice(0, 60),
          id: element.getAttribute('aria-controls') ?? '',
        }))
        .filter((entry) => entry.id === '' || document.getElementById(entry.id) === null),
    );

    expect(dangling, JSON.stringify(dangling, null, 2)).toEqual([]);

    // The guard that stops the assertion above passing on a page with no such attribute at all —
    // the #80 lesson applied to a different measurement.
    expect(await page.locator('[aria-controls]').count()).toBeGreaterThan(0);
  });

  /**
   * **The bag page with the mini bag open over it** — the one bag state nothing had scanned, and the one
   * with a reason to be suspicious: every line is then rendered twice, the two Remove controls carry
   * byte-identical accessible names, and two `role="alert"` regions are bound to the same error. The
   * dialog's `aria-modal` should hide the page's copy from a screen reader, which would make all three
   * harmless — but "should" is what a scan is for.
   */
  test('the bag page with the mini bag open over it has no serious or critical violation', async ({
    page,
  }) => {
    await useStubApi(page);
    await addToBag(page, 6);
    await page.goto('/bag');
    await expect(page.locator('.bag__line')).toHaveCount(1);

    await page.locator('[data-testid="bag-toggle"]').click();
    await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeVisible();

    // Both copies really are present: without this the scan below could pass by there being only one.
    await expect(page.locator('.bag__line')).toHaveCount(1);
    await expect(page.locator('.mini-bag__line')).toHaveCount(1);

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /** And nothing in the bag's own markup sits outside a landmark, as the loop asserts for every page. */
  test('the bag page with an item leaves nothing outside a landmark', async ({ page }) => {
    await useStubApi(page);
    await addToBag(page);
    await page.goto('/bag');
    await expect(page.locator('.bag__line')).toHaveCount(1);

    const found = (await everything(page)).filter((violation) => violation.id === 'region');
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /**
   * The 503 page (#30). It is a page a crawler and a shopper both meet during an outage, and it was
   * built after the last hand-run axe pass, so nothing has ever scanned it.
   */
  test('the catalogue-unavailable page has no serious or critical violation', async ({
    page,
    request,
  }) => {
    await request.get(`${liveOnlyApiURL}/__test__/departments-fault?mode=status`);
    try {
      await page.route('**/api/catalog/**', async (route) => {
        const url = new URL(route.request().url());
        const response = await route.fetch({ url: `${liveOnlyApiURL}${url.pathname}${url.search}` });
        await route.fulfill({ response });
      });
      await page.goto(`${liveOnlyCatalogueURL}/women/coats`);
      await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();

      const found = await violations(page);
      expect(found, JSON.stringify(found, null, 2)).toEqual([]);
    } finally {
      // The fault is shared with `catalog-availability.e2e.ts`, which sets it on the way in for
      // exactly this reason; cleared here as well so a run of this file alone leaves nothing behind.
      await request.get(`${liveOnlyApiURL}/__test__/departments-fault?mode=none`);
    }
  });

  /** #65 names the admin sign-in: it is the one admin page an unauthenticated visitor can reach. */
  test('the admin sign-in has no serious or critical violation', async ({ page }) => {
    await page.goto(`${adminURL}/login`);
    await expect(page.getByRole('heading', { level: 1 })).toBeAttached();

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /**
   * Everything axe found at any impact, on the record rather than in a tester's scratch directory.
   *
   * It does not fail the suite — the gate is serious and critical — but it prints, so a moderate or
   * minor finding is something somebody decided about rather than something nobody saw. #65 calls out
   * one known moderate (`region`, on an `sr-only` node outside a landmark) and asks for it to be fixed
   * or explicitly allowed; this is where it shows up either way.
   */
  test('reports every axe finding at any impact, for the record', async ({ page }) => {
    await useStubApi(page);

    const report: Record<string, unknown> = {};
    for (const subject of PAGES) {
      await page.goto(subject.path);
      await expect(page.getByRole('heading', { level: 1 })).toBeAttached();
      report[subject.path] = await everything(page);
    }

    test.info().annotations.push({ type: 'axe', description: JSON.stringify(report) });

    // Asserted only for the shape, so the annotation is the output and the test is not a gate.
    expect(Object.keys(report)).toHaveLength(PAGES.length);
  });
});

/**
 * **#76 and #20's SH11: the `h1` came after the mega menu's headings on every page.**
 *
 * A screen reader met `H3 Girls, H3 Boys, H3 Baby, H2 Menu, H1 <the page>` — three department
 * headings and a "Menu" before the page said what it was. axe reports nothing, because nested heading
 * order is a best-practice rule rather than a WCAG failure, which is exactly why it survived.
 *
 * Measured over the **DOM order of every heading in the document**, not over what is visible: the
 * drawer and the mega menu are in the markup of every page whether they are open or not, and the whole
 * defect was that being in the markup earlier was enough.
 */
test.describe('heading order (#76, #20 SH11)', () => {
  async function headings(page: Page): Promise<string[]> {
    return page.evaluate(() =>
      [...document.querySelectorAll('h1, h2, h3, h4, h5, h6')].map(
        (element) => `${element.tagName} ${(element.textContent ?? '').trim().slice(0, 40)}`,
      ),
    );
  }

  const PAGES = ['/', '/women', '/women/jeans', '/product/test-product-1', '/not-a-dept'] as const;

  for (const path of PAGES) {
    test(`${path} says what the page is before anything else`, async ({ page }) => {
      await useStubApi(page);
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 })).toBeAttached();

      const found = await headings(page);

      expect(found.length, 'the page has no headings at all').toBeGreaterThan(0);
      expect(found[0], `headings were ${JSON.stringify(found)}`).toMatch(/^H1 /);
    });
  }

  /**
   * **The 503 page, which the claim "every page type" did not actually cover.**
   *
   * #76's own table measured it - `H3 Girls, H3 Boys, H3 Baby, H2 Menu, H1 We could not load this page
   * just now` - and the block above covers five pages, none of them this one. It needs the fault switch
   * and a second server pair, which is why it was left out; left out is not the same as covered.
   */
  test('the catalogue-unavailable page says what it is before anything else', async ({
    page,
    request,
  }) => {
    await request.get(`${liveOnlyApiURL}/__test__/departments-fault?mode=status`);
    try {
      await page.route('**/api/catalog/**', async (route) => {
        const url = new URL(route.request().url());
        const response = await route.fetch({ url: `${liveOnlyApiURL}${url.pathname}${url.search}` });
        await route.fulfill({ response });
      });
      await page.goto(`${liveOnlyCatalogueURL}/women/coats`);
      await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();

      const found = await headings(page);

      expect(found.length).toBeGreaterThan(0);
      expect(found[0], `headings were ${JSON.stringify(found)}`).toMatch(/^H1 /);
    } finally {
      await request.get(`${liveOnlyApiURL}/__test__/departments-fault?mode=none`);
    }
  });

  test('the h1 is still first with the mega menu open', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await useStubApi(page);
    await page.goto('/');

    await page.locator('header').getByRole('link', { name: 'Women', exact: true }).first().hover();
    await expect(page.locator('#mega-women')).toBeVisible();

    expect((await headings(page))[0]).toMatch(/^H1 /);
  });

  test('the h1 is still first with the mobile drawer open', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await useStubApi(page);
    await page.goto('/');

    await page.locator('[data-testid="menu-toggle"]').click();
    await expect(page.locator('#storefront-menu')).toBeVisible();

    expect((await headings(page))[0]).toMatch(/^H1 /);
  });

  /**
   * Taking the headings out must not take the relationship out. The category list under a group title
   * is `aria-labelledby` the title, so a screen reader still says which group it is in — and the
   * drawer still has an accessible name, which is what its `h2` was really for.
   */
  test('a mega-menu group still labels its own list of categories', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await useStubApi(page);
    await page.goto('/');

    await page.locator('header').getByRole('link', { name: 'Kids', exact: true }).first().hover();
    await expect(page.locator('#mega-kids')).toBeVisible();

    const labelled = page.locator('#mega-kids ul[aria-labelledby]');
    expect(await labelled.count()).toBeGreaterThan(0);

    // The label resolves to the group's own title, not to a dangling id.
    const name = await labelled.first().evaluate((list) => {
      const id = list.getAttribute('aria-labelledby') ?? '';
      return document.getElementById(id)?.textContent?.trim() ?? null;
    });
    expect(name).toBeTruthy();
  });

  test('the mobile drawer still has an accessible name', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await useStubApi(page);
    await page.goto('/');

    await page.locator('[data-testid="menu-toggle"]').click();

    await expect(page.getByRole('dialog', { name: 'Menu' })).toBeVisible();
  });
});

/**
 * **#20's SH8: the mega menu re-opened when focus came back to a department after Escape.**
 *
 * Reproduced 6/6 at 1280 px: focus Women, press Escape, Tab to Men, Shift+Tab back to Women — and the
 * menu Escape had just closed was open again. The cause was `onFocusOut` clearing the dismissed guard
 * per list item, so tabbing off Women wiped the very fact that Women had been dismissed.
 *
 * Driven with real key presses rather than by calling the handler, because what was wrong was which
 * events the browser delivers and in what order, and a test that called `onFocusOut` directly would
 * have agreed with the broken code.
 */
test.describe('the mega menu after Escape (#20 SH8)', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await useStubApi(page);
    await page.goto('/');
  });

  function department(page: Page, name: string): Locator {
    return page.locator('header').getByRole('link', { name, exact: true }).first();
  }

  test('focus opens it, which is how a keyboard reaches the categories at all', async ({ page }) => {
    await department(page, 'Women').focus();

    await expect(page.locator('#mega-women')).toBeVisible();
  });

  test('Escape closes it and leaves focus on the department', async ({ page }) => {
    await department(page, 'Women').focus();
    await expect(page.locator('#mega-women')).toBeVisible();

    await page.keyboard.press('Escape');

    await expect(page.locator('#mega-women')).toBeHidden();
    await expect(department(page, 'Women')).toBeFocused();
  });

  test('it stays closed when focus leaves the department and comes back', async ({ page }) => {
    await department(page, 'Women').focus();
    await page.keyboard.press('Escape');
    await expect(page.locator('#mega-women')).toBeHidden();

    await page.keyboard.press('Tab');
    await page.keyboard.press('Shift+Tab');

    await expect(department(page, 'Women')).toBeFocused();
    await expect(page.locator('#mega-women')).toBeHidden();
  });

  /**
   * **F3: a second Escape used to overwrite the first.**
   *
   * The guard was a single `string | null`, so dismissing a second department forgot the first and four
   * keystrokes brought SH8's own symptom back: focus Women, Escape, Tab to Men, Escape, Shift+Tab to
   * Women - and Women was open again, because the slot now held `men`. Every test above probes a single
   * dismissal, which is why the single slot looked correct.
   *
   * Driven with real key presses, like the rest of this block: the defect was about what the component
   * remembers between two browser events, and a test that called the handler twice would have agreed
   * with the broken code.
   */
  test('a second Escape does not undo the first', async ({ page }) => {
    await department(page, 'Women').focus();
    await expect(page.locator('#mega-women')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#mega-women')).toBeHidden();

    await page.keyboard.press('Tab');
    await expect(department(page, 'Men')).toBeFocused();
    await expect(page.locator('#mega-men')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#mega-men')).toBeHidden();

    await page.keyboard.press('Shift+Tab');

    await expect(department(page, 'Women')).toBeFocused();
    expect(await page.locator('#mega-women').isVisible(), 'Women reopened after a second Escape').toBe(
      false,
    );
  });

  /** And a third, because "remembers more than one" should not mean "remembers exactly two". */
  test('all three dismissals are remembered', async ({ page }) => {
    for (const name of ['Women', 'Men', 'Kids'] as const) {
      await department(page, name).focus();
      await page.keyboard.press('Escape');
    }

    // Walk back along the nav; none of the three may reopen on the way.
    for (const name of ['Kids', 'Men', 'Women'] as const) {
      await department(page, name).focus();
      expect(await page.locator(`#mega-${name.toLowerCase()}`).isVisible(), name).toBe(false);
    }
  });

  /** Tabbing on must still open the next department: the guard is Women's, not the navigation's. */
  test('the next department still opens on the way past', async ({ page }) => {
    await department(page, 'Women').focus();
    await page.keyboard.press('Escape');

    await department(page, 'Men').focus();

    await expect(page.locator('#mega-men')).toBeVisible();
  });

  /** A pointer arriving is a fresh request, even on the department Escape dismissed. */
  test('hovering the dismissed department opens it again', async ({ page }) => {
    await department(page, 'Women').focus();
    await page.keyboard.press('Escape');
    await expect(page.locator('#mega-women')).toBeHidden();

    await department(page, 'Women').hover();

    await expect(page.locator('#mega-women')).toBeVisible();
  });

  /** And so is focus returning after it has left the navigation altogether. */
  test('leaving the navigation and coming back opens it again', async ({ page }) => {
    await department(page, 'Women').focus();
    await page.keyboard.press('Escape');
    await expect(page.locator('#mega-women')).toBeHidden();

    await page.locator('[data-testid="search-toggle"], .site-header__search-inline input').first().focus();
    await department(page, 'Women').focus();

    await expect(page.locator('#mega-women')).toBeVisible();
  });
});

/**
 * **#38: the product card's wishlist heart had no visible focus ring.**
 *
 * The card drew one on a `::after` with `inset: 2px` — inside its own border box — which is invisible
 * from outside the control and, at 2 px in on a 44 px button, sat under the white `::before` fill at
 * the edges. #38's own instruction is the reason this uses `edgesWithoutRing`: an assertion that reads
 * `outline-width` back off the computed style is testing the stylesheet rather than the screen, and
 * lesson L4 is a ring an ancestor was clipping away while exactly that assertion passed.
 */
test.describe('focus rings (#38)', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  test('the product card’s wishlist heart has a ring on every edge', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/women/jeans');

    const heart = page.locator('.card__wish').first();
    await expect(heart).toBeVisible();
    // A real key press, so `:focus-visible` is matched the way a keyboard user matches it — which a
    // programmatic `focus()` after a mouse interaction does not reliably do in Chromium.
    await heart.evaluate((element: HTMLElement) => element.focus({ preventScroll: true }));
    await page.keyboard.press('Shift+Tab');
    await page.keyboard.press('Tab');
    await expect(heart).toBeFocused();

    expect(await edgesWithoutRing(page, heart)).toEqual([]);
  });

  /** The same control on the home page's product rows, which is where #38 was found. */
  test('the wishlist heart on the home page has one too', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/');

    const heart = page.locator('.card__wish').first();
    await expect(heart).toBeVisible();
    await heart.evaluate((element: HTMLElement) => element.focus({ preventScroll: true }));
    await page.keyboard.press('Shift+Tab');
    await page.keyboard.press('Tab');
    await expect(heart).toBeFocused();

    expect(await edgesWithoutRing(page, heart)).toEqual([]);
  });
});

/**
 * **#20's SH12: at 320 px the announcement text wrapped to three lines once a third button shared the
 * row.**
 *
 * The issue suggested hiding Previous and Next below 375 px. Only Previous is hidden, and the
 * difference is the point: under `prefers-reduced-motion` the bar does not rotate, so the pause
 * control is not drawn at all — with both arrows gone as well, the second and third messages would be
 * unreachable on a 320 px phone. That is content a visitor cannot get to rather than a layout nicety.
 * Next on its own still reaches every message, because it cycles.
 */
test.describe('the announcement bar at 320px (#20 SH12)', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await useStubApi(page);
  });

  test('the message fits in two lines', async ({ page }) => {
    await page.goto('/');

    const lines = await page.locator('[data-testid="announcement"]').evaluate((element) => {
      const style = getComputedStyle(element);
      const lineHeight = parseFloat(style.lineHeight);
      // The element's own min-height is a tap target, so the text's height is what is measured.
      const range = document.createRange();
      range.selectNodeContents(element);
      return Math.round(range.getBoundingClientRect().height / lineHeight);
    });

    expect(lines).toBeLessThanOrEqual(2);
  });

  test('Previous is the only control that goes, and Next and Pause still work', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByRole('button', { name: 'Previous announcement' })).toBeHidden();
    await expect(page.getByRole('button', { name: 'Next announcement' })).toBeVisible();
    await expect(page.locator('[data-testid="announcement-pause"]')).toBeVisible();
  });

  /**
   * The reason Previous and not both: with the rotation off, the pause button is not rendered, and
   * Next is then the only way to the other messages.
   */
  /**
   * **Why this cycles rather than clicking twice and reading.** The first version clicked and then
   * read `textContent()` straight away, twice. It passed on Windows and failed on Linux CI in roughly
   * three runs of five, always `Expected: 3, Received: 2` — because the bar is **server-rendered**, so
   * Playwright finds the button and considers it actionable before Angular has hydrated. A click that
   * lands on inert markup does nothing, and a read taken immediately after a click that *did* land can
   * still return the previous message. Either way two of the three reads are the same string.
   *
   * That is #89's family: acting on markup the server already sent, with no gate on the page's own
   * state having been applied. The fix is to wait for the message to **change** after each click, and
   * to tolerate a click that was lost — which is what makes this honest about the property being
   * tested. The property is *"all three are reachable with Next alone"*, not *"exactly two clicks
   * visit all three"*; the bar cycles, so revisiting one costs nothing.
   */
  test('every message is still reachable under reduced motion', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.goto('/');

    const message = page.locator('[data-testid="announcement"]');
    const next = page.getByRole('button', { name: 'Next announcement' });
    const read = async () => (await message.textContent())?.trim() ?? '';

    const seen = new Set<string>([await read()]);
    let last = '';

    // Three messages cycling, so three effective clicks reach all of them; the spare attempts absorb
    // clicks lost to hydration. A bound rather than a while-true, so a bar that stops advancing fails
    // on the assertion below instead of timing the suite out with no diagnosis.
    for (let attempt = 0; attempt < 8 && seen.size < 3; attempt += 1) {
      const before = await read();
      await next.click();

      try {
        await expect(message).not.toHaveText(before, { timeout: 2_000 });
      } catch {
        // The click did not take — almost always hydration on a loaded runner. Try again.
        continue;
      }

      last = await read();
      seen.add(last);
    }

    expect(seen.size).toBe(3);
    // And the visually hidden status says what changed, which is the only thing that announces.
    await expect(page.locator('[data-testid="announcement-status"]')).toHaveText(last);
  });

  test('the controls that remain still meet the 44px touch minimum', async ({ page }) => {
    await page.goto('/');

    expect(await tapTargets(page, '.announce button')).toEqual([]);
  });
});

/**
 * The breakpoint sweep GLB-09 and GLB-12 ask for, on the pages this task changed: no horizontal
 * scroll, and every control in the shell at or above the 44 px touch minimum.
 */
test.describe('the shell at every breakpoint', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  for (const width of BREAKPOINTS) {
    test(`${width}px: the not-found page has no horizontal scroll and usable controls`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/not-a-dept');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
      // Every control the page draws, not the ones it happens to style as buttons: the 503 page's own
      // version of this assertion was scoped to `.btn` and missed five 20 px links beside them.
      expect(await tapTargets(page, '.missing a, .missing .btn')).toEqual([]);
    });
  }

  for (const width of BREAKPOINTS) {
    test(`${width}px: the announcement bar and header have no horizontal scroll`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');
      await expect(page.locator('[data-testid="announcement"]')).toBeVisible();

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    });
  }
});
