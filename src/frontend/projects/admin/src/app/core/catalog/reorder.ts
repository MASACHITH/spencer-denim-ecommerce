import { AdminCategoryNode } from './admin-catalog.model';

/**
 * The arithmetic behind the reorder controls (ADM-11, W1-12), as pure functions.
 *
 * **Why the screen holds a working copy at all.** The API takes a whole sibling list in the order it should
 * end up in, and refuses a list that is not exactly the set it finds (`stale_order`). That is what stops two
 * admins reordering at once from interleaving into an order neither chose. The screen therefore lets an
 * admin make several moves, shows them, and sends each changed list once — rather than firing a request per
 * keypress, which would be the increment-per-row design the API deliberately does not have.
 */

/** Moves one item of a flat list by `delta`, or returns null when it cannot move that way. */
export function moveInArray<T>(items: readonly T[], index: number, delta: number): T[] | null {
  const target = index + delta;
  if (index < 0 || index >= items.length || target < 0 || target >= items.length) {
    return null;
  }

  const moved = [...items];
  [moved[index], moved[target]] = [moved[target], moved[index]];

  return moved;
}

/**
 * Moves a category among its own siblings, anywhere in the tree, carrying its whole subtree with it.
 * Returns a new tree, or null when the category is already at that end of its list.
 *
 * Siblings only, and that is the point: a category changes parent through the edit form's parent field,
 * which is a move the API re-paths a subtree for. Letting "move up" cross into another parent would make a
 * re-parenting happen by accident, from a button whose label says nothing about it.
 */
export function moveWithinSiblings(
  nodes: readonly AdminCategoryNode[],
  categoryId: number,
  delta: number,
): readonly AdminCategoryNode[] | null {
  const index = nodes.findIndex((node) => node.id === categoryId);

  if (index >= 0) {
    return moveInArray(nodes, index, delta);
  }

  for (let i = 0; i < nodes.length; i++) {
    const movedChildren = moveWithinSiblings(nodes[i].children, categoryId, delta);
    if (movedChildren) {
      const next = [...nodes];
      next[i] = { ...nodes[i], children: movedChildren };
      return next;
    }
  }

  return null;
}

/** One sibling list whose order differs from the server's, ready to send. */
export interface ChangedSiblingOrder {
  readonly parentCategoryId: number | null;
  readonly orderedCategoryIds: number[];
}

/**
 * Every sibling list whose order the admin has changed, each as the whole list the API expects.
 *
 * Only the lists that moved: saving an untouched list would be a write with nothing in it, and the API
 * answers it with "nothing changed" and no audit row — but it would still be a request, and a chance to
 * collide with somebody who is reordering that list for real.
 */
export function changedSiblingOrders(
  original: readonly AdminCategoryNode[],
  working: readonly AdminCategoryNode[],
  parentCategoryId: number | null = null,
): ChangedSiblingOrder[] {
  const changed: ChangedSiblingOrder[] = [];

  const originalIds = original.map((node) => node.id);
  const workingIds = working.map((node) => node.id);

  if (originalIds.length !== workingIds.length || originalIds.some((id, i) => id !== workingIds[i])) {
    changed.push({ parentCategoryId, orderedCategoryIds: workingIds });
  }

  const originalById = new Map(original.map((node) => [node.id, node]));
  for (const node of working) {
    const before = originalById.get(node.id);
    if (before) {
      changed.push(...changedSiblingOrders(before.children, node.children, node.id));
    }
  }

  return changed;
}

/** Whether a category can move that way: it has a sibling on that side. */
export function canMove(
  nodes: readonly AdminCategoryNode[],
  categoryId: number,
  delta: number,
): boolean {
  return moveWithinSiblings(nodes, categoryId, delta) !== null;
}
