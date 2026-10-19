/**
 * The inventory routes (ADM-12, #190) of the admin stub, in their own module as the product, customer and
 * reporting routes are.
 *
 * **Field for field with the real contracts** (`SpencerDenim.Application.Inventory.Contracts`;
 * `AdminInventoryShapeTests` reads the builders below and fails if a key is added or missing), and **no more
 * permissive than the real API** (`AdminInventoryEndpoints`, `InventoryWriteHandlers`, `StockChangeRules`,
 * `InventoryStore`):
 *
 * - the list's `status`, `sort` and `direction` are names and never numbers, and page sizes clamp at 100;
 * - a stock change must send `reason` and `rows`, and every row all four keys; the reason is trimmed,
 *   required and at most 250 characters; `kind` is Receive, Adjust or SetCount by name; quantities are whole
 *   numbers in the real ranges; `expectedOnHand` is required for SetCount and refused on the others; a
 *   variant named twice, no rows and more than 500 rows are refused — **all or nothing, with a 400 keyed
 *   `rows[i].field`**;
 * - a well-formed change is applied **row by row** in VariantId order: Refused below reserved or above
 *   1,000,000, Changed when a count's figure moved, NotFound for an unknown variant — answered in the order
 *   sent, with one batch id for a request of more than one row;
 * - a threshold must send `lowStockThreshold`, a whole number from 0 to 10,000 or null; an unknown variant is
 *   404.
 *
 * **Everything starts empty**, as a new installation's stock screen would show nothing until products exist:
 * `/__test__/inventory?set=some|many` makes test fixtures. They are never shop data.
 *
 * The real behaviour — the conditional updates, the races, the ledger and the audit rows — is proved against
 * SQL Server in `AdminInventoryEndpointTests`, `InventoryConcurrencyTests` and `OrderStockMovementTests`.
 */

const MAX_PAGE_SIZE = 100;
const DEFAULT_THRESHOLD = 5;
const MAX_QUANTITY = 100_000;
const MAX_ON_HAND = 1_000_000;
const MAX_THRESHOLD = 10_000;
const MAX_ROWS = 500;
const MAX_REASON = 250;

const DEPARTMENTS = [
  { id: 1, name: 'Women' },
  { id: 2, name: 'Men' },
  { id: 3, name: 'Kids' },
];

const CATEGORIES = [
  { id: 10, departmentId: 1, name: 'Dresses', depth: 1, path: '/10/' },
  { id: 11, departmentId: 1, name: 'Maxi dresses', depth: 2, path: '/10/11/' },
  { id: 20, departmentId: 2, name: 'Shirts', depth: 1, path: '/20/' },
  { id: 30, departmentId: 3, name: 'Tees', depth: 1, path: '/30/' },
];

export function freshInventoryState() {
  return { inventory: [], movements: [], nextMovementId: 1 };
}

function variant(variantId, overrides = {}) {
  return {
    variantId,
    productId: 100 + variantId,
    productName: 'Linen shirt',
    productCode: `SD-${String(100 + variantId).padStart(4, '0')}`,
    departmentId: 2,
    categoryId: 20,
    sku: `SD-${String(100 + variantId).padStart(4, '0')}-WHT-M`,
    colour: 'White',
    size: 'M',
    sortOrder: 0,
    hasStockRecord: true,
    onHand: 20,
    reserved: 0,
    ownThreshold: null,
    updatedAtUtc: '2026-10-19T04:30:00Z',
    ...overrides,
  };
}

function inventoryFor(set) {
  if (set === 'none') {
    return [];
  }

  const some = [
    variant(1, { productName: 'Linen shirt', sku: 'SD-0101-WHT-M', onHand: 12, reserved: 4 }),
    variant(2, { productName: 'Linen shirt', sku: 'SD-0101-WHT-L', size: 'L', onHand: 3, reserved: 0 }),
    variant(3, { productName: 'Linen shirt', sku: 'SD-0101-WHT-XL', size: 'XL', onHand: 2, reserved: 2 }),
    variant(4, {
      productName: 'Wrap dress in washed cotton with a very long name that has to wrap at three hundred and twenty',
      departmentId: 1,
      categoryId: 11,
      sku: 'SD-0104-NAVY-S-A-VERY-LONG-SKU-CODE',
      colour: 'Navy',
      size: 'S',
      onHand: 40,
      reserved: 1,
      ownThreshold: 50,
    }),
    variant(5, { productName: 'Kids tee', departmentId: 3, categoryId: 30, sku: 'SD-0105-RED-6', colour: 'Red', size: '6', onHand: 30, reserved: 0, ownThreshold: 0 }),
  ];

  if (set !== 'many') {
    return some;
  }

  const many = [...some];
  for (let index = 0; index < 30; index += 1) {
    many.push(variant(10 + index, { productName: `Tee ${index + 1}`, sku: `SD-T${index + 1}-M`, onHand: index, reserved: 0 }));
  }

  return many;
}

