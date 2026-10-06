/**
 * The `/api/admin/products` half of the admin stub (ADM-10, W1-03).
 *
 * It lives beside `admin-catalog-stub.mjs` rather than inside it because that file is already a thousand
 * lines about ADM-11, and because this part has its own rules worth reading on their own: the publish
 * checklist, the slug confirmation, and the one that matters most — **a `PUT` whose body carries no
 * `media` field leaves the photographs alone, while one carrying an empty array clears them.**
 *
 * **It is deliberately not more polite than the real API.** It answers the same field names
 * (`SpencerDenim.Application.Catalog.Admin.Products.Contracts`), the same statuses and the same RFC 9457
 * bodies with a `code` beside the sentence, and it enforces the refusals a screen can get wrong:
 *
 * - a missing or stale row version is a 409 `stale_row_version`, and a successful write **moves** the row
 *   version, so a screen that re-sent a cached token is caught;
 * - changing the slug of a product that has been published without `confirmSlugChange` is a 409
 *   `slug_change_needs_confirmation`, with both slugs;
 * - publishing a product that fails doc 11 §7 G-01's checklist is a 409 `publish_checklist_failed` with
 *   the blockers;
 * - dropping a variant that cannot be deleted is a 409 `variant_not_deletable`, with `orderedSkus` and
 *   `stockedSkus` listed separately.
 *
 * Without those, a screen that stopped sending the row version, or that conflated "say nothing about
 * media" with "clear the media", would pass every browser test.
 *
 * The endpoint behaviour itself is proved against a real SQL Server in `AdminProductEndpointTests`; what
 * these browser tests need is a predictable product at a predictable shape, in real CSS.
 */

/** `rowversion` is eight bytes; the shape matters because the screen round-trips it untouched. */
function rowVersion(seed) {
  return Buffer.from([0, 0, 0, 0, 0, 0, seed >> 8, seed & 0xff]).toString('base64');
}

let tick = 900;

function nextRowVersion() {
  tick += 1;
  return rowVersion(tick);
}

/** The reference lists the form chooses from (doc 11 P1 steps 6–12). */
export const FORM_OPTIONS = {
  departments: [
    { id: 1, name: 'Women' },
    { id: 2, name: 'Men' },
    { id: 3, name: 'Kids' },
  ],
  categories: [
    { id: 10, departmentId: 1, parentCategoryId: null, depth: 1, name: 'Dresses', displayName: 'Dresses', isActive: true },
    { id: 11, departmentId: 1, parentCategoryId: 10, depth: 2, name: 'Midi dresses', displayName: 'Dresses › Midi dresses', isActive: true },
    { id: 20, departmentId: 1, parentCategoryId: null, depth: 1, name: 'Jeans', displayName: 'Jeans', isActive: true },
    { id: 30, departmentId: 1, parentCategoryId: null, depth: 1, name: 'Traditional wear', displayName: 'Traditional wear', isActive: false },
    { id: 50, departmentId: 2, parentCategoryId: null, depth: 1, name: 'Shirts', displayName: 'Shirts', isActive: true },
    { id: 70, departmentId: 3, parentCategoryId: null, depth: 1, name: 'Girls', displayName: 'Girls', isActive: true },
  ],
  ageGroups: [
    { id: 1, name: 'Baby' },
    { id: 2, name: 'Toddler' },
    { id: 3, name: 'Kids' },
  ],
  materials: [
    { id: 41, name: 'Denim' },
    { id: 42, name: 'Linen' },
  ],
  fits: [{ id: 51, name: 'Slim' }],
  occasions: [{ id: 61, name: 'Everyday' }],
  colourFamilies: [
    { id: 71, name: 'Blue', hexCode: '#28415b' },
    { id: 72, name: 'Black', hexCode: '#111111' },
  ],
  sizes: [
    { id: 81, scaleName: 'Alpha', label: 'S', sortOrder: 1 },
    { id: 82, scaleName: 'Alpha', label: 'M', sortOrder: 2 },
    { id: 83, scaleName: 'Alpha', label: 'L', sortOrder: 3 },
    { id: 91, scaleName: 'Kids age', label: '4–5Y', sortOrder: 1 },
  ],
  sizeGuides: [{ id: 101, name: 'Women tops' }],
};

