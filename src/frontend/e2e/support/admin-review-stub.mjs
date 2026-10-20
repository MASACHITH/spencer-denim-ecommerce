/**
 * Review moderation (ADM-16, #189) for `admin-catalog-stub.mjs`.
 *
 * Field for field `AdminReviewDto`, `AdminReviewListPage` and `AdminModerationResponse`, and no more permissive
 * than `AdminReviewEndpoints` / `ModerateReviewHandler`: `status` and `verified` accept exact names only (no
 * numbers, no comma lists); `rowVersion` is required and must be eight bytes of base64; `note` is optional text
 * up to 500 characters with the body's character rules; a stale version is 409 `stale_review` carrying the
 * review as it now is; a review already in the target status is 200 with `changed: false`. The real rules,
 * the audit rows and the rating recompute are pinned against SQL Server by `AdminReviewEndpointTests`.
 */

const STATUSES = ['Pending', 'Approved', 'Hidden', 'All'];
const NOTE_MAX = 500;

export function freshReviewState() {
  return { reviews: [], reviewVersion: 1 };
}

function version(state) {
  state.reviewVersion += 1;
  const bytes = Buffer.alloc(8);
  bytes.writeUInt32BE(state.reviewVersion, 4);
  return bytes.toString('base64');
}

function review(state, id, overrides = {}) {
  return {
    id,
    productId: 100 + id,
    productName: 'Relaxed linen shirt',
    productCode: `SD-${String(100 + id).padStart(4, '0')}`,
    productSlug: `relaxed-linen-shirt-${id}`,
    rating: 4,
    title: 'Lovely fabric',
    body: 'Fits well and washes well.',
    displayName: 'Nimal P.',
    isVerifiedBuyer: false,
    status: 'Pending',
    createdAtUtc: `2026-10-${String(10 + id).padStart(2, '0')}T04:30:00Z`,
    editedAtUtc: null,
    moderatedAtUtc: null,
    moderatedByName: null,
    moderationNote: null,
    rowVersion: version(state),
    ...overrides,
  };
}

function reviewsFor(state, set) {
  if (set === 'none') {
    return [];
  }

  const rows = [
    review(state, 1, { isVerifiedBuyer: true, rating: 5, title: 'Exactly as pictured', body: 'Soft linen.\nTrue to size.' }),
    review(state, 2, { rating: 2, title: '<b>Too</b> small', body: '<img src=x onerror="window.__adminXss=1"> Runs a size small.', displayName: 'Ruwan P.' }),
    review(state, 3, { status: 'Approved', rating: 4, moderatedAtUtc: '2026-10-14T04:30:00Z', moderatedByName: 'Ann Staff' }),
    review(state, 4, { status: 'Hidden', rating: 1, title: 'Call me', body: 'Phone 0771234567', moderatedAtUtc: '2026-10-15T04:30:00Z', moderatedByName: 'Ann Staff', moderationNote: 'Personal data' }),
    review(state, 5, { title: null, body: 'word'.repeat(60), displayName: 'A very long display name that still fits sixty chars' }),
  ];

  if (set === 'many') {
    for (let id = 6; id <= 30; id++) {
      rows.push(review(state, id));
    }
  }

  return rows;
}

function exactName(raw, names) {
  if (raw === null || raw.trim() === '') {
    return { value: null };
  }
  const found = names.find((name) => name.toLowerCase() === raw.trim().toLowerCase());
  return found ? { value: found } : { invalid: true };
}

function clamp(raw, fallback, min, max) {
  if (raw === null || raw === '') {
    return fallback;
  }
  const number = Number(raw);
  return Number.isInteger(number) ? Math.min(Math.max(number, min), max) : null;
}

function noteProblem(note) {
  for (const character of note) {
    const code = character.codePointAt(0);
    if (character === '\n' || character === '\t') {
      continue;
    }
    if (code < 0x20 || (code >= 0x7f && code <= 0x9f) || (code >= 0x202a && code <= 0x202e) || (code >= 0x2066 && code <= 0x2069)) {
      return 'This contains a hidden control character. Please retype it.';
    }
  }
  return note.trim().length > NOTE_MAX ? `Keep this to ${NOTE_MAX} characters or fewer (it is ${note.trim().length}).` : null;
}