const applied = (stock) => stock.ownThreshold ?? DEFAULT_THRESHOLD;

function available(stock) {
  return stock.hasStockRecord ? stock.onHand - stock.reserved : 0;
}

/** `StockAvailability.ForVariant`: out at 0 available, low at 1 to the threshold, in stock above. */
function statusOf(stock) {
  const free = available(stock);
  return free <= 0 ? 'OutOfStock' : free <= applied(stock) ? 'LowStock' : 'InStock';
}

function categoryName(stock) {
  return CATEGORIES.find((category) => category.id === stock.categoryId)?.name ?? null;
}

function departmentName(stock) {
  return DEPARTMENTS.find((department) => department.id === stock.departmentId)?.name ?? '';
}

/** `InventoryRow`, exactly. */
function inventoryRow(stock) {
  return {
    variantId: stock.variantId,
    productId: stock.productId,
    productName: stock.productName,
    productCode: stock.productCode,
    departmentName: departmentName(stock),
    categoryName: categoryName(stock),
    sku: stock.sku,
    colour: stock.colour,
    size: stock.size,
    hasStockRecord: stock.hasStockRecord,
    onHand: stock.hasStockRecord ? stock.onHand : 0,
    reserved: stock.hasStockRecord ? stock.reserved : 0,
    available: available(stock),
    threshold: applied(stock),
    ownThreshold: stock.ownThreshold,
    stockStatus: statusOf(stock),
    updatedAtUtc: stock.hasStockRecord ? stock.updatedAtUtc : null,
  };
}

/** `InventoryMovementRow`, exactly. */
function movementRow(movement) {
  return {
    movementId: movement.movementId,
    movementType: movement.movementType,
    onHandDelta: movement.onHandDelta,
    reservedDelta: movement.reservedDelta,
    onHandAfter: movement.onHandAfter,
    reservedAfter: movement.reservedAfter,
    orderNumber: movement.orderNumber,
    batchId: movement.batchId,
    reason: movement.reason,
    createdBy: movement.createdBy,
    createdAtUtc: movement.createdAtUtc,
  };
}

/** `StockChangeRowResult`, exactly. */
function changeResult(variantId, outcome, message, onHandDelta, stock) {
  return {
    variantId,
    outcome,
    message,
    onHandDelta,
    onHand: stock ? stock.onHand : null,
    reserved: stock ? stock.reserved : null,
    available: stock ? stock.onHand - stock.reserved : null,
  };
}

function clampPage(url) {
  const page = Math.max(1, Math.trunc(Number(url.searchParams.get('page') ?? '1')) || 1);
  const size = Math.trunc(Number(url.searchParams.get('pageSize') ?? '25')) || 25;

  return { page, pageSize: Math.min(MAX_PAGE_SIZE, Math.max(1, size)) };
}

/** A name from `allowed`, never a number; the server's sentence on a refusal. */
function readName(url, field, allowed, fallback, errors) {
  const raw = url.searchParams.get(field);

  if (raw === null || raw.trim() === '') {
    return fallback;
  }

  const trimmed = raw.trim();
  const match = allowed.find((name) => name.toLowerCase() === trimmed.toLowerCase());

  if (!match || /^[\d-]/.test(trimmed)) {
    errors[field] = [`Use ${field}=${allowed.join(', ')}.`];
    return fallback;
  }

  return match;
}

function invalid(json, response, errors) {
  json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors });
}

function sortKey(stock, sort) {
  switch (sort) {
    case 'Sku':
      return stock.sku;
    case 'OnHand':
      return stock.hasStockRecord ? stock.onHand : 0;
    case 'Reserved':
      return stock.hasStockRecord ? stock.reserved : 0;
    case 'Available':
      return available(stock);
    default:
      return stock.productName;
  }
}

function compare(a, b) {
  if (typeof a === 'number' && typeof b === 'number') {
    return a - b;
  }

  return String(a) < String(b) ? -1 : String(a) > String(b) ? 1 : 0;
}

