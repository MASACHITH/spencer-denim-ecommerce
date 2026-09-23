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
      env: { PORT: String(PORT), API_INTERNAL_URL: apiURL },
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
      env: { PORT: String(DEEP_PORT), API_INTERNAL_URL: `http://localhost:${DEEP_API_PORT}` },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
