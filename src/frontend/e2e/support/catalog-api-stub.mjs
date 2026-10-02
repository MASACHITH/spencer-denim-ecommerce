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
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';

const PORT = Number(process.env.E2E_API_PORT ?? 4421);

/**
 * Where the media pipeline's files live, as the API reports them.
 *
 * Doc 08 §3.3: the database holds a relative storage key and `MediaUrlBuilder.ToPublicUrl` joins
 * it to the configured base. In a deployment that base is root-relative, because Caddy serves
 * `/media` from the public uploads volume on the storefront's own origin (doc 03 §"Deployment
 * topology"), so a published media address never names a host. This stub says the same thing.
 *
 * It used to say `http://localhost:${PORT}/media` instead, which the builder can indeed emit —
 * it returns an already-absolute key unchanged — but these URLs are server-rendered into card
 * and gallery markup, so the stub's own port ended up in the HTML. GLB-11 in
 * `product-listing.e2e.ts` asserts that no internal address does, and it failed every run. The
 * assertion is the right one: HTML that names the machine it was rendered on is HTML that breaks
 * as soon as it is served from anywhere else.
 *
 * The bytes still have to arrive — several tests turn vacuous if a card quietly falls back to
 * its placeholder — and these tests have no reverse proxy. So the SSR server carries the proxy's
 * job for the length of a run: `E2E_MEDIA_PASSTHROUGH_URL`, set per storefront in
 * `playwright.config.ts` beside the `API_INTERNAL_URL` it mirrors, mounts a `/media` passthrough
 * back to this stub. Unset, which is every real deployment, that route does not exist.
 */
const MEDIA_BASE = '/media';

/**
 * A 1x1 WebP, served for every rendition below.
 *
 * WebP rather than the GIF used elsewhere in this file: the URLs end `.webp` and the browser is
 * told so, and a picture whose bytes disagree with its type is one the browser refuses to decode.
 * Nothing here measures pixels — what matters is that the file really arrives, so the card does
 * not fall back to its placeholder and `currentSrc` says which candidate was chosen.
 */
const WEBP_PIXEL = Buffer.from(
  'UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==',
  'base64',
);

/** A stable asset id per picture, in the shape of the GUIDs `MediaAssets.PublicId` holds. */
function publicId(seed) {
  return `00000000-0000-4000-8000-${String(seed).padStart(12, '0')}`;
}

/** `public/renditions/{PublicId}/{code}.webp` — the key convention of `MediaRenditions`. */
function rendition(id, code) {
  return `${MEDIA_BASE}/public/renditions/${id}/${code}.webp`;
}

/**
 * What a card's `imageSrcSet` looks like: thumb, card and large with their real widths. The zoom
 * rendition is deliberately absent — a card is never drawn at 2000 px, and only the product
 * gallery offers it ({@link gallerySrcSet}).
 */
function cardSrcSet(id) {
  return (
    `${rendition(id, 'thumb')} 160w, ` +
    `${rendition(id, 'card')} 600w, ` +
    `${rendition(id, 'large')} 1200w`
  );
}

/** The gallery's ladder, which runs all the way to the full-screen view's 2000 px file. */
function gallerySrcSet(id) {
  return `${cardSrcSet(id)}, ${rendition(id, 'zoom')} 2000w`;
}

/** Matches the seeded tree of docs/08 §9 closely enough to exercise all three route depths. */
const DEPARTMENTS = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: 'Everyday essentials and occasion pieces, cut for Sri Lankan weather.',
    heroImageUrl: null,
    heroImageSrcSet: null,
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
    heroImageSrcSet: null,
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
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category(31, 'Girls', 'girls', 'Dresses, tops and bottoms for girls.', 1, [
        category(311, 'Girls Dresses', 'girls-dresses', 'Cotton dresses.', 1, [], 2),
      ]),
    ],
  },
];

/**
 * A category the live catalogue has and the storefront's frozen fallback
 * (`core/navigation/navigation.data.ts`) does not.
 *
 * It stands in for the first category ADM-11 lets an admin add. Everything else in
 * {@link DEPARTMENTS} is also in that fallback, so without it the two trees can never disagree and
 * the storefront's answer for "a real page the fallback has never heard of" cannot be measured at
 * all — which is exactly how #30 stayed invisible.
 */
const LIVE_ONLY_CATEGORY = category(14, 'Coats', 'coats', 'Trench, wool and rain coats.', 3);

/** {@link DEPARTMENTS} with that category under Women. Served when `E2E_DEPARTMENTS=live-only`. */
const LIVE_ONLY_DEPARTMENTS = DEPARTMENTS.map((department) =>
  department.slug === 'women'
    ? { ...department, categories: [...department.categories, LIVE_ONLY_CATEGORY] }
    : department,
);

const COLOURS = [
  { id: 1, name: 'Indigo', familyName: 'Blue', familySlug: 'blue', hexCode: '#2E6DB4', swatchImageUrl: null },
  { id: 2, name: 'Black', familyName: 'Black', familySlug: 'black', hexCode: '#101010', swatchImageUrl: null },
  { id: 3, name: 'Ecru', familyName: 'Cream', familySlug: 'cream', hexCode: '#F3EADA', swatchImageUrl: null },
];

const BADGES = [null, 'New', 'Sale', 'BestSeller', 'LowStock'];

/**
 * Every colourway the demo catalogue seeds, hex for hex (`DemoCatalogueData`).
 *
 * The three colours above are enough for a card, but not for the sold-out swatch: whether its
 * mark can be seen depends entirely on the colour underneath it, and testing one swatch tests one
 * colour. The wholly sold-out product carries all sixteen so the check covers the palette a
 * shopper can actually meet.
 */
const SEEDED_PALETTE = [
  ['Sandstone Beige', 'beige', '#D8CBB6'],
  ['Optic White', 'white', '#FFFFFF'],
  ['Coconut Cream', 'cream', '#F3EADA'],
  ['Deep Navy', 'navy', '#1F2A44'],
  ['True Black', 'black', '#101010'],
  ['Charcoal Grey', 'grey', '#4A4D52'],
  ['Indigo Rinse', 'blue', '#3A5A8C'],
  ['Mid Wash Blue', 'blue', '#6E93C4'],
  ['Olive Green', 'green', '#5C6B4A'],
  ['Terracotta', 'orange', '#C4703F'],
  ['Blush Pink', 'pink', '#E8A0BF'],
  ['Deep Maroon', 'red', '#7E2B2B'],
  ['Mustard', 'yellow', '#D9A93C'],
  ['Plum', 'purple', '#6C4A8F'],
  ['Chocolate', 'brown', '#5B3F2E'],
  ['Deep Teal', 'green', '#2E6B66'],
].map(([name, familySlug, hexCode], index) => ({
  id: 101 + index,
  name,
  familyName: familySlug[0].toUpperCase() + familySlug.slice(1),
  familySlug,
  hexCode,
  swatchImageUrl: null,
}));

/**
 * Enough products that "Load More" has something to add at every page size the listing asks for.
 * Raised through the environment when a test needs a deep restore to be worth restoring.
 */
const TOTAL_PRODUCTS = Number(process.env.E2E_PRODUCT_TOTAL ?? 120);

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
    imageSrcSet: null,
    showInMegaMenu: true,
    megaMenuColumn,
    ageGroupSlug: null,
    children,
  };
}

