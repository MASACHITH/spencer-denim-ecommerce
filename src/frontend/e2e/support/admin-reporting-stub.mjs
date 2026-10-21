/**
 * The dashboard (ADM-04, ADM-05, ADM-07, ADM-08) and reports (ADM-19) routes of the admin stub, in their own
 * module as the product and customer routes are.
 *
 * **Field for field with the real contracts** (`SpencerDenim.Application.Admin.Reporting.Contracts`;
 * `AdminReportingShapeTests` reads the row builders below and fails if a key is added or missing), and **no
 * more permissive than the real API**: a date is `yyyy-MM-dd` between 2000-01-01 and 2099-12-31, a period
 * ends on or after it starts and is at most 366 days, an interval or grouping or stock status is a name and
 * never a number, the trend is `Week` or `Month` only, page sizes clamp at 100, and every 400 names its field.
 * The periods, previous periods, ISO weeks and months are worked out the way the server works them out, so a
 * screen that sent the wrong day would be shown the wrong window.
 *
 * **Everything starts empty, as a new installation does**: the dashboard answers real zeros, the low-stock
 * list is empty and every report has no rows, until a test asks for fixtures with
 * `/__test__/reporting?set=some`. Those figures are test fixtures and nothing else — they are never shop
 * data, and the screen never shows a figure the server did not send.
 *
 * The figures themselves (the revenue rule, Colombo days, the previous period, reconciliation) are proved
 * against SQL Server in `AdminDashboardEndpointTests`, `AdminReportEndpointTests`,
 * `AdminInventoryReportTests` and `ReportingReconciliationTests` (#126).
 */

const DAY = 24 * 60 * 60 * 1000;
const MAX_PAGE_SIZE = 100;
const EARLIEST = '2000-01-01';
const LATEST = '2099-12-31';

export function freshReportingState() {
  return { reporting: null, reportExports: 0 };
}

// ---- dates, the server's way ------------------------------------------------------------------------

function addDays(day, days) {
  return new Date(Date.parse(`${day}T00:00:00Z`) + days * DAY).toISOString().slice(0, 10);
}

function daysBetween(from, to) {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY) + 1;
}

function colomboToday() {
  return new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 10);
}

function weekStart(day) {
  const weekday = new Date(`${day}T00:00:00Z`).getUTCDay(); // 0 Sunday
  return addDays(day, -((weekday + 6) % 7));
}

function readDay(raw, field, errors) {
  if (raw === null || raw.trim() === '') {
    return null;
  }

  const day = raw.trim();
  const valid = /^\d{4}-\d{2}-\d{2}$/.test(day) && new Date(`${day}T00:00:00Z`).toISOString().slice(0, 10) === day;

  if (!valid) {
    errors[field] = ['Use a date in the form yyyy-MM-dd.'];
    return null;
  }

  if (day < EARLIEST || day > LATEST) {
    errors[field] = [`Use a date between ${EARLIEST} and ${LATEST}.`];
    return null;
  }

  return day;
}

/** `ReportPeriod.Parse`: the last 30 days by default, refusals keyed by field. */
function readPeriod(url, errors) {
  const before = Object.keys(errors).length;
  const to = readDay(url.searchParams.get('to'), 'to', errors) ?? colomboToday();
  const from = readDay(url.searchParams.get('from'), 'from', errors);

  if (Object.keys(errors).length > before) {
    return null;
  }

  const start = from ?? addDays(to, -29);

  if (start > to) {
    errors.to = ['The end date is before the start date.'];
    return null;
  }

  if (daysBetween(start, to) > 366) {
    errors.from = ['A period can be at most 366 days long.'];
    return null;
  }

  return { from: start, to, days: daysBetween(start, to) };
}

function readName(raw, field, allowed, fallback, errors) {
  if (raw === null || raw.trim() === '') {
    return fallback;
  }

  const match = allowed.find((name) => name.toLowerCase() === raw.trim().toLowerCase());

  if (!match || /^\d/.test(raw.trim())) {
    errors[field] = [`Use ${field}=${allowed.join(', ')}.`];
    return fallback;
  }

  return match;
}

function previousOf(period) {
  return { from: addDays(period.from, -period.days), to: addDays(period.from, -1), days: period.days };
}

