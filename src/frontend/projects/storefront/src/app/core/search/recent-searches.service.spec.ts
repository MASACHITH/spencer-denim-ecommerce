import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import {
  RECENT_SEARCHES_KEY,
  RECENT_SEARCHES_LIMIT,
  RecentSearches,
  isPrivate,
} from './recent-searches.service';

/** An in-memory stand-in for `localStorage`, as the other browser-storage tests use. */
class StorageStub {
  readonly entries = new Map<string, string>();

  read(key: string): string | null {
    return this.entries.get(key) ?? null;
  }

  write(key: string, value: string): boolean {
    this.entries.set(key, value);
    return true;
  }

  remove(key: string): boolean {
    return this.entries.delete(key);
  }
}

/** Storage that refuses everything, the way a private window can (lesson L1). */
class BlockedStorage extends StorageStub {
  override read(): null {
    return null;
  }

  override write(): boolean {
    return false;
  }
}

/**
 * The history is read in `afterNextRender`, so a component has to render for it to fill — which is
 * also proof of the rule the service exists to keep: nothing is read from storage, and therefore
 * nothing is rendered from it, until the page is live. On the server it stays empty.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p>{{ recent.recent().length }}</p>`,
})
class Host {
  readonly recent = inject(RecentSearches);
}

describe('RecentSearches (SRCH-01)', () => {
  function setUp(storage: StorageStub = new StorageStub()) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: SafeLocalStorage, useValue: storage }],
    });

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    return { storage, fixture, service: fixture.componentInstance.recent };
  }

  function stored(storage: StorageStub): readonly string[] {
    return JSON.parse(storage.entries.get(RECENT_SEARCHES_KEY) ?? '[]') as string[];
  }

  it('starts empty when nothing has been stored', () => {
    expect(setUp().service.recent()).toEqual([]);
  });

  it('reads what an earlier visit left', () => {
    const storage = new StorageStub();
    storage.entries.set(RECENT_SEARCHES_KEY, JSON.stringify(['linen shirt', 'denim']));

    expect(setUp(storage).service.recent()).toEqual(['linen shirt', 'denim']);
  });

  it('puts the newest first and never twice, whatever the case', () => {
    const { service, storage } = setUp();

    service.remember('linen');
    service.remember('denim');
    service.remember('LINEN');

    expect(service.recent()).toEqual(['LINEN', 'denim']);
    expect(stored(storage)).toEqual(['LINEN', 'denim']);
  });

  it('keeps only the most recent few', () => {
    const { service } = setUp();

    for (let index = 0; index < RECENT_SEARCHES_LIMIT + 3; index++) {
      service.remember(`term ${index}`);
    }

    expect(service.recent()).toHaveLength(RECENT_SEARCHES_LIMIT);
    expect(service.recent()[0]).toBe(`term ${RECENT_SEARCHES_LIMIT + 2}`);
  });

  it('normalises before storing, so the same search is not remembered twice over', () => {
    const { service } = setUp();

    service.remember('  Linen   Shirt ');

    expect(service.recent()).toEqual(['Linen Shirt']);
  });

  it('refuses a term the API would not accept', () => {
    const { service } = setUp();

    service.remember('a');
    service.remember('a b');
    service.remember('');

    expect(service.recent()).toEqual([]);
  });

  // ------------------------------------------------------------------ the privacy rules

  /**
   * Doc 08 DB25 keeps these out of `analytics.SearchQueriesDaily`; the browser keeps them out of its
   * own history for the same reason, and more urgently — this is a shared family laptop as often as
   * it is a private phone, and the history is on screen in the header.
   */
  it('never remembers something that looks like an email address', () => {
    const { service, storage } = setUp();

    service.remember('someone@example.com');

    expect(service.recent()).toEqual([]);
    expect(storage.entries.has(RECENT_SEARCHES_KEY)).toBe(false);
  });

  it('never remembers something with seven or more digits in it', () => {
    const { service } = setUp();

    service.remember('0771234567');
    service.remember('order 20260115001');

    expect(service.recent()).toEqual([]);
  });

  /**
   * **The rule has to be unanchored.** The first version tested
   * `/^[^@\s]+@[^@\s]+\.[^@\s]+$/`, which only matched a term that *was* an address — so every
   * one of these was stored and rendered back in the overlay, measured in a real browser out of
   * `localStorage`. A shopper does not type a bare address into a search box; they type a sentence
   * with one in it.
   *
   * It matters more than a browser's own storage, too: the same rule decides what reaches
   * `analytics.SearchQueriesDaily`, which keeps rows for **13 months** (doc 08 DB25, issue #78).
   */
  it.each([
    'my email is bob@example.com',
    'contact someone@example.com',
    'someone@example.com please',
    'sachith.m+shop@gmail.com',
  ])('never remembers %p, an address inside a sentence', (term) => {
    const { service, storage } = setUp();

    service.remember(term);

    expect(service.recent()).toEqual([]);
    expect(storage.entries.has(RECENT_SEARCHES_KEY)).toBe(false);
  });

  it('catches the fullwidth at-sign a CJK keyboard produces, spaces and all', () => {
    const { service } = setUp();

    service.remember('john ＠ example.com');
    service.remember('john ﹫ example.com');

    expect(service.recent()).toEqual([]);
  });

  /**
   * `\d` and `\D` are ASCII-only even under the `u` flag, so a telephone number typed on an Arabic
   * or a Japanese keyboard counted as **zero** digits and was stored.
   */
  it.each([
    ['Arabic-Indic', '٠٧٧١٢٣٤٥٦٧'],
    ['fullwidth', '０７７１２３４５６７'],
  ])('never remembers a telephone number in %s digits', (_script, term) => {
    const { service } = setUp();

    service.remember(term);

    expect(service.recent()).toEqual([]);
  });

  /** The rule must not eat the feature: these are product searches and they are kept. */
  it.each(['32 waist jeans', 'size 10 dress', 'order 123456', 'linen shirt'])(
    'still remembers %p',
    (term) => {
      const { service } = setUp();

      service.remember(term);

      expect(service.recent()).toEqual([term]);
    },
  );

  it('still remembers a term with a few digits, which is a real product search', () => {
    const { service } = setUp();

    service.remember('32 waist jeans');

    expect(service.recent()).toEqual(['32 waist jeans']);
  });

  it('drops a private term that an older build had already stored', () => {
    const storage = new StorageStub();
    storage.entries.set(
      RECENT_SEARCHES_KEY,
      JSON.stringify(['linen', 'someone@example.com', '0771234567']),
    );

    expect(setUp(storage).service.recent()).toEqual(['linen']);
  });

  // ------------------------------------------------------------------ the shopper's own control

  it('clears everything, key and all', () => {
    const { service, storage } = setUp();
    service.remember('linen');

    service.clear();

    expect(service.recent()).toEqual([]);
    expect(storage.entries.has(RECENT_SEARCHES_KEY)).toBe(false);
  });



  // ------------------------------------------------------------------ lesson L1: junk in storage

  it.each([
    ['not json at all', '{{{'],
    ['an object instead of a list', '{"a":1}'],
    ['a list of the wrong things', '[1,2,{"q":"x"}]'],
    ['a list with holes in it', '[null,"linen",null]'],
  ])('survives %s', (_label, raw) => {
    const storage = new StorageStub();
    storage.entries.set(RECENT_SEARCHES_KEY, raw);

    const { service } = setUp(storage);

    expect(service.recent().every((term) => typeof term === 'string')).toBe(true);
  });

  it('de-duplicates what it reads back', () => {
    const storage = new StorageStub();
    storage.entries.set(RECENT_SEARCHES_KEY, JSON.stringify(['linen', 'LINEN', 'linen']));

    expect(setUp(storage).service.recent()).toEqual(['linen']);
  });

  it('works when storage refuses everything, as a private window can', () => {
    const { service } = setUp(new BlockedStorage());

    service.remember('linen');

    // Still on screen for this page view, and simply not persisted.
    expect(service.recent()).toEqual(['linen']);
  });
});

describe('isPrivate', () => {
  it.each([
    'someone@example.com',
    'my email is bob@example.com',
    'contact someone@example.com',
    'someone@example.com please',
    'sachith.m+shop@gmail.com',
    'john ＠ example.com',
    '0771234567',
    '+94 77 123 4567',
    '077-123-4567',
    'SD-1234-5678',
    '4111 1111 1111 1111',
    'ORD 2026 0115 001',
    '٠٧٧١٢٣٤٥٦٧',
    '０７７１２３４５６７',
  ])('%p is not kept', (term) => {
    expect(isPrivate(term)).toBe(true);
  });

  /**
   * The other direction, and it is the half that keeps the rule honest: widening it until it eats
   * ordinary product searches would be a worse answer than the anchored version it replaced.
   */
  it.each(['linen shirt', '32 waist', 'size 10 dress', 'order 123456', 'size 8', 'jeans 2026'])(
    '%p is kept',
    (term) => {
      expect(isPrivate(term)).toBe(false);
    },
  );
});
