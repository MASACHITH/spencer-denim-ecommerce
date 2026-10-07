/**
 * A stand-in for the admin API, and a static host for the built admin application, while the browser tests
 * run (ADM-11, W1-12).
 *
 * The admin app is a plain SPA — no SSR, no server of its own — so the browser suite needs something to
 * serve `dist/admin` with an index fallback, and something to answer `/api/admin/*` from the same origin
 * (the session cookie and the antiforgery cookie are both same-origin, and in a deployment Caddy puts the
 * app and the API on one host). This is both, in one process, so one `webServer` entry starts it.
 *
 * **Why a stub rather than the real API.** Starting .NET would mean a SQL Server instance, migrations and a
 * seeded catalogue for every `npx playwright test`. What these tests need is a predictable tree at a
 * predictable shape; the endpoint behaviour itself is proved against a real SQL Server in
 * `AdminCatalogEndpointTests` and `AdminDeactivationEffectTests`.
 *
 * **It is deliberately not more polite than the real API**, and fix round 1 made that true rather than
 * merely claimed. It answers the same field names (`SpencerDenim.Application.Catalog.Admin.Contracts`), the
 * same statuses and the same RFC 9457 bodies with a `code` beside the sentence, and it enforces the four
 * refusals a screen can actually get wrong:
 *
 * - no `X-XSRF-TOKEN` on a mutating request is a 400, as all eight real write routes answer;
 * - a missing or stale row version is a 409 `stale_row_version` on the five routes that take one, and a
 *   successful write moves the row version, so a screen that re-sent a stale token would be caught;
 * - a sibling list that is not the set the server holds is a 409 `stale_order`;
 * - a category something still points at is a 409 `category_in_use`.
 *
 * The first two were missing, and the consequence was precise: if the screen had stopped sending the
 * antiforgery header or the row version, all 246 browser tests would still have passed.
 *
 * `PUT /categories/{id}` also honours `parentCategoryId` and re-paths the subtree, so re-parenting — the
 * largest thing this screen does — has browser coverage at all.
 */
import { readFileSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve } from 'node:path';

import { freshProductState, handleProductRoute } from './admin-product-stub.mjs';

const PORT = Number(process.env.E2E_ADMIN_PORT ?? 4430);
const DIST = resolve(process.env.E2E_ADMIN_DIST ?? 'dist/admin/browser');

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

/** The signed-in staff user every test runs as: no pending password change, no pending enrolment. */
const SESSION = {
  userId: 7,
  email: 'catalogue@example.com',
  firstName: 'Cata',
  lastName: 'Logue',
  grantsAllPermissions: true,
  permissions: [
    'Categories.View',
    'Categories.Create',
    'Categories.Edit',
    'Categories.Delete',

    // ADM-10 (W1-03): the product screens.
    'Products.View',
    'Products.Create',
    'Products.Edit',
    'Products.Delete',

    // ADM-22. Settings.Edit and Media.Create: the settings screen's logo flow uploads to the library before
    // it points the setting at the result, so both are needed to drive it.
    'Settings.Edit',
    'Media.Create',
  ],
  mustChangePassword: false,
  twoFactorEnabled: true,
  twoFactorEnrolmentRequired: false,
  twoFactorRequired: false,
};

/** The token this stub issues; a mutating request has to carry it, as the real API requires. */
const ANTIFORGERY_TOKEN = 'e2e-antiforgery-token';

/** `rowversion` is eight bytes; the shape matters because the screen round-trips it untouched. */
function rowVersion(seed) {
  return Buffer.from([0, 0, 0, 0, 0, 0, 0, seed]).toString('base64');
}

/** Moves a row version on, so a token a screen cached before a write is genuinely stale afterwards. */
let rowVersionTick = 200;

function nextRowVersion() {
  rowVersionTick += 1;
  return rowVersion(rowVersionTick);
}

/**
 * The row version the caller is working from: `If-Match` if it sent one, otherwise the body's own field —
 * the same order `AdminWriteRequest.RowVersion` reads them in, quotes and all.
 *
 * One input is read differently, and it is written down rather than chased: for `If-Match: ""abc""` the
 * real reader's `Trim('"')` strips every quote and gives `abc`, while this strips one from each end and
 * gives `"abc"`. Nothing sends a doubly quoted ETag — the screen builds the header itself from a base64
 * token — so the divergence is unreachable, and making the stub match exactly would mean reimplementing
 * `Trim` rather than reading a header.
 */
