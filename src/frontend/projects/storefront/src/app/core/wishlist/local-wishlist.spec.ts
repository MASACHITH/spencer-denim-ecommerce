import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { LocalWishlist } from './local-wishlist.service';

/**
 * The per-browser wishlist (WISH-01), and in particular **whether it tells the truth about having
 * saved anything**. Its order, its stored shape and the "absent means say nothing" rule on a save are
 * in `local-wishlist-list.spec.ts`, which needs a stub that really stores.
 *
 * `SafeLocalStorage.write` cannot throw — it catches everything and returns `false` — so the only
 * failure either method can have is a silent one. Both used to discard that boolean, which made the
 * badge, the pressed heart and `has()` all claim a save that had not happened; and because
 * `CartStore.moveToWishlist` destroys a bag line on the strength of it, the lie cost the item.
 */
describe('LocalWishlist (WISH-01)', () => {
  function setUp(writes: boolean, stored: string | null = null): LocalWishlist {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SafeLocalStorage,
          useValue: { read: () => stored, write: () => writes, remove: () => writes },
        },
      ],
    });

    return TestBed.inject(LocalWishlist);
  }

  describe('when storage works', () => {
    it('saves, then unsaves, and says which it did', () => {
      const wishlist = setUp(true);

      expect(wishlist.toggle('linen-shirt')).toBe('saved');
      expect(wishlist.has('linen-shirt')).toBe(true);
      expect(wishlist.count()).toBe(1);

      expect(wishlist.toggle('linen-shirt')).toBe('removed');
      expect(wishlist.has('linen-shirt')).toBe(false);
      expect(wishlist.count()).toBe(0);
    });

    it('save() is idempotent and reports success either way', () => {
      const wishlist = setUp(true);

      expect(wishlist.save('linen-shirt')).toBe(true);
      expect(wishlist.save('linen-shirt')).toBe(true);
      expect(wishlist.count()).toBe(1);
    });
  });

  describe('when storage refuses every write', () => {
    /**
     * The heart on a card or the PDP. Nothing may claim to have saved: not the return value, not
     * `has()`, and not the count the header badge reads.
     */
    it('toggle reports it could not save, and changes nothing', () => {
      const wishlist = setUp(false);

      expect(wishlist.toggle('linen-shirt')).toBe('unavailable');
      expect(wishlist.has('linen-shirt')).toBe(false);
      expect(wishlist.count()).toBe(0);
    });

    it('save reports failure and changes nothing', () => {
      const wishlist = setUp(false);

      expect(wishlist.save('linen-shirt')).toBe(false);
      expect(wishlist.has('linen-shirt')).toBe(false);
      expect(wishlist.count()).toBe(0);
    });

    /**
     * **`save()` must not treat the signal as evidence of persistence.** It used to return `true` on an
     * early `has()` check without attempting a write at all, which is the hole the heart-then-move
     * journey went through. Here the slug is in the stored set this instance read at construction, and
     * writes are refused — so `save()` cannot vouch for anything and must not pretend to.
     */
    it('save re-asserts the write even for a slug it already holds, and reports the refusal', () => {
      const wishlist = setUp(false, JSON.stringify(['linen-shirt']));

      expect(wishlist.has('linen-shirt')).toBe(true);
      expect(wishlist.save('linen-shirt')).toBe(false);
    });

    /** Removing is a write too, so it cannot be claimed either. */
    it('toggle reports it could not remove a product it holds', () => {
      const wishlist = setUp(false, JSON.stringify(['linen-shirt']));

      expect(wishlist.toggle('linen-shirt')).toBe('unavailable');
      expect(wishlist.has('linen-shirt')).toBe(true);
    });
  });
});
