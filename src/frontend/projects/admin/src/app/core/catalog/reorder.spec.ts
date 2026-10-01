import { AdminCategoryNode } from './admin-catalog.model';
import { canMove, changedSiblingOrders, moveInArray, moveWithinSiblings } from './reorder';

/**
 * The arithmetic the reorder buttons rest on (ADM-11, W1-12).
 *
 * It is tested without a component because what matters is the shape of what gets sent: the API refuses a
 * sibling list that is not exactly the set it finds, so a screen that computed the wrong list would be told
 * "stale_order" and an admin would be told to reload a page that was never stale.
 */
describe('reorder', () => {
  function node(
    id: number,
    depth: number,
    path: string,
    parentCategoryId: number | null,
    children: AdminCategoryNode[] = [],
  ): AdminCategoryNode {
    return {
      id,
      departmentId: 1,
      parentCategoryId,
      path,
      depth,
      name: `Category ${id}`,
      slug: `category-${id}`,
      description: null,
      imageAssetId: null,
      bannerAssetId: null,
      bannerLinkUrl: null,
      ageGroupId: null,
      sizeGuideId: null,
      sortOrder: 0,
      showInMegaMenu: true,
      megaMenuColumn: null,
      isActive: true,
      seoTitle: null,
      seoDescription: null,
      rowVersion: `rv-${id}`,
      productCount: 0,
      primaryProductCount: 0,
      children,
    };
  }

  /** /1/ (with /1/11/ and /1/12/ under it) and /2/. */
  function tree(): AdminCategoryNode[] {
    return [
      node(1, 1, '/1/', null, [node(11, 2, '/1/11/', 1), node(12, 2, '/1/12/', 1)]),
      node(2, 1, '/2/', null),
    ];
  }

  describe('moveInArray', () => {
    it('swaps an item with its neighbour', () => {
      expect(moveInArray(['a', 'b', 'c'], 1, -1)).toEqual(['b', 'a', 'c']);
      expect(moveInArray(['a', 'b', 'c'], 1, 1)).toEqual(['a', 'c', 'b']);
    });

    it('refuses to move past either end rather than wrapping round', () => {
      expect(moveInArray(['a', 'b'], 0, -1)).toBeNull();
      expect(moveInArray(['a', 'b'], 1, 1)).toBeNull();
    });

    it('leaves the original alone', () => {
      const items = ['a', 'b'];
      moveInArray(items, 0, 1);
      expect(items).toEqual(['a', 'b']);
    });
  });

  describe('moveWithinSiblings', () => {
    it('reorders a top-level pair', () => {
      const moved = moveWithinSiblings(tree(), 2, -1);

      expect(moved?.map((n) => n.id)).toEqual([2, 1]);
    });

    it('reorders children deep in the tree without touching their parent', () => {
      const moved = moveWithinSiblings(tree(), 12, -1);

      expect(moved?.map((n) => n.id)).toEqual([1, 2]);
      expect(moved?.[0].children.map((n) => n.id)).toEqual([12, 11]);
    });

    /**
     * A category changes parent through the edit form, which re-paths its subtree. "Move up" from the top of
     * a list must therefore do nothing at all, rather than promoting it a level — a re-parenting from a
     * button whose label says "up" is a move an admin did not ask for.
     */
    it('does nothing at the top of a sibling list rather than crossing into the parent', () => {
      expect(moveWithinSiblings(tree(), 11, -1)).toBeNull();
      expect(moveWithinSiblings(tree(), 12, 1)).toBeNull();
    });

    it('carries a whole subtree with the category that moved', () => {
      const deep = [
        node(1, 1, '/1/', null, [node(11, 2, '/1/11/', 1, [node(111, 3, '/1/11/111/', 11)])]),
        node(2, 1, '/2/', null),
      ];

      const moved = moveWithinSiblings(deep, 1, 1);

      expect(moved?.map((n) => n.id)).toEqual([2, 1]);
      expect(moved?.[1].children[0].children[0].id).toBe(111);
    });

    it('returns null for an id that is not in the tree', () => {
      expect(moveWithinSiblings(tree(), 999, -1)).toBeNull();
    });
  });

  describe('canMove', () => {
    it('matches what moveWithinSiblings would do', () => {
      const nodes = tree();

      expect(canMove(nodes, 1, -1)).toBe(false);
      expect(canMove(nodes, 1, 1)).toBe(true);
      expect(canMove(nodes, 11, -1)).toBe(false);
      expect(canMove(nodes, 11, 1)).toBe(true);
    });
  });

  describe('changedSiblingOrders', () => {
    it('finds nothing when nothing moved', () => {
      expect(changedSiblingOrders(tree(), tree())).toEqual([]);
    });

    it('names the one list that moved, as the whole list', () => {
      const working = moveWithinSiblings(tree(), 2, -1)!;

      expect(changedSiblingOrders(tree(), working)).toEqual([
        { parentCategoryId: null, orderedCategoryIds: [2, 1] },
      ]);
    });

    it('names a child list by its parent', () => {
      const working = moveWithinSiblings(tree(), 12, -1)!;

      expect(changedSiblingOrders(tree(), working)).toEqual([
        { parentCategoryId: 1, orderedCategoryIds: [12, 11] },
      ]);
    });

    /**
     * Two moves in different lists are two decisions, so they are two requests and two audit rows — not one
     * request carrying a tree, which the API has no route for.
     */
    it('names every list that moved when several did', () => {
      const once = moveWithinSiblings(tree(), 2, -1)!;
      const twice = moveWithinSiblings(once, 12, -1)!;

      expect(changedSiblingOrders(tree(), twice)).toEqual([
        { parentCategoryId: null, orderedCategoryIds: [2, 1] },
        { parentCategoryId: 1, orderedCategoryIds: [12, 11] },
      ]);
    });

    it('does not report a list whose children merely moved underneath it', () => {
      const working = moveWithinSiblings(tree(), 12, -1)!;
      const changed = changedSiblingOrders(tree(), working);

      expect(changed.map((list) => list.parentCategoryId)).toEqual([1]);
    });
  });
});
