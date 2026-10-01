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
  it.each(['someone@example.com', '0771234567', '077-123-4567', 'ORD 2026 0115 001'])(
    '%p is not kept',
    (term) => {
      expect(isPrivate(term)).toBe(true);
    },
  );

  it.each(['linen shirt', '32 waist', 'size 10 dress'])('%p is kept', (term) => {
    expect(isPrivate(term)).toBe(false);
  });
});