function suppliedRowVersion(request, body) {
  const header = request.headers['if-match'];
  if (typeof header === 'string' && header.trim().length > 0) {
    return header.trim().replace(/^W\//i, '').replace(/^"|"$/g, '');
  }

  return body && typeof body.rowVersion === 'string' ? body.rowVersion : null;
}

function department(id, name, slug, sortOrder, isActive, categoryCount, productCount) {
  return {
    id,
    name,
    slug,
    description: `${name} description.`,

    // Not null, so a save that drops the field cannot round-trip null to null unnoticed — the shape of the
    // fix-round-1 blocker.
    heroImageAssetId: 41,
    sortOrder,
    isActive,
    seoTitle: `${name} | Colombo Wardrobe`,
    seoDescription: null,
    rowVersion: rowVersion(id),
    categoryCount,
    storefrontProductCount: productCount,
  };
}

function category(id, name, slug, parentCategoryId, depth, path, sortOrder, options = {}) {
  return {
    id,
    departmentId: 1,
    parentCategoryId,
    path,
    depth,
    name,
    slug,
    description: null,
    imageAssetId: null,
    bannerAssetId: null,
    bannerLinkUrl: null,
    ageGroupId: null,
    sizeGuideId: null,
    sortOrder,
    showInMegaMenu: true,
    megaMenuColumn: null,
    isActive: options.isActive ?? true,
    seoTitle: null,
    seoDescription: null,
    rowVersion: rowVersion(id),
    productCount: options.productCount ?? 0,
    primaryProductCount: options.primaryProductCount ?? 0,
    children: options.children ?? [],
  };
}

/** Rebuilt per run, because the tests write to it. */
function freshState() {
  return {
    departments: [
      department(1, 'Women', 'women', 0, true, 4, 24),
      department(2, 'Men', 'men', 1, true, 2, 16),
      department(3, 'Kids', 'kids', 2, false, 1, 8),
    ],
    categories: {
      1: [
        category(10, 'Dresses', 'dresses', null, 1, '/10/', 0, {
          productCount: 6,
          primaryProductCount: 6,
          children: [
            category(11, 'Midi dresses', 'midi-dresses', 10, 2, '/10/11/', 0, {
              productCount: 3,
              primaryProductCount: 3,
            }),
            category(12, 'Maxi dresses', 'maxi-dresses', 10, 2, '/10/12/', 1, {
              productCount: 2,
              primaryProductCount: 2,
            }),
          ],
        }),
        category(20, 'Jeans', 'jeans', null, 1, '/20/', 1, {
          productCount: 5,
          primaryProductCount: 5,
        }),
        category(30, 'Traditional wear', 'traditional-wear', null, 1, '/30/', 2, {
          isActive: false,
        }),
        category(40, 'Knitwear', 'knitwear', null, 1, '/40/', 3),
      ],
      2: [
        category(50, 'Shirts', 'shirts', null, 1, '/50/', 0, { productCount: 7, primaryProductCount: 7 }),
        category(60, 'Shorts', 'shorts', null, 1, '/60/', 1, { productCount: 2, primaryProductCount: 2 }),
      ],
      3: [category(70, 'Girls', 'girls', null, 1, '/70/', 0, { productCount: 4, primaryProductCount: 4 })],
    },

    // ADM-22. Every value null, which is how the real seeder leaves them: the client has uploaded no logo
    // and sent no profile addresses. Any other starting state would hide the fallback the screen spends
    // most of its life in.
    settings: {
      'Store.LogoAssetId': null,
      'Contact.WhatsApp': null,
      'Social.FacebookUrl': null,
      'Social.InstagramUrl': null,
      'Social.TikTokUrl': null,

      // PAY-01 / doc 11 P7's preconditions. Seeded null, like the real seeder: the client fills them in.
      'Bank.BankName': null,
      'Bank.Branch': null,
      'Bank.AccountName': null,
      'Bank.AccountNumber': null,
      'Bank.Instructions': null,
    },
    logo: null,
    uploads: new Map(),

    // PAY-01 / ADM-09 (W3-03): doc 11 P7 step 4's queue. Two rows, deliberately different — one with a
    // reference and a slip and most of its window left, one overdue with neither — because the screen
    // renders a different action and a different pill for each, and a fixture of two identical rows would
    // exercise one of them twice.
    bankTransfers: [
      {
        orderPublicId: '44444444-4444-4444-8444-444444444444',
        orderNumber: 'SD-2026-000123',
        customerName: 'Nimali Perera',
        customerEmail: 'nimali@example.com',
        amountLkr: 15596.6,
        currency: 'LKR',
        payerReference: 'CBC-778812',
        slipAssetPublicId: '66666666-6666-4666-8666-666666666666',
        slipContentType: 'image/jpeg',
        placedAtUtc: '2026-10-05T04:30:00Z',
        paymentDueAtUtc: '2026-10-07T04:30:00Z',
        secondsUntilExpiry: 19 * 60 * 60,
      },
      {
        orderPublicId: '55555555-5555-4555-8555-555555555555',
        orderNumber: 'SD-2026-000124',
        customerName: 'Ruwan Jayasuriya',
        customerEmail: 'ruwan@example.com',
        amountLkr: 4290,
        currency: 'LKR',
        payerReference: null,
        slipAssetPublicId: null,
        slipContentType: null,
        placedAtUtc: '2026-10-04T06:00:00Z',
        paymentDueAtUtc: '2026-10-06T06:00:00Z',
        secondsUntilExpiry: -37 * 60,
      },
    ],

    // What the next decision answers. A fixture switch rather than a payload, so a test names a *state*
    // and this file owns what that state's bytes are — the rule the site-settings switch follows.
    bankTransferOutcome: 'ok',

    // ADM-10 (W1-03). Its own module, because this file is already a thousand lines about ADM-11.
    ...freshProductState(),
  };
}

/**
 * The settings groups, built from the state's values (ADM-22).
 *
 * <b>The row version is per *save unit*, not per group</b>, exactly as the real read model publishes it:
 * `Contact` and `Social` are one form with one Save button, so their token covers both groups' rows. The first
 * version of the real API got this wrong and answered 409 to every valid save; a stub that published a
 * per-group token would let the screen regress to it with every browser test still passing.
 */
function settingsPayload(state) {
  const groups = [
    { groupKey: 'Store', keys: ['Store.LogoAssetId'], types: { 'Store.LogoAssetId': 'MediaAssetRef' } },
    { groupKey: 'Contact', keys: ['Contact.WhatsApp'], types: {} },
    {
      groupKey: 'Social',
      keys: ['Social.FacebookUrl', 'Social.InstagramUrl', 'Social.TikTokUrl'],
      types: {},
    },

    // PAY-01. Its own group and its own save unit, because it is the one group guarded by a different
    // permission (Settings.Payments) — see SettingCatalog.SaveUnits.
    {
      groupKey: 'Bank',
      keys: [
        'Bank.BankName',
        'Bank.Branch',
        'Bank.AccountName',
        'Bank.AccountNumber',
        'Bank.Instructions',
      ],
      types: {},
      permission: 'Settings.Payments',
    },
  ];

  const bankKeys = [
    'Bank.BankName',
    'Bank.Branch',
    'Bank.AccountName',
    'Bank.AccountNumber',
    'Bank.Instructions',
  ];

  const unitFor = (groupKey) => {
    if (groupKey === 'Store') {
      return ['Store.LogoAssetId'];
    }

    if (groupKey === 'Bank') {
      return bankKeys;
    }

    return ['Contact.WhatsApp', 'Social.FacebookUrl', 'Social.InstagramUrl', 'Social.TikTokUrl'];
  };

  const tokenFor = (groupKey) =>
    Buffer.from(unitFor(groupKey).map((key) => `${key}=${state.settings[key] ?? ''}`).join('|')).toString('base64');

  return {
    groups: groups.map((group) => ({
      groupKey: group.groupKey,
      rowVersion: tokenFor(group.groupKey),
      settings: group.keys.map((key) => ({
        key,
        valueType: group.types[key] ?? 'String',
        value: state.settings[key],
        isSecret: false,
        hasValue: Boolean(state.settings[key]),
        requiredPermission: group.permission ?? 'Settings.Edit',
        description: `What ${key} is for.`,
        updatedAtUtc: null,
      })),
    })),
    logo: state.logo,
  };
}

/** The token a save of `groupKey` must carry, so a stale one is genuinely refused. */
function settingsToken(state, groupKey) {
  return settingsPayload(state).groups.find((group) => group.groupKey === groupKey).rowVersion;
}

/**
 * The same absolute-URL rule the API applies (`SettingFieldRules.IsAcceptableProfileUrl`).
 *
 * The stub enforces it because without it the screen could stop showing field errors and every browser test
 * would still pass — the shape of gap that let W1-12's antiforgery header go unchecked for 246 tests.
 */
function acceptableProfileUrl(value) {
  if (typeof value !== 'string') {
    return false;
  }

  const candidate = value.trim();
  if (candidate.length === 0 || /[\\\u0000-\u001f\s]/u.test(candidate)) {
    return false;
  }

  try {
    const parsed = new URL(candidate);
    return (
      (parsed.protocol === 'https:' || parsed.protocol === 'http:') &&
      parsed.hostname.length > 0 &&
      parsed.username.length === 0
    );
  } catch {
    return false;
  }
}

function acceptableWhatsApp(value) {
  return typeof value === 'string' && /^[1-9][0-9]{6,14}$/.test(value.trim());
}

/**
 * One state per browser context, keyed by the `e2e_state` cookie each test sets to its worker index.
 *
 * <b>Why not one shared state.</b> Playwright runs this file's tests in parallel across workers, and the
 * tests reorder and remove things; with a single mutable state one worker's `__reset` wiped another
 * worker's tree mid-test and a click timed out waiting for a row that had just been recreated. The stub is
 * one process serving several browsers, so the state belongs to the browser rather than to the process.
 */
const states = new Map();

function stateKey(request) {
  const cookies = request.headers.cookie ?? '';
  const match = cookies.match(/(?:^|;\s*)e2e_state=([^;]+)/);
  return match ? match[1] : 'shared';
}

function stateFor(request) {
  const key = stateKey(request);
  if (!states.has(key)) {
    states.set(key, freshState());
  }

  return states.get(key);
}

function json(response, status, body) {
  const payload = JSON.stringify(body);
  response.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'Content-Length': Buffer.byteLength(payload),
  });
  response.end(payload);
}