function product(index) {
  const discounted = index % 3 === 0;
  const current = 2490 + index * 110;
  const main = publicId(index * 10 + 1);
  const hover = publicId(index * 10 + 2);

  return {
    id: index,
    productCode: `SD-${String(index).padStart(4, '0')}`,
    name: `Test Product ${index}`,
    slug: `test-product-${index}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    // The card rendition, never the original: a listing that fetched originals weighed about
    // 1,780 KB a page. This is the shape the regression test in `product-listing.e2e.ts` reads.
    imageUrl: rendition(main, 'card'),
    imageSrcSet: cardSrcSet(main),
    hoverImageUrl: rendition(hover, 'card'),
    hoverImageSrcSet: cardSrcSet(hover),
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

/**
 * The sitemap feed's body: every department with its category trails, and every product.
 *
 * Built from the same {@link DEPARTMENTS} tree the menu is served from, so a test can assert that a
 * listing the menu links to is also a listing the sitemap offers. `E2E_SITEMAP_TRUNCATED` reports the
 * cap as reached without having to invent 45,000 products.
 */
function sitemapPayload() {
  const tree = Array.isArray(DEPARTMENTS_BODY) && DEPARTMENTS_BODY.length > 0 ? DEPARTMENTS_BODY : DEPARTMENTS;

  const trails = (nodes, ancestors) =>
    nodes.flatMap((node) => {
      const trail = [...ancestors, node.slug];
      return [
        { slugTrail: trail, lastModifiedUtc: `2026-09-${String(10 + (node.id % 18)).padStart(2, '0')}T08:00:00Z` },
        ...trails(node.children ?? [], trail),
      ];
    });

  return {
    generatedAtUtc: '2026-10-02T00:00:00Z',
    departments: tree.map((department) => ({
      slug: department.slug,
      lastModifiedUtc: '2026-09-01T08:00:00Z',
      categories: trails(department.categories ?? [], []),
    })),
    products: Array.from({ length: TOTAL_PRODUCTS }, (_, index) => ({
      slug: `test-product-${index + 1}`,
      lastModifiedUtc: `2026-09-${String(1 + (index % 28)).padStart(2, '0')}T08:00:00Z`,
    })),
    // Which of the API's three caps to report as reached. A comma list rather than a boolean, because
    // there are three caps and the renderer writes a different note for each; `all` is the shorthand.
    //
    // It is wired into the **deep** server pair in `playwright.config.ts`, which is the pair that exists
    // for a catalogue big enough to matter - and "big enough to truncate" is the same idea. It was an
    // unset variable for a while, which meant the API-shape -> asSitemap -> caps -> rendered-note chain
    // for these flags shipped unexercised; a switch nothing sets is not a fixture.
    departmentsTruncated: truncated('departments'),
    categoriesTruncated: truncated('categories'),
    productsTruncated: truncated('products'),
  };
}

/** Whether `E2E_SITEMAP_TRUNCATED` asks for this list to be reported as capped. */
function truncated(list) {
  const asked = (process.env.E2E_SITEMAP_TRUNCATED ?? '')
    .split(',')
    .map((entry) => entry.trim().toLowerCase())
    .filter(Boolean);

  return asked.includes('all') || asked.includes(list);
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

// ------------------------------------------------------------------ GET /api/catalog/search
//
// `SearchTerms`, `ProductSearchQuery.Create`, `ProductListQuery.Create` and
// `ProductCatalogReader.ResolveFiltersAsync`, mirrored rather than simplified.
//
// The temptation with a search box is to make the stub answer whatever the component hoped for.
// That is the shape of bug that survived three rounds of green tests on the listing (see `answer`
// in `listing-page.spec.ts`), and it is worse here, because the interesting cases are all the ones
// where the API says **no**.
//
// The first version of this mirrored the `q` rules and nothing else, and the gap was not academic:
// it answered 200 to all thirteen query strings the real API rejects with a 400, which meant the
// `'query'` branch of `searchFailure` — the one that shows the API's own message about an ambiguous
// category — could not be reached from a browser at all. So the listing validation and the filter
// resolution are mirrored here too.
//
// `productsFor` deliberately does **not** use them yet, and the reason is about the fixture rather than
// about the tests. The `E2E_DEPARTMENTS=empty` pair serves `[]` from the departments feed **while the
// catalogue still holds products** — a state the real system cannot be in, and deliberately so, because
// that pair exists to measure what the storefront does when the feed answers uselessly. A faithful
// resolver in `productsFor` would answer "no such department" there, and the fixture would stop
// modelling the thing it was built for. Making `productsFor` faithful therefore means fixing that
// fixture in the same change; both halves are filed as **#82**.
//
// One consequence of the split, written down so nobody finds it by debugging: `searchFor` resolves
// against `DEPARTMENTS_BODY`, so on the empty pair `/search?...&department=women` answers 200 with no
// results where `/api/catalog/products?department=women` answers 120. No search test runs against that
// pair today, so nothing is broken — but the two halves of this stub now answer the same question
// differently, which is the divergence class this file was failed for one level up.

const MIN_TERM_LENGTH = 2;
const MAX_TERM_LENGTH = 100;
const MAX_TOKENS = 5;

/**
 * The bounds of `ProductListQuery`, named as it names them.
 *
 * The slug pattern itself is `SLUG_PATTERN` further down, which `productBySlug` already declares:
 * it is the one `CatalogSlug` rule, and a second copy of the same regular expression in one file is
 * a thing to keep in step by hand. Only the maximum length differs by column, so that is a parameter.
 */
const MAX_SLUG_LENGTH = 100;
const MAX_SIZE_LABEL_LENGTH = 20;
const MAX_FILTER_VALUES = 25;
const MAX_PRICE_VALUE = 10_000_000;
const MAX_PAGE = 1_000;
const MAX_PAGE_SIZE = 96;
const DEFAULT_PAGE_SIZE = 24;

/** `ProductSortOptions.All`. */
const SORTS = [
  'recommended',
  'newest',
  'best-selling',
  'price-asc',
  'price-desc',
  'rating',
  'discount',
];

/** `SearchTerms.Tokenise`. */
function tokenise(raw) {
  const trimmed = (raw ?? '').trim().slice(0, MAX_TERM_LENGTH);
  const tokens = [];

  for (const part of trimmed.split(/\s+/u)) {
    const token = part.toLowerCase();
    if (token.length < MIN_TERM_LENGTH || tokens.includes(token)) {
      continue;
    }

    tokens.push(token);
    if (tokens.length === MAX_TOKENS) {
      break;
    }
  }

  return tokens;
}

/**
 * The attributes `catalog.ProductSearchIndex.SearchText` carries and `ProductCardDto` does not:
 * material, fit, occasion and the variant SKUs.
 *
 * Held beside the card rather than on it, deliberately. The card DTO has no such fields, so putting
 * them there to make the search corpus richer would have made the stub's listing payload a shape the
 * real API never sends — and five other suites read that payload.
 */
const MATERIALS = ['Cotton', 'Linen', 'Denim'];
const FITS = ['Relaxed', 'Slim', 'Straight'];
const OCCASIONS = ['Everyday', 'Workwear', 'Evening'];

/**
 * Size labels from the seeded scales (`CatalogReferenceData`), **one scale per category**, which is how
 * a real product carries them.
 *
 * They exist because the search page's filter panel offers size checkboxes, and a stub where every size
 * matched nothing showed a shopper "no products found" where production shows them products. The API
 * matches on the **label**, case-insensitively, not on a slug or a scale.
 *
 * The scale follows the category because the alternative is a different wrong answer. Measured on the
 * real API: `?q=jeans&size=M` is **0** and `?q=jeans&size=30` is **5**, because jeans are on the Women
 * waist scale and `M` belongs to the Alpha scale; `?q=shirt&size=M` is 12 of 14. A stub that put alpha
 * sizes on half its jeans would narrow where production does not, which is the same class of divergence
 * as the one that put a department name in the search index.
 */
const WAIST_SIZES = [
  ['26', '28'],
  ['28', '30'],
  ['30', '32'],
];

/** The one-size scale, for the saree — as `DemoCatalogueData` gives it (`CatalogReferenceData`). */
const ONE_SIZE = ['One Size'];

function attributesFor(card, index) {
  // Every "Test Product" is in Women > Jeans, so every one of them is on the waist scale; the saree is
  // in Traditional Wear and is one size, exactly as the seeded one is.
  const sizes = card.categorySlug === 'jeans' ? WAIST_SIZES[index % 3] : ONE_SIZE;

  return {
    material: MATERIALS[index % MATERIALS.length],
    fit: FITS[index % FITS.length],
    occasion: OCCASIONS[index % OCCASIONS.length],
    sizes,
    // The real SKU is per variant: the product code plus the size it is (`DemoCatalogueSeeder`).
    skus: sizes.map((size) => `${card.productCode}-${size}`),
  };
}

/**
 * The longest product name the demo catalogue actually seeds — 41 characters, with Tamil script and
 * combining marks (`DemoCatalogueData`, `SD-W-0016`).
 *
 * The first version of this used "Tailored Wool-Look Trousers" and called it the longest; it is the
 * third longest, and the two above it are both Indic. That matters for a 320 px measurement: a
 * combining mark does not add a break opportunity, and `overflow-wrap: anywhere` is what has to save
 * the layout rather than a space. It is in the **search** corpus only, never in the listing's 120
 * products, so nothing that counts cards or reads the grid changes.
 */
const LONG_NAME_PRODUCT = {
  ...product(9001),
  productCode: 'SD-W-0016',
  name: 'Kanjivaram (காஞ்சிபுரம்) Silk-Blend Saree',
  slug: 'kanjivaram-silk-blend-saree',
  categoryName: 'Traditional Wear',
  categorySlug: 'traditional-wear',
};

/**
 * One row of `catalog.ProductSearchIndex`, built the way `DemoCatalogueSeeder.BuildSearchIndex`
 * builds it: name, **category path names**, colour names, colour family names, material, fit,
 * occasion, product code, SKUs — de-duplicated case-insensitively and lower-cased.
 *
 * **What is deliberately absent, because it is absent in production:** the department name and every
 * slug. `PathNames` walks `ParentCategoryId` only, so it never reaches a department, and nothing in
 * that builder adds a slug. The first version of this stub joined `departmentName` and
 * `categorySlug` into the text, which made `q=women` match all 120 products here and none in
 * production — and made `q=linen`, which this suite used to mean "matches nothing", match every
 * linen product in production. The meaning of those tests inverted on the real API.
 *
 * Every product in this corpus sits in a depth-1 category, so its path names are just its own
 * category's name. A deeper category would contribute its ancestors too.
 */
function searchEntry(card, index) {
  const attributes = attributesFor(card, index);
  const parts = [
    card.name,
    card.categoryName,
    ...card.colours.flatMap((colour) => [colour.name, colour.familyName]),
    attributes.material,
    attributes.fit,
    attributes.occasion,
    card.productCode,
    ...attributes.skus,
  ];

  const seen = new Set();
  const kept = [];
  for (const part of parts) {
    const folded = part.toLowerCase();
    if (!seen.has(folded)) {
      seen.add(folded);
      kept.push(part);
    }
  }

  return { card, attributes, text: kept.join(' ').toLowerCase() };
}

/** The whole corpus, built once: the listing's 120 products plus the long-name one. */
const SEARCH_CORPUS = [
  ...Array.from({ length: TOTAL_PRODUCTS }, (_, index) => searchEntry(product(index + 1), index + 1)),
  searchEntry(LONG_NAME_PRODUCT, 9001),
];

/** A slug as `ProductListQuery.NormaliseSlug` would read it, or an error. */
function readSlug(raw, field, errors, maxLength = MAX_SLUG_LENGTH) {
  if (raw === null || raw === undefined || raw.trim() === '') {
    return null;
  }

  const slug = raw.trim().toLowerCase();
  if (slug.length > maxLength) {
    errors.push({ field, message: `${field} must be ${maxLength} characters or fewer.` });
    return null;
  }
  if (!SLUG_PATTERN.test(slug)) {
    errors.push({
      field,
      message: `${field} must be a slug: lower-case letters, digits and single hyphens.`,
    });
    return null;
  }

  return slug;
}

function readSlugList(params, name, errors) {
  const values = [];
  for (const raw of params.getAll(name)) {
    const slug = readSlug(raw, name, errors);
    if (slug !== null && !values.includes(slug)) {
      values.push(slug);
    }
  }

  if (values.length > MAX_FILTER_VALUES) {
    errors.push({ field: name, message: `${name} accepts at most ${MAX_FILTER_VALUES} values.` });
    return [];
  }

  return values;
}

function readLabelList(params, name, errors) {
  const values = [];
  for (const raw of params.getAll(name)) {
    const label = raw.trim();
    if (label === '') {
      continue;
    }
    if (label.length > MAX_SIZE_LABEL_LENGTH) {
      errors.push({
        field: name,
        message: `${name} values must be ${MAX_SIZE_LABEL_LENGTH} characters or fewer.`,
      });
      continue;
    }
    if (!values.some((value) => value.toLowerCase() === label.toLowerCase())) {
      values.push(label);
    }
  }

  if (values.length > MAX_FILTER_VALUES) {
    errors.push({ field: name, message: `${name} accepts at most ${MAX_FILTER_VALUES} values.` });
    return [];
  }

  return values;
}

/**
 * A decimal parameter inside its bounds.
 *
 * The pattern is the invariant `-1234.56` of `ProductListQuery.QueryNumberStyles`: a leading sign and
 * a decimal point, and **no group separators**, because they mean different things in different
 * locales. `Number('')` is 0 and `Number('NaN')` is NaN, so neither is left to `Number` alone.
 */
function readNumber(params, name, minimum, maximum, errors) {
  const raw = params.get(name);
  if (raw === null || raw.trim() === '') {
    return null;
  }

  const text = raw.trim();
  // `5.` and `.5` are both accepted, because `decimal.TryParse` with `AllowDecimalPoint` accepts both.
  // Measured against `ProductListQuery.Create`, which answers 200 for `minPrice=5.` and reads it as 5;
  // the first version of this pattern required a digit after the point and rejected it. A bare `.`
  // matches neither branch, and `1,000` still matches nothing, because a group separator means
  // different things in different locales and the storefront never writes one.
  if (!/^[+-]?(\d+\.?\d*|\.\d+)$/u.test(text)) {
    errors.push({
      field: name,
      message: `${name} must be a number between ${minimum} and ${maximum}.`,
    });
    return null;
  }

  const value = Number(text);
  if (value < minimum || value > maximum) {
    errors.push({ field: name, message: `${name} must be between ${minimum} and ${maximum}.` });
    return null;
  }

  return value;
}

function readWholeNumber(params, name, minimum, maximum, errors, fallback) {
  const raw = params.get(name);
  if (raw === null || raw.trim() === '') {
    return fallback;
  }

  const text = raw.trim();
  const value = Number(text);

  // Two messages, and which one you get depends on whether `int.TryParse` succeeded — not on whether
  // the number is in range. `page=2147483648` and `page=99999999999999999999` overflow an `int`, so the
  // real API never reaches its range check and says "must be a whole number"; `page=0` is a perfectly
  // good `int` and gets "must be between". Both measured against `ProductListQuery.Create`.
  const isInt32 = /^[+-]?\d+$/u.test(text) && value >= -2_147_483_648 && value <= 2_147_483_647;
  if (!isInt32) {
    errors.push({
      field: name,
      message: `${name} must be a whole number between ${minimum} and ${maximum}.`,
    });
    return fallback;
  }

  if (value < minimum || value > maximum) {
    errors.push({ field: name, message: `${name} must be between ${minimum} and ${maximum}.` });
    return fallback;
  }

  return value;
}

/** Two decimals, as `NormaliseMoney` stores them. */
function roundMoney(value) {
  return value === null ? null : Math.round(value * 100) / 100;
}

/** The four spellings `ProductListQuery.ParseBoolean` accepts. Anything else is a typo worth naming. */
function readBoolean(params, name, errors) {
  const raw = params.get(name);
  if (raw === null || raw.trim() === '') {
    return null;
  }

  const value = raw.trim().toLowerCase();
  if (value === 'true' || value === '1') {
    return true;
  }
  if (value === 'false' || value === '0') {
    return false;
  }

  errors.push({ field: name, message: `${name} must be true or false (1 and 0 are also accepted).` });
  return null;
}

/**
 * `ProductListQuery.Create`: every problem collected, so one 400 answers the whole request rather
 * than only the first mistake.
 */
function readListQuery(params) {
  const errors = [];

  const query = {
    department: readSlug(params.get('department'), 'department', errors),
    category: readSlug(params.get('category'), 'category', errors),
    ageGroups: readSlugList(params, 'ageGroup', errors),
    colours: readSlugList(params, 'colour', errors),
    materials: readSlugList(params, 'material', errors),
    fits: readSlugList(params, 'fit', errors),
    occasions: readSlugList(params, 'occasion', errors),
    sizes: readLabelList(params, 'size', errors),
    // `NormaliseMoney` rounds to two decimals away from zero; a negative price is rejected above, so
    // half-up is the same thing here. Rating and discount are deliberately **not** rounded, because the
    // real one does not round them either.
    minPrice: roundMoney(readNumber(params, 'minPrice', 0, MAX_PRICE_VALUE, errors)),
    maxPrice: roundMoney(readNumber(params, 'maxPrice', 0, MAX_PRICE_VALUE, errors)),
    minRating: readNumber(params, 'minRating', 0, 5, errors),
    minDiscountPercent: readNumber(params, 'minDiscountPercent', 0, 100, errors),
    inStock: readBoolean(params, 'inStock', errors) ?? false,
    onSale: readBoolean(params, 'onSale', errors) ?? false,
    newArrivals: readBoolean(params, 'newArrivals', errors) ?? false,
    sort: params.get('sort'),
    page: readWholeNumber(params, 'page', 1, MAX_PAGE, errors, 1),
    pageSize: readWholeNumber(params, 'pageSize', 1, MAX_PAGE_SIZE, errors, DEFAULT_PAGE_SIZE),
  };

  if (query.minPrice !== null && query.maxPrice !== null && query.minPrice > query.maxPrice) {
    errors.push({ field: 'minPrice', message: 'minPrice must not be greater than maxPrice.' });
  }

  if (query.sort === null || query.sort.trim() === '') {
    query.sort = 'recommended';
  } else if (!SORTS.includes(query.sort.trim().toLowerCase())) {
    errors.push({ field: 'sort', message: `sort must be one of: ${SORTS.join(', ')}.` });
  } else {
    query.sort = query.sort.trim().toLowerCase();
  }

  // Asking for a discount is asking for the sale, which is how the API reads it too.
  if ((query.minDiscountPercent ?? 0) > 0) {
    query.onSale = true;
  }

  return { query, errors };
}

/**
 * `ProductCatalogReader.ResolveFiltersAsync`, for the two slugs that need the catalogue to resolve
 * them.
 *
 * It reads the **configured** tree rather than whatever the `/__test__/departments-fault` switch is
 * currently doing to the feed: those faults are about the feed being unreachable, not about the
 * catalogue having changed, and a search that started answering "no such department" during a feed
 * outage would be modelling something the real API does not do.
 *
 * Three outcomes, as the real one has: a rejection, "matches nothing", or resolved ids.
 */
function resolveFilters(query) {
  const departments = Array.isArray(DEPARTMENTS_BODY) ? DEPARTMENTS_BODY : [];

  let department = null;
  if (query.department !== null) {
    department = departments.find((candidate) => candidate.slug === query.department) ?? null;
    if (department === null) {
      return { matchesNothing: true };
    }
  }

  if (query.category === null) {
    return { department, category: null };
  }

  const matches = [];
  for (const candidate of department ? [department] : departments) {
    walkCategories(candidate.categories ?? [], (node) => {
      if (node.slug === query.category) {
        matches.push({ node, departmentSlug: candidate.slug });
      }
    });
  }

  if (matches.length === 0) {
    return { matchesNothing: true };
  }

  // Doc 08 §3.2 makes a category slug unique per (department, parent), not globally, so "jeans"
  // names one category in each department. Serving whichever sorts first would quietly hide the
  // rest, so an ambiguous slug is refused however it came about.
  if (matches.length > 1) {
    const departmentSlugs = [...new Set(matches.map((match) => match.departmentSlug))];
    return {
      rejection: {
        field: 'category',
        message:
          departmentSlugs.length > 1
            ? `The category '${query.category}' exists in more than one department. ` +
              `Add ${describeChoices(departmentSlugs)}.`
            : `The category '${query.category}' names ${matches.length} active categories in ` +
              `department=${departmentSlugs[0]}, so naming the department does not tell them apart. ` +
              'The duplicate slugs have to be corrected in the catalogue.',
      },
    };
  }

  return { department: department ?? { slug: matches[0].departmentSlug }, category: matches[0].node };
}

/**
 * `ProductCatalogReader.DescribeChoices`: "department=a", then "department=a or department=b", then
 * "department=a, department=b or department=c" from three upwards.
 *
 * Unreachable today — `jeans` is the only slug shared between departments and it is shared by two — but
 * the version this replaces was `join(' or department=')`, which would have produced "a or b or c" for
 * three and been wrong without anything going red.
 */
function describeChoices(departmentSlugs) {
  const choices = departmentSlugs.map((slug) => `department=${slug}`);

  return choices.length === 1
    ? choices[0]
    : `${choices.slice(0, -1).join(', ')} or ${choices[choices.length - 1]}`;
}

/** Walks a category tree, visiting every node at every depth. */
function walkCategories(categories, visit) {
  for (const category of categories) {
    visit(category);
    walkCategories(category.children ?? [], visit);
  }
}

/** A name as a slug, for comparing a card's attributes against a slug filter. */
function slugify(value) {
  return value.toLowerCase().replace(/[^a-z0-9]+/gu, '-').replace(/^-|-$/gu, '');
}

/**
 * Whether one corpus entry survives the filters.
 *
 * **Size is a real predicate** over real labels (see {@link attributesFor}). The first version of this
 * answered nothing for every size and justified it as "the same as the real API's answer for a value it
 * cannot resolve" — which is true of an *unresolvable* label and false of `size=M`, which the real
 * catalogue resolves and which 21 seeded products carry. The search page's panel offers size
 * checkboxes, so that stub showed no results where production shows products.
 *
 * **Age group still excludes everything, and that one is genuine parity rather than a gap.** Every
 * product in this corpus is in the Women department, and a Women product carries `AgeGroupSlug = null`
 * in the real schema — only the Kids tree uses the four seeded groups. So an age-group filter matches
 * none of them in production either.
 */
function matchesFilters(entry, query, resolved) {
  const { card } = entry;

  if (resolved.department && card.departmentSlug !== resolved.department.slug) {
    return false;
  }
  if (resolved.category && card.categorySlug !== resolved.category.slug) {
    return false;
  }
  if (query.colours.length > 0 && !card.colours.some((colour) => query.colours.includes(colour.familySlug))) {
    return false;
  }
  if (query.materials.length > 0 && !query.materials.includes(slugify(entry.attributes.material))) {
    return false;
  }
  if (query.fits.length > 0 && !query.fits.includes(slugify(entry.attributes.fit))) {
    return false;
  }
  if (query.occasions.length > 0 && !query.occasions.includes(slugify(entry.attributes.occasion))) {
    return false;
  }
  if (
    query.sizes.length > 0 &&
    !query.sizes.some((label) =>
      entry.attributes.sizes.some((size) => size.toLowerCase() === label.toLowerCase()),
    )
  ) {
    return false;
  }
  // See the docblock: a Women product carries no age group in the real schema either.
  if (query.ageGroups.length > 0) {
    return false;
  }
  if (query.minPrice !== null && card.price.current < query.minPrice) {
    return false;
  }
  if (query.maxPrice !== null && card.price.current > query.maxPrice) {
    return false;
  }
  if (query.minRating !== null && (card.rating.average ?? 0) < query.minRating) {
    return false;
  }
  if (query.inStock && !card.isInStock) {
    return false;
  }
  if (query.onSale && !card.price.hasDiscount) {
    return false;
  }
  if (query.minDiscountPercent !== null && card.price.discountPercent < query.minDiscountPercent) {
    return false;
  }
  if (query.newArrivals && card.badge !== 'New') {
    return false;
  }

  return true;
}

/** The relevance bands of doc 08 §7.6, collapsed to an order: name prefix, name, anything else. */
function relevance(card, token) {
  const name = card.name.toLowerCase();
  if (name.startsWith(token)) {
    return 0;
  }

  return name.includes(token) ? 1 : 2;
}

/**
 * The sorts of PLP-04.
 *
 * "Best Selling" has no sales data here and falls back to the catalogue order, which is stated
 * rather than hidden: a test that wanted to prove best-seller ordering would have to prove it
 * against the real API, and `ProductDetailAndSearchTests` is where that belongs.
 */
function applySort(matched, sort, firstToken) {
  switch (sort) {
    case 'price-asc':
      return matched.sort((left, right) => left.price.current - right.price.current);
    case 'price-desc':
      return matched.sort((left, right) => right.price.current - left.price.current);
    case 'newest':
      return matched.sort((left, right) => right.id - left.id);
    case 'rating':
      return matched.sort((left, right) => (right.rating.average ?? 0) - (left.rating.average ?? 0));
    case 'discount':
      return matched.sort((left, right) => right.price.discountPercent - left.price.discountPercent);
    case 'best-selling':
      return matched;
    default:
      return matched.sort((left, right) => relevance(left, firstToken) - relevance(right, firstToken));
  }
}

function problem(errors) {
  const grouped = {};
  for (const { field, message } of errors) {
    grouped[field] ??= [];
    if (!grouped[field].includes(message)) {
      grouped[field].push(message);
    }
  }

  return {
    status: 400,
    body: { title: 'One or more query parameters are invalid.', status: 400, errors: grouped },
  };
}

function searchFor(params) {
  const term = (params.get('q') ?? '').trim();
  const { query, errors } = readListQuery(params);

  if (term.length < MIN_TERM_LENGTH) {
    errors.push({ field: 'q', message: `q must be at least ${MIN_TERM_LENGTH} characters.` });
  } else if (term.length > MAX_TERM_LENGTH) {
    errors.push({ field: 'q', message: `q must be ${MAX_TERM_LENGTH} characters or fewer.` });
  }

  const tokens = tokenise(term);
  if (!errors.some((error) => error.field === 'q') && tokens.length === 0) {
    errors.push({
      field: 'q',
      message: `q must contain a word of at least ${MIN_TERM_LENGTH} characters.`,
    });
  }

  if (errors.length > 0) {
    return problem(errors);
  }

  const resolved = resolveFilters(query);
  if (resolved.rejection) {
    return problem([resolved.rejection]);
  }

  const empty = {
    status: 200,
    body: {
      term,
      tokens,
      results: { items: [], page: query.page, pageSize: query.pageSize, totalCount: 0 },
    },
  };

  if (resolved.matchesNothing) {
    return empty;
  }

  const matched = SEARCH_CORPUS.filter(
    (entry) => tokens.every((token) => entry.text.includes(token)) && matchesFilters(entry, query, resolved),
  ).map((entry) => entry.card);

  const ordered = applySort(matched, query.sort, tokens[0]);
  const skip = (query.page - 1) * query.pageSize;

  return {
    status: 200,
    body: {
      term,
      tokens,
      results: {
        items: ordered.slice(skip, skip + query.pageSize),
        page: query.page,
        pageSize: query.pageSize,
        totalCount: ordered.length,
      },
    },
  };
}

/**
 * A 1x1 transparent GIF, used as every gallery image and every video poster.
 *
 * There is no media server behind these tests (W1-04 has not landed), and a broken image is a
 * Definition-of-Done failure, so the stub serves a picture the browser can really decode rather
 * than a URL that 404s. The real API can legitimately produce an absolute URL here too:
 * `MediaUrlBuilder.ToPublicUrl` returns a storage key unchanged when it is already an absolute
 * URL, which is how a remote placeholder reaches the storefront.
 */
const PIXEL =
  'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

/**
 * One gallery slide, in the API's shape: `url` is the large rendition, with the thumb the rail
 * draws and the zoom the full-screen view magnifies named separately, because the three are
 * wanted at 56 px, ~600 px and 3x and one file cannot be right for all of them.
 */
function galleryImage(id, overrides) {
  const asset = publicId(id);

  return {
    id,
    url: rendition(asset, 'large'),
    srcSet: gallerySrcSet(asset),
    thumbnailUrl: rendition(asset, 'thumb'),
    zoomUrl: rendition(asset, 'zoom'),
    altText: null,
    role: 'Gallery',
    colourId: null,
    width: 1200,
    height: 1600,
    posterUrl: null,
    sortOrder: 1,
    ...overrides,
  };
}

/** The sizes every demo product is made in, in the API's shape. */
const SIZES = [
  { id: 10, label: 'S', scaleName: 'Apparel alpha', sortOrder: 1 },
  { id: 11, label: 'M', scaleName: 'Apparel alpha', sortOrder: 2 },
  { id: 12, label: 'L', scaleName: 'Apparel alpha', sortOrder: 3 },
];

/**
 * The seeded women's guide (PDP-04), as `SizeGuideContentDto`.
 *
 * Four columns and six rows, copied from `CatalogReferenceData` cell for cell rather than
 * simplified, because the only genuinely hard part of this dialog is what a table this wide does on
 * a 320 px screen. A two-column stand-in would have drawn a table that fits and proved nothing.
 *
 * It is kept faithful on purpose, so the markup-in-a-cell case (cells are interpolated as text and
 * never bound to `innerHTML`) is proven in `size-guide-modal.spec.ts` instead of by bending one of
 * these measurements into a tag.
 */
const WOMENS_GUIDE = {
  introHtml: '<p>Measurements are in centimetres and describe the body, not the garment.</p>',
  table: {
    columns: ['Size', 'Bust', 'Waist', 'Hip'],
    rows: [
      ['XS', '78-82', '60-64', '86-90'],
      ['S', '83-87', '65-69', '91-95'],
      ['M', '88-93', '70-75', '96-101'],
      ['L', '94-99', '76-81', '102-107'],
      ['XL', '100-106', '82-88', '108-114'],
      ['XXL', '107-113', '89-95', '115-121'],
    ],
  },
  measuringTipsHtml:
    '<p>Measure over light clothing, keeping the tape level and snug but not tight.</p>',
  imageUrl: null,
  imageAltText: null,
};

/**
 * A deliberately wide guide, for the one thing the seeded tables cannot test reliably.
 *
 * The real women's table is 246 px wide, which *fits* a 320 px dialog with about 8 px to spare — so
 * "the container must be scrollable at 320 px" is an assertion riding a margin that moves with font
 * loading and platform metrics. Measured in a browser: it overflows under one set of metrics and
 * fits under another, which is a flake waiting to be blamed on something else.
 *
 * Seven columns cannot fit any phone under any font, so the scroller is exercised by content that
 * is certain rather than by a breakpoint that happens to be tight. A real guide could be this wide:
 * ADM-11 lets an admin add columns.
 */
const WIDE_GUIDE = {
  introHtml: '<p>Seven columns, which no phone can fit.</p>',
  table: {
    columns: ['Size', 'Bust', 'Waist', 'Hip', 'Inseam', 'Sleeve', 'Shoulder'],
    rows: [
      ['XS', '78-82', '60-64', '86-90', '74', '58', '36'],
      ['S', '83-87', '65-69', '91-95', '75', '59', '37'],
      ['M', '88-93', '70-75', '96-101', '76', '60', '38'],
    ],
  },
  measuringTipsHtml: null,
  imageUrl: null,
  imageAltText: null,
};

/** Roughly what `NewsletterEmail.TryNormalise` accepts: enough to tell the two branches apart. */
const EMAIL_PATTERN = /^[^\s@]+@[^\s@.]+\.[^\s@.]{2,}$/;

/** `CatalogSlug`: lower-case letters and digits, separated by single hyphens. */
const SLUG_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * How many units of one colour x size there are. Deliberately uneven, the way a real catalogue
 * is: the second colourway is never made in L, the first colourway's M is always sold out, and
 * every eleventh product has nothing left at all. A grid where everything exists and everything
 * is in stock would agree with any implementation.
 */
function availableFor(index, colourIndex, sizeIndex) {
  if (colourIndex === 1 && sizeIndex === 2) {
    return null; // not made in this size at all
  }

  if (index % 11 === 0 || (colourIndex === 0 && sizeIndex === 1)) {
    return 0; // made, sold out
  }

  return 2 + colourIndex + sizeIndex;
}

/**
 * The product page payload for one product index, mirroring
 * `SpencerDenim.Application.Catalog.Contracts.ProductDetailDto` field for field.
 *
 * The media follow the demo seeder: one gallery shot per colourway carrying its `colourId`, then
 * one shared detail shot with no colour. Every fifth product also has a video and every seventh
 * has no media at all, so a test reaches all three gallery shapes by asking for a slug rather
 * than by telling the stub what to say.
 */
function productDetail(index, slug) {
  const card = product(index);
  // The wholly sold-out product wears the whole seeded palette, so the sold-out mark is checked
  // against every colour it has to show up on rather than against one.
  const colours = index % 11 === 0 ? SEEDED_PALETTE : card.colours;
  const base = card.price.current;

  const images =
    index % 7 === 0
      ? []
      : [
          ...colours.map((colour, position) => galleryImage(index * 100 + position, {
            altText: `${card.name} in ${colour.name}`,
            role: 'Gallery',
            colourId: colour.id,
            sortOrder: position + 1,
          })),
          galleryImage(index * 100 + 90, {
            altText: `${card.name}, fabric detail`,
            role: 'Hover',
            colourId: null,
            sortOrder: colours.length + 1,
          }),
        ];

  if (index % 5 === 0 && images.length > 0) {
    // A video has no renditions — the pipeline only resizes images — so it is the one slide that
    // still carries an original, and the null srcset the gallery has to fall back from.
    images.push({
      id: index * 100 + 95,
      url: `${PIXEL}#video`,
      srcSet: null,
      thumbnailUrl: null,
      zoomUrl: null,
      altText: null,
      role: 'Video',
      colourId: null,
      width: 1920,
      height: 1080,
      posterUrl: PIXEL,
      sortOrder: colours.length + 2,
    });
  }

  const variants = [];
  const sizes = [];

  SIZES.forEach((size, sizeIndex) => {
    let anyStock = false;

    colours.forEach((colour, colourIndex) => {
      const available = availableFor(index, colourIndex, sizeIndex);
      if (available === null) {
        return;
      }

      anyStock = anyStock || available > 0;
      // The last size costs more, which is what ProductVariants.PriceOverride does in the demo
      // catalogue, and what makes "from Rs. X" mean something.
      const current = sizeIndex === 2 ? base + 500 : base;
      // Follows the card's own rule rather than always being on: the real API sends
      // hasDiscount: false for two products in three, and a stub that discounts everything never
      // lets the no-discount branch reach a browser.
      const discounted = card.price.hasDiscount;

      variants.push({
        id: variants.length + 1,
        sku: `${card.productCode}-${colour.id}-${size.id}`,
        colourId: colour.id,
        colourName: colour.name,
        sizeId: size.id,
        sizeLabel: size.label,
        scaleName: size.scaleName,
        price: {
          current,
          original: discounted ? Math.round(current * 1.4) : current,
          discountPercent: discounted ? 28 : 0,
          hasDiscount: discounted,
          currency: 'LKR',
        },
        stockStatus: available === 0 ? 'OutOfStock' : available <= 3 ? 'LowStock' : 'InStock',
        isInStock: available > 0,
        availableQuantity: available,
      });
    });

    sizes.push({ ...size, isInStock: anyStock });
  });

  const prices = variants.map((variant) => variant.price.current);
  const discounted = card.price.hasDiscount;
  const inStock = variants.reduce((total, variant) => total + variant.availableQuantity, 0);
  const lowStock = inStock > 0 && variants.every((variant) => variant.availableQuantity <= 3);

  return {
    id: index,
    productCode: card.productCode,
    name: card.name,
    slug,
    department: { id: 1, name: 'Women', slug: 'women' },
    breadcrumb: [{ id: 12, name: 'Jeans', slug: 'jeans' }],
    shortDescription: `${card.name}, cut for Sri Lankan weather.`,
    // Sanitised HTML, as `catalog.Products.Description` is (doc 08 §3.2) and as the demo seeder
    // writes it. A plain sentence here would have hidden the page printing the tags at a shopper.
    description: `<p>${card.name}: rigid cotton denim with a touch of stretch.</p><p>Cut high on the waist and straight through the leg.</p>`,
    careInstructions: 'Machine wash cold, inside out. Line dry.',
    attributes: {
      material: 'Denim',
      materialSlug: 'denim',
      fit: 'Straight',
      fitSlug: 'straight',
      occasion: null,
      occasionSlug: null,
      ageGroup: null,
      ageGroupSlug: null,
    },
    price: {
      current: Math.min(...prices),
      original: discounted ? Math.round(Math.max(...prices) * 1.4) : Math.max(...prices),
      discountPercent: discounted ? 28 : 0,
      hasDiscount: discounted,
      currency: 'LKR',
    },
    minPrice: Math.min(...prices),
    maxPrice: Math.max(...prices),
    rating: card.rating,
    badge: card.badge,
    // Derived from the variants, the way the API derives it, rather than asserted: a payload
    // that says InStock over a grid of zeroes is a payload the API cannot produce.
    stockStatus: inStock === 0 ? 'OutOfStock' : lowStock ? 'LowStock' : 'InStock',
    isInStock: inStock > 0,
    seo: { title: card.name, description: `${card.name} at Colombo Wardrobe.` },
    // The real API sends null for 6 of its 45 products — a category with no guide set — and the
    // seeded women's guide, with its measurements, for the rest.
    //
    // The copy is the real seeded guide's, cell for cell, because the whole hard part of PDP-04 is
    // what four columns and six rows do at 320 px. A stub with two columns would have drawn a
    // table that fits and proved nothing.
    sizeGuide:
      index % 4 === 0
        ? null
        : index % 4 === 2
          ? { id: 2, name: 'Wide', content: WIDE_GUIDE }
          : { id: 1, name: 'Women apparel', content: WOMENS_GUIDE },
    newUntilUtc: null,
    images,
    colours,
    sizes,
    variants,
    relatedProducts: [product(index + 1), product(index + 2)],
  };
}

