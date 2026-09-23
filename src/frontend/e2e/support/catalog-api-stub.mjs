/**
 * A stand-in for `GET /api/catalog/*` while the browser tests run.
 *
 * The listing is server-rendered, so the SSR server has to be able to fetch products before it can
 * answer a request at all. Starting the real .NET API would mean a SQL Server instance, migrations
 * and a seeded catalogue for every `npx playwright test`; what these tests actually need is a
 * predictable number of products at a predictable shape, which is what this serves.
 *
 * It answers the same field names, enum spellings and paging contract as
 * `SpencerDenim.Application.Catalog.Contracts`. When that contract changes, this changes with it —
 * the unit tests are what pin the mapping; this only has to be shaped like the real thing.
 */
import { createServer } from 'node:http';

const PORT = Number(process.env.E2E_API_PORT ?? 4421);

/** Matches the seeded tree of docs/08 §9 closely enough to exercise all three route depths. */
const DEPARTMENTS = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: 'Everyday essentials and occasion pieces, cut for Sri Lankan weather.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category(11, 'Dresses', 'dresses', 'From weekday shifts to evening midis.', 1),
      category(12, 'Jeans', 'jeans', 'Denim in high-rise, straight and wide-leg cuts.', 2),
      category(13, 'Skirts', 'skirts', 'Mini, midi and maxi in easy fabrics.', 2),
    ],
  },
  {
    id: 2,
    name: 'Men',
    slug: 'men',
    description: 'Workwear, weekend shirts and denim built to last.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category(21, 'Shirts', 'shirts', 'Linen, oxford and poplin, cut for the climate.', 1),
      category(22, 'Jeans', 'jeans', 'Slim, straight and relaxed denim.', 2),
    ],
  },
  {
    id: 3,
    name: 'Kids',
    slug: 'kids',
    description: 'Girls, boys and baby — soft fabrics and room to grow.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category(31, 'Girls', 'girls', 'Dresses, tops and bottoms for girls.', 1, [
        category(311, 'Girls Dresses', 'girls-dresses', 'Cotton dresses.', 1, [], 2),
      ]),
    ],
  },
];

const COLOURS = [
  { id: 1, name: 'Indigo', familyName: 'Blue', familySlug: 'blue', hexCode: '#2E6DB4', swatchImageUrl: null },
  { id: 2, name: 'Black', familyName: 'Black', familySlug: 'black', hexCode: '#101010', swatchImageUrl: null },
  { id: 3, name: 'Ecru', familyName: 'Cream', familySlug: 'cream', hexCode: '#F3EADA', swatchImageUrl: null },
];

const BADGES = [null, 'New', 'Sale', 'BestSeller', 'LowStock'];

/** Enough products that "Load More" has something to add at every page size the listing asks for. */
const TOTAL_PRODUCTS = 120;

/** One colour filter narrows the catalogue; another matches nothing, for the empty state. */
const COLOUR_TOTALS = { blue: 30, black: 18, cream: 0 };

function category(id, name, slug, description, megaMenuColumn, children = [], depth = 1) {
  return {
    id,
    name,
    slug,
    depth,
    description,
    imageUrl: null,
    showInMegaMenu: true,
    megaMenuColumn,
    ageGroupSlug: null,
    children,
  };
}

function product(index) {
  const discounted = index % 3 === 0;
  const current = 2490 + index * 110;

  return {
    id: index,
    productCode: `SD-${String(index).padStart(4, '0')}`,
    name: `Test Product ${index}`,
    slug: `test-product-${index}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    // Null rather than a URL: there is no media server here, and a broken image is a DoD failure.
    imageUrl: null,
    hoverImageUrl: null,
    imageAltText: null,
    price: {
      current,
      original: discounted ? Math.round(current * 1.4) : current,
      discountPercent: discounted ? 28 : 0,
      hasDiscount: discounted,
      currency: 'LKR',
    },
    rating: { average: 3.5 + (index % 3) * 0.5, count: 10 + index },
    badge: BADGES[index % BADGES.length],
    stockStatus: 'InStock',
    isInStock: true,
    colours: COLOURS.slice(0, (index % 3) + 1),
  };
}

function productsFor(params) {
  const colour = params.getAll('colour')[0];
  const total = colour === undefined ? TOTAL_PRODUCTS : (COLOUR_TOTALS[colour] ?? 0);

  const page = Number(params.get('page') ?? '1');
  const pageSize = Number(params.get('pageSize') ?? '24');
  const skip = (page - 1) * pageSize;

  const items = Array.from({ length: Math.max(0, Math.min(pageSize, total - skip)) }, (_, index) =>
    product(skip + index + 1),
  );

  return { items, page, pageSize, totalCount: total };
}

const server = createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://localhost:${PORT}`);

  // The browser talks to this stub across an origin; in production the reverse proxy puts the API
  // behind the storefront's own origin and no CORS header is involved.
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Content-Type', 'application/json; charset=utf-8');
  response.setHeader('Cache-Control', 'no-store');

  if (url.pathname === '/api/catalog/departments') {
    response.end(JSON.stringify(DEPARTMENTS));
    return;
  }

  if (url.pathname === '/api/catalog/products') {
    response.end(JSON.stringify(productsFor(url.searchParams)));
    return;
  }

  response.statusCode = 404;
  response.end(JSON.stringify({ title: 'Not found', status: 404 }));
});

server.listen(PORT, () => process.stdout.write(`catalog stub on ${PORT}\n`));