function problem(response, status, title, detail, code, extra = {}) {
  json(response, status, {
    type: 'about:blank',
    title,
    status,
    detail,
    code,
    ...extra,
  });
}

function noContent(response) {
  response.writeHead(204, { 'Cache-Control': 'no-store' });
  response.end();
}

/** A 1x1 WebP, so the logo preview loads a real image rather than a data URL. */
const LOGO_PIXEL = Buffer.from('UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==', 'base64');

/** Reads a multipart body to the end and hands back its raw text. */
function drain(request) {
  return new Promise((done) => {
    let raw = '';
    request.on('data', (chunk) => {
      raw += chunk;
    });
    request.on('end', () => done(raw));
  });
}

/**
 * The `altText` field of a multipart upload, or null when it was not sent.
 *
 * Echoed rather than invented, because what the library stores decides whether doc 11 §7 G-01's
 * third rule can block a publish. A stub that answered with an alt text of its own made every
 * uploaded photograph look described and quietly disabled the rule in every browser test.
 */
function uploadedAltText(body) {
  const at = body.indexOf('name="altText"');

  if (at < 0) {
    return null;
  }

  // A multipart part's value starts after the blank line that ends its own headers and runs to the
  // next boundary. Parsed by hand because the stub has no dependencies, and only loosely because
  // all it has to tell apart is "a description arrived" from "none did".
  const blank = '\r\n\r\n';
  const headersEnd = body.indexOf(blank, at);

  if (headersEnd < 0) {
    return null;
  }

  const described = body.slice(headersEnd + blank.length).split('\r')[0].trim();

  return described.length > 0 ? described : null;
}

