import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { LocalWishlist } from './local-wishlist.service';

/**
 * The saved **list** (W2-08, WISH-01): its order, its shape in storage, what a save that is silent
 * about a field does to it, and what happens when a browser hands back something odd.
 *
 * `local-wishlist.spec.ts` covers the other half — whether the service ever claims a save it did not
 * make — and uses a storage stub that reports success without storing, which is the right stub for
 * that question and the wrong one for this. Everything here needs the written value read back, so the
 * stub below really stores.
 */
describe('LocalWishlist, the saved list itself (WISH-01)', () => {
  function setUp(stored: string | null = null): {
    wishlist: LocalWishlist;
    stored: () => string | null;
  } {
    let value = stored;

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SafeLocalStorage,
          useValue: {
            read: () => value,
            write: (_key: string, next: string) => {
              value = next;
              return true;
            },
            remove: () => {
              value = null;
              return true;
            },
          },
        },
      ],
    });

    return { wishlist: TestBed.inject(LocalWishlist), stored: () => value };
  }

  /** Storage that refuses every write while allowing `removeItem`, which is the shape `clear` relies on. */
  function refusing(stored: string): LocalWishlist {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SafeLocalStorage,
          useValue: { read: () => stored, write: () => false, remove: () => true },
        },
      ],
    });

    return TestBed.inject(LocalWishlist);
  }

  describe('order and membership', () => {
    it('keeps the most recently saved first, which is the order the page draws', () => {
      const { wishlist } = setUp();

      wishlist.save('first');
      wishlist.save('second');
      wishlist.save('third');

      expect(wishlist.slugs()).toEqual(['third', 'second', 'first']);
      expect(wishlist.count()).toBe(3);
    });

    /**
     * Re-saving must not reorder the shopper's list under them. `save` re-asserts the write every time
     * on purpose (the W2-09 hardening: the signal is not evidence of persistence), so a list that
     * jumped on every re-press would be unusable.
     */
    it('does not move an entry to the front when it is saved again', () => {
      const { wishlist } = setUp();

      wishlist.save('first');
      wishlist.save('second');
      wishlist.save('first');

      expect(wishlist.slugs()).toEqual(['second', 'first']);
    });

    it('removes one entry and leaves the rest in order', () => {
      const { wishlist } = setUp();

      wishlist.save('a');
      wishlist.save('b');
      wishlist.save('c');

      expect(wishlist.remove('b')).toBe(true);
      expect(wishlist.slugs()).toEqual(['c', 'a']);
    });

    /**
     * Removing something that is not there is the end state the caller asked for, so it is success. A
     * second tap on Remove must not read as a storage failure and tell the shopper their browser is
     * blocking site data.
     */
    it('reports success when removing something that was never saved', () => {
      const { wishlist } = setUp();

      expect(wishlist.remove('never-saved')).toBe(true);
      expect(wishlist.count()).toBe(0);
    });

    it('clears everything', () => {
      const { wishlist } = setUp();

      wishlist.save('a');
      wishlist.save('b');

      expect(wishlist.clear()).toBe(true);
      expect(wishlist.count()).toBe(0);
      expect(wishlist.entries()).toEqual([]);
    });

    /**
     * **Clear writes an empty list rather than removing the key**, because a write is the only thing
     * that finds out whether storage is working, and this caller is about to tell the shopper their
     * wishlist is empty. The stub here refuses `write` and allows `remove`, so a `removeItem`
     * implementation would have reported success over a wishlist that was still full.
     */
    it('reports failure when clearing is refused, and keeps everything saved', () => {
      const wishlist = refusing(JSON.stringify({ v: 2, items: [{ slug: 'linen-shirt' }] }));

      expect(wishlist.clear()).toBe(false);
      expect(wishlist.count()).toBe(1);
    });

    it('reports failure when a removal is refused, and keeps the item saved', () => {
      const wishlist = refusing(JSON.stringify({ v: 2, items: [{ slug: 'linen-shirt' }] }));

      expect(wishlist.remove('linen-shirt')).toBe(false);
      expect(wishlist.has('linen-shirt')).toBe(true);
    });
  });

  describe('a save that does not mention a field says nothing about it (W1-12)', () => {
    /**
     * **W1-12's defect in the shape it would take here.** That task's category save did not mention the
     * image columns and cleared every one of them, past 1,571 passing tests, because every fixture had
     * those columns null. The fixture here deliberately has **real data** in the field the second save
     * is silent about: the bag's "move to wishlist" records a colourway, and a listing card's heart on
     * the same product knows the name and nothing about a colour.
     */
    it('a card heart on a product saved from the bag keeps the colourway', () => {
      const { wishlist } = setUp();

      wishlist.save({
        slug: 'linen-shirt',
        name: 'Relaxed Linen Blend Shirt',
        colourId: 2,
        colourName: 'Beige',
      });

      // What `ProductCard.toggleWishlist` sends: the name, and no colour field at all.
      expect(wishlist.save({ slug: 'linen-shirt', name: 'Relaxed Linen Blend Shirt' })).toBe(true);

      expect(wishlist.entry('linen-shirt')).toMatchObject({
        name: 'Relaxed Linen Blend Shirt',
        colourId: 2,
        colourName: 'Beige',
      });
    });

    /** An explicit null is the other half of the contract: it really does clear the field. */
    it('an explicit null clears the colourway', () => {
      const { wishlist } = setUp();

      wishlist.save({ slug: 'linen-shirt', colourId: 2, colourName: 'Beige' });
      wishlist.save({ slug: 'linen-shirt', colourId: null, colourName: null });

      expect(wishlist.entry('linen-shirt')).toMatchObject({ colourId: null, colourName: null });
    });

    it('keeps the stored name when a save mentions only a colourway', () => {
      const { wishlist } = setUp();

      wishlist.save({ slug: 'linen-shirt', name: 'Relaxed Linen Blend Shirt' });
      wishlist.save({ slug: 'linen-shirt', colourId: 7, colourName: 'Indigo' });

      expect(wishlist.entry('linen-shirt')).toMatchObject({
        name: 'Relaxed Linen Blend Shirt',
        colourId: 7,
      });
    });

    it('keeps the original save time through a re-save', () => {
      const { wishlist } = setUp(
        JSON.stringify({ v: 2, items: [{ slug: 'linen-shirt', addedAt: 1000 }] }),
      );

      wishlist.save({ slug: 'linen-shirt', name: 'Relaxed Linen Blend Shirt' });

      expect(wishlist.entry('linen-shirt')?.addedAt).toBe(1000);
    });
  });

  describe('what a browser hands back (lesson L1)', () => {
    /**
     * **The version-1 shape is read, not discarded.** Until W2-08 the stored value was a bare array of
     * slugs, and shoppers have been tapping the heart since W2-04. Throwing those away on the day the
     * wishlist page shipped would have emptied every existing shopper's wishlist.
     */
    it('reads a version-1 array of slugs, keeping its order', () => {
      const { wishlist } = setUp(JSON.stringify(['linen-shirt', 'denim-jacket']));

      expect(wishlist.slugs()).toEqual(['linen-shirt', 'denim-jacket']);
      expect(wishlist.entry('linen-shirt')).toMatchObject({
        name: null,
        colourId: null,
        addedAt: 0,
      });
    });

    /**
     * **The version-1 branch de-duplicates, which it did not** (round 2, m2). The version-2 branch has
     * had a `seen` set from the start, with a reason written beside it; the version-1 array had none, so
     * a repeated slug became two rows for one product — and the first Remove press destroyed **both**,
     * because `without()` filters every match. A normally-written version-1 list cannot hold a duplicate
     * (it was serialised from a `Set`), but this method's whole premise is that the value came from
     * another tab, an older build or a curious visitor.
     */
    it('reads a duplicated slug in a version-1 array as one entry', () => {
      const { wishlist } = setUp(
        JSON.stringify(['test-product-4', 'test-product-4', 'test-product-5']),
      );

      expect(wishlist.slugs()).toEqual(['test-product-4', 'test-product-5']);
      expect(wishlist.count()).toBe(2);
    });

    /**
     * **One Remove press takes one product, not two rows at once.**
     *
     * The assertion that matters is the *delta*, not the end state: `without()` filters every match, so
     * the list after the press is `['b']` either way. Without de-duplication the shopper saw three rows,
     * pressed Remove once and watched two of them go — which is why this counts before as well as
     * after. A test that only checked the end state would have passed over the defect, and the first
     * version of this one did.
     */
    it('removes one product from a de-duplicated version-1 list', () => {
      const { wishlist } = setUp(JSON.stringify(['a', 'a', 'b']));

      const before = wishlist.count();
      expect(wishlist.remove('a')).toBe(true);

      expect(before).toBe(2);
      expect(before - wishlist.count()).toBe(1);
      expect(wishlist.slugs()).toEqual(['b']);
    });

    it('upgrades the stored shape on the first write', () => {
      const { wishlist, stored } = setUp(JSON.stringify(['linen-shirt']));

      wishlist.save({ slug: 'denim-jacket', name: 'Denim Jacket' });

      expect(JSON.parse(stored() ?? 'null')).toMatchObject({ v: 2 });
      expect(wishlist.slugs()).toEqual(['denim-jacket', 'linen-shirt']);
    });

    it.each([
      ['a JSON object that is not a wishlist', '{"nope":true}'],
      ['a string', '"linen-shirt"'],
      ['a number', '42'],
      ['truncated JSON', '{"v":2,"items":['],
      ['items that is not an array', '{"v":2,"items":{"slug":"x"}}'],
      ['null', 'null'],
    ])('reads %s as an empty wishlist', (_what, raw) => {
      const { wishlist } = setUp(raw);

      expect(wishlist.count()).toBe(0);
    });

    it('drops entries with no usable slug and keeps the rest', () => {
      const { wishlist } = setUp(
        JSON.stringify({
          v: 2,
          items: [{ slug: 'keeper' }, { slug: '   ' }, { slug: 42 }, null, 'nope', { name: 'x' }],
        }),
      );

      expect(wishlist.slugs()).toEqual(['keeper']);
    });

    /** Doc 11 §553: one entry per product. A duplicate in storage must not be counted twice. */
    it('reads a duplicated slug as one entry', () => {
      const { wishlist } = setUp(
        JSON.stringify({ v: 2, items: [{ slug: 'linen-shirt' }, { slug: 'linen-shirt' }] }),
      );

      expect(wishlist.count()).toBe(1);
    });

    it('replaces a junk label with null rather than failing the whole entry', () => {
      const { wishlist } = setUp(
        JSON.stringify({
          v: 2,
          items: [
            { slug: 'linen-shirt', name: 42, colourId: 'two', colourName: [], addedAt: 'now' },
          ],
        }),
      );

      expect(wishlist.entry('linen-shirt')).toEqual({
        slug: 'linen-shirt',
        name: null,
        colourId: null,
        colourName: null,
        addedAt: 0,
      });
    });

    /** Nothing a newer build of the site leaves in the stored shape travels on into a signal. */
    it('does not carry unknown stored fields into an entry', () => {
      const { wishlist } = setUp(
        JSON.stringify({ v: 3, items: [{ slug: 'linen-shirt', sizeId: 9, note: 'hello' }] }),
      );

      expect(Object.keys(wishlist.entry('linen-shirt') ?? {}).sort()).toEqual([
        'addedAt',
        'colourId',
        'colourName',
        'name',
        'slug',
      ]);
    });
  });
});