/** `ReportBuckets.Split`: days, ISO weeks or calendar months, cut to the period. */
function split(period, interval) {
  const buckets = [];
  let start = period.from;

  while (start <= period.to) {
    let end = start;

    if (interval === 'Week') {
      end = addDays(weekStart(start), 6);
    } else if (interval === 'Month') {
      end = addDays(`${addDays(`${start.slice(0, 8)}28`, 4).slice(0, 8)}01`, -1);
    }

    end = end > period.to ? period.to : end;
    buckets.push({ start, end });
    start = addDays(end, 1);
  }

  return buckets;
}

function trendWindow(to, interval) {
  if (interval === 'Month') {
    let from = `${to.slice(0, 8)}01`;
    for (let month = 0; month < 11; month++) {
      from = `${addDays(from, -1).slice(0, 8)}01`;
    }

    return { from, to, days: daysBetween(from, to) };
  }

  const from = addDays(weekStart(to), -7 * 51);
  return { from, to, days: daysBetween(from, to) };
}

function clampPage(url) {
  const page = Math.max(1, Number.parseInt(url.searchParams.get('page') ?? '1', 10) || 1);
  const asked = Number.parseInt(url.searchParams.get('pageSize') ?? '25', 10) || 25;

  return { page, pageSize: Math.min(MAX_PAGE_SIZE, Math.max(1, asked)) };
}

// ---- the rows, field for field with the C# records -----------------------------------------------------

function figure(current, previous) {
  const change = current === null || previous === null ? null : current - previous;
  const percentChange = change === null || previous === 0 ? null : Math.round((change / previous) * 1000) / 10;

  return { current, previous, change, percentChange };
}

function salesRow(start, end, values) {
  return {
    start,
    end,
    orders: values.orders,
    paidOrders: values.paidOrders,
    grossRevenue: values.grossRevenue,
    itemsRevenue: values.itemsRevenue,
    deliveryRevenue: values.deliveryRevenue,
    refundsCompleted: values.refundsCompleted,
    netRevenue: values.grossRevenue - values.refundsCompleted,
  };
}

function inventoryRow(index) {
  const available = index % 7 === 0 ? 0 : index % 5 === 0 ? 3 : 20 + index;
  const reserved = index % 3 === 0 ? 1 : 0;
  const threshold = index === 10 ? 4 : 5;

  return {
    variantId: 500 + index,
    productId: 100 + Math.floor(index / 3),
    productName: index === 1 ? 'Handloom cotton sarong with a very long name that has to wrap on a phone' : `Linen shirt ${index}`,
    productCode: `SD-${String(100 + Math.floor(index / 3)).padStart(4, '0')}`,
    department: index % 2 === 0 ? 'Men' : 'Women',
    sku: `SKU-${String(index).padStart(3, '0')}`,
    colour: 'White',
    size: 'M',
    onHand: available + reserved,
    reserved,
    available,
    threshold,
    thresholdIsDefault: threshold === 5,
    stockStatus: available <= 0 ? 'OutOfStock' : available <= threshold ? 'LowStock' : 'InStock',
  };
}

function customerRow(email, name, district, hasAccount, paidOrders, grossRevenue, firstPaidOn, lastPaidOn) {
  return { email, name, district, hasAccount, paidOrders, grossRevenue, firstPaidOn, lastPaidOn };
}

function buyersPoint(start, end, buyers, firstTimeBuyers) {
  return {
    start,
    end,
    buyers,
    firstTimeBuyers,
    returningBuyers: buyers - firstTimeBuyers,
  };
}

function lowStockRow(row) {
  return {
    variantId: row.variantId,
    productId: row.productId,
    productName: row.productName,
    productCode: row.productCode,
    sku: row.sku,
    colour: row.colour,
    size: row.size,
    available: row.available,
    threshold: row.threshold,
  };
}

/** Test fixtures: which days carry a sale. Every other day in any period is a real zero. */
function fixtures() {
  return {
    sales: [
      { day: colomboToday(), orders: 3, paidOrders: 2, grossRevenue: 18400, itemsRevenue: 17600, deliveryRevenue: 800, refundsCompleted: 0 },
      { day: addDays(colomboToday(), -3), orders: 2, paidOrders: 2, grossRevenue: 9800, itemsRevenue: 9000, deliveryRevenue: 800, refundsCompleted: 2400 },
    ],
    inventory: Array.from({ length: 30 }, (_, index) => inventoryRow(index + 1)),
    customers: [
      customer('dilani@example.com', 'Dilani Wickramasinghe', 'Gampaha', true, 2, 18400),
      customer('=HYPERLINK("http://evil.example";"x")@example.com', '-2+3', 'Kandy', false, 1, 6600),
      customer('nimali@example.lk', 'නිමාලි පෙරේරා', 'Jaffna', false, 1, 3200),
    ],
    // For the customers chart (#186b): which fixture buyers had paid before any period a test asks about.
    // Kept beside the rows, never on them, so `customerRow` stays field for field with the contract.
    paidBefore: ['dilani@example.com'],
  };
}