/**
 * Answers `GET /api/catalog/products/{slug}` the way the API does: a slug that is not a slug is a
 * 400 naming `slug`, a well-formed slug nothing is published under is a 404, and anything else is
 * the payload. `E2E_PRODUCT_STATUS` forces a status, for the fault branches.
 */
function productBySlug(slug) {
  const forced = Number(process.env.E2E_PRODUCT_STATUS ?? 0);
  if (forced >= 400) {
    return { status: forced, body: { title: 'Forced failure', status: forced } };
  }

  if (!slug || slug.length > 220 || !SLUG_PATTERN.test(slug)) {
    return {
      status: 400,
      body: {
        title: 'One or more query parameters are invalid.',
        status: 400,
        errors: {
          slug: ['slug must be lower-case letters and digits separated by single hyphens.'],
        },
      },
    };
  }

  const match = /^test-product-(\d+)$/.exec(slug);
  const index = match ? Number(match[1]) : 0;

  if (!match || index < 1 || index > TOTAL_PRODUCTS) {
    return {
      status: 404,
      body: {
        title: 'Product not found',
        status: 404,
        detail: `No published product has the slug '${slug}'.`,
      },
    };
  }

  return { status: 200, body: productDetail(index, slug) };
}


// ---------------------------------------------------------------- the storefront content API

