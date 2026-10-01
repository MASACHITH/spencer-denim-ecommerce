/**
 * What a search box value has to become before it may be put in a URL or sent to the API
 * (SRCH-01, SRCH-02).
 *
 * The bounds and the tokenising rules below are the API's own —
 * `SpencerDenim.Application.Catalog.Search.SearchTerms` and `ProductSearchQuery.Create`. Mirroring
 * them here is not belt-and-braces, for the same reason `listing-state.ts` mirrors the listing's:
 * a term arrives from a shopper's keyboard, from a pasted link or from a crawler, and a value the
 * API would answer with a 400 is a broken page for someone who only followed a link.
 *
 * It also removes characters that cannot survive the journey at all. `encodeURIComponent` — which
 * both `HttpParams` and the router's URL serialiser end up calling — **throws** `URIError` on an
 * unpaired surrogate, so a single half of an emoji pasted into the header field used to take the
 * whole navigation down with an uncaught error rather than searching for nothing. A `NUL` and the
 * other C0 controls are the same shape of problem one layer down: they travel, and then they are
 * part of a `LIKE` pattern and part of a page title.
 */

/** `SearchTerms.MinTermLength`: one character matches almost everything. */
export const MIN_TERM_LENGTH = 2;

/** `SearchTerms.MaxTermLength`: longer than the indexed column is worth searching. */
export const MAX_TERM_LENGTH = 100;

/** `SearchTerms.MaxTokens`: doc 08 §7.6, "all of up to 5 tokens must match". */
export const MAX_TOKENS = 5;

/**
 * The term as it may be written into a URL and sent to the API: no characters that cannot be
 * encoded, no control characters, single spaces, trimmed, and no longer than the API accepts.
 *
 * Anything unusable is dropped rather than repaired into a different search. A control character
 * becomes a space, because gluing the words either side of it together would invent a token the
 * shopper never typed.
 */
export function normaliseTerm(raw: string | null | undefined): string {
  if (typeof raw !== 'string' || raw.length === 0) {
    return '';
  }

  let kept = '';
  for (let index = 0; index < raw.length; index++) {
    const code = raw.charCodeAt(index);

    // A high surrogate only counts if its low half follows; a lone half of either kind is dropped.
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = raw.charCodeAt(index + 1);
      if (low >= 0xdc00 && low <= 0xdfff) {
        kept += raw[index] + raw[index + 1];
        index++;
      }
      continue;
    }
    if (code >= 0xdc00 && code <= 0xdfff) {
      continue;
    }

    // C0 controls (including NUL), DEL and the C1 range. A space, not nothing: see above.
    if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) {
      kept += ' ';
      continue;
    }

    kept += raw[index];
  }

  return truncate(kept.replace(/\s+/gu, ' ').trim());
}

/**
 * The tokens the API will match on, exactly as `SearchTerms.Tokenise` derives them: lower-cased,
 * split on whitespace, short tokens dropped, de-duplicated, at most {@link MAX_TOKENS}.
 *
 * The storefront needs them for two things it cannot ask the server about — whether a term is
 * worth a request at all, and which categories in the cached tree match what is being typed.
 */
export function tokenise(raw: string | null | undefined): readonly string[] {
  const term = normaliseTerm(raw);
  if (term.length === 0) {
    return [];
  }

  const tokens: string[] = [];
  for (const part of term.split(' ')) {
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
 * Whether the API would accept this term, which is the same question as "is it worth a request".
 *
 * Both of `ProductSearchQuery.Create`'s term rules, in the same order: long enough, and with at
 * least one word long enough to be a token. `"a b"` passes the first and fails the second, and a
 * storefront that only checked the length sent it and showed the shopper a validation error from
 * the API instead of "keep typing".
 */
export function isSearchable(raw: string | null | undefined): boolean {
  return normaliseTerm(raw).length >= MIN_TERM_LENGTH && tokenise(raw).length > 0;
}

/** The term shortened to {@link MAX_TERM_LENGTH}, never through the middle of a surrogate pair. */
function truncate(value: string): string {
  if (value.length <= MAX_TERM_LENGTH) {
    return value;
  }

  const cut = value.slice(0, MAX_TERM_LENGTH);
  const last = cut.charCodeAt(cut.length - 1);
  const whole = last >= 0xd800 && last <= 0xdbff ? cut.slice(0, -1) : cut;

  // The cut may have landed on a space, and a trailing space would travel into the URL.
  return whole.trimEnd();
}