function customer(email, name, district, hasAccount, paidOrders, grossRevenue) {
  return customerRow(email, name, district, hasAccount, paidOrders, grossRevenue, addDays(colomboToday(), -3), colomboToday());
}

function inPeriod(rows, period) {
  return rows.filter((row) => row.day >= period.from && row.day <= period.to);
}

function sum(rows, key) {
  return rows.reduce((total, row) => total + row[key], 0);
}

function totalsOf(state, period) {
  const rows = state.reporting ? inPeriod(state.reporting.sales, period) : [];

  return {
    gross: sum(rows, 'grossRevenue'),
    paid: sum(rows, 'paidOrders'),
    orders: sum(rows, 'orders'),
    refunds: sum(rows, 'refundsCompleted'),
    items: sum(rows, 'itemsRevenue'),
  };
}

function dashboardBody(state, period) {
  const previous = previousOf(period);
  const now = totalsOf(state, period);
  const before = totalsOf(state, previous);
  const average = (gross, paid) => (paid === 0 ? null : Math.round((gross / paid) * 100) / 100);
  const share = (revenue) => (now.items === 0 ? 0 : Math.round((revenue / now.items) * 1000) / 10);
  const some = state.reporting !== null && now.items > 0;

  const women = some ? Math.round(now.items * 0.75) : 0;
  const men = now.items - women;

  return {
    period,
    previousPeriod: previous,
    currency: 'LKR',
    kpis: {
      grossRevenue: figure(now.gross, before.gross),
      refundsCompleted: figure(now.refunds, before.refunds),
      netRevenue: figure(now.gross - now.refunds, before.gross - before.refunds),
      orders: figure(now.orders, before.orders),
      paidOrders: figure(now.paid, before.paid),
      averageOrderValue: figure(average(now.gross, now.paid), average(before.gross, before.paid)),
    },
    departments: [
      { id: 1, name: 'Women', units: some ? 4 : 0, revenue: women, sharePercent: share(women) },
      { id: 2, name: 'Men', units: some ? 1 : 0, revenue: men, sharePercent: share(men) },
      { id: 3, name: 'Kids', units: 0, revenue: 0, sharePercent: 0 },
    ],
    topCategories: some
      ? [
          { id: 11, name: 'Women · Traditional Wear', units: 4, revenue: women, sharePercent: share(women) },
          { id: 21, name: 'Men · Shirts', units: 1, revenue: men, sharePercent: share(men) },
        ]
      : [],
    topProducts: some
      ? [{ productId: 101, name: 'Handloom saree', productCode: 'SD-0101', units: 4, revenue: figure(women, 0) }]
      : [],
    orderStatuses: [
      ['PendingPayment', 'Awaiting payment'],
      ['Paid', 'Paid'],
      ['Processing', 'Processing'],
      ['Shipped', 'Shipped or ready for collection'],
      ['Delivered', 'Delivered or collected'],
      ['Cancelled', 'Cancelled'],
      ['Expired', 'Expired'],
      ['Refunded', 'Refunded'],
    ].map(([status, label]) => ({
      status,
      label,
      count: status === 'Paid' ? now.paid : status === 'PendingPayment' ? now.orders - now.paid : 0,
    })),
    paymentMethods: [
      { code: 'BankTransfer', name: 'Bank transfer', paidOrders: now.paid, revenue: now.gross },
      { code: 'PayPal', name: 'PayPal', paidOrders: 0, revenue: 0 },
    ],
    // The fixture's three buyers (see `fixtures`), by the district their orders were delivered to.
    buyersByDistrict: {
      distinctBuyers: some ? 3 : 0,
      // Owner decision 2026-10-21: an overseas row has no district and is named by its country.
      districts: some
        ? [
            { districtId: 2, name: 'Gampaha', buyers: 1, paidOrders: 2, revenue: 18400, countryCode: 'LK' },
            { districtId: 4, name: 'Kandy', buyers: 1, paidOrders: 1, revenue: 6600, countryCode: 'LK' },
            { districtId: 10, name: 'Jaffna', buyers: 1, paidOrders: 1, revenue: 3200, countryCode: 'LK' },
          ]
        : [],
    },
  };
}

