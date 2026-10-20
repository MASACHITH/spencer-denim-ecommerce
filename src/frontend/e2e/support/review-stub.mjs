/**
 * The review routes (PDP-08, #189) for `catalog-api-stub.mjs`.
 *
 * **Field for field the server's DTOs, and no more permissive than its validators.** `ReviewRules.Clean` and
 * `ProductReviewEndpoints` decide what a write may carry; this refuses the same things with the same field
 * names: a rating that is missing, out of range or not a JSON whole number; a title or body that is not text;
 * a title on two lines; control and direction-override characters; text over 120 / 4,000 characters after
 * trimming; a second review of one product (409 `already_reviewed` with the saved review); an edit of nothing
 * (404 `not_reviewed`); more than ten writes an hour (429 with `Retry-After`); a body that is not JSON (415).
 * The real rules are pinned against SQL Server by `ReviewSubmissionEndpointTests`.
 *
 * **Who is signed in.** The real customer cookie is an encrypted ticket; here `sd_customer_auth` carries a token
 * a test registered with `/__test__/review-customer`, and everything a customer writes is kept under that token
 * so parallel tests never see each other's reviews. `/api/account/session` answers that customer, and nobody
 * for an unknown or missing token — exactly the stub's old answer for everyone.
 *
 * **What is published.** Fixture reviews for `test-product-3` (one verified, one carrying markup, one edited),
 * nothing for any other product, plus whatever a customer registered with `approval=off` has written — those
 * are visible to everyone, as on the real site, so a test that publishes uses a product nobody else asserts on.
 */

const TITLE_MAX = 120;
const BODY_MAX = 4000;
const WRITES_PER_HOUR = 10;
const PAGE_SIZE_DEFAULT = 10;
const PAGE_SIZE_MAX = 50;

/** token -> { customer, approval, verifiedProducts:Set<number>, writes:number[], reviews: Map<productId, review> } */
const customers = new Map();
let nextReviewId = 5000;

const FIXTURE_REVIEWS = {
  3: [
    {
      id: 301,
      rating: 5,
      title: 'Exactly as pictured',
      body: 'Soft linen and a relaxed fit.\nTrue to size.',
      displayName: 'Kamala S.',
      isVerifiedBuyer: true,
      createdAtUtc: '2026-10-18T04:30:00Z',
      editedAtUtc: null,
    },
    {
      id: 302,
      rating: 4,
      title: '<b>Good</b> shirt',
      body: '<img src=x onerror="window.__reviewXss=1"> Washes well & keeps its shape.',
      displayName: 'Ruwan P.',
      isVerifiedBuyer: false,
      createdAtUtc: '2026-10-15T04:30:00Z',
      editedAtUtc: null,
    },
    {
      id: 303,
      rating: 4,
      title: null,
      body: 'A '.repeat(30) + 'longlonglonglonglonglonglonglonglonglonglonglonglonglonglonglonglong-unbroken-word',
      displayName: 'Anushka W.',
      isVerifiedBuyer: false,
      createdAtUtc: '2026-10-10T04:30:00Z',
      editedAtUtc: '2026-10-12T04:30:00Z',
    },
  ],
};

function send(response, status, body, headers = {}) {
  response.statusCode = status;
  for (const [name, value] of Object.entries(headers)) {
    response.setHeader(name, value);
  }
  response.end(body === undefined ? '' : JSON.stringify(body));
  return true;
}

function noStore(response) {
  response.setHeader('Cache-Control', 'no-store, no-cache, private');
  response.setHeader('Vary', 'Cookie');
}

function tokenOf(request) {
  const cookie = String(request.headers.cookie ?? '');
  const match = /(?:^|;\s*)sd_customer_auth=([^;]+)/.exec(cookie);
  return match ? decodeURIComponent(match[1]) : null;
}

function published(productId) {
  const own = [...customers.values()]
    .map((entry) => entry.reviews.get(productId))
    .filter((review) => review && review.status === 'Approved')
    .map(({ id, rating, title, body, displayName, isVerifiedBuyer, createdAtUtc, editedAtUtc }) => ({
      id, rating, title, body, displayName, isVerifiedBuyer, createdAtUtc, editedAtUtc,
    }));

  return [...own, ...(FIXTURE_REVIEWS[productId] ?? [])].sort(
    (left, right) => right.createdAtUtc.localeCompare(left.createdAtUtc) || right.id - left.id,
  );
}

function summaryOf(reviews) {
  const count = reviews.length;
  const sum = reviews.reduce((total, review) => total + review.rating, 0);

  return {
    average: count === 0 ? null : Math.round((sum / count) * 100) / 100,
    count,
    distribution: [5, 4, 3, 2, 1].map((stars) => ({ stars, count: reviews.filter((review) => review.rating === stars).length })),
  };
}

