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
 * **It is deliberately not more polite than the real API.** It answers the same field names
 * (`SpencerDenim.Application.Catalog.Admin.Contracts`), the same statuses, and the same RFC 9457 bodies with
 * a `code` beside the sentence — including the two refusals that matter: a stale sibling list is a 409
 * `stale_order` and a category something points at is a 409 `category_in_use`. A stub that said 200 to
 * everything would let a screen that ignores failures pass.
 */
import { readFileSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve } from 'node:path';

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
  permissions: ['Categories.View', 'Categories.Create', 'Categories.Edit', 'Categories.Delete'],
  mustChangePassword: false,
  twoFactorEnabled: true,
  twoFactorEnrolmentRequired: false,
  twoFactorRequired: false,
};

/** `rowversion` is eight bytes; the shape matters because the screen round-trips it untouched. */
function rowVersion(seed) {
  return Buffer.from([0, 0, 0, 0, 0, 0, 0, seed]).toString('base64');
}

function department(id, name, slug, sortOrder, isActive, categoryCount, productCount) {
  return {
    id,
    name,
    slug,
    description: `${name} description.`,
    heroImageAssetId: null,
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
  };
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
  if (path === '/__reset') {
    states.set(stateKey(request), freshState());
    return json(response, 200, { ok: true });
  }

  const state = stateFor(request);

  // ---- auth ---------------------------------------------------------------------------------
  if (path === '/api/admin/auth/whoami') {
    return json(response, 200, SESSION);
  }

  if (path === '/api/admin/auth/antiforgery-token') {
    return json(response, 200, { token: 'e2e-antiforgery-token' });
  }

  if (path === '/api/admin/auth/logout') {
    return noContent(response);
  }

  if (path === '/api/ping') {
    return json(response, 200, { message: 'pong', utcNow: new Date().toISOString() });
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

    node.isActive = body.isActive;
    return noContent(response);
  }

  const departmentActiveMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)\/active$/);
  if (departmentActiveMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const found = state.departments.find((candidate) => candidate.id === Number(departmentActiveMatch[1]));

    if (!found) {
      return problem(response, 404, 'Not found', 'No such department.', 'not_found');
    }

    found.isActive = body.isActive;
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

    node.name = body.name;
    node.slug = body.slug;
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

    found.name = body.name;
    found.description = body.description ?? null;
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
