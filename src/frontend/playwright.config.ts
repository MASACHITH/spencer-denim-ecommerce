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
 * The listing that exists live and not in `core/navigation/navigation.data.ts`.
 *
 * Every other listing URL is in both trees, so only this one can tell "the catalogue does not have
 * it" from "I could not find out".
 */
export const LIVE_ONLY_LISTING = '/women/coats';

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  fullyParallel: true,
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