function bucketed(state, period, interval) {
  const days = state.reporting ? inPeriod(state.reporting.sales, period) : [];

  return split(period, interval).map(({ start, end }) => {
    const inside = days.filter((row) => row.day >= start && row.day <= end);
    return salesRow(start, end, {
      orders: sum(inside, 'orders'),
      paidOrders: sum(inside, 'paidOrders'),
      grossRevenue: sum(inside, 'grossRevenue'),
      itemsRevenue: sum(inside, 'itemsRevenue'),
      deliveryRevenue: sum(inside, 'deliveryRevenue'),
      refundsCompleted: sum(inside, 'refundsCompleted'),
    });
  });
}

function total(period, rows) {
  const add = (key) => rows.reduce((sum2, row) => sum2 + row[key], 0);

  return salesRow(period.from, period.to, {
    orders: add('orders'),
    paidOrders: add('paidOrders'),
    grossRevenue: add('grossRevenue'),
    itemsRevenue: add('itemsRevenue'),
    deliveryRevenue: add('deliveryRevenue'),
    refundsCompleted: add('refundsCompleted'),
  });
}

function matchingInventory(state, url) {
  const status = url.searchParams.get('status');
  const search = (url.searchParams.get('search') ?? '').trim().toLowerCase();

  return (state.reporting?.inventory ?? []).filter(
    (row) => (!status || status === 'All' || row.stockStatus === status)
      && (search === '' || row.sku.toLowerCase().startsWith(search) || row.productCode.toLowerCase().startsWith(search) || row.productName.toLowerCase().includes(search)),
  );
}

function matchingCustomers(state, url, period) {
  const search = (url.searchParams.get('search') ?? '').trim().toLowerCase();
  const paid = state.reporting ? inPeriod(state.reporting.sales, period).length > 0 : false;

  return paid ? state.reporting.customers.filter((row) => search === '' || row.email.toLowerCase().startsWith(search)) : [];
}

/** `GetCustomerBuyersQueryHandler.IntervalFor`: Day up to 31 days, Week up to 182, Month beyond. */
function buyersInterval(period) {
  return period.days <= 31 ? 'Day' : period.days <= 182 ? 'Week' : 'Month';
}

/**
 * The customers chart (#186b) over exactly the report's matching buyers. A fixture buyer paid on their
 * `firstPaidOn` and `lastPaidOn` days (those inside the period); a buyer is first-time in
 * the bucket of their `firstPaidOn` unless `paidBefore` names them, and returning in any other bucket they
 * paid in. Returning = buyers - first-time, per point and over the period, as the server works it out.
 */
function buyersBody(state, url, period) {
  const matching = matchingCustomers(state, url, period);
  const before = new Set(state.reporting?.paidBefore ?? []);
  const interval = buyersInterval(period);
  const within = (day, start, end) => day >= start && day <= end;
  const paidDays = (row) => [row.firstPaidOn, row.lastPaidOn].filter((day) => within(day, period.from, period.to));
  const firstTime = (row, start, end) => !before.has(row.email) && within(row.firstPaidOn, start, end);

  const points = split(period, interval).map(({ start, end }) => {
    const inside = matching.filter((row) => paidDays(row).some((day) => within(day, start, end)));
    return buyersPoint(start, end, inside.length, inside.filter((row) => firstTime(row, start, end)).length);
  });

  // The report's own count: every fixture buyer it lists paid on one of the fixture's sale days.
  const buyers = matching.length;
  const firstTimeBuyers = matching.filter((row) => firstTime(row, period.from, period.to)).length;

  return {
    period,
    interval,
    buyers,
    firstTimeBuyers,
    returningBuyers: buyers - firstTimeBuyers,
    points,
  };
}

function counts(rows) {
  return {
    inStock: rows.filter((row) => row.stockStatus === 'InStock').length,
    lowStock: rows.filter((row) => row.stockStatus === 'LowStock').length,
    outOfStock: rows.filter((row) => row.stockStatus === 'OutOfStock').length,
  };
}