function list(state, url, json, response) {
  const errors = {};
  const status = readName(url, 'status', ['All', 'InStock', 'LowStock', 'OutOfStock'], 'All', errors);
  const sort = readName(url, 'sort', ['Product', 'Sku', 'OnHand', 'Reserved', 'Available'], 'Product', errors);
  const direction = readName(url, 'direction', ['Asc', 'Desc'], 'Asc', errors);

  if (Object.keys(errors).length > 0) {
    return invalid(json, response, errors);
  }

  const { page, pageSize } = clampPage(url);
  const search = (url.searchParams.get('search') ?? '').trim().slice(0, 100).toLowerCase();
  const departmentId = url.searchParams.get('departmentId');
  const categoryId = url.searchParams.get('categoryId');
  const category = categoryId ? CATEGORIES.find((candidate) => candidate.id === Number(categoryId)) : null;

  const scoped = state.inventory.filter((stock) =>
    (search === ''
      || stock.sku.toLowerCase().startsWith(search)
      || stock.productCode.toLowerCase().startsWith(search)
      || stock.productName.toLowerCase().includes(search))
    && (!departmentId || stock.departmentId === Number(departmentId))
    && (!categoryId || (category && CATEGORIES.some((below) => below.id === stock.categoryId && below.path.startsWith(category.path)))));

  const counts = {
    inStock: scoped.filter((stock) => statusOf(stock) === 'InStock').length,
    lowStock: scoped.filter((stock) => statusOf(stock) === 'LowStock').length,
    outOfStock: scoped.filter((stock) => statusOf(stock) === 'OutOfStock').length,
  };

  const matching = scoped
    .filter((stock) => status === 'All' || statusOf(stock) === status)
    .sort((a, b) => {
      const order = compare(sortKey(a, sort), sortKey(b, sort));
      return (direction === 'Desc' ? -order : order) || a.variantId - b.variantId;
    });

  return json(response, 200, {
    rows: matching.slice((page - 1) * pageSize, page * pageSize).map(inventoryRow),
    page,
    pageSize,
    totalCount: matching.length,
    defaultThreshold: DEFAULT_THRESHOLD,
    counts,
  });
}

/** `AdminInventoryEndpoints.ReadRow` + `InventoryWriteHandlers.Validate` + `StockChangeRules`, in that order. */
function validate(body) {
  const errors = {};
  const add = (field, message) => {
    errors[field] = [...(errors[field] ?? []), message];
  };

  if (!Object.hasOwn(body, 'reason')) {
    add('reason', 'Send reason.');
    return { errors };
  }

  if (body.reason !== null && typeof body.reason !== 'string') {
    add('reason', 'Send reason as text.');
    return { errors };
  }

  if (!Object.hasOwn(body, 'rows')) {
    add('rows', 'Send rows.');
    return { errors };
  }

  if (body.rows !== null && !Array.isArray(body.rows)) {
    add('rows', 'Send rows as a list.');
    return { errors };
  }

  const rows = body.rows ?? [];
  const shapeErrors = {};

  rows.forEach((row, index) => {
    const prefix = `rows[${index}].`;

    if (row === null || typeof row !== 'object' || Array.isArray(row)) {
      shapeErrors[`rows[${index}]`] = [`Send rows[${index}] as an object.`];
      return;
    }

    for (const field of ['variantId', 'quantity', 'expectedOnHand']) {
      if (!Object.hasOwn(row, field)) {
        shapeErrors[prefix + field] = [`Send ${field}.`];
      } else if (row[field] !== null && !Number.isInteger(row[field])) {
        shapeErrors[prefix + field] = [`Send ${field} as a whole number between -2,147,483,648 and 2,147,483,647.`];
      } else if (row[field] !== null && (row[field] > 2147483647 || row[field] < -2147483648)) {
        shapeErrors[prefix + field] = [`Send ${field} as a whole number between -2,147,483,648 and 2,147,483,647.`];
      }
    }

    if (!Object.hasOwn(row, 'kind')) {
      shapeErrors[`${prefix}kind`] = ['Send kind.'];
    } else if (row.kind !== null
      && (typeof row.kind !== 'string' || /^[\s]*[\d-]/.test(row.kind) || !['receive', 'adjust', 'setcount'].includes(row.kind.trim().toLowerCase()))) {
      shapeErrors[`${prefix}kind`] = ['Use kind Receive, Adjust or SetCount.'];
    }
  });

  if (Object.keys(shapeErrors).length > 0) {
    return { errors: shapeErrors };
  }

  if (rows.length === 0) {
    add('rows', 'Send at least one change.');
  } else if (rows.length > MAX_ROWS) {
    add('rows', `Send at most ${MAX_ROWS} changes at a time.`);
  } else {
    const seen = new Set();

    rows.forEach((row, index) => {
      const prefix = `rows[${index}].`;
      const kind = row.kind === null ? null : row.kind.trim().toLowerCase();

      if (row.variantId === null || row.variantId <= 0) {
        add(`${prefix}variantId`, 'Say which variant to change.');
      } else if (seen.has(row.variantId)) {
        add(`${prefix}variantId`, 'This variant is in the list twice; change it once.');
      } else {
        seen.add(row.variantId);
      }

      if (kind === null) {
        add(`${prefix}kind`, 'Use kind Receive, Adjust or SetCount.');
      } else if (row.quantity === null) {
        add(`${prefix}quantity`, 'Enter a quantity.');
      } else if (kind === 'receive' && (row.quantity < 1 || row.quantity > MAX_QUANTITY)) {
        add(`${prefix}quantity`, 'Receive between 1 and 100,000 units.');
      } else if (kind === 'adjust' && row.quantity === 0) {
        add(`${prefix}quantity`, 'Enter how many units to add or remove; 0 changes nothing.');
      } else if (kind === 'adjust' && Math.abs(row.quantity) > MAX_QUANTITY) {
        add(`${prefix}quantity`, 'Add or remove at most 100,000 units at a time.');
      } else if (kind === 'setcount' && (row.quantity < 0 || row.quantity > MAX_ON_HAND)) {
        add(`${prefix}quantity`, 'Set a count between 0 and 1,000,000.');
      }

      if (kind === 'setcount' && row.quantity !== null) {
        if (row.expectedOnHand === null) {
          add(`${prefix}expectedOnHand`, 'Send the on-hand figure the screen was showing, so a count that changed meanwhile is caught.');
        } else if (row.expectedOnHand < 0) {
          add(`${prefix}expectedOnHand`, 'The on-hand figure cannot be negative.');
        }
      }

      if ((kind === 'receive' || kind === 'adjust') && row.quantity !== null && row.expectedOnHand !== null) {
        add(`${prefix}expectedOnHand`, 'Only a SetCount carries the on-hand figure; leave it out.');
      }
    });
  }

  const reason = (body.reason ?? '').trim();

  if (reason === '') {
    add('reason', 'Give a reason, for example "Delivery from the factory" or "Damaged in store".');
  } else if (reason.length > MAX_REASON) {
    add('reason', `Keep the reason to ${MAX_REASON} characters.`);
  }

  return Object.keys(errors).length > 0 ? { errors } : { rows, reason };
}

