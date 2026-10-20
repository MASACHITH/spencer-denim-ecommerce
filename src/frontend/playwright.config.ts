import { createHash } from 'node:crypto';
import { resolve } from 'node:path';

import { defineConfig, devices } from '@playwright/test';

/**
 * Browser tests for the storefront shell.
 *
 * The unit suite runs in jsdom, which has no layout and no media queries, so it cannot see the
 * class of bug these tests exist for: an element that a stylesheet has not finished revealing
 * cannot take focus (lesson L3). Anything that depends on real CSS — `prefers-reduced-motion`,
 * visibility, layout — belongs here rather than in a `.spec.ts`.
 *
 * The tests run against the built SSR server, which the config starts and stops itself, so
 * `npm run test:e2e` needs nothing running beforehand beyond `npm run build:storefront`.
 *
 * The listing is rendered on the server, so the SSR process needs a catalogue before it can
 * answer at all; `e2e/support/catalog-api-stub.mjs` is it, and it says why it exists rather than
 * the real API.
 */
const PORT = Number(process.env['E2E_PORT'] ?? 4420);
const API_PORT = Number(process.env['E2E_API_PORT'] ?? 4421);
const baseURL = `http://localhost:${PORT}`;

/** Exported so a test can send the browser's own `/api` calls to the same stub. */
export const apiURL = `http://localhost:${API_PORT}`;

/**
 * A second pair of servers whose catalogue answers `200 []` — a freshly migrated database before
 * anyone runs the seeder, which the README says is off by default. The storefront has to render
 * from its seeded fallback there, not send every listing link home.
 */
const EMPTY_PORT = Number(process.env['E2E_EMPTY_PORT'] ?? 4422);
const EMPTY_API_PORT = Number(process.env['E2E_EMPTY_API_PORT'] ?? 4423);

export const emptyCatalogueURL = `http://localhost:${EMPTY_PORT}`;

/**
 * A third pair whose catalogue is deep enough for a restore to be worth restoring: 399 products,
 * so `?pages=5` spans two requests and the second one has to start where the first stopped.
 */
const DEEP_PORT = Number(process.env['E2E_DEEP_PORT'] ?? 4424);
const DEEP_API_PORT = Number(process.env['E2E_DEEP_API_PORT'] ?? 4425);

export const deepCatalogueURL = `http://localhost:${DEEP_PORT}`;

/** What the deep stub holds, so a test can work out what a page count should show. */
export const DEEP_CATALOGUE_TOTAL = 399;

/**
 * A fourth pair whose catalogue answers every product request with a 500. The product page has to
 * tell "this product is not there" (404) from "the store is broken" (503 with `Retry-After`), and
 * the status on the wire is the part only a real server can show.
 */
const FAULTY_PORT = Number(process.env['E2E_FAULTY_PORT'] ?? 4426);
const FAULTY_API_PORT = Number(process.env['E2E_FAULTY_API_PORT'] ?? 4427);

export const faultyCatalogueURL = `http://localhost:${FAULTY_PORT}`;

/**
 * A fifth pair whose **content** service is down while the catalogue is fine. The homepage is
 * the one page whose whole body comes from that service, and it still has to render the shell
 * and the way into the shop at a real 200 rather than a blank screen (HOME-01..10).
 */
const CONTENT_FAULT_PORT = Number(process.env['E2E_CONTENT_FAULT_PORT'] ?? 4428);
const CONTENT_FAULT_API_PORT = Number(process.env['E2E_CONTENT_FAULT_API_PORT'] ?? 4429);

export const faultyContentURL = `http://localhost:${CONTENT_FAULT_PORT}`;

/** Exported so a browser-side route can send the page's own calls to the same failing stub. */
export const faultyContentApiURL = `http://localhost:${CONTENT_FAULT_API_PORT}`;

/**
 * A sixth pair whose catalogue holds a category the storefront's frozen fallback has never heard
 * of, and whose **departments** feed can be made unhealthy while the server runs (#30).
 *
 * The other pairs fix their fault with an environment variable. This one cannot: #30 lists four
 * unhealthy modes and each has to be measured on the wire, so an environment variable would mean
 * four more SSR servers on top of the five this file already starts. The stub takes the mode from
 * `/__test__/departments-fault?mode=...` instead, which is why `catalog-availability.e2e.ts` runs
 * serially.
 */
const DEPT_FAULT_PORT = Number(process.env['E2E_DEPT_FAULT_PORT'] ?? 4432);
const DEPT_FAULT_API_PORT = Number(process.env['E2E_DEPT_FAULT_API_PORT'] ?? 4433);

export const liveOnlyCatalogueURL = `http://localhost:${DEPT_FAULT_PORT}`;

