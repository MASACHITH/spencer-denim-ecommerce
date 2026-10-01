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
    seo: { title: card.name, description: `${card.name} at Spencer Denim.` },
    // The real API sends null for 6 of its 45 products — a category with no guide set. It also
    // sends no measurements with the ones it does fill in, which is issue #32 and why the
    // storefront shows no size-guide trigger at all today.
    sizeGuide: index % 4 === 0 ? null : { id: 1, name: 'Women apparel' },
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
  contentSection(6, 'ActivewearFeature', {
    title: 'Activewear',
    overlay: 'dark',
    items: [
      contentItem(61, {
        title: 'Move in it',
        body: 'Breathable pieces for the gym and the walk home.',
        buttonLabel: 'Shop activewear',
        linkUrl: '/women/dresses?occasion=active',
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
      contentItem(81, { imageUrl: PIXEL, imageAltText: 'A denim jacket', linkUrl: 'https://instagram.com/example', title: 'Spencer Denim on Instagram', sortOrder: 1 }),
      contentItem(82, { imageUrl: PIXEL, imageAltText: 'A summer dress', sortOrder: 2 }),
      contentItem(83, { imageUrl: PIXEL, imageAltText: 'A pair of jeans', linkUrl: 'https://facebook.com/example', title: 'Spencer Denim on Facebook', sortOrder: 3 }),
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

const server = createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://localhost:${PORT}`);

  // The browser talks to this stub across an origin; in production the reverse proxy puts the API
  // behind the storefront's own origin and no CORS header is involved.
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Content-Type', 'application/json; charset=utf-8');
  response.setHeader('Cache-Control', 'no-store');

  if (url.pathname === '/api/catalog/departments') {
    const body =
      DEPARTMENTS_MODE === 'empty' ? [] : DEPARTMENTS_MODE === 'garbage' ? { oops: true } : DEPARTMENTS;

    setTimeout(() => response.end(JSON.stringify(body)), DELAY_MS);
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