const MOVEMENT = { receive: 'Receive', setcount: 'CountCorrection' };

function adjust(state, body, json, response) {
  if (body === undefined || body === null || typeof body !== 'object' || Array.isArray(body)) {
    return json(response, 400, { title: 'Malformed JSON', status: 400, detail: 'The request body is not valid JSON for this endpoint.', code: 'malformed_json' });
  }

  const { errors, rows, reason } = validate(body);

  if (errors) {
    return invalid(json, response, errors);
  }

  const batchId = rows.length > 1 ? '5b7f4c1e-8a2d-4e6b-9c3f-1d2e3f4a5b6c' : null;
  const now = new Date().toISOString();
  const results = new Map();

  for (const row of [...rows].sort((a, b) => a.variantId - b.variantId)) {
    const stock = state.inventory.find((candidate) => candidate.variantId === row.variantId);
    const kind = row.kind.trim().toLowerCase();

    if (!stock || !stock.hasStockRecord) {
      results.set(row.variantId, changeResult(row.variantId, 'NotFound', 'This variant does not exist or has no stock record, so nothing was changed. Reload the page.', 0, null));
      continue;
    }

    if (kind === 'setcount' && stock.onHand !== row.expectedOnHand) {
      results.set(row.variantId, changeResult(row.variantId, 'Changed', `On hand is now ${stock.onHand}, not the ${row.expectedOnHand} you were looking at: an order or another change moved it. Nothing was changed; check the count and save it again.`, 0, stock));
      continue;
    }

    const target = kind === 'setcount' ? row.quantity : stock.onHand + row.quantity;

    if (target < stock.reserved) {
      const message = stock.reserved === 1
        ? '1 unit is reserved for an unpaid order, so on hand cannot go below 1. Nothing was changed.'
        : `${stock.reserved} units are reserved for unpaid orders, so on hand cannot go below ${stock.reserved}. Nothing was changed.`;
      results.set(row.variantId, changeResult(row.variantId, 'Refused', message, 0, stock));
      continue;
    }

    if (target > MAX_ON_HAND) {
      results.set(row.variantId, changeResult(row.variantId, 'Refused', 'On hand cannot go above 1,000,000. Nothing was changed.', 0, stock));
      continue;
    }

    const delta = target - stock.onHand;
    stock.onHand = target;
    stock.updatedAtUtc = now;

    state.movements.push({
      movementId: state.nextMovementId++,
      variantId: stock.variantId,
      movementType: MOVEMENT[kind] ?? (delta > 0 ? 'AdjustIn' : 'AdjustOut'),
      onHandDelta: delta,
      reservedDelta: 0,
      onHandAfter: stock.onHand,
      reservedAfter: stock.reserved,
      orderNumber: null,
      batchId,
      reason,
      createdBy: 'catalogue@example.com',
      createdAtUtc: now,
    });

    results.set(row.variantId, changeResult(row.variantId, 'Applied', null, delta, stock));
  }

  const answered = rows.map((row) => results.get(row.variantId));
  const appliedCount = answered.filter((row) => row.outcome === 'Applied').length;

  return json(response, 200, { batchId, appliedCount, notAppliedCount: answered.length - appliedCount, rows: answered });
}