/** `CsvDocument.Cell`, exactly: neutralise a formula start, then quote and double quotes. */
function csvCell(value) {
  const text = value === null || value === undefined ? '' : String(value);

  if (text === '') {
    return '""';
  }

  const safe = /^[\t\r]/.test(text) || /^ *[=+\-@]/.test(text) ? `'${text}` : text;
  return `"${safe.replace(/"/g, '""')}"`;
}

function sendCsv(response, fileName, rows) {
  const body = Buffer.concat([
    Buffer.from([0xef, 0xbb, 0xbf]),
    Buffer.from(rows.map((cells) => `${cells.map(csvCell).join(',')}\r\n`).join(''), 'utf8'),
  ]);

  response.writeHead(200, {
    'Content-Type': 'text/csv; charset=utf-8',
    'Content-Disposition': `attachment; filename=${fileName}`,
    'Cache-Control': 'no-store, no-cache, private',
    'Content-Length': body.length,
  });
  response.end(body);
}

const money = (amount) => amount.toFixed(2);

function invalid(json, response, errors) {
  json(response, 400, { title: 'One or more fields are invalid.', status: 400, errors });
  return true;
}

// ---- traffic (ADM-06, ADM-20, W4-03) ---------------------------------------------------------------
//
// Field for field with `SpencerDenim.Application.Analytics.Reporting` (`TrafficShapeTests` reads the row
// builders below). A new installation (`set=none`) has no traffic at all, and says so; `set=some` answers test
// fixtures that are never shop data. Visits, page views and crawler views per day follow a fixed pattern so a test
// can work out a total; the channels, devices, sources and pages are fixed shares of it.

const CHANNELS = ['Direct', 'Organic', 'Social', 'Referral', 'Email', 'Paid', 'Other'];
const CHANNEL_SHARES = [0.4, 0.3, 0.2, 0.1, 0, 0, 0];
const DEVICES = ['Desktop', 'Mobile', 'Tablet', 'Other'];
const DEVICE_SHARES = [0.3, 0.6, 0.1, 0];

function eachDay(period) {
  return Array.from({ length: period.days }, (_, index) => addDays(period.from, index));
}

function trafficDays(state, period) {
  return eachDay(period).map((date, index) =>
    state.reporting === null
      ? trafficDayRow(date, 0, 0, 0)
      : trafficDayRow(date, (index % 4) + 1, ((index % 4) + 1) * 3, index % 2),
  );
}

function trafficDayRow(date, visits, pageViews, botPageViews) {
  return { date, visits, pageViews, botPageViews };
}

/** Splits a whole number by shares, the remainder to the first: integers that add up to the total. */
function splitWhole(total, shares) {
  const parts = shares.map((share) => Math.floor(total * share));
  parts[0] += total - parts.reduce((sum, part) => sum + part, 0);
  return parts;
}

function sharePercent(part, whole) {
  return whole === 0 ? 0 : Math.round((part / whole) * 1000) / 10;
}

function trafficChannelRow(channel, visits, pageViews, total) {
  return { channel, label: channel, visits, pageViews, sharePercent: sharePercent(visits, total) };
}

function trafficDeviceRow(device, visits, pageViews, total) {
  return { device, label: device, visits, pageViews, sharePercent: sharePercent(visits, total) };
}

function trafficSourceRow(source, visits, pageViews) {
  return { source, visits, pageViews };
}

function trafficPageRow(path, pageType, productName, pageViews, visits) {
  return { path, pageType, productName, pageViews, visits };
}

function channelsOf(visits, pageViews) {
  const v = splitWhole(visits, visits === 0 ? CHANNEL_SHARES.map(() => 0) : CHANNEL_SHARES);
  const p = splitWhole(pageViews, pageViews === 0 ? CHANNEL_SHARES.map(() => 0) : CHANNEL_SHARES);
  return CHANNELS.map((channel, index) => trafficChannelRow(channel, v[index], p[index], visits));
}