function wholeNumber(raw, fallback, min, max) {
  if (raw === null || raw === '') {
    return fallback;
  }
  if (!/^-?\d+$/.test(raw)) {
    return null;
  }
  return Math.min(Math.max(Number(raw), min), max);
}

/** `ReviewRules.CleanText`, line for line. */
function cleanText(raw, field, max, multiline, errors) {
  if (raw === null || raw === undefined) {
    return null;
  }

  let text = raw.replace(/\r\n/g, '\n').replace(/\r/g, '\n');

  for (const character of text) {
    const code = character.codePointAt(0);
    if (character === '\n') {
      if (!multiline) {
        errors[field] = ['Write the title on one line.'];
        return null;
      }
      continue;
    }
    if (character === '\t' && multiline) {
      continue;
    }
    if (code >= 0xd800 && code <= 0xdfff) {
      errors[field] = ['This contains a character that cannot be stored. Please retype it.'];
      return null;
    }
    const control = code < 0x20 || (code >= 0x7f && code <= 0x9f);
    const bidi = (code >= 0x202a && code <= 0x202e) || (code >= 0x2066 && code <= 0x2069) || code === 0x200e || code === 0x200f || code === 0x061c;
    if (control || bidi) {
      errors[field] = ['This contains a hidden control character. Please retype it.'];
      return null;
    }
  }

  text = text.trim();
  if (multiline) {
    text = text.replace(/\n{3,}/g, '\n\n');
  }
  if (text.length === 0) {
    return null;
  }
  if (text.length > max) {
    errors[field] = [`Keep this to ${max.toLocaleString('en-GB')} characters or fewer (it is ${text.length.toLocaleString('en-GB')}).`];
    return null;
  }
  return text;
}

function readJson(request, response, then) {
  const contentType = String(request.headers['content-type'] ?? '').toLowerCase();
  if (!contentType.includes('application/json')) {
    send(response, 415, { title: 'Not JSON', status: 415, detail: 'Send the request body as application/json.', code: 'not_json' });
    return;
  }

  let raw = '';
  request.on('data', (chunk) => {
    raw += chunk;
  });
  request.on('end', () => {
    let body;
    try {
      body = JSON.parse(raw);
    } catch {
      send(response, 400, { title: 'Malformed JSON', status: 400, code: 'malformed_json' });
      return;
    }
    if (body === null || typeof body !== 'object' || Array.isArray(body)) {
      send(response, 400, { title: 'Empty request body', status: 400, code: 'empty_body' });
      return;
    }
    then(body);
  });
}

function mineDto(review) {
  const { id, productId, rating, title, body, displayName, isVerifiedBuyer, status, createdAtUtc, editedAtUtc } = review;
  return { id, productId, rating, title, body, displayName, isVerifiedBuyer, status, createdAtUtc, editedAtUtc };
}

function write(request, response, entry, productId, isEdit) {
  const now = Date.now();
  entry.writes = entry.writes.filter((at) => now - at < 60 * 60 * 1000);
  if (entry.writes.length >= WRITES_PER_HOUR) {
    return send(
      response,
      429,
      { title: 'Too many reviews', status: 429, detail: 'You have written or edited a lot of reviews in the last hour. Please try again later.', code: 'rate_limited' },
      { 'Retry-After': '3600' },
    );
  }
  entry.writes.push(now);

  readJson(request, response, (body) => {
    const errors = {};
    let rating = null;

    if (Object.hasOwn(body, 'rating') && body.rating !== null) {
      if (typeof body.rating !== 'number' || !Number.isInteger(body.rating)) {
        errors.rating = ['The rating must be a whole number from 1 to 5.'];
      } else {
        rating = body.rating;
      }
    }

    for (const field of ['title', 'body']) {
      if (Object.hasOwn(body, field) && body[field] !== null && typeof body[field] !== 'string') {
        errors[field] = [`Send ${field} as text.`];
      }
    }

    if (Object.keys(errors).length > 0) {
      return send(response, 400, { title: 'One or more validation errors occurred.', status: 400, errors });
    }

    if (rating === null) {
      errors.rating = ['Choose a star rating from 1 to 5.'];
    } else if (rating < 1 || rating > 5) {
      errors.rating = ['The rating must be a whole number from 1 to 5.'];
    }

    const title = cleanText(body.title ?? null, 'title', TITLE_MAX, false, errors);
    const text = cleanText(body.body ?? null, 'body', BODY_MAX, true, errors);

    if (Object.keys(errors).length > 0) {
      return send(response, 400, { title: 'One or more validation errors occurred.', status: 400, errors });
    }

    const existing = entry.reviews.get(productId);

    if (!isEdit && existing) {
      return send(response, 409, {
        title: 'Already reviewed',
        status: 409,
        detail: 'You have already reviewed this product. You can edit your review instead.',
        code: 'already_reviewed',
        review: mineDto(existing),
      });
    }

    if (isEdit && !existing) {
      return send(response, 404, { title: 'No review to edit', status: 404, detail: 'You have not reviewed this product yet.', code: 'not_reviewed' });
    }

    const verifiedNow = entry.verifiedProducts.has(productId);
    const status = existing?.status === 'Hidden' ? 'Hidden' : entry.approval ? 'Pending' : 'Approved';
    const stamp = new Date().toISOString();

    const saved = existing
      ? { ...existing, rating, title, body: text, status, editedAtUtc: stamp, isVerifiedBuyer: existing.isVerifiedBuyer || verifiedNow }
      : {
          id: nextReviewId++,
          productId,
          rating,
          title,
          body: text,
          displayName: entry.customer.lastName ? `${entry.customer.firstName} ${entry.customer.lastName[0].toUpperCase()}.` : entry.customer.firstName,
          isVerifiedBuyer: verifiedNow,
          status,
          createdAtUtc: stamp,
          editedAtUtc: null,
        };

    entry.reviews.set(productId, saved);

    const outcome = status === 'Approved' ? 'Published' : status === 'Hidden' ? 'StillHidden' : 'Pending';
    return send(response, isEdit ? 200 : 201, { review: mineDto(saved), outcome });
  });

  return true;
}