/**
 * The seeded products.
 *
 * **Both of them have real media attached**, and that is the fixture W1-12 did not have: every one of its
 * fixtures set the media columns to null, so a save that cleared them round-tripped null to null and
 * nothing noticed.
 */
export function freshProductState() {
  return {
    nextProductId: 400,
    products: [
      {
        id: 301,
        productCode: 'SD-W-0301',
        name: 'Wide-Leg Rinse Jeans',
        slug: 'wide-leg-rinse-jeans',
        departmentId: 1,
        primaryCategoryId: 20,
        extraCategoryIds: [10],
        ageGroupId: null,
        shortDescription: 'Everyday denim.',
        description: '<p>Everyday denim, cut for Sri Lankan weather.</p>',
        careInstructions: 'Machine wash cold.',
        materialId: 41,
        fitId: 51,
        occasionId: 61,
        basePrice: 5490,
        compareAtPrice: 6990,
        badgeOverride: null,
        newUntilUtc: null,
        recommendedRank: null,
        sizeGuideId: 101,
        seoTitle: 'Wide-Leg Rinse Jeans | Colombo Wardrobe',
        seoDescription: 'Everyday denim.',
        isPublished: true,
        firstPublishedAtUtc: '2026-09-20T08:00:00Z',
        cardImageUrl: '/__test__/one.webp',
        hoverImageUrl: '/__test__/two.webp',
        colours: [
          { id: 501, name: 'Indigo Rinse', colourFamilyId: 71, colourFamilyName: 'Blue', swatchHex: '#28415b', swatchAssetPublicId: null, swatchAssetUrl: null, sortOrder: 0 },
          { id: 502, name: 'Jet Black', colourFamilyId: 72, colourFamilyName: 'Black', swatchHex: '#111111', swatchAssetPublicId: null, swatchAssetUrl: null, sortOrder: 1 },
        ],
        variants: [
          { id: 601, colourName: 'Indigo Rinse', sizeId: 81, sizeLabel: 'S', sizeScaleName: 'Alpha', sku: 'SD-W-0301-IR-S', barcode: null, priceOverride: null, compareAtPriceOverride: null, isActive: true, sortOrder: 0, hasOrders: false, onHand: 6 },
          { id: 602, colourName: 'Indigo Rinse', sizeId: 82, sizeLabel: 'M', sizeScaleName: 'Alpha', sku: 'SD-W-0301-IR-M', barcode: null, priceOverride: null, compareAtPriceOverride: null, isActive: true, sortOrder: 1, hasOrders: true, onHand: 2 },
          { id: 603, colourName: 'Jet Black', sizeId: 81, sizeLabel: 'S', sizeScaleName: 'Alpha', sku: 'SD-W-0301-JB-S', barcode: null, priceOverride: null, compareAtPriceOverride: null, isActive: true, sortOrder: 2, hasOrders: false, onHand: 0 },
        ],
        media: [
          { id: 701, assetPublicId: '0000beef-0000-4000-8000-000000000001', mediaType: 1, url: '/__test__/one.webp', posterUrl: null, colourName: null, mediaRole: 1, sortOrder: 0, altTextOverride: 'Front view', assetAltText: 'A photograph' },
          { id: 702, assetPublicId: '0000beef-0000-4000-8000-000000000002', mediaType: 1, url: '/__test__/two.webp', posterUrl: null, colourName: 'Jet Black', mediaRole: 1, sortOrder: 1, altTextOverride: 'Jet Black, front', assetAltText: 'A photograph' },
          { id: 703, assetPublicId: '0000beef-0000-4000-8000-000000000003', mediaType: 1, url: '/__test__/three.webp', posterUrl: null, colourName: null, mediaRole: 2, sortOrder: 0, altTextOverride: 'Back view', assetAltText: 'A photograph' },
          { id: 704, assetPublicId: '0000beef-0000-4000-8000-000000000004', mediaType: 2, url: '/__test__/clip.mp4', posterUrl: '/__test__/poster.webp', colourName: null, mediaRole: 3, sortOrder: 0, altTextOverride: null, assetAltText: null },
        ],
        relatedProducts: [
          { relatedProductId: 302, name: 'Linen Shirt Dress', productCode: 'SD-W-0302', isPublished: false, sortOrder: 0 },
        ],
        rowVersion: rowVersion(301),
        updatedAtUtc: '2026-10-05T09:00:00Z',
      },
      {
        // Unpublished and incomplete: no photograph, so the publish checklist refuses it. The storefront's
        // own listing would not show this product at all, which is the whole reason the admin list exists.
        id: 302,
        productCode: 'SD-W-0302',
        name: 'Linen Shirt Dress',
        slug: 'linen-shirt-dress',
        departmentId: 1,
        primaryCategoryId: 11,
        extraCategoryIds: [],
        ageGroupId: null,
        shortDescription: null,
        description: null,
        careInstructions: null,
        materialId: 42,
        fitId: null,
        occasionId: null,
        basePrice: 7490,
        compareAtPrice: null,
        badgeOverride: 3,
        newUntilUtc: null,
        recommendedRank: null,
        sizeGuideId: null,
        seoTitle: null,
        seoDescription: null,
        isPublished: false,
        firstPublishedAtUtc: null,
        cardImageUrl: null,
        hoverImageUrl: null,
        colours: [
          { id: 511, name: 'Chalk', colourFamilyId: 72, colourFamilyName: 'Black', swatchHex: '#f1efe9', swatchAssetPublicId: null, swatchAssetUrl: null, sortOrder: 0 },
        ],
        variants: [
          { id: 611, colourName: 'Chalk', sizeId: 81, sizeLabel: 'S', sizeScaleName: 'Alpha', sku: 'SD-W-0302-CH-S', barcode: null, priceOverride: null, compareAtPriceOverride: null, isActive: true, sortOrder: 0, hasOrders: false, onHand: 0 },
        ],
        media: [],
        relatedProducts: [],
        rowVersion: rowVersion(302),
        updatedAtUtc: '2026-10-04T09:00:00Z',
      },
    ],
  };
}

