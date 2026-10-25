/**
 * The customer (ADM-15) and newsletter (ADM-21) routes of the admin stub, in their own module as the
 * product routes are.
 *
 * **Field for field with the real contracts** (`SpencerDenim.Application.Admin.Customers.Contracts`,
 * `SpencerDenim.Application.Marketing.Newsletter.Admin`), and **no more permissive than the real API**:
 * page sizes clamp at 100, a status is a name and never a number, a date is `yyyy-MM-dd`, a to-date before
 * a from-date is a 400 naming `to`, and the export neutralises formula cells, starts with a UTF-8 BOM, ignores
 * `status` and leaves out every unsubscribed address. The antiforgery check on the export is the main
 * stub's, which refuses every non-GET without the token.
 *
 * **Both lists start empty**, as a new installation's do: nothing is seeded in production, so nothing is
 * here until a test asks for it with `/__test__/customers?set=some|many` or
 * `/__test__/subscribers?set=some`. The rows those make are test fixtures, never shop data.
 *
 * The endpoint behaviour itself — search classification, the privacy decoys, the audit row — is proved
 * against SQL Server in `AdminCustomerEndpointTests` and `AdminNewsletterEndpointTests` (#126).
 */

/** Doc 08 §9's 25 districts, in the seed's own order, as `ref.Districts` holds them. */
const DISTRICTS = [
  'Colombo', 'Gampaha', 'Kalutara', 'Kandy', 'Matale', 'Nuwara Eliya', 'Galle', 'Matara', 'Hambantota',
  'Jaffna', 'Kilinochchi', 'Mannar', 'Vavuniya', 'Mullaitivu', 'Batticaloa', 'Ampara', 'Trincomalee',
  'Kurunegala', 'Puttalam', 'Anuradhapura', 'Polonnaruwa', 'Badulla', 'Monaragala', 'Ratnapura', 'Kegalle',
].map((name, index) => ({ id: index + 1, name }));

const MAX_PAGE_SIZE = 100;

/** The two countries the fixtures use. The real list is read off the customers' default addresses. */
const COUNTRY_CODES = { 'Sri Lanka': 'LK', Canada: 'CA' };

/** Sri Lanka, and every other country a customer's default address is in, by name. */
function countryOptions(customers) {
  const names = new Set(['Sri Lanka']);
  for (const row of customers) {
    if (row.countryName) {
      names.add(row.countryName);
    }
  }

  return [...names]
    .sort((a, b) => a.localeCompare(b))
    .map((name) => ({ code: COUNTRY_CODES[name] ?? '??', name }));
}

export function freshCustomerState() {
  return { customers: [], subscribers: [] };
}

function customer(customerId, overrides = {}) {
  return {
    customerId,
    firstName: 'Nimali',
    lastName: 'Perera',
    email: `customer${customerId}@example.com`,
    emailConfirmed: true,
    phone: '+94771234567',
    districtName: 'Kandy',
    isActive: true,
    registeredAtUtc: '2026-09-01T04:30:00Z',
    lastSignInAtUtc: null,
    orderCount: 0,
    lifetimeSpend: 0,
    currency: 'LKR',
    lastOrderAtUtc: null,
    // Owner decision 2026-10-21: the country of the default saved address, or null with none.
    countryName: 'Sri Lanka',
    addresses: [],
    orders: [],
    ...overrides,
  };
}