function trafficBody(state, period) {
  const previous = previousOf(period);
  const days = trafficDays(state, period);
  const before = trafficDays(state, previous);
  const sum = (rows, key) => rows.reduce((total, row) => total + row[key], 0);
  const visits = sum(days, 'visits');
  const pageViews = sum(days, 'pageViews');
  const deviceVisits = splitWhole(visits, visits === 0 ? DEVICE_SHARES.map(() => 0) : DEVICE_SHARES);
  const devicePages = splitWhole(pageViews, pageViews === 0 ? DEVICE_SHARES.map(() => 0) : DEVICE_SHARES);
  const some = visits > 0;

  return {
    period,
    previousPeriod: previous,
    liveFrom: addDays(colomboToday(), -3),
    visits: figure(visits, sum(before, 'visits')),
    pageViews: figure(pageViews, sum(before, 'pageViews')),
    botPageViews: sum(days, 'botPageViews'),
    days,
    channels: channelsOf(visits, pageViews),
    devices: DEVICES.map((device, index) => trafficDeviceRow(device, deviceVisits[index], devicePages[index], visits)),
    sources: some
      ? [
          trafficSourceRow('www.google.com', Math.ceil(visits * 0.3), Math.ceil(pageViews * 0.3)),
          trafficSourceRow('(direct)', Math.ceil(visits * 0.4), Math.ceil(pageViews * 0.4)),
          trafficSourceRow('a-fixture-referrer-with-a-very-long-host-name.example', 1, 2),
        ]
      : [],
    topPages: some
      ? [
          trafficPageRow('/product/fixture-linen-wrap-dress', 'Product', 'Fixture linen wrap dress', Math.ceil(pageViews * 0.4), Math.ceil(visits * 0.5)),
          trafficPageRow('/women/dresses?colours=blue&colours=red&maxPrice=5000&minPrice=1000&sizes=M&sort=price-asc', 'Category', null, 3, 2),
          trafficPageRow('/order/*', 'Order', null, 2, 2),
          trafficPageRow('/', 'Home', null, 1, 1),
        ]
      : [],
  };
}

function trafficSourcesBody(state, period) {
  const days = trafficDays(state, period);
  const visits = days.reduce((total, row) => total + row.visits, 0);
  const pageViews = days.reduce((total, row) => total + row.pageViews, 0);
  const paidOrders = state.reporting === null ? 0 : 3;

  return {
    period,
    visits,
    paidOrders,
    conversionPercent: visits === 0 ? null : Math.round((paidOrders * 1000) / visits) / 10,
    channels: channelsOf(visits, pageViews),
  };
}