/** Doc 11 §7 G-01, the same three rules the API refuses with. */
function publishBlockers(product) {
  const blockers = [];

  if (!product.variants.some((variant) => variant.isActive)) {
    blockers.push(
      product.variants.length === 0
        ? 'Add at least one colour and size, so there is something a customer can put in their bag.'
        : 'At least one variant has to be switched on — every colour and size is currently off.',
    );
  }

  if (!product.media.some((row) => row.mediaRole === 1)) {
    blockers.push('Add at least one gallery photograph: it becomes the picture on the listing card.');
  }

  const withoutAlt = product.media.filter(
    (row) => row.mediaRole !== 3 && !row.altTextOverride && !row.assetAltText,
  ).length;

  if (withoutAlt > 0) {
    blockers.push(
      `${withoutAlt} photograph${withoutAlt === 1 ? ' has' : 's have'} no alt text. Describe each one, so ` +
        'the page works for a customer using a screen reader.',
    );
  }

  return blockers;
}

function listItem(product) {
  return {
    id: product.id,
    productCode: product.productCode,
    name: product.name,
    slug: product.slug,
    departmentId: product.departmentId,
    departmentName: FORM_OPTIONS.departments.find((d) => d.id === product.departmentId)?.name ?? '',
    primaryCategoryId: product.primaryCategoryId,
    primaryCategoryName:
      FORM_OPTIONS.categories.find((c) => c.id === product.primaryCategoryId)?.name ?? '',
    basePrice: product.basePrice,
    compareAtPrice: product.compareAtPrice,
    badgeOverride: product.badgeOverride,
    isPublished: product.isPublished,
    firstPublishedAtUtc: product.firstPublishedAtUtc,
    updatedAtUtc: product.updatedAtUtc,
    variantCount: product.variants.filter((variant) => variant.isActive).length,
    colourCount: product.colours.length,
    hasVideo: product.media.some((row) => row.mediaRole === 3),
    cardImageUrl: product.cardImageUrl,
    rowVersion: product.rowVersion,
  };
}

function detail(product) {
  const blockers = publishBlockers(product);

  return {
    ...product,
    canBePublished: blockers.length === 0,
    publishBlockers: blockers,
  };
}

/** Which colourway a name refers to, the way `UX_ProductColours_Product_Name` compares it. */
function colourNamed(colours, name) {
  return colours.find(
    (colour) => colour.name.trim().toLowerCase() === String(name ?? '').trim().toLowerCase(),
  );
}