function customersFor(set) {
  if (set === 'none') {
    return [];
  }

  const some = [
    customer(41, {
      firstName: 'Dilani',
      lastName: 'Wickramasinghe',
      email: 'dilani@example.com',
      districtName: 'Gampaha',
      registeredAtUtc: '2026-10-01T04:30:00Z',
      lastSignInAtUtc: '2026-10-10T04:30:00Z',
      orderCount: 2,
      lifetimeSpend: 9800,
      lastOrderAtUtc: '2026-10-05T04:30:00Z',
      addresses: [
        {
          addressId: 7,
          label: 'Home',
          recipientName: 'Dilani Wickramasinghe',
          phone: '+94771234567',
          line1: '14 Galle Road',
          line2: null,
          city: 'Kollupitiya',
          districtName: 'Gampaha',
          postalCode: '00300',
          landmark: null,
          isDefault: true,
          countryCode: 'LK',
          countryName: 'Sri Lanka',
          region: null,
        },
      ],
      // The two stub orders this customer owns, by the tokens the order screen already serves, so a link
      // from the history opens a real order detail.
      orders: [
        {
          orderPublicId: '44444444-4444-4444-8444-444444444444',
          orderNumber: 'SD-2026-000123',
          status: 'Paid',
          statusLabel: 'Paid',
          grandTotal: 9800,
          currency: 'LKR',
          placedAtUtc: '2026-10-05T04:30:00Z',
          paidAtUtc: '2026-10-05T09:00:00Z',
        },
        {
          orderPublicId: '77777777-7777-4777-8777-777777777777',
          orderNumber: 'SD-2026-000126',
          status: 'PendingPayment',
          statusLabel: 'Awaiting payment',
          grandTotal: 4500,
          currency: 'LKR',
          placedAtUtc: '2026-10-04T04:30:00Z',
          paidAtUtc: null,
        },
      ],
    }),
    customer(42, {
      firstName: 'Kasun',
      lastName: null,
      email: 'kasun.a.very.long.address.that.must.wrap.at.three.hundred.and.twenty@example.com',
      emailConfirmed: false,
      phone: null,
      districtName: null,
      registeredAtUtc: '2026-09-20T04:30:00Z',
      // An overseas buyer's address book (owner decision 2026-10-21): no district, a region and a country.
      countryName: 'Canada',
      addresses: [
        {
          addressId: 9,
          label: 'Toronto',
          recipientName: 'Kasun Abeysekera',
          phone: '+1 416 555 0199',
          line1: '290 Bremner Boulevard',
          line2: null,
          city: 'Toronto',
          districtName: null,
          postalCode: 'M5V 3L9',
          landmark: null,
          isDefault: true,
          countryCode: 'CA',
          countryName: 'Canada',
          region: 'Ontario',
        },
      ],
    }),
  ];

  if (set !== 'many') {
    return some;
  }

  const many = [...some];
  for (let index = 0; index < 28; index += 1) {
    many.push(customer(100 + index, {
      firstName: `Customer${index + 1}`,
      registeredAtUtc: new Date(Date.UTC(2026, 7, 1 + index)).toISOString(),
    }));
  }

  return many;
}

function subscriber(subscriberId, email, status, consentAtUtc, source = 'footer', unsubscribedAtUtc = null) {
  return { subscriberId, email, status, source, consentAtUtc, unsubscribedAtUtc };
}

function subscribersFor(set) {
  if (set !== 'some') {
    return [];
  }

  return [
    subscriber(1, 'nimali@example.com', 'Subscribed', '2026-10-01T04:30:00Z'),
    subscriber(2, 'kasun@example.com', 'Unsubscribed', '2026-09-01T04:30:00Z', 'homepage', '2026-09-20T04:30:00Z'),
    subscriber(3, '=1+2@example.com', 'Subscribed', '2026-09-15T04:30:00Z'),
    subscriber(4, 'නිමාලි@example.lk', 'Subscribed', '2026-08-01T04:30:00Z', 'homepage'),
  ];
}

function clampPage(url) {
  const page = Math.max(1, Math.trunc(Number(url.searchParams.get('page') ?? '1')) || 1);
  const size = Math.trunc(Number(url.searchParams.get('pageSize') ?? '25')) || 25;

  return { page, pageSize: Math.min(MAX_PAGE_SIZE, Math.max(1, size)) };
}

function listRow(row) {
  return {
    customerId: row.customerId,
    name: row.lastName ? `${row.firstName} ${row.lastName}` : row.firstName,
    email: row.email,
    emailConfirmed: row.emailConfirmed,
    phone: row.phone,
    districtName: row.districtName,
    isActive: row.isActive,
    registeredAtUtc: row.registeredAtUtc,
    orderCount: row.orderCount,
    lifetimeSpend: row.lifetimeSpend,
    currency: row.currency,
    lastOrderAtUtc: row.lastOrderAtUtc,
    countryName: row.countryName,
  };
}