/** Exported so a test can switch the fault, and send the browser's own calls to the same stub. */
export const liveOnlyApiURL = `http://localhost:${DEPT_FAULT_API_PORT}`;

/**
 * A seventh pair for the content pages and the contact form (W2-11, GLB-10).
 *
 * **It exists because that spec switches state in the stub process, and the main stub is shared.**
 * `/__test__/content-pages` and `/__test__/site-settings` set module-level variables, and
 * `fullyParallel` is on — so with the spec on the main pair, a test that published all six pages did it
 * for every other test running at that moment, in every other file, on every worker. The W2-11 tester
 * measured it: the full suite scored 856/7 and then 859/4 with a *different* set of failures, and
 * 863 passed, 0 failed at `--workers=1`. No product defect was behind any of it.
 *
 * This is the sixth pair's reasoning applied a second time, and the convention it already set: a stub
 * with a runtime switch gets its own process, and the spec that turns the switch runs serially against
 * it. The alternative — keying every switch to a cookie so the state could be per test — would have to
 * survive the **server** render, which is where the storefront reads both of these feeds, so the cookie
 * would have to be forwarded by the SSR server. That is a bigger change to the shell than the problem
 * justifies.
 */
const CONTENT_PAGES_PORT = Number(process.env['E2E_CONTENT_PAGES_PORT'] ?? 4434);
const CONTENT_PAGES_API_PORT = Number(process.env['E2E_CONTENT_PAGES_API_PORT'] ?? 4435);

export const contentPagesURL = `http://localhost:${CONTENT_PAGES_PORT}`;

/** Exported so a test can turn the switch, and send the browser's own calls to the same stub. */
export const contentPagesApiURL = `http://localhost:${CONTENT_PAGES_API_PORT}`;

/**
 * An eighth pair for the branding and settings spec (ADM-22), for the same reason as the seventh.
 *
 * `site-branding.e2e.ts` switches `/__test__/site-settings` — the logo, the social addresses, the
 * WhatsApp number — on a stub every other spec shares, and the shell reads those settings on every
 * page. It was already `mode: 'serial'`, which orders its own tests and does nothing about the other
 * files running beside it. With the content-pages spec moved off the main pair, this was the last
 * writer left on it, and the suite still scored 863/2 in parallel against 865/0 at `--workers=1`, with
 * both failures in specs that pass 159/159 on their own: `product-page` and `search`, which render the
 * header and footer from exactly these settings.
 *
 * So this one moves too, and the main pair now has no test that writes to it.
 */
const BRANDING_PORT = Number(process.env['E2E_BRANDING_PORT'] ?? 4436);
const BRANDING_API_PORT = Number(process.env['E2E_BRANDING_API_PORT'] ?? 4437);

export const brandingURL = `http://localhost:${BRANDING_PORT}`;

/** Exported so a test can turn the switch, and send the browser's own calls to the same stub. */
export const brandingApiURL = `http://localhost:${BRANDING_API_PORT}`;

/**
 * The mega-menu pair (#215). Its stub's `/__test__/mega-menu-cards?state=â€¦` switch puts admin cards on the
 * departments feed the SSR server reads, so the spec can measure the server-rendered HTML â€” the only place
 * escaping is proved â€” as well as the browser. A pair of its own for the reason the branding pair gives: a
 * switch on a stub every other spec shares changes the header under them.
 */
const MEGAMENU_PORT = Number(process.env['E2E_MEGAMENU_PORT'] ?? 4438);
const MEGAMENU_API_PORT = Number(process.env['E2E_MEGAMENU_API_PORT'] ?? 4439);

export const megaMenuURL = `http://localhost:${MEGAMENU_PORT}`;

/** Exported so a test can turn the switch, and send the browser's own calls to the same stub. */
export const megaMenuApiURL = `http://localhost:${MEGAMENU_API_PORT}`;

/**
 * The listing that exists live and not in `core/navigation/navigation.data.ts`.
 *
 * Every other listing URL is in both trees, so only this one can tell "the catalogue does not have
 * it" from "I could not find out".
 */
export const LIVE_ONLY_LISTING = '/women/coats';

/**
 * The admin application and a stand-in for its API, on one origin (ADM-11, W1-12).
 *
 * The admin app is a plain SPA with no server of its own, and its session and antiforgery cookies are both
 * same-origin, so one process serves `dist/admin` and answers `/api/admin/*`. See
 * `e2e/support/admin-catalog-stub.mjs` for why it is a stub and what it refuses.
 */