function list(state, url, json, response) {
  const errors = {};
  const status = exactName(url.searchParams.get('status'), STATUSES);
  if (status.invalid) {
    errors.status = ['Use status=Pending, Approved, Hidden or All.'];
  }

  const verifiedRaw = url.searchParams.get('verified');
  let verified = null;
  if (verifiedRaw !== null && verifiedRaw.trim() !== '') {
    const text = verifiedRaw.trim().toLowerCase();
    if (text === 'true' || text === 'false') {
      verified = text === 'true';
    } else {
      errors.verified = ['Use verified=true or verified=false.'];
    }
  }

  const page = clamp(url.searchParams.get('page'), 1, 1, 10_000);
  const pageSize = clamp(url.searchParams.get('pageSize'), 25, 1, 100);
  if (page === null || pageSize === null) {
    return json(response, 400, { title: 'Bad request', status: 400 });
  }

  if (Object.keys(errors).length > 0) {
    return json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors });
  }

  const chosen = status.value ?? 'Pending';
  const search = (url.searchParams.get('search') ?? '').trim().slice(0, 100).toLowerCase();

  let rows = state.reviews.filter((row) => chosen === 'All' || row.status === chosen);
  if (verified !== null) {
    rows = rows.filter((row) => row.isVerifiedBuyer === verified);
  }
  if (search !== '') {
    rows = rows.filter((row) =>
      [row.productName, row.productCode, row.displayName, row.title ?? '', row.body ?? ''].some((text) => text.toLowerCase().includes(search)),
    );
  }

  rows = [...rows].sort((left, right) =>
    chosen === 'Pending'
      ? left.createdAtUtc.localeCompare(right.createdAtUtc) || left.id - right.id
      : right.createdAtUtc.localeCompare(left.createdAtUtc) || right.id - left.id,
  );

  const count = (value) => state.reviews.filter((row) => row.status === value).length;

  return json(response, 200, {
    items: rows.slice((page - 1) * pageSize, page * pageSize),
    page,
    pageSize,
    totalCount: rows.length,
    counts: { pending: count('Pending'), approved: count('Approved'), hidden: count('Hidden') },
  });
}

export async function handleReviewRoute({ path, method, url, request, response, state, json, problem, readBody }) {
  if (path === '/__test__/reviews') {
    state.reviews = reviewsFor(state, url.searchParams.get('set') ?? 'some');
    json(response, 200, { count: state.reviews.length });
    return true;
  }

  // Test-only: the author edits a review behind the moderator's back (a new version, a new title).
  if (path === '/__test__/review-edited') {
    const row = state.reviews.find((candidate) => candidate.id === Number(url.searchParams.get('id')));
    if (row) {
      Object.assign(row, { title: url.searchParams.get('title') ?? 'Edited', status: 'Pending', editedAtUtc: '2026-10-20T04:30:00Z', rowVersion: version(state) });
    }
    json(response, row ? 200 : 404, { ok: Boolean(row) });
    return true;
  }

  if (path === '/api/admin/reviews' && method === 'GET') {
    list(state, url, json, response);
    return true;
  }

  const match = /^\/api\/admin\/reviews\/(\d+)\/(approve|hide)$/.exec(path);
  if (!match || method !== 'POST') {
    return false;
  }

  const body = await readBody(request);
  if (body === undefined || body === null || typeof body !== 'object' || Array.isArray(body)) {
    problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    return true;
  }

  if (!Object.hasOwn(body, 'rowVersion')) {
    json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: { rowVersion: ['Send rowVersion.'] } });
    return true;
  }

  if (body.rowVersion !== null && typeof body.rowVersion !== 'string') {
    json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: { rowVersion: ['Send rowVersion as text.'] } });
    return true;
  }

  if (Object.hasOwn(body, 'note') && body.note !== null && typeof body.note !== 'string') {
    json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: { note: ['Send note as text.'] } });
    return true;
  }

  const errors = {};
  const decoded = typeof body.rowVersion === 'string' && /^[A-Za-z0-9+/]+={0,2}$/.test(body.rowVersion.trim())
    ? Buffer.from(body.rowVersion.trim(), 'base64')
    : null;
  if (!decoded || decoded.length !== 8) {
    errors.rowVersion = ['Send the review\'s rowVersion as the list gave it, so a change made since you loaded it is not overwritten.'];
  }

  const note = typeof body.note === 'string' ? body.note : null;
  const badNote = note === null ? null : noteProblem(note);
  if (badNote) {
    errors.note = [badNote];
  }

  if (Object.keys(errors).length > 0) {
    json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors });
    return true;
  }

  const row = state.reviews.find((candidate) => candidate.id === Number(match[1]));
  if (!row) {
    problem(response, 404, 'Not found', 'There is no such review, or its product has been removed.', 'not_found');
    return true;
  }

  if (row.rowVersion !== body.rowVersion.trim()) {
    problem(response, 409, 'The review has changed', 'Somebody changed this review since you loaded it.', 'stale_review', { review: row });
    return true;
  }

  const target = match[2] === 'approve' ? 'Approved' : 'Hidden';
  if (row.status === target) {
    json(response, 200, { review: row, changed: false });
    return true;
  }

  const cleanNote = note === null || note.trim() === '' ? null : note.trim();
  Object.assign(row, {
    status: target,
    moderatedAtUtc: new Date().toISOString(),
    moderatedByName: 'Test Admin',
    moderationNote: cleanNote,
    rowVersion: version(state),
  });

  json(response, 200, { review: row, changed: true });
  return true;
}