/**
 * The homepage as GET /api/content/home sends it (HOME-01..10).
 *
 * It is deliberately **richer than the seeded database**, not tidier than it. The seeder switches
 * the four artwork sections off because there is no media library yet (W1-04), so a stub that
 * mirrored the seed would leave the hero, both banners and the gallery untested in a browser —
 * and those are exactly the blocks whose reduced-motion, focus and overflow behaviour only a real
 * browser can show. So every section is here, with the artwork the client will eventually upload,
 * plus the two states the page has to survive: a section that is on with nothing in it, and a
 * tile whose stored link must never reach an href.
 */
function homePage() {
  return process.env.E2E_CONTENT === 'empty' ? { sections: [] } : { sections: SECTIONS };
}

function contentItem(id, overrides) {
  return {
    id,
    title: null,
    subtitle: null,
    body: null,
    buttonLabel: null,
    linkUrl: null,
    imageUrl: null,
    imageSrcSet: null,
    imageAltText: null,
    mobileImageUrl: null,
    mobileImageSrcSet: null,
    mobileImageAltText: null,
    videoUrl: null,
    videoPosterUrl: null,
    iconKey: null,
    departmentSlug: null,
    departmentName: null,
    productSlug: null,
    sortOrder: 1,
    ...overrides,
  };
}