/**
 * A port of its own per worktree, derived from the worktree's path.
 *
 * **Why, concretely.** `reuseExistingServer` is on outside CI, so a fixed port means a server another
 * worktree started answers this run's requests — serving that worktree's `dist/admin` against this one's
 * tests. It cost this branch a run of 113 failures before it was understood, and the size-guide branch
 * learned the same lesson on this same port number.
 *
 * A derived port makes a collision **unlikely, not impossible**: 60 slots is about a 1-in-60 chance per
 * pair of worktrees, and with seven of them on this machine something like a 1-in-5 chance that some pair
 * shares one — at which point `reuseExistingServer` reproduces the original bug silently. So the suite does
 * not rely on the arithmetic: `admin-catalog.e2e.ts` asks the server which worktree it is serving before it
 * trusts a single answer from it. `E2E_ADMIN_PORT` still overrides the port for anyone who wants a fixed
 * one.
 *
 * The band starts at 4500 so it cannot drift into 4420–4429, which the storefront servers above hold.
 * Those are fixed and shared, and this is not the task that changes them — but they have the same hazard,
 * and whoever hits it next should move them the same way rather than re-deriving the diagnosis.
 */
function portForThisWorktree(base: number): number {
  // `__dirname` rather than `import.meta.dirname`: Playwright loads this config as CommonJS, where
  // `import.meta` is a syntax error. The worktree root is two levels above `src/frontend`.
  const digest = createHash('sha256').update(resolve(__dirname, '..', '..')).digest();

  return base + ((digest[0] << 8) | digest[1]) % 60;
}

const ADMIN_PORT = Number(process.env['E2E_ADMIN_PORT'] ?? portForThisWorktree(4500));

export const adminURL = `http://localhost:${ADMIN_PORT}`;