/**
 * Applies a form body to a product.
 *
 * **`body.media === undefined` leaves `product.media` alone.** That is the rule the W1-12 defect broke,
 * and a stub that rebuilt the gallery from whatever arrived would let the screen regress to it with every
 * browser test still green.
 */
function apply(product, body, state) {
  product.productCode = body.productCode ?? product.productCode;
  product.name = body.name ?? product.name;
  product.slug = body.slug ?? product.slug;
  product.departmentId = body.departmentId ?? product.departmentId;
  product.primaryCategoryId = body.primaryCategoryId ?? product.primaryCategoryId;
  product.extraCategoryIds = body.extraCategoryIds ?? [];
  product.ageGroupId = body.ageGroupId ?? null;
  product.shortDescription = body.shortDescription ?? null;
  product.description = body.description ?? null;
  product.careInstructions = body.careInstructions ?? null;
  product.materialId = body.materialId ?? null;
  product.fitId = body.fitId ?? null;
  product.occasionId = body.occasionId ?? null;
  product.basePrice = body.basePrice ?? product.basePrice;
  product.compareAtPrice = body.compareAtPrice ?? null;
  product.badgeOverride = body.badgeOverride ?? null;
  product.recommendedRank = body.recommendedRank ?? null;
  product.sizeGuideId = body.sizeGuideId ?? null;
  product.seoTitle = body.seoTitle ?? null;
  product.seoDescription = body.seoDescription ?? null;

  const previousColours = product.colours;

  product.colours = (body.colours ?? []).map((colour, index) => {
    const existing =
      (colour.id != null && previousColours.find((candidate) => candidate.id === colour.id)) ||
      colourNamed(previousColours, colour.name);

    return {
      id: existing?.id ?? state.nextProductId++,
      name: colour.name,
      colourFamilyId: colour.colourFamilyId,
      colourFamilyName:
        FORM_OPTIONS.colourFamilies.find((family) => family.id === colour.colourFamilyId)?.name ?? null,
      swatchHex: colour.swatchHex ?? null,
      swatchAssetPublicId: colour.swatchAssetPublicId ?? null,
      swatchAssetUrl: null,
      sortOrder: index,
    };
  });

  const previousVariants = product.variants;

  product.variants = (body.variants ?? []).map((variant, index) => {
    const existing =
      (variant.id != null && previousVariants.find((candidate) => candidate.id === variant.id)) ||
      previousVariants.find(
        (candidate) =>
          candidate.colourName === variant.colourName && candidate.sizeId === variant.sizeId,
      );

    const size = FORM_OPTIONS.sizes.find((candidate) => candidate.id === variant.sizeId);

    return {
      id: existing?.id ?? state.nextProductId++,
      colourName: variant.colourName,
      sizeId: variant.sizeId,
      sizeLabel: size?.label ?? String(variant.sizeId),
      sizeScaleName: size?.scaleName ?? '',
      sku: variant.sku,
      barcode: variant.barcode ?? null,
      priceOverride: variant.priceOverride ?? null,
      compareAtPriceOverride: variant.compareAtPriceOverride ?? null,
      isActive: variant.isActive !== false,
      sortOrder: index,
      hasOrders: existing?.hasOrders ?? false,
      onHand: existing?.onHand ?? 0,
    };
  });

  if (body.media !== undefined) {
    const previousMedia = product.media;

    product.media = body.media.map((row, index) => {
      const existing = previousMedia.find(
        (candidate) =>
          candidate.assetPublicId === row.assetPublicId &&
          (candidate.colourName ?? null) === (row.colourName ?? null),
      );

      const uploaded = state.uploads.get(row.assetPublicId);

      return {
        id: existing?.id ?? state.nextProductId++,
        assetPublicId: row.assetPublicId,
        mediaType: existing?.mediaType ?? uploaded?.mediaType ?? (row.mediaRole === 3 ? 2 : 1),
        url: existing?.url ?? uploaded?.url ?? null,
        posterUrl: existing?.posterUrl ?? uploaded?.posterUrl ?? null,
        colourName: row.colourName ?? null,
        mediaRole: row.mediaRole,
        sortOrder: index,
        altTextOverride: row.altTextOverride ?? null,
        assetAltText: existing?.assetAltText ?? uploaded?.altText ?? null,
      };
    });
  }

  product.relatedProducts = (body.relatedProducts ?? []).map((pick, index) => {
    const target = state.products.find((candidate) => candidate.id === pick.relatedProductId);

    return {
      relatedProductId: pick.relatedProductId,
      name: target?.name ?? `Product ${pick.relatedProductId}`,
      productCode: target?.productCode ?? '',
      isPublished: target?.isPublished ?? false,
      sortOrder: index,
    };
  });

  // Doc 08 §3.2: the card and hover images are denormalised from the gallery on save.
  const gallery = product.media.filter((row) => row.mediaRole === 1);
  const hover = product.media.filter((row) => row.mediaRole === 2);

  product.cardImageUrl = gallery[0]?.url ?? null;
  product.hoverImageUrl = hover[0]?.url ?? gallery[1]?.url ?? null;

  product.rowVersion = nextRowVersion();
  product.updatedAtUtc = new Date().toISOString();
}