function contentSection(id, type, overrides) {
  return {
    id,
    type,
    title: null,
    subtitle: null,
    sortOrder: id,
    linkUrl: null,
    linkLabel: null,
    overlay: null,
    items: [],
    products: [],
    ...overrides,
  };
}

/**
 * A hero video the browser can really decode, served from this stub at {@link VIDEO}.
 *
 * It used to be a `data:` URI carrying an MP4 header and nothing else, which was enough to prove a
 * `<video>` element existed and nothing more — so the test could not tell autoplay working from
 * autoplay refused, and it passed while the hero was a frozen poster (the `muted` property was
 * never set on the element Angular creates). `support/hero-sample.webm` is two and a half seconds
 * of VP8 recorded with this same Chromium, so "it plays" is now a thing a test can watch happen —
 * a dot crosses the bottom of the frame for that reason alone.
 *
 * **It is 360 x 640, like the file the client delivered, and it carries a marker band.** The hero
 * frame is 16:9 from 768 px, so a portrait source overflows vertically and `object-fit: cover`
 * throws most of its height away — which is why HOME-01 was held off the front page in W1-13 and
 * why `.hero__video` carries an `object-position` now (W1-14). A magenta band fills exactly
 * 25%–42% of this file's height, which is where the model's face sits in the real one, so a test
 * can photograph the hero and say whether the part that must stay in frame is on the screen. A
 * video whose content did not matter could not answer that question at all.
 *
 * It has no audio track, which is the honest limit of what this can show: Playwright's Chromium
 * runs with the autoplay policy disabled, so an unmuted video plays here whether or not it should.
 * What the tests measure is the `muted` property itself — which really was `false` before — and
 * that the element is running and the control agrees with it.
 *
 * The URL sits under {@link MEDIA_BASE} and so is root-relative for the same reason the
 * renditions are, and reaches this stub by the same `/media` passthrough — a video is a media
 * asset in the real API too, not a thing of its own. What it must keep clear of is the tests'
 * own `page.route` handler: a looping video held that handler open while everything else on the
 * page queued behind it, which made an unrelated newsletter assertion time out about one run in
 * three. Those handlers match paths under `/api/` only, so `/media/...` stays outside them.
 */