/** The worktree this configuration belongs to, so a test can check it is talking to its own server. */
export const adminWorktreeRoot = resolve(__dirname, '..', '..');

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  fullyParallel: true,

  // **A quarter of the cores, not Playwright's default half, and it is measured rather than cautious.**
  //
  // This config starts **sixteen** web servers — eight storefront SSR processes and eight stubs — and
  // every one of them is a Node process competing with the browsers for the same CPUs. The default of
  // 50% then oversubscribes the machine, and what that looks like is not a slow run: it looks like
  // three or four tests failing per run, in a **different** set every time, all of them timing
  // assertions — `toBeVisible`, a 44px measurement, an axe scan, "keeps Tab inside the panel". That
  // reads exactly like a product defect and is not one.
  //
  // Measured on 8 logical CPUs, this branch, after the state interference above was fixed: default
  // workers 863/2 then 861/4 then 862/3, each a different set; `--workers=2` **865 passed, 0 failed**
  // in 7.3 min; `--workers=1` 865/0 in 11.0 min. So a quarter is both the reliable setting and faster
  // than serial. A percentage rather than a number because CI's machine is not this one.
  //
  // Raising this is a decision about hardware. If a run is slow, the honest fix is fewer servers, not
  // more workers: every pair above says why it exists, and the newest two say what it cost.
  workers: '25%',

  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  // On CI: annotations on the PR plus an HTML report to upload when something fails.
  // 'github' alone writes no files, so the failure artefact would always be empty.
  reporter: process.env['CI'] ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'node e2e/support/admin-catalog-stub.mjs',
      url: `${adminURL}/api/admin/catalog/departments`,
      env: { E2E_ADMIN_PORT: String(ADMIN_PORT) },
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `${apiURL}/api/catalog/departments`,
      env: { E2E_API_PORT: String(API_PORT) },
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${baseURL}/healthz`,
      env: { PORT: String(PORT), API_INTERNAL_URL: apiURL, E2E_MEDIA_PASSTHROUGH_URL: apiURL },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
    {
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `http://localhost:${EMPTY_API_PORT}/api/catalog/departments`,
      env: { E2E_API_PORT: String(EMPTY_API_PORT), E2E_DEPARTMENTS: 'empty' },
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${emptyCatalogueURL}/healthz`,
      env: {
        PORT: String(EMPTY_PORT),
        API_INTERNAL_URL: `http://localhost:${EMPTY_API_PORT}`,
        E2E_MEDIA_PASSTHROUGH_URL: `http://localhost:${EMPTY_API_PORT}`,
      },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
    {
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `http://localhost:${DEEP_API_PORT}/api/catalog/departments`,
      env: {
        E2E_API_PORT: String(DEEP_API_PORT),
        E2E_PRODUCT_TOTAL: String(DEEP_CATALOGUE_TOTAL),
        // This pair also reports two of the sitemap's three caps as reached (GLB-11). It belongs here
        // rather than on a seventh pair: the deep catalogue is the one that exists because the shop is
        // big, and "big enough that one sitemap cannot list it" is the same idea. Nothing else reads
        // this pair's sitemap.
        E2E_SITEMAP_TRUNCATED: 'categories,products',
      },
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${deepCatalogueURL}/healthz`,
      env: {
        PORT: String(DEEP_PORT),
        API_INTERNAL_URL: `http://localhost:${DEEP_API_PORT}`,
        E2E_MEDIA_PASSTHROUGH_URL: `http://localhost:${DEEP_API_PORT}`,
      },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
    {
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `http://localhost:${CONTENT_FAULT_API_PORT}/api/catalog/departments`,
      env: { E2E_API_PORT: String(CONTENT_FAULT_API_PORT), E2E_CONTENT_STATUS: '500' },
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${faultyContentURL}/healthz`,
      env: {
        PORT: String(CONTENT_FAULT_PORT),
        API_INTERNAL_URL: faultyContentApiURL,
        E2E_MEDIA_PASSTHROUGH_URL: faultyContentApiURL,
      },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
    {
      // Waiting on the fault switch rather than on `/departments`, which is the one endpoint this
      // stub is asked to break: `reuseExistingServer` would otherwise hand a run a stub left
      // mid-fault by a failed one, and under `destroy` a readiness probe on `/departments` never
      // answers at all. Probing this resets the fault, so every run starts healthy.
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `${liveOnlyApiURL}/__test__/departments-fault?mode=none`,
      env: { E2E_API_PORT: String(DEPT_FAULT_API_PORT), E2E_DEPARTMENTS: 'live-only' },
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${liveOnlyCatalogueURL}/healthz`,
      env: {
        PORT: String(DEPT_FAULT_PORT),
        API_INTERNAL_URL: liveOnlyApiURL,
        E2E_MEDIA_PASSTHROUGH_URL: liveOnlyApiURL,
      },
      // `reuseExistingServer: false`, unlike every pair above, and it is not a preference.
      // Measured: this pair was first written on 4430, another session on this machine then started
      // the **admin** application there, its dev server answered `/healthz` with 200, and Playwright
      // reused it — so the whole suite ran against the admin panel and the touch-target test failed
      // reporting "Try again" missing from a page that was never the storefront. A port that
      // answers is not proof that it is yours (doc 14). Refusing to reuse turns that into a bind
      // error naming the port instead of a silent pass against a stranger's process; it costs
      // nothing, because Playwright stops the servers it starts at the end of a run anyway.
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      // The branding pair. See `brandingURL` for why this spec cannot share a stub, and the
      // departments-fault pair for why a stub with a switch is never reused between runs.
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `${brandingApiURL}/__test__/site-settings?logo=none&social=none`,
      env: { E2E_API_PORT: String(BRANDING_API_PORT) },
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${brandingURL}/healthz`,
      env: {
        PORT: String(BRANDING_PORT),
        API_INTERNAL_URL: brandingApiURL,
        E2E_MEDIA_PASSTHROUGH_URL: brandingApiURL,
      },
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      // The mega-menu pair (#215). Not reused, and the readiness probe resets the switch, as the pairs above.
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `${megaMenuApiURL}/__test__/mega-menu-cards?state=none`,
      env: { E2E_API_PORT: String(MEGAMENU_API_PORT) },
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${megaMenuURL}/healthz`,
      env: {
        PORT: String(MEGAMENU_PORT),
        API_INTERNAL_URL: megaMenuApiURL,
        E2E_MEDIA_PASSTHROUGH_URL: megaMenuApiURL,
      },
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      // The content-pages pair. `reuseExistingServer: false` and a readiness probe that *resets* the
      // switch, for the same reason the departments-fault stub above does it: reusing a stub left
      // mid-state by a failed run is how a suite goes green against the wrong bytes.
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `${contentPagesApiURL}/__test__/content-pages?state=none`,
      env: { E2E_API_PORT: String(CONTENT_PAGES_API_PORT) },
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${contentPagesURL}/healthz`,
      env: {
        PORT: String(CONTENT_PAGES_PORT),
        API_INTERNAL_URL: contentPagesApiURL,
        E2E_MEDIA_PASSTHROUGH_URL: contentPagesApiURL,
      },
      // Not reused, for the reason the pair above gives at length: a port that answers /healthz is not
      // proof that it is the storefront, and a silent pass against a stranger's process is worse than
      // a bind error naming the port.
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: 'node e2e/support/catalog-api-stub.mjs',
      url: `http://localhost:${FAULTY_API_PORT}/api/catalog/departments`,
      env: { E2E_API_PORT: String(FAULTY_API_PORT), E2E_PRODUCT_STATUS: '500' },
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'npm run serve:ssr:storefront',
      url: `${faultyCatalogueURL}/healthz`,
      env: {
        PORT: String(FAULTY_PORT),
        API_INTERNAL_URL: `http://localhost:${FAULTY_API_PORT}`,
        E2E_MEDIA_PASSTHROUGH_URL: `http://localhost:${FAULTY_API_PORT}`,
      },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
