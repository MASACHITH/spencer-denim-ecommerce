import { MAX_TERM_LENGTH, MAX_TOKENS, isSearchable, normaliseTerm, tokenise } from './search-term';

/**
 * These mirror `SearchTermsTests` and `ProductSearchQueryTests` on the backend, case for case, plus
 * the cases only a browser has: a value that `encodeURIComponent` refuses, and a value a shopper can
 * paste in but the API would answer 400 for.
 */
describe('normaliseTerm (SRCH-01)', () => {
  it('trims and collapses whitespace', () => {
    expect(normaliseTerm('  Linen   Shirt \n ')).toBe('Linen Shirt');
  });

  it('keeps the case the shopper typed, because the term is shown back to them', () => {
    expect(normaliseTerm('Linen')).toBe('Linen');
  });

  it.each([null, undefined, '', '   ', '\t\n'])('%p becomes an empty term', (raw) => {
    expect(normaliseTerm(raw)).toBe('');
  });

  /**
   * The bug this exists for: `encodeURIComponent` **throws** `URIError` on half a surrogate pair,
   * and both `HttpParams` and the router's URL serialiser call it. A shopper pasting one character
   * of an emoji used to take the whole navigation down with an uncaught error.
   */
  it('drops a lone high surrogate', () => {
    expect(normaliseTerm('\ud83d')).toBe('');
    expect(() => encodeURIComponent(normaliseTerm('linen \ud83d'))).not.toThrow();
  });

  it('drops a lone low surrogate', () => {
    expect(normaliseTerm('a\udc00b')).toBe('ab');
  });

  it('keeps a whole surrogate pair, because an emoji is a legitimate thing to search for', () => {
    expect(normaliseTerm('👗 dress')).toBe('👗 dress');
    expect(() => encodeURIComponent(normaliseTerm('👗 dress'))).not.toThrow();
  });

  it('turns a NUL into a space rather than deleting it, so two words do not become one', () => {
    expect(normaliseTerm('linen\u0000shirt')).toBe('linen shirt');
  });

  it.each(['\u0001', '\u001f', '\u007f', '\u009f'])('strips the control character %p', (control) => {
    expect(normaliseTerm(`a${control}b`)).toBe('a b');
  });

  it('cuts a term longer than the API accepts', () => {
    const term = normaliseTerm('x'.repeat(2_000));

    expect(term).toHaveLength(MAX_TERM_LENGTH);
  });

  it('never cuts through the middle of a surrogate pair', () => {
    // 99 characters, then an emoji: cutting at 100 would land between its two halves.
    const term = normaliseTerm(`${'x'.repeat(MAX_TERM_LENGTH - 1)}👗`);

    expect(term).toHaveLength(MAX_TERM_LENGTH - 1);
    expect(() => encodeURIComponent(term)).not.toThrow();
  });

  it('does not leave a trailing space behind when the cut lands on one', () => {
    const term = normaliseTerm(`${'x'.repeat(MAX_TERM_LENGTH - 1)} yyy`);

    expect(term).toBe('x'.repeat(MAX_TERM_LENGTH - 1));
  });

  it('leaves punctuation alone: it is a term the API will match on and find nothing', () => {
    expect(normaliseTerm('!!!')).toBe('!!!');
  });
});

describe('tokenise (doc 08 §7.6)', () => {
  it('lower-cases, splits on whitespace and de-duplicates', () => {
    expect(tokenise('  Linen   SHIRT linen ')).toEqual(['linen', 'shirt']);
  });

  it('keeps at most five tokens', () => {
    const tokens = tokenise('one two three four five six seven');

    expect(tokens).toHaveLength(MAX_TOKENS);
    expect(tokens).not.toContain('six');
  });

  it('drops tokens shorter than the minimum', () => {
    expect(tokenise('a shirt')).toEqual(['shirt']);
  });

  it('produces nothing for a term made only of one-character words', () => {
    expect(tokenise('a b c')).toEqual([]);
  });

  it('survives a non-Latin script, as the platform collation does', () => {
    // Built from two variables rather than written out twice: the Sinhala and Tamil viramas look
    // identical in a monospaced font and the first version of this test compared one with the other.
    const sinhala = 'ඔසරිය';
    const tamil = 'காஞ்சிபுரம்';

    expect(tokenise(`${sinhala} ${tamil}`)).toEqual([sinhala, tamil]);
  });
});

describe('isSearchable', () => {
  it.each(['linen', 'ab', '!!!', '👗'])('%p is worth a request', (term) => {
    expect(isSearchable(term)).toBe(true);
  });

  it.each([null, undefined, '', ' ', 'a', '\ud83d'])('%p is not', (term) => {
    expect(isSearchable(term)).toBe(false);
  });

  /**
   * Both of the API's term rules, not just the length one. `"a b"` is three characters long and
   * tokenises to nothing, and a storefront that only checked the length sent it and showed the
   * shopper a validation error from the API instead of "keep typing".
   */
  it('refuses a term that is long enough but tokenises to nothing', () => {
    expect(normaliseTerm('a b')).toHaveLength(3);
    expect(isSearchable('a b')).toBe(false);
  });
});