function detail(row) {
  const { orders: _orders, ...rest } = row;
  return rest;
}

/** The real filter's checks, in the real order, with the real field names. */
function newsletterErrors(url, readStatus) {
  const errors = {};
  const status = url.searchParams.get('status');

  if (readStatus && status && status.toLowerCase() !== 'all') {
    if (/^\s*\d/.test(status) || !['subscribed', 'unsubscribed'].includes(status.trim().toLowerCase())) {
      errors.status = ['Use status=Subscribed or status=Unsubscribed, or leave it out for both.'];
    }
  }

  const from = url.searchParams.get('from');
  const to = url.searchParams.get('to');
  const shape = /^\d{4}-\d{2}-\d{2}$/;
  const valid = (value) => shape.test(value) && !Number.isNaN(Date.parse(`${value}T00:00:00Z`))
    && new Date(`${value}T00:00:00Z`).toISOString().startsWith(value);

  if (from && !valid(from.trim())) {
    errors.from = ['Use a date in the form yyyy-MM-dd.'];
  }

  if (to && !valid(to.trim())) {
    errors.to = ['Use a date in the form yyyy-MM-dd.'];
  }

  if (from && to && !errors.from && !errors.to && from.trim() > to.trim()) {
    errors.to = ['The end date is before the start date.'];
  }

  return Object.keys(errors).length > 0 ? errors : null;
}

/** Colombo midnight of a `yyyy-MM-dd` day, as a UTC instant in milliseconds. */
function colomboMidnight(day) {
  return Date.parse(`${day}T00:00:00+05:30`);
}

function matchingSubscribers(state, url) {
  const search = (url.searchParams.get('search') ?? '').trim().toUpperCase();
  const from = url.searchParams.get('from');
  const to = url.searchParams.get('to');

  return state.subscribers.filter((row) => {
    const at = Date.parse(row.consentAtUtc);

    return (search === '' || row.email.toUpperCase().includes(search))
      && (!from || at >= colomboMidnight(from.trim()))
      && (!to || at < colomboMidnight(to.trim()) + 24 * 60 * 60 * 1000);
  });
}

/** `CsvDocument.Cell`, exactly: neutralise a formula start, then quote and double quotes. */
function csvCell(value) {
  if (value === null || value === undefined || value === '') {
    return '""';
  }

  const text = /^[\t\r]/.test(value) || /^ *[=+\-@]/.test(value) ? `'${value}` : value;
  return `"${text.replace(/"/g, '""')}"`;
}

function colomboStamp(utc) {
  const local = new Date(Date.parse(utc) + 330 * 60 * 1000).toISOString();
  return `${local.slice(0, 10)} ${local.slice(11, 16)}`;
}