/** Returns true when it answered the request. */
export async function handleInventoryRoute({ path, method, url, request, response, state, json, readBody }) {
  if (path === '/__test__/inventory') {
    state.inventory = inventoryFor(url.searchParams.get('set') ?? 'some');
    state.movements = [
      // One automatic movement, so the history shows an order's row next to a staff member's.
      {
        movementId: 900,
        variantId: 1,
        movementType: 'Reserve',
        onHandDelta: 0,
        reservedDelta: 4,
        onHandAfter: 12,
        reservedAfter: 4,
        orderNumber: 'SD-2026-000123',
        batchId: null,
        reason: null,
        createdBy: null,
        createdAtUtc: '2026-10-18T04:30:00Z',
      },
    ];
    state.nextMovementId = 1000;
    json(response, 200, { count: state.inventory.length });
    return true;
  }

  if (path === '/api/admin/inventory' && method === 'GET') {
    list(state, url, json, response);
    return true;
  }

  if (path === '/api/admin/inventory/filters' && method === 'GET') {
    json(response, 200, {
      defaultThreshold: DEFAULT_THRESHOLD,
      departments: DEPARTMENTS,
      categories: CATEGORIES.map(({ id, departmentId, name, depth }) => ({ id, departmentId, name, depth })),
    });
    return true;
  }

  const history = /^\/api\/admin\/inventory\/(\d+)\/movements$/.exec(path);
  if (history && method === 'GET') {
    const stock = state.inventory.find((candidate) => candidate.variantId === Number(history[1]));

    if (!stock) {
      json(response, 404, { title: 'Not found', status: 404, detail: `There is no variant ${history[1]}.`, code: 'not_found' });
      return true;
    }

    const { page, pageSize } = clampPage(url);
    // Newest first, the reader's order: CreatedAtUtc descending, then the id.
    const rows = state.movements
      .filter((movement) => movement.variantId === stock.variantId)
      .sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc) || b.movementId - a.movementId);

    json(response, 200, {
      variant: inventoryRow(stock),
      rows: rows.slice((page - 1) * pageSize, page * pageSize).map(movementRow),
      page,
      pageSize,
      totalCount: rows.length,
    });
    return true;
  }

  if (path === '/api/admin/inventory/adjustments' && method === 'POST') {
    adjust(state, await readBody(request), json, response);
    return true;
  }

  const threshold = /^\/api\/admin\/inventory\/(\d+)\/threshold$/.exec(path);
  if (threshold && method === 'PUT') {
    const body = await readBody(request);

    if (body === undefined || body === null || typeof body !== 'object' || Array.isArray(body)) {
      json(response, 400, { title: 'Malformed JSON', status: 400, code: 'malformed_json' });
      return true;
    }

    if (!Object.hasOwn(body, 'lowStockThreshold')) {
      invalid(json, response, { lowStockThreshold: ['Send lowStockThreshold.'] });
      return true;
    }

    const value = body.lowStockThreshold;

    if (value !== null && !Number.isInteger(value)) {
      invalid(json, response, { lowStockThreshold: ['Send lowStockThreshold as a whole number.'] });
      return true;
    }

    if (value !== null && (value < 0 || value > MAX_THRESHOLD)) {
      invalid(json, response, { lowStockThreshold: ['Use a threshold between 0 and 10,000, or leave it blank for the shop default.'] });
      return true;
    }

    const stock = state.inventory.find((candidate) => candidate.variantId === Number(threshold[1]));

    if (!stock || !stock.hasStockRecord) {
      json(response, 404, { title: 'Not found', status: 404, detail: `Variant ${threshold[1]} does not exist, or has no stock record. Reload the page.`, code: 'not_found' });
      return true;
    }

    stock.ownThreshold = value;
    json(response, 200, inventoryRow(stock));
    return true;
  }

  return false;
}