/**
 * Answers a review route, or the session route, and says whether it did.
 *
 * @param {{ totalProducts: number, slugPattern: RegExp }} catalogue
 */
export function handleReviewRoute(request, response, url, catalogue) {
  // Test-only: register a signed-in customer. `approval=off` publishes their reviews at once; `verified=3,7`
  // lists the products a Delivered order of theirs contains.
  if (url.pathname === '/__test__/review-customer') {
    const token = url.searchParams.get('token');
    if (!token) {
      return send(response, 400, { title: 'token is required' });
    }
    customers.set(token, {
      customer: {
        userId: 7000 + customers.size,
        email: `${token}@example.invalid`,
        firstName: url.searchParams.get('firstName') ?? 'Nimal',
        lastName: url.searchParams.get('lastName') ?? 'Perera',
        emailConfirmed: true,
      },
      approval: url.searchParams.get('approval') !== 'off',
      verifiedProducts: new Set((url.searchParams.get('verified') ?? '').split(',').filter(Boolean).map(Number)),
      writes: [],
      reviews: new Map(),
    });
    return send(response, 200, { ok: true });
  }

  if (url.pathname === '/api/account/session') {
    const entry = customers.get(tokenOf(request) ?? '');
    noStore(response);
    return send(response, 200, { customer: entry ? entry.customer : null });
  }

  const publicMatch = /^\/api\/catalog\/products\/([^/]+)\/reviews$/.exec(url.pathname);
  if (publicMatch && request.method === 'GET') {
    const slug = decodeURIComponent(publicMatch[1]);
    if (!catalogue.slugPattern.test(slug) || slug.length > 220) {
      return send(response, 400, { title: 'One or more validation errors occurred.', status: 400, errors: { slug: ['slug must be lower-case letters and digits separated by single hyphens.'] } });
    }
    const match = /^test-product-(\d+)$/.exec(slug);
    const productId = match ? Number(match[1]) : 0;
    if (!match || productId < 1 || productId > catalogue.totalProducts) {
      return send(response, 404, { title: 'Product not found', status: 404, detail: `No published product has the slug '${slug}'.` });
    }

    const page = wholeNumber(url.searchParams.get('page'), 1, 1, 10_000);
    const pageSize = wholeNumber(url.searchParams.get('pageSize'), PAGE_SIZE_DEFAULT, 1, PAGE_SIZE_MAX);
    if (page === null || pageSize === null) {
      return send(response, 400, { title: 'Bad request', status: 400 });
    }

    const reviews = published(productId);
    return send(response, 200, {
      summary: summaryOf(reviews),
      items: reviews.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: reviews.length,
    });
  }

  const accountMatch = /^\/api\/account\/reviews\/(-?\d+)$/.exec(url.pathname);
  if (accountMatch) {
    noStore(response);
    const entry = customers.get(tokenOf(request) ?? '');
    if (!entry) {
      return send(response, 401, undefined);
    }

    const productId = Number(accountMatch[1]);
    if (productId < 1 || productId > catalogue.totalProducts) {
      return send(response, 404, { title: 'Product not found', status: 404, detail: `No published product has the id ${productId}.`, code: 'product_not_found' });
    }

    if (request.method === 'GET') {
      const review = entry.reviews.get(productId);
      return send(response, 200, {
        review: review ? mineDto(review) : null,
        wouldBeVerified: review?.isVerifiedBuyer === true || entry.verifiedProducts.has(productId),
      });
    }

    if (request.method === 'POST' || request.method === 'PUT') {
      return write(request, response, entry, productId, request.method === 'PUT');
    }

    return send(response, 405, { title: 'Method not allowed', status: 405 });
  }

  return false;
}