function readBody(request) {
  return new Promise((done) => {
    let raw = '';
    request.on('data', (chunk) => {
      raw += chunk;
    });
    request.on('end', () => {
      try {
        done(raw.length === 0 ? null : JSON.parse(raw));
      } catch {
        done(undefined);
      }
    });
  });
}

/** Every category of a department, flattened depth-first, as the screen sees them. */
function flatten(nodes) {
  return nodes.flatMap((node) => [node, ...flatten(node.children)]);
}

function findSiblings(state, departmentId, parentCategoryId) {
  const roots = state.categories[departmentId] ?? [];
  if (parentCategoryId === null || parentCategoryId === undefined) {
    return roots;
  }

  const parent = flatten(roots).find((node) => node.id === parentCategoryId);
  return parent ? parent.children : null;
}

/**
 * The real API's optimistic-concurrency check: the caller's token has to be the row's current one, and a
 * missing one is refused too (a client that sent none cannot have seen the row). Returns true when the
 * request may go ahead; otherwise it has already answered.
 */
function rowVersionOk(response, request, body, row) {
  const supplied = suppliedRowVersion(request, body);

  if (supplied === row.rowVersion) {
    return true;
  }

  problem(
    response,
    409,
    'This change cannot be applied',
    supplied === null
      ? 'This request did not carry the current version of the row, so it cannot be applied safely.'
      : 'Somebody else changed this while you were editing it. Reload the page to see their change.',
    'stale_row_version',
  );

  return false;
}