const VIDEO = `${MEDIA_BASE}/hero-sample.webm`;

const HERO_VIDEO_FILE = new URL('./hero-sample.webm', import.meta.url);

const SECTIONS = [
  contentSection(1, 'HeroVideo', {
    overlay: 'dark',
    items: [
      contentItem(11, {
        title: 'New season linen',
        subtitle: 'Cut for the weather here',
        buttonLabel: 'Shop new arrivals',
        linkUrl: '/new-arrivals',
        imageUrl: PIXEL,
        imageAltText: 'A linen shirt on a balcony',
        mobileImageUrl: PIXEL,
        mobileImageAltText: 'A linen shirt on a balcony',
        videoUrl: VIDEO,
        videoPosterUrl: PIXEL,
      }),
    ],
  }),
  contentSection(2, 'ShopByDepartment', {
    title: 'Shop by department',
    items: [
      contentItem(21, { departmentSlug: 'women', departmentName: 'Women', linkUrl: '/women', buttonLabel: 'Shop Women', imageUrl: PIXEL, sortOrder: 1 }),
      contentItem(22, { departmentSlug: 'men', departmentName: 'Men', linkUrl: '/men', buttonLabel: 'Shop Men', imageUrl: PIXEL, sortOrder: 2 }),
      // No picture, which is how the seeder writes all three today.
      contentItem(23, { departmentSlug: 'kids', departmentName: 'Kids', linkUrl: '/kids', buttonLabel: 'Shop Kids', sortOrder: 3 }),
    ],
  }),
  contentSection(3, 'NewArrivals', {
    title: 'New arrivals',
    linkUrl: '/new-arrivals',
    linkLabel: 'View all new arrivals',
    products: Array.from({ length: 8 }, (_, index) => product(index + 1)),
  }),
  contentSection(4, 'TrendingItems', {
    title: 'Trending now',
    items: [
      contentItem(41, { title: 'Wide leg denim', subtitle: 'Women', linkUrl: '/women/jeans?fit=wide-leg', imageUrl: PIXEL, sortOrder: 1 }),
      contentItem(42, { title: 'Linen shirts', subtitle: 'Men', linkUrl: '/men/shirts?material=linen', imageUrl: PIXEL, sortOrder: 2 }),
      contentItem(43, { title: 'Half price or better', linkUrl: '/sale?minDiscountPercent=50', imageUrl: PIXEL, sortOrder: 3 }),
      // A tile the admin screens will eventually refuse. It must not reach an href, and it must
      // not keep a section alive on its own.
      contentItem(44, { title: 'Unsafe', linkUrl: 'javascript:alert(1)', imageUrl: PIXEL, sortOrder: 4 }),
    ],
  }),
  contentSection(5, 'CampaignBanner', {
    overlay: 'dark',
    items: [
      contentItem(51, {
        title: 'Mid-season sale',
        subtitle: 'Up to 50% off selected styles',
        buttonLabel: 'Shop the sale',
        linkUrl: '/sale',
        imageUrl: PIXEL,
        mobileImageUrl: PIXEL,
      }),
    ],
  }),
  // HOME-06 carries the copy and the destination the importer really writes (W1-14). It used to
  // carry the old activewear wording over a linen flat-lay pointing at `/women/activewear`, which
  // meant the one suite that renders this block in a browser rendered precisely the picture /
  // words / destination contradiction issue #63 exists about. The link is deliberately the real
  // one, repeated parameters and all, so `toLink` → `routerLink` → the listing is exercised rather
  // than assumed.
  contentSection(6, 'ActivewearFeature', {
    title: 'Linen and denim',
    overlay: 'dark',
    items: [
      contentItem(61, {
        title: 'Linen and denim',
        subtitle: 'Breathable linen for the heat, washed indigo for everything else.',
        buttonLabel: 'Shop linen and denim',
        linkUrl: '/women?material=linen&material=linen-blend&material=denim',
        imageUrl: PIXEL,
      }),
    ],
  }),
  contentSection(7, 'SaleSection', {
    title: 'Sale',
    linkUrl: '/sale',
    linkLabel: 'Shop all sale',
    products: Array.from({ length: 4 }, (_, index) => product(index + 3)),
  }),
  contentSection(8, 'SocialGallery', {
    title: 'Follow us',
    items: [
      contentItem(81, { imageUrl: PIXEL, imageAltText: 'A denim jacket', linkUrl: 'https://instagram.com/example', title: 'Colombo Wardrobe on Instagram', sortOrder: 1 }),
      contentItem(82, { imageUrl: PIXEL, imageAltText: 'A summer dress', sortOrder: 2 }),
      contentItem(83, { imageUrl: PIXEL, imageAltText: 'A pair of jeans', linkUrl: 'https://facebook.com/example', title: 'Colombo Wardrobe on Facebook', sortOrder: 3 }),
    ],
  }),
  contentSection(9, 'Benefits', {
    items: [
      contentItem(91, { iconKey: 'delivery', title: 'Island-wide delivery', body: 'We deliver across Sri Lanka. The charge for your district is shown at checkout.', sortOrder: 1 }),
      contentItem(92, { iconKey: 'returns', title: 'Returns and exchanges', body: 'Changed your mind, or need another size? Get in touch and we will sort it out.', sortOrder: 2 }),
      contentItem(93, { iconKey: 'quality', title: 'Made to last', body: 'Denim and everyday pieces in fabrics chosen for the weather here.', sortOrder: 3 }),
    ],
  }),
  contentSection(10, 'Newsletter', {
    title: 'Newsletter',
    subtitle: 'New arrivals and sale news, straight to your inbox.',
  }),
  // On, and empty. The API sends it because the client switched it on; the page must draw
  // absolutely nothing for it — no heading, no frame, no bordered gap.
  contentSection(11, 'CampaignBanner', { title: 'Nothing to see', sortOrder: 11 }),
];