/** Returns true when it answered the request. */
export async function handleCustomerRoute({ path, method, url, response, state, json }) {
  if (path === '/__test__/customers') {
    state.customers = customersFor(url.searchParams.get('set') ?? 'some');
    json(response, 200, { count: state.customers.length });
    return true;
  }

  if (path === '/__test__/subscribers') {
    state.subscribers = subscribersFor(url.searchParams.get('set') ?? 'some');
    json(response, 200, { count: state.subscribers.length });
    return true;
  }

  // ---- ADM-15 ---------------------------------------------------------------------------------
  if (path === '/api/admin/customers' && method === 'GET') {
    const { page, pageSize } = clampPage(url);
    const search = (url.searchParams.get('search') ?? '').trim().toLowerCase();
    const districtId = url.searchParams.get('districtId');
    const district = districtId ? DISTRICTS.find((candidate) => candidate.id === Number(districtId)) : null;
    const countryCode = (url.searchParams.get('countryCode') ?? '').trim().toUpperCase();

    // Deliberately crude: the real classification is proved against SQL Server. Newest registration first.
    const digits = search.replace(/\D/g, '');
    const matching = state.customers
      .filter((row) => !districtId || row.districtName === district?.name)
      .filter((row) => countryCode === '' || COUNTRY_CODES[row.countryName] === countryCode)
      .filter((row) => search === ''
        || listRow(row).name.toLowerCase().includes(search)
        || row.email.toLowerCase().startsWith(search)
        || (digits.length >= 7 && (row.phone ?? '').endsWith(digits.slice(-9))))
      .sort((a, b) => b.registeredAtUtc.localeCompare(a.registeredAtUtc) || b.customerId - a.customerId);

    json(response, 200, {
      rows: matching.slice((page - 1) * pageSize, page * pageSize).map(listRow),
      page,
      pageSize,
      totalCount: matching.length,
      districts: DISTRICTS,
      countries: countryOptions(state.customers),
    });
    return true;
  }

  const one = /^\/api\/admin\/customers\/(\d+)$/.exec(path);
  if (one && method === 'GET') {
    const row = state.customers.find((candidate) => candidate.customerId === Number(one[1]));
    if (!row) {
      json(response, 404, { title: 'Not Found', status: 404 });
    } else {
      json(response, 200, detail(row));
    }
    return true;
  }

  const history = /^\/api\/admin\/customers\/(\d+)\/orders$/.exec(path);
  if (history && method === 'GET') {
    const row = state.customers.find((candidate) => candidate.customerId === Number(history[1]));
    if (!row) {
      json(response, 404, { title: 'Not Found', status: 404 });
      return true;
    }

    const { page, pageSize } = clampPage(url);
    json(response, 200, {
      rows: row.orders.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: row.orders.length,
    });
    return true;
  }

  // ---- ADM-21 ---------------------------------------------------------------------------------
  if (path === '/api/admin/newsletter/subscribers' && method === 'GET') {
    const errors = newsletterErrors(url, true);
    if (errors) {
      json(response, 400, { title: 'One or more fields are invalid.', status: 400, errors });
      return true;
    }

    const { page, pageSize } = clampPage(url);
    const status = (url.searchParams.get('status') ?? '').trim().toLowerCase();
    const matching = matchingSubscribers(state, url)
      .filter((row) => status === '' || status === 'all' || row.status.toLowerCase() === status)
      .sort((a, b) => b.consentAtUtc.localeCompare(a.consentAtUtc) || b.subscriberId - a.subscriberId);

    json(response, 200, {
      rows: matching.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: matching.length,
      subscribedCount: state.subscribers.filter((row) => row.status === 'Subscribed').length,
      unsubscribedCount: state.subscribers.filter((row) => row.status === 'Unsubscribed').length,
    });
    return true;
  }

  if (path === '/api/admin/newsletter/subscribers/export' && method === 'POST') {
    const errors = newsletterErrors(url, false);
    if (errors) {
      json(response, 400, { title: 'One or more fields are invalid.', status: 400, errors });
      return true;
    }

    // Subscribed only, whatever ?status= says — the real reader puts it in its WHERE clause.
    const rows = matchingSubscribers(state, url)
      .filter((row) => row.status === 'Subscribed')
      .sort((a, b) => a.consentAtUtc.localeCompare(b.consentAtUtc) || a.subscriberId - b.subscriberId);

    const lines = [
      ['Email', 'Source', 'Consent given (Colombo time)'],
      ...rows.map((row) => [row.email, row.source, colomboStamp(row.consentAtUtc)]),
    ].map((cells) => cells.map(csvCell).join(','));

    const body = Buffer.concat([
      Buffer.from([0xef, 0xbb, 0xbf]),
      Buffer.from(lines.map((line) => `${line}\r\n`).join(''), 'utf8'),
    ]);

    state.exports = (state.exports ?? 0) + 1;

    response.writeHead(200, {
      'Content-Type': 'text/csv; charset=utf-8',
      'Content-Disposition': 'attachment; filename=newsletter-subscribers-2026-10-14.csv',
      'Cache-Control': 'no-store, no-cache, private',
      'Content-Length': body.length,
    });
    response.end(body);
    return true;
  }

  return false;
}