/** The server's own guard: the list sent must be exactly the sibling set, or nothing is applied. */
function applyOrder(response, siblings, requested) {
  const actual = siblings.map((node) => node.id);
  const missing = actual.filter((id) => !requested.includes(id));
  const unknown = requested.filter((id) => !actual.includes(id));

  if (missing.length > 0 || unknown.length > 0) {
    problem(
      response,
      409,
      'This change cannot be applied',
      'This order was made from an older version of the list. Reload the page and put them in order again.',
      'stale_order',
      { missingIds: missing, unknownIds: unknown },
    );
    return false;
  }

  const byId = new Map(siblings.map((node) => [node.id, node]));
  siblings.length = 0;
  requested.forEach((id, index) => {
    const node = byId.get(id);
    node.sortOrder = index;
    siblings.push(node);
  });

  return true;
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url, `http://localhost:${PORT}`);
  const path = url.pathname;

  // ---- test control -------------------------------------------------------------------------

  // Which worktree's build this process is serving. A derived port makes a collision unlikely, not
  // impossible, and `reuseExistingServer` means a collision is answered by another worktree's server
  // serving another worktree's `dist/admin` against these tests — which is how a run of 113 failures
  // looked the first time. The suite asks before it trusts a single answer.
  if (path === '/__worktree') {
    // Derived from the working directory, which is `src/frontend`, so it is the same two levels the
    // Playwright config computes the port from — the two values have to agree or the check means nothing.
    return json(response, 200, { root: resolve(process.cwd(), '..', '..') });
  }

  if (path === '/__reset') {
    states.set(stateKey(request), freshState());
    return json(response, 200, { ok: true });
  }

  const state = stateFor(request);

  // Every mutating route on the real API validates the antiforgery token by hand and answers a readable
  // 400 when it is missing. Without this the screen could stop sending the header and every browser test
  // would still pass.
  if (request.method !== 'GET' && path.startsWith('/api/')) {
    if (request.headers['x-xsrf-token'] !== ANTIFORGERY_TOKEN) {
      return problem(
        response,
        400,
        'Missing antiforgery token',
        'Send the antiforgery token in the X-XSRF-TOKEN header.',
        'invalid_antiforgery_token',
      );
    }
  }

  // ---- auth ---------------------------------------------------------------------------------
  if (path === '/api/admin/auth/whoami') {
    return json(response, 200, SESSION);
  }

  if (path === '/api/admin/auth/antiforgery-token') {
    return json(response, 200, { token: ANTIFORGERY_TOKEN });
  }

  if (path === '/api/admin/auth/logout') {
    return noContent(response);
  }

  if (path === '/api/ping') {
    return json(response, 200, { message: 'pong', utcNow: new Date().toISOString() });
  }

  // ---- settings (ADM-22, GLB-02) -------------------------------------------------------------
  // ---- PAY-01 / ADM-09: doc 11 P7 step 4's queue and its three decisions -------------------------

  // Test-only, and named so. `outcome=expired|short|paid` makes the next decision answer the 409 the real
  // API answers in that state, which is how the screen's Accept-late branch and its short-line list are
  // reachable at all.
  if (path === '/__test__/bank-transfer-outcome') {
    state.bankTransferOutcome = url.searchParams.get('outcome') ?? 'ok';
    return json(response, 200, { outcome: state.bankTransferOutcome });
  }

  if (path === '/api/admin/orders/bank-transfers' && request.method === 'GET') {
    const page = Math.max(1, Number(url.searchParams.get('page') ?? '1'));
    const pageSize = Math.max(1, Number(url.searchParams.get('pageSize') ?? '20'));

    return json(response, 200, {
      rows: state.bankTransfers.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: state.bankTransfers.length,
    });
  }

  const slipRoute = /^\/api\/admin\/orders\/bank-transfers\/([^/]+)\/slip$/.exec(path);
  if (slipRoute && request.method === 'GET') {
    // The real route streams the private file as an attachment. The stub only has to be openable; the
    // headers are what the e2e asserts, because they are the part that keeps a stranger's bank document
    // out of a browser's cache and out of the page.
    response.writeHead(200, {
      'Content-Type': 'image/jpeg',
      'Content-Disposition': 'attachment',
      'X-Content-Type-Options': 'nosniff',
      'Cache-Control': 'no-store, no-cache, private',
    });

    return response.end('slip-bytes');
  }

  const decision = /^\/api\/admin\/orders\/bank-transfers\/([^/]+)\/(approve|reject|accept-late)$/.exec(path);
  if (decision && request.method === 'POST') {
    const [, orderPublicId, action] = decision;
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const row = state.bankTransfers.find((candidate) => candidate.orderPublicId === orderPublicId);
    if (!row) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    // The same refusal the API makes, so the screen's own guard is not the only thing tested.
    if (action === 'reject' && (body.note === null || body.note === undefined || String(body.note).trim() === '')) {
      return json(response, 400, {
        title: 'One or more fields are invalid.',
        status: 400,
        errors: { note: ['Say why the transfer is being rejected.'] },
      });
    }

    if (state.bankTransferOutcome === 'expired' && action === 'approve') {
      return problem(
        response,
        409,
        'This decision could not be applied',
        'This order expired before the transfer was approved, so its items have been released. If the '
          + 'money has arrived, use Accept late payment and the stock will be re-checked.',
        'order_expired',
        { orderStatus: 'Expired', paymentStatus: 'Expired' },
      );
    }

    if (state.bankTransferOutcome === 'paid') {
      return problem(
        response,
        409,
        'This decision could not be applied',
        'This order has already been paid. Somebody else approved it, or the payment was confirmed '
          + 'another way.',
        'order_already_paid',
        { orderStatus: 'Paid', paymentStatus: 'Succeeded' },
      );
    }

    if (state.bankTransferOutcome === 'short' && action === 'accept-late') {
      return problem(
        response,
        409,
        'Some items are no longer in stock',
        'This order cannot be paid now, because some of its items have been sold since it expired.',
        'short_stock',
        {
          shortLines: [
            {
              variantId: 470,
              sku: 'SKU-470',
              productName: 'Slim jeans',
              colourName: 'Indigo',
              sizeLabel: '32',
              needed: 1,
              available: 0,
            },
            {
              variantId: 471,
              sku: 'SKU-471',
              productName: 'Denim jacket',
              colourName: 'Washed blue',
              sizeLabel: 'M',
              needed: 2,
              available: 1,
            },
          ],
        },
      );
    }

    return json(response, 200, {
      orderNumber: row.orderNumber,
      invoiceNumber: action === 'reject' ? null : 'INV-2026-000045',
    });
  }

  if (path === '/api/admin/settings' && request.method === 'GET') {
    return json(response, 200, settingsPayload(state));
  }

  // The media upload the screen does before it points the setting at the result. Multipart, so the body is
  // not parsed: what matters is that a file arrived, and that the answer carries a publicId the next call
  // can use — which is what makes the two-step flow testable at all.
  if (path === '/api/admin/media/images' && request.method === 'POST') {
    const altText = uploadedAltText(await drain(request));

    const publicId = `0000f00d-0000-4000-8000-${String(state.uploads.size + 1).padStart(12, '0')}`;
    const asset = {
      publicId,

      // media.MediaAssets.MediaType, as a *name* — the real API registers a global
      // JsonStringEnumConverter, so every enum in a response is a string. ADM-10's product form reads
      // this to tell a photograph from the video; the logo flow ignores it.
      mediaType: 'Image',
      url: '/__test__/logo.webp',
      posterUrl: null,
      width: 600,
      height: 150,
      altText,
      originalFileName: 'logo.png',
    };

    state.uploads.set(publicId, asset);

    return json(response, 201, asset);
  }

  // The product video and its poster, in one multipart request (ADM-10, doc 08 §3.3: a video row cannot
  // exist without a poster, and nothing on the server extracts a frame). The body is not parsed; what
  // matters is that two files arrived and that the answer carries a publicId the form can reference.
  if (path === '/api/admin/media/videos' && request.method === 'POST') {
    const altText = uploadedAltText(await drain(request));

    const publicId = `0000f00d-0000-4000-8000-${String(state.uploads.size + 1).padStart(12, '0')}`;
    const asset = {
      publicId,
      mediaType: 'Video',
      url: '/__test__/clip.mp4',
      posterUrl: '/__test__/poster.webp',
      durationSeconds: 12.5,
      altText,
      originalFileName: 'clip.mp4',
    };

    state.uploads.set(publicId, asset);

    return json(response, 201, asset);
  }

  if (path === '/api/admin/settings/store-logo' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Store')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed these settings while you were editing them.',
        'stale_row_version',
      );
    }

    const wanted = body.logoAssetPublicId ?? null;

    if (wanted !== null && !state.uploads.has(wanted)) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          logoAssetPublicId: ['That image is not in the media library, or is not a ready public image.'],
        },
      });
    }

    state.settings['Store.LogoAssetId'] = wanted;
    state.logo = wanted === null ? null : state.uploads.get(wanted);

    return noContent(response);
  }

  if (path === '/api/admin/settings/bank' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Bank')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed the bank details while you were editing them.',
        'stale_row_version',
      );
    }

    // The API's one real format rule, mirrored — so the screen's error display for this field is actually
    // exercised rather than assumed. Without it the field error could stop rendering and every browser
    // test would still pass, which is the gap that let W1-12's antiforgery header go unchecked for 246
    // tests.
    if (
      body.accountNumber !== null
      && body.accountNumber !== undefined
      && String(body.accountNumber).trim() !== ''
      && !/^[0-9 -]+$/.test(String(body.accountNumber))
    ) {
      return json(response, 400, {
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          accountNumber: [
            'An account number can only hold digits, spaces and dashes. Enter the bank name in the bank '
              + 'name box and anything else in the instructions.',
          ],
        },
      });
    }

    // **Only the fields the body carried.** A key the request omits is a key left alone — the W1-12 rule,
    // and the one this group makes real. A stub that assigned all five from `body` would let the screen
    // start sending nulls and no browser test would notice.
    for (const [field, key] of [
      ['bankName', 'Bank.BankName'],
      ['branch', 'Bank.Branch'],
      ['accountName', 'Bank.AccountName'],
      ['accountNumber', 'Bank.AccountNumber'],
      ['instructions', 'Bank.Instructions'],
    ]) {
      if (Object.hasOwn(body, field)) {
        const value = body[field];
        state.settings[key] = value === null || String(value).trim() === '' ? null : String(value).trim();
      }
    }

    return noContent(response);
  }

  if (path === '/api/admin/settings/contact-social' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Contact')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed these settings while you were editing them.',
        'stale_row_version',
      );
    }

    // The same per-field refusals the API makes, so the screen's error display is actually exercised.
    const errors = {};
    const fields = {
      whatsApp: 'Contact.WhatsApp',
      facebookUrl: 'Social.FacebookUrl',
      instagramUrl: 'Social.InstagramUrl',
      tikTokUrl: 'Social.TikTokUrl',
    };

    for (const field of ['facebookUrl', 'instagramUrl', 'tikTokUrl']) {
      const value = body[field];
      if (value !== null && value !== undefined && String(value).trim() !== '' && !acceptableProfileUrl(value)) {
        errors[field] = [
          'The address must be a full web address starting with https://. Leave the box empty to hide the icon.',
        ];
      }
    }

    const whatsApp = body.whatsApp;
    if (whatsApp !== null && whatsApp !== undefined && String(whatsApp).trim() !== '' && !acceptableWhatsApp(whatsApp)) {
      errors.whatsApp = [
        'The WhatsApp number must be 7 to 15 digits in international form, with no plus sign, spaces or '
        + 'leading zero — for example 94771234567.',
      ];
    }

    if (Object.keys(errors).length > 0) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors,
      });
    }

    // PUT replaces: a field the body omits is a field the API is told to clear. The stub does the same, so a
    // screen that stopped sending one would show up as a cleared value rather than as nothing at all —
    // which is the W1-12 blocker's exact shape.
    for (const [field, key] of Object.entries(fields)) {
      const value = body[field];
      state.settings[key] = value === null || value === undefined || String(value).trim() === ''
        ? null
        : String(value).trim();
    }

    return noContent(response);
  }

  // The logo the preview points at. A real 1x1 WebP, so a broken image is visibly broken.
  if (path === '/__test__/logo.webp') {
    response.writeHead(200, { 'Content-Type': 'image/webp', 'Cache-Control': 'no-store' });
    response.end(LOGO_PIXEL);
    return;
  }

  // ---- products (ADM-10, W1-03) --------------------------------------------------------------
  if (
    await handleProductRoute({
      path,
      method: request.method,
      url,
      request,
      response,
      state,
      json,
      problem,
      noContent,
      readBody,
      suppliedRowVersion,
    })
  ) {
    return undefined;
  }

  // ---- catalogue ----------------------------------------------------------------------------
  if (path === '/api/admin/catalog/departments' && request.method === 'GET') {
    return json(response, 200, state.departments);
  }

  const treeMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)\/categories$/);
  if (treeMatch && request.method === 'GET') {
    const id = Number(treeMatch[1]);
    const found = state.departments.find((candidate) => candidate.id === id);

    if (!found) {
      return problem(response, 404, 'Not found', `No department has the id ${id}.`, 'not_found');
    }

    return json(response, 200, {
      departmentId: found.id,
      departmentName: found.name,
      departmentSlug: found.slug,
      departmentIsActive: found.isActive,
      categories: state.categories[id] ?? [],
    });
  }

  const impactMatch = path.match(/^\/api\/admin\/catalog\/categories\/(\d+)\/deactivation-impact$/);
  if (impactMatch && request.method === 'GET') {
    const id = Number(impactMatch[1]);
    const node = Object.values(state.categories).flatMap(flatten).find((candidate) => candidate.id === id);

    if (!node) {
      return problem(response, 404, 'Not found', `No category has the id ${id}.`, 'not_found');
    }

    const descendants = flatten(node.children);

    return json(response, 200, {
      categoryId: node.id,
      name: node.name,
      isActive: node.isActive,
      productsLeavingTheStorefront: node.primaryProductCount,
      descendantCategories: descendants.length,
      descendantProductsLosingTheirMenuRoute: descendants
        .filter((child) => child.isActive)
        .reduce((total, child) => total + child.primaryProductCount, 0),
      examples: node.primaryProductCount > 0 ? ['Linen shift dress', 'Cotton midi dress'] : [],
    });
  }

  if (path === '/api/admin/catalog/category-form-options' && request.method === 'GET') {
    return json(response, 200, {
      ageGroups: [
        { id: 1, name: 'Baby' },
        { id: 2, name: 'Toddler' },
        { id: 3, name: 'Kids' },
      ],
      sizeGuides: [
        { id: 2, name: "Women's clothing" },
        { id: 5, name: "Kids' clothing" },
      ],
    });
  }

  if (path === '/api/admin/catalog/departments/order' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    return applyOrder(response, state.departments, body.orderedDepartmentIds ?? [])
      ? noContent(response)
      : undefined;
  }

  if (path === '/api/admin/catalog/categories/order' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const siblings = findSiblings(state, body.departmentId, body.parentCategoryId ?? null);
    if (!siblings) {
      return problem(response, 404, 'Not found', 'No such parent category.', 'not_found');
    }

    return applyOrder(response, siblings, body.orderedCategoryIds ?? []) ? noContent(response) : undefined;
  }

  if (path === '/api/admin/catalog/categories' && request.method === 'POST') {
    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    // The one validation worth stubbing, because the screen has to show it against the right box.
    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(body.slug ?? '')) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          slug: [
            'The URL slug may contain only lower-case letters, digits and single hyphens between them, ' +
              "for example 'denim-jackets'.",
          ],
        },
      });
    }

    const siblings = findSiblings(state, body.departmentId, body.parentCategoryId ?? null);
    if (!siblings) {
      return problem(response, 400, 'Invalid', 'No such parent category.', 'invalid');
    }

    const parent =
      body.parentCategoryId === null || body.parentCategoryId === undefined
        ? null
        : Object.values(state.categories).flatMap(flatten).find((node) => node.id === body.parentCategoryId);

    const id = 900 + Object.values(state.categories).flatMap(flatten).length;
    const depth = parent ? parent.depth + 1 : 1;

    siblings.push(
      category(
        id,
        body.name,
        body.slug,
        parent ? parent.id : null,
        depth,
        parent ? `${parent.path}${id}/` : `/${id}/`,
        siblings.length,
      ),
    );

    return json(response, 201, { id });
  }

  const activeMatch = path.match(/^\/api\/admin\/catalog\/categories\/(\d+)\/active$/);
  if (activeMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const node = Object.values(state.categories)
      .flatMap(flatten)
      .find((candidate) => candidate.id === Number(activeMatch[1]));

    if (!node) {
      return problem(response, 404, 'Not found', 'No such category.', 'not_found');
    }

    if (!rowVersionOk(response, request, body, node)) {
      return undefined;
    }

    node.isActive = body.isActive;
    node.rowVersion = nextRowVersion();
    return noContent(response);
  }

  const departmentActiveMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)\/active$/);
  if (departmentActiveMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const found = state.departments.find((candidate) => candidate.id === Number(departmentActiveMatch[1]));

    if (!found) {
      return problem(response, 404, 'Not found', 'No such department.', 'not_found');
    }

    if (!rowVersionOk(response, request, body, found)) {
      return undefined;
    }

    found.isActive = body.isActive;
    found.rowVersion = nextRowVersion();
    return noContent(response);
  }

  const categoryMatch = path.match(/^\/api\/admin\/catalog\/categories\/(\d+)$/);
  if (categoryMatch && (request.method === 'PUT' || request.method === 'DELETE')) {
    const id = Number(categoryMatch[1]);
    const all = Object.values(state.categories).flatMap(flatten);
    const node = all.find((candidate) => candidate.id === id);

    if (!node) {
      return problem(response, 404, 'Not found', `No category has the id ${id}.`, 'not_found');
    }

    if (request.method === 'DELETE') {
      if (!rowVersionOk(response, request, null, node)) {
        return undefined;
      }

      // Doc 08 §3.2's guard, not a cascade — the refusal the screen has to be able to show.
      if (node.children.length > 0 || node.productCount > 0) {
        return problem(
          response,
          409,
          'This change cannot be applied',
          `'${node.name}' still has ` +
            [
              node.children.length > 0 ? `${node.children.length} categories underneath it` : null,
              node.productCount > 0 ? `${node.productCount} products in it` : null,
            ]
              .filter(Boolean)
              .join(' and ') +
            '. Move or remove those first.',
          'category_in_use',
          { childCategories: node.children.length, linkedProducts: node.productCount },
        );
      }

      const siblings = findSiblings(state, node.departmentId, node.parentCategoryId);
      siblings.splice(
        siblings.findIndex((candidate) => candidate.id === id),
        1,
      );

      return noContent(response);
    }

    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(body.slug ?? '')) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: { slug: ['The URL slug may contain only lower-case letters, digits and single hyphens.'] },
      });
    }

    if (!rowVersionOk(response, request, body, node)) {
      return undefined;
    }

    const newParentId = body.parentCategoryId ?? null;
    if (newParentId !== node.parentCategoryId) {
      // The move: the subtree is re-pathed and re-levelled, as the real use case does in one transaction.
      // Without this the largest thing the screen does had no browser coverage at all.
      const newParent =
        newParentId === null ? null : all.find((candidate) => candidate.id === newParentId);

      if (newParentId !== null && !newParent) {
        return json(response, 400, {
          type: 'about:blank',
          title: 'One or more fields are invalid.',
          status: 400,
          errors: { parentCategoryId: [`No category has the id ${newParentId}.`] },
        });
      }

      if (newParent && newParent.path.startsWith(node.path)) {
        return json(response, 400, {
          type: 'about:blank',
          title: 'One or more fields are invalid.',
          status: 400,
          errors: {
            parentCategoryId: [
              `'${newParent.name}' is inside '${node.name}', so moving it there would make the tree a loop.`,
            ],
          },
        });
      }

      const oldSiblings = findSiblings(state, node.departmentId, node.parentCategoryId);
      oldSiblings.splice(
        oldSiblings.findIndex((candidate) => candidate.id === id),
        1,
      );

      const oldPath = node.path;
      const newPath = newParent ? `${newParent.path}${id}/` : `/${id}/`;

      for (const descendant of [node, ...flatten(node.children)]) {
        descendant.path = newPath + descendant.path.slice(oldPath.length);
        descendant.depth = descendant.path.split('/').filter(Boolean).length;
      }

      node.parentCategoryId = newParentId;

      const newSiblings = newParent ? newParent.children : (state.categories[node.departmentId] ??= []);
      node.sortOrder = newSiblings.length;
      newSiblings.push(node);
    }

    node.name = body.name;
    node.slug = body.slug;
    node.description = body.description ?? null;
    node.imageAssetId = body.imageAssetId ?? null;
    node.bannerAssetId = body.bannerAssetId ?? null;
    node.bannerLinkUrl = body.bannerLinkUrl ?? null;
    node.ageGroupId = body.ageGroupId ?? null;
    node.sizeGuideId = body.sizeGuideId ?? null;
    node.showInMegaMenu = body.showInMegaMenu ?? true;
    node.megaMenuColumn = body.megaMenuColumn ?? null;
    node.seoTitle = body.seoTitle ?? null;
    node.seoDescription = body.seoDescription ?? null;
    node.rowVersion = nextRowVersion();

    return noContent(response);
  }

  const departmentMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)$/);
  if (departmentMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const found = state.departments.find((candidate) => candidate.id === Number(departmentMatch[1]));

    if (!found) {
      return problem(response, 404, 'Not found', 'No such department.', 'not_found');
    }

    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (!rowVersionOk(response, request, body, found)) {
      return undefined;
    }

    // The name and the slug are not editable at all (owner, 2026-10-02), so the stub ignores them exactly
    // as the real request DTO does — it has no field for either.
    found.description = body.description ?? null;
    found.heroImageAssetId = body.heroImageAssetId ?? null;
    found.seoTitle = body.seoTitle ?? null;
    found.seoDescription = body.seoDescription ?? null;
    found.rowVersion = nextRowVersion();
    return noContent(response);
  }

  if (path.startsWith('/api/')) {
    return problem(response, 404, 'Not found', `The stub has no route for ${path}.`, 'not_found');
  }

  // ---- the built application ----------------------------------------------------------------
  return serveStatic(path, response);
});

/**
 * Serves `dist/admin/browser`, falling back to `index.html` for anything that is not a file — which is what
 * a deployment's reverse proxy does for a client-routed application, and without it a direct visit to
 * `/admin/catalog` would be a 404 and every test would be testing the 404.
 */
function serveStatic(path, response) {
  const withoutBase = path.replace(/^\/admin\/?/, '/');
  const candidate = resolve(join(DIST, normalize(withoutBase)));

  if (!candidate.startsWith(DIST)) {
    response.writeHead(403).end();
    return;
  }

  let file = candidate;
  try {
    if (!statSync(file).isFile()) {
      file = join(DIST, 'index.html');
    }
  } catch {
    file = join(DIST, 'index.html');
  }

  try {
    const body = readFileSync(file);
    response.writeHead(200, {
      'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store',
      'Content-Length': body.length,
    });
    response.end(body);
  } catch {
    response.writeHead(404, { 'Content-Type': 'text/plain' }).end('not found');
  }
}

server.listen(PORT, () => {
  console.log(`admin catalogue stub on http://localhost:${PORT} serving ${DIST}`);
});