/** Returns true when it answered the request. */
export async function handleReportingRoute({ path, method, url, response, state, json }) {
  if (path === '/__test__/reporting') {
    state.reporting = url.searchParams.get('set') === 'none' ? null : fixtures();
    json(response, 200, { ok: true });
    return true;
  }

  if (path === '/__test__/reporting/exports') {
    json(response, 200, { exports: state.reportExports });
    return true;
  }

  const errors = {};

  if (path === '/api/admin/dashboard' && method === 'GET') {
    const period = readPeriod(url, errors);
    return period ? (json(response, 200, dashboardBody(state, period)), true) : invalid(json, response, errors);
  }

  if (path === '/api/admin/dashboard/trend' && method === 'GET') {
    const interval = readName(url.searchParams.get('interval'), 'interval', ['Week', 'Month'], 'Month', errors);
    const to = readDay(url.searchParams.get('to'), 'to', errors) ?? colomboToday();

    if (Object.keys(errors).length > 0) {
      return invalid(json, response, errors);
    }

    const window = trendWindow(to, interval);
    const points = bucketed(state, window, interval).map((row) => ({
      start: row.start,
      end: row.end,
      grossRevenue: row.grossRevenue,
      paidOrders: row.paidOrders,
      orders: row.orders,
    }));

    json(response, 200, { interval, window, currency: 'LKR', points });
    return true;
  }

  if (path === '/api/admin/traffic' && method === 'GET') {
    const period = readPeriod(url, errors);
    return period ? (json(response, 200, trafficBody(state, period)), true) : invalid(json, response, errors);
  }

  if (path === '/api/admin/dashboard/traffic-sources' && method === 'GET') {
    const period = readPeriod(url, errors);
    return period ? (json(response, 200, trafficSourcesBody(state, period)), true) : invalid(json, response, errors);
  }

  if (path === '/api/admin/dashboard/low-stock' && method === 'GET') {
    const low = (state.reporting?.inventory ?? [])
      .filter((row) => row.stockStatus === 'LowStock')
      .sort((a, b) => a.available - b.available || a.sku.localeCompare(b.sku));

    json(response, 200, { defaultThreshold: 5, totalCount: low.length, rows: low.slice(0, 10).map(lowStockRow) });
    return true;
  }

  const sales = /^\/api\/admin\/reports\/sales(\/export)?$/.exec(path);
  if (sales && method === (sales[1] ? 'POST' : 'GET')) {
    const groupBy = readName(url.searchParams.get('groupBy'), 'groupBy', ['Day', 'Week', 'Month'], 'Day', errors);
    const period = readPeriod(url, errors);

    if (!period || Object.keys(errors).length > 0) {
      return invalid(json, response, errors);
    }

    const rows = bucketed(state, period, groupBy);

    if (!sales[1]) {
      json(response, 200, { period, groupBy, currency: 'LKR', rows, totals: total(period, rows) });
      return true;
    }

    state.reportExports += 1;
    const totals = total(period, rows);
    const cells = (first, row) => [first, row.end, row.orders, row.paidOrders, money(row.grossRevenue), money(row.itemsRevenue), money(row.deliveryRevenue), money(row.refundsCompleted), money(row.netRevenue)];

    sendCsv(response, `sales-report-${period.from}-to-${period.to}.csv`, [
      ['From', 'To', 'Orders placed', 'Paid orders', 'Gross revenue (LKR)', 'Items after promo (LKR)', 'Delivery (LKR)', 'Refunds completed (LKR)', 'Net revenue (LKR)'],
      ...rows.map((row) => cells(row.start, row)),
      cells('Total', totals),
    ]);
    return true;
  }

  const inventory = /^\/api\/admin\/reports\/inventory(\/export)?$/.exec(path);
  if (inventory && method === (inventory[1] ? 'POST' : 'GET')) {
    readName(url.searchParams.get('status'), 'status', ['All', 'InStock', 'LowStock', 'OutOfStock'], 'All', errors);

    if (Object.keys(errors).length > 0) {
      return invalid(json, response, errors);
    }

    const matching = matchingInventory(state, url);

    if (!inventory[1]) {
      const { page, pageSize } = clampPage(url);
      json(response, 200, {
        rows: matching.slice((page - 1) * pageSize, page * pageSize),
        page,
        pageSize,
        totalCount: matching.length,
        defaultThreshold: 5,
        counts: counts(state.reporting?.inventory ?? []),
      });
      return true;
    }

    state.reportExports += 1;
    sendCsv(response, `inventory-report-${colomboToday()}.csv`, [
      ['Product', 'Product code', 'Department', 'SKU', 'Colour', 'Size', 'On hand', 'Reserved', 'Available', 'Low-stock threshold', 'Threshold from', 'Status'],
      ...matching.map((row) => [row.productName, row.productCode, row.department, row.sku, row.colour, row.size, row.onHand, row.reserved, row.available, row.threshold, row.thresholdIsDefault ? 'Shop default' : 'This variant', { InStock: 'In stock', LowStock: 'Low stock', OutOfStock: 'Out of stock' }[row.stockStatus]]),
    ]);
    return true;
  }

  if (path === '/api/admin/reports/customers/buyers' && method === 'GET') {
    const period = readPeriod(url, errors);
    return period ? (json(response, 200, buyersBody(state, url, period)), true) : invalid(json, response, errors);
  }

  const customers = /^\/api\/admin\/reports\/customers(\/export)?$/.exec(path);
  if (customers && method === (customers[1] ? 'POST' : 'GET')) {
    const period = readPeriod(url, errors);

    if (!period) {
      return invalid(json, response, errors);
    }

    const matching = matchingCustomers(state, url, period);

    if (!customers[1]) {
      const { page, pageSize } = clampPage(url);
      json(response, 200, {
        period,
        currency: 'LKR',
        rows: matching.slice((page - 1) * pageSize, page * pageSize),
        page,
        pageSize,
        totalCount: matching.length,
        totalRevenue: matching.reduce((sum2, row) => sum2 + row.grossRevenue, 0),
      });
      return true;
    }

    state.reportExports += 1;
    sendCsv(response, `customers-report-${period.from}-to-${period.to}.csv`, [
      ['Email', 'Name', 'Delivery district (latest order)', 'Has an account', 'Paid orders', 'Gross revenue (LKR)', 'First paid (Colombo)', 'Last paid (Colombo)'],
      ...matching.map((row) => [row.email, row.name, row.district, row.hasAccount ? 'Yes' : 'No', row.paidOrders, money(row.grossRevenue), row.firstPaidOn, row.lastPaidOn]),
    ]);
    return true;
  }

  return false;
}