/**
 * Handles a `/api/admin/products` request, or returns false when the path is not one of ours.
 *
 * The helpers come from the catalogue stub so the two halves answer with the same `json`, `problem` and
 * `noContent` shapes — a second copy would be a second chance for one of them to drift.
 */
export async function handleProductRoute({
  path,
  method,
  url,
  request,
  response,
  state,
  json,
  problem,
  noContent,
  readBody,
  suppliedRowVersion,
}) {
  if (!path.startsWith('/api/admin/products')) {
    return false;
  }

  if (path === '/api/admin/products/form-options' && method === 'GET') {
    json(response, 200, FORM_OPTIONS);
    return true;
  }

  if (path === '/api/admin/products/related-search' && method === 'GET') {
    const term = (url.searchParams.get('term') ?? '').trim().toLowerCase();
    const exclude = Number(url.searchParams.get('excludeProductId') ?? Number.NaN);

    json(
      response,
      200,
      state.products
        .filter((product) => product.id !== exclude)
        .filter(
          (product) =>
            term.length === 0 ||
            product.name.toLowerCase().includes(term) ||
            product.productCode.toLowerCase().includes(term),
        )
        .map((product) => ({
          relatedProductId: product.id,
          name: product.name,
          productCode: product.productCode,
          isPublished: product.isPublished,
          sortOrder: 0,
        })),
    );

    return true;
  }

  if (path === '/api/admin/products' && method === 'GET') {
    const term = (url.searchParams.get('term') ?? '').trim().toLowerCase();
    const published = url.searchParams.get('published');
    const departmentId = Number(url.searchParams.get('departmentId') ?? Number.NaN);

    let items = state.products.slice();

    if (term.length > 0) {
      items = items.filter(
        (product) =>
          product.name.toLowerCase().includes(term) ||
          product.productCode.toLowerCase().includes(term) ||
          product.slug.toLowerCase().includes(term) ||
          product.variants.some((variant) => variant.sku.toLowerCase().includes(term)),
      );
    }

    if (published === 'true' || published === 'false') {
      items = items.filter((product) => product.isPublished === (published === 'true'));
    }

    if (Number.isInteger(departmentId)) {
      items = items.filter((product) => product.departmentId === departmentId);
    }

    json(response, 200, {
      items: items.map(listItem),
      page: 1,
      pageSize: 24,
      totalCount: items.length,
      totalPages: items.length === 0 ? 0 : 1,
      hasPreviousPage: false,
      hasNextPage: false,
    });

    return true;
  }

  if (path === '/api/admin/products' && method === 'POST') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
      return true;
    }

    if (state.products.some((product) => product.slug === body.slug)) {
      json(response, 400, {
        title: 'One or more fields are invalid.',
        errors: { slug: [`Another product already uses the address /p/${body.slug}.`] },
      });
      return true;
    }

    const created = {
      id: state.nextProductId++,
      colours: [],
      variants: [],
      media: [],
      relatedProducts: [],
      isPublished: false,
      firstPublishedAtUtc: null,
      newUntilUtc: null,
      cardImageUrl: null,
      hoverImageUrl: null,
      rowVersion: nextRowVersion(),
    };

    apply(created, body, state);
    created.isPublished = false;
    created.firstPublishedAtUtc = null;
    state.products.push(created);

    json(response, 201, { id: created.id });
    return true;
  }

  const idMatch = /^\/api\/admin\/products\/(\d+)(\/published)?$/.exec(path);
  if (!idMatch) {
    problem(response, 404, 'Not found', `The stub has no route for ${path}.`, 'not_found');
    return true;
  }

  const productId = Number(idMatch[1]);
  const product = state.products.find((candidate) => candidate.id === productId);

  if (!product) {
    problem(response, 404, 'Not found', `No product has the id ${productId}.`, 'not_found');
    return true;
  }

  if (!idMatch[2] && method === 'GET') {
    json(response, 200, detail(product));
    return true;
  }

  const body = method === 'DELETE' ? null : await readBody(request);
  const supplied = suppliedRowVersion(request, body);

  if (supplied !== product.rowVersion) {
    problem(
      response,
      409,
      'This change cannot be applied',
      supplied === null
        ? 'This request did not carry the row’s current version, so it cannot be applied safely.'
        : 'Somebody else changed this while you were editing it. Reload the page to see their change.',
      'stale_row_version',
    );
    return true;
  }

  if (idMatch[2] === '/published' && method === 'PUT') {
    if (body.isPublished === true) {
      const blockers = publishBlockers(product);
      if (blockers.length > 0) {
        problem(
          response,
          409,
          'This change cannot be applied',
          `This product is not ready to go on the shop yet: ${blockers.join(' ')}`,
          'publish_checklist_failed',
          { blockers },
        );
        return true;
      }

      product.isPublished = true;
      product.firstPublishedAtUtc ??= new Date().toISOString();
    } else {
      product.isPublished = false;
    }

    product.rowVersion = nextRowVersion();
    noContent(response);
    return true;
  }

  if (!idMatch[2] && method === 'PUT') {
    // Doc 11 P1: a slug change after first publish needs the admin to confirm it, because there are no
    // redirects in this version.
    if (body.slug !== product.slug && product.firstPublishedAtUtc && body.confirmSlugChange !== true) {
      problem(
        response,
        409,
        'This change cannot be applied',
        `This product has been published at /p/${product.slug}, so that address may be in a customer’s ` +
          `bookmarks. Changing it to /p/${body.slug} will make the old address show “page not found”.`,
        'slug_change_needs_confirmation',
        { currentSlug: product.slug, requestedSlug: body.slug },
      );
      return true;
    }

    // Doc 08 §3.2: an ordered variant is deactivated, never deleted.
    const sentIds = new Set((body.variants ?? []).map((variant) => variant.id).filter((id) => id != null));
    const droppedOrdered = product.variants.filter(
      (variant) => variant.hasOrders && !sentIds.has(variant.id),
    );

    // The second reason, and the one a real database insists on:
    // FK_StockLevels_ProductVariants_VariantId is NO ACTION, so a variant that holds stock cannot be
    // deleted at all. The real guard reads inventory.StockLevels; the stub reads the seeded onHand.
    const droppedStocked = product.variants.filter(
      (variant) => !variant.hasOrders && variant.onHand > 0 && !sentIds.has(variant.id),
    );

    if (droppedOrdered.length > 0 || droppedStocked.length > 0) {
      const orderedSkus = droppedOrdered.map((variant) => variant.sku);
      const stockedSkus = droppedStocked.map((variant) => variant.sku);
      const reasons = [];

      if (orderedSkus.length > 0) {
        reasons.push(`${orderedSkus.length} already been ordered (${orderedSkus.join(', ')})`);
      }

      if (stockedSkus.length > 0) {
        reasons.push(`${stockedSkus.length} stock recorded against them (${stockedSkus.join(', ')})`);
      }

      problem(
        response,
        409,
        'This change cannot be applied',
        `Some of these sizes cannot be deleted: ${reasons.join('; and ')}. Switch them off instead.`,
        'variant_not_deletable',
        { orderedSkus, stockedSkus },
      );
      return true;
    }

    apply(product, body, state);
    noContent(response);
    return true;
  }

  if (!idMatch[2] && method === 'DELETE') {
    state.products = state.products.filter((candidate) => candidate.id !== productId);
    noContent(response);
    return true;
  }

  problem(response, 404, 'Not found', `The stub has no route for ${method} ${path}.`, 'not_found');
  return true;
}