/** The announcement strip (GLB-03), with one line that carries a link. */
/**
 * The third line is deliberately **not** one of `PLACEHOLDER_ANNOUNCEMENTS`. A test that asserted
 * the free-delivery line passed identically whether the content feed worked or fell back to its
 * constants, because that sentence is in both — which is no test at all.
 */
const ANNOUNCEMENTS = [
  // Deliberately not the fallback's wording, so a test can tell a live strip from a fallback one,
  // and deliberately promising nothing — see #34.
  { id: 1, message: 'Stitched in Sri Lanka', linkUrl: null },
  { id: 2, message: 'Shop the season sale', linkUrl: '/sale' },
  { id: 3, message: 'Stitched in Sri Lanka since 2011', linkUrl: null },
];

/** A slow answer, so a test can prove the menu does not hold the page open waiting for it. */
const DELAY_MS = Number(process.env.E2E_API_DELAY_MS ?? 0);

/**
 * What `/departments` answers instead of the tree, for the shapes that are a successful response
 * carrying a useless body: `empty` is a freshly migrated database before anyone runs the seeder,
 * `garbage` is a proxy or a bad deploy answering 200 with something else entirely.
 */
const DEPARTMENTS_MODE = process.env.E2E_DEPARTMENTS ?? 'tree';

/** The tree this stub serves when nothing is wrong with it. */
const DEPARTMENTS_BODY =
  DEPARTMENTS_MODE === 'empty'
    ? []
    : DEPARTMENTS_MODE === 'garbage'
      ? { oops: true }
      : DEPARTMENTS_MODE === 'live-only'
        ? LIVE_ONLY_DEPARTMENTS
        : DEPARTMENTS;

/**
 * How the departments feed is unhealthy *right now* — switched by a test through
 * `/__test__/departments-fault?mode=...` rather than fixed by an environment variable.
 *
 * #30 lists four unhealthy modes and every one of them has to be measured on the wire, which
 * means a server-side render, which means an SSR server. One switchable stub is one SSR server
 * for all four; an environment variable would have been four more of each, and this suite already
 * starts five pairs.
 *
 * - `none`     — healthy.
 * - `status`   — 500 as RFC 9457, which is what the API's own problem middleware sends.
 * - `destroy`  — the connection goes away with nothing sent.
 * - `slow`     — an answer that arrives long after the storefront's own timeout.
 * - `empty`    — `200 []`, a migrated database nobody has seeded, or every department deactivated.
 * - `garbage`  — `200 {"oops":true}`, a proxy or a bad deploy answering with something else
 *                entirely. The same shape `E2E_DEPARTMENTS=garbage` fixes for a whole server, but
 *                until now it was only ever measured in a unit test and never on the wire.
 */
let departmentsFault = 'none';

/**
 * Long enough past `DEPARTMENTS_TIMEOUT_MS` (2 s) to be the same thing as never, and the figure
 * #30 was measured with.
 */
const SLOW_DEPARTMENTS_MS = 12_000;

const server = createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://localhost:${PORT}`);

  // The browser talks to this stub across an origin; in production the reverse proxy puts the API
  // behind the storefront's own origin and no CORS header is involved.
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Content-Type', 'application/json; charset=utf-8');
  response.setHeader('Cache-Control', 'no-store');

  // Test-only, and deliberately named so: it is a switch on a fixture, not an API the storefront
  // knows about. Nothing in `src/` ever asks for this path.
  //
  // It is also what `playwright.config.ts` waits on to decide this stub is up, which is not an
  // accident: `reuseExistingServer` means a run that ended mid-fault would otherwise hand the next
  // run a stub that is still broken — and a readiness probe on `/departments` could not even tell,
  // because under `destroy` it never answers. Probing this resets the fault on the way in.
  if (url.pathname === '/__test__/departments-fault') {
    departmentsFault = url.searchParams.get('mode') ?? 'none';
    response.end(JSON.stringify({ fault: departmentsFault }));
    return;
  }

  if (url.pathname === '/api/catalog/departments') {
    if (departmentsFault === 'destroy') {
      // Nothing written first: the storefront sees a connection that went away, not a short body.
      request.socket.destroy();
      return;
    }

    if (departmentsFault === 'status') {
      response.statusCode = 500;
      // The media type the API really sends for a failure (`AddProblemDetails`), so the storefront
      // is parsing what it will parse in production rather than a convenient `application/json`.
      response.setHeader('Content-Type', 'application/problem+json; charset=utf-8');
      response.end(
        JSON.stringify({
          type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
          title: 'Forced failure',
          status: 500,
        }),
      );
      return;
    }

    const body =
      departmentsFault === 'empty'
        ? []
        : departmentsFault === 'garbage'
          ? { oops: true }
          : DEPARTMENTS_BODY;
    const delay = departmentsFault === 'slow' ? SLOW_DEPARTMENTS_MS : DELAY_MS;

    setTimeout(() => {
      // The storefront cancels at its own timeout, so by now the socket is usually gone. Writing
      // to it would raise an error nobody is listening for and take the stub down mid-run.
      if (!response.writableEnded && !response.destroyed) {
        response.end(JSON.stringify(body));
      }
    }, delay).unref();
    return;
  }

  // Stands in for `MediaFileEndpoints`: anything under the media base is a real file with a real
  // media type, so an `<img>` that asks for a rendition gets one instead of a 404 the card would
  // have to swap for its placeholder.
  if (url.pathname.startsWith('/media/public/')) {
    response.setHeader('Content-Type', 'image/webp');
    response.end(WEBP_PIXEL);
    return;
  }

  if (url.pathname === '/media/hero-sample.webm') {
    // Served with a real media type and range support off: the element only has to decode it.
    response.setHeader('Content-Type', 'video/webm');
    response.end(readFileSync(HERO_VIDEO_FILE));
    return;
  }

  if (url.pathname === '/api/content/home') {
    const forced = Number(process.env.E2E_CONTENT_STATUS ?? 0);
    if (forced >= 400) {
      response.statusCode = forced;
      response.end(JSON.stringify({ title: 'Forced failure', status: forced }));
      return;
    }

    response.end(JSON.stringify(homePage()));
    return;
  }

  if (url.pathname === '/api/content/announcements') {
    response.end(JSON.stringify(process.env.E2E_CONTENT === 'empty' ? [] : ANNOUNCEMENTS));
    return;
  }

  if (url.pathname === '/api/content/newsletter') {
    // Mirrors the endpoint: an address the API will not hold is an RFC 9457 problem naming the
    // field, and everything else is one sentence, whether or not the address was already listed.
    let body = '';
    request.on('data', (chunk) => (body += chunk));
    request.on('end', () => {
      let email = '';
      try {
        email = String(JSON.parse(body || '{}').email ?? '');
      } catch {
        email = '';
      }

      if (!EMAIL_PATTERN.test(email.trim())) {
        response.statusCode = 400;
        response.end(
          JSON.stringify({
            title: 'That email address could not be read.',
            status: 400,
            errors: { email: ['Enter an email address, for example name@example.com.'] },
          }),
        );
        return;
      }

      response.end(JSON.stringify({ message: 'Thank you. Your email address is on the list.' }));
    });
    return;
  }

  // Ahead of `/api/catalog/products`, because the prefix match below would otherwise never see it.
  if (url.pathname === '/api/catalog/search') {
    const answer = searchFor(url.searchParams);
    response.statusCode = answer.status;
    if (answer.status >= 400) {
      // The media type the API really sends for a rejected query string, so the storefront parses
      // what it will parse in production.
      response.setHeader('Content-Type', 'application/problem+json; charset=utf-8');
    }
    response.end(JSON.stringify(answer.body));
    return;
  }

  // The sitemap feed (GLB-11). It mirrors `/internal/catalog/sitemap`: slugs and timestamps only, with
  // the storefront turning them into addresses. **Not** under `/api`, because the real one is not:
  // doc 03 §4 routes `/api` from the public origin and this is the heaviest read in the system, so the
  // SSR process is the only thing that should be able to ask for it.
  //
  // It honours `E2E_PRODUCT_STATUS` rather than the switchable `departmentsFault`, and deliberately so.
  // The sitemap is a catalogue read, so "the catalogue is unhappy" is the fault that matters to it, and
  // the faulty pair already has that fault fixed for the whole server — which means the sitemap's
  // unhealthy behaviour can be measured in a parallel test rather than inside the serial file that owns
  // the fault switch.
  if (url.pathname === '/internal/catalog/sitemap') {
    const forced = Number(process.env.E2E_PRODUCT_STATUS ?? 0);
    if (forced >= 400) {
      response.statusCode = forced;
      response.end(JSON.stringify({ title: 'Forced failure', status: forced }));
      return;
    }

    response.end(JSON.stringify(sitemapPayload()));
    return;
  }

  if (url.pathname === '/api/catalog/products') {
    response.end(JSON.stringify(productsFor(url.searchParams)));
    return;
  }

  if (url.pathname.startsWith('/api/catalog/products/')) {
    // Decoded, because the storefront encodes the segment before it asks.
    const slug = decodeURIComponent(url.pathname.slice('/api/catalog/products/'.length));
    const answer = productBySlug(slug);

    response.statusCode = answer.status;
    response.end(JSON.stringify(answer.body));
    return;
  }

  response.statusCode = 404;
  response.end(JSON.stringify({ title: 'Not found', status: 404 }));
});

server.listen(PORT, () => process.stdout.write(`catalog stub on ${PORT}\n`));
