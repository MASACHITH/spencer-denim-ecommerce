import { ProductCardDto } from '../catalog/catalog.model';
import { CategoryNodeDto, DepartmentDto } from '../navigation/catalog-departments.model';

/**
 * The contents of the search suggestions overlay, as data (SRCH-01).
 *
 * Everything here is pure: no DOM, no network, no signals. The combobox component holds the state
 * and draws the markup; this decides what is in the list, in what order, and which option the arrow
 * keys move to. That split is what makes the keyboard behaviour testable without a browser — and
 * the browser suite then drives the real keyboard over the real markup, because a unit test that
 * calls the handler is testing the handler, not the screen.
 */

/** What choosing an option does. */
export type SuggestionKind =
  /** Opens a product page. */
  | 'product'
  /** Opens a category listing. */
  | 'category'
  /** Runs one of this browser's earlier searches again. */
  | 'recent'
  /** A way into the catalogue, offered when there is nothing else to show. */
  | 'browse';

export interface Suggestion {
  /** The DOM id `aria-activedescendant` points at. Unique per combobox instance. */
  readonly id: string;
  readonly kind: SuggestionKind;
  /** What the shopper reads, and what a screen reader announces. */
  readonly label: string;
  /** A second line: the category path for a product, the department for a category. */
  readonly detail: string | null;
  readonly imageUrl: string | null;
  readonly imageSrcSet: string | null;
  /** Where choosing it navigates. Null on a `recent` option, which searches again instead. */
  readonly path: string | null;
  /** The term a `recent` option searches for. Null on everything else. */
  readonly term: string | null;
}

/**
 * One labelled block of options. A `role="group"` inside the `role="listbox"`, which is what lets
 * "Products" and "Categories" be headings a screen reader announces rather than options a shopper
 * can land on with the arrow keys.
 */
export interface SuggestionGroup {
  readonly id: string;
  /** The id of the element that names the group, for its `aria-labelledby`. */
  readonly headingId: string;
  readonly label: string;
  readonly options: readonly Suggestion[];
}

/** Doc 08 §7.6 suggests six products; five leaves room for the categories on a 320 px screen. */
export const PRODUCT_SUGGESTION_LIMIT = 5;

/** Enough to be useful, few enough that the products stay above the fold. */
export const CATEGORY_SUGGESTION_LIMIT = 3;

/** How many products to ask the API for. One more than shown, so the limit is ours, not the page's. */
export const SUGGESTION_PAGE_SIZE = 6;

/** The products matching the term, as options. */
export function productSuggestions(
  cards: readonly ProductCardDto[],
  prefix: string,
): readonly Suggestion[] {
  return cards.slice(0, PRODUCT_SUGGESTION_LIMIT).map((card, index) => ({
    id: `${prefix}-product-${index}`,
    kind: 'product' as const,
    label: card.name,
    detail: `${card.departmentName} › ${card.categoryName}`,
    imageUrl: card.imageUrl,
    imageSrcSet: card.imageSrcSet,
    path: `/product/${card.slug}`,
    term: null,
  }));
}

/**
 * The categories in the cached department tree whose names match every token, best first.
 *
 * It is answered locally rather than by the API, and that is the whole reason the existing search
 * endpoint is enough for SRCH-01: the storefront already holds the department tree for the mega
 * menu, the breadcrumb and the route guards (`NavigationService`), so a category suggestion costs
 * no request at all and is correct the instant a character is typed.
 *
 * A name that *starts* with the first token sorts above one that merely contains it, which is the
 * same relevance idea doc 08 §7.6 applies to products.
 */
export function categorySuggestions(
  departments: readonly DepartmentDto[],
  tokens: readonly string[],
  prefix: string,
): readonly Suggestion[] {
  if (tokens.length === 0) {
    return [];
  }

  const matches: { readonly rank: number; readonly suggestion: Omit<Suggestion, 'id'> }[] = [];

  for (const department of departments) {
    walk(department.categories, `/${department.slug}`, (category, path) => {
      const name = category.name.toLowerCase();
      if (!tokens.every((token) => name.includes(token))) {
        return;
      }

      matches.push({
        rank: name.startsWith(tokens[0]) ? 0 : 1,
        suggestion: {
          kind: 'category',
          label: category.name,
          detail: department.name,
          imageUrl: null,
          imageSrcSet: null,
          path,
          term: null,
        },
      });
    });
  }

  return matches
    // A stable sort, so two categories of the same rank stay in tree order.
    .sort((left, right) => left.rank - right.rank)
    .slice(0, CATEGORY_SUGGESTION_LIMIT)
    .map((match, index) => ({ ...match.suggestion, id: `${prefix}-category-${index}` }));
}

/** This browser's earlier searches, as options that run again. */
export function recentSuggestions(
  terms: readonly string[],
  prefix: string,
): readonly Suggestion[] {
  return terms.map((term, index) => ({
    id: `${prefix}-recent-${index}`,
    kind: 'recent' as const,
    label: term,
    detail: null,
    imageUrl: null,
    imageSrcSet: null,
    path: null,
    term,
  }));
}

/**
 * Ways into the catalogue, for an overlay that has nothing else to put in front of a shopper who
 * has not typed anything and has no history.
 *
 * **This is not "popular searches", and it must never be labelled as if it were.** SRCH-01 asks for
 * popular searches and nothing in this system records what anyone has searched for:
 * `analytics.SearchQueriesDaily` is designed (doc 08 §3, §7.6) and not built, and the reporting
 * milestone that would build it is week 4 (ADM-19/20). A hard-coded list under the word "Popular"
 * would be a fabricated statistic on the client's shop. These are the catalogue's own departments
 * and its two permanent listings — real links, labelled as what they are.
 */
export function browseSuggestions(
  departments: readonly DepartmentDto[],
  prefix: string,
): readonly Suggestion[] {
  const entries = [
    ...departments.map((department) => ({ label: department.name, path: `/${department.slug}` })),
    { label: 'New Arrivals', path: '/new-arrivals' },
    { label: 'Sale', path: '/sale' },
  ];

  return entries.map((entry, index) => ({
    id: `${prefix}-browse-${index}`,
    kind: 'browse' as const,
    label: entry.label,
    detail: null,
    imageUrl: null,
    imageSrcSet: null,
    path: entry.path,
    term: null,
  }));
}

/** Every option across every group, in the order the arrow keys walk them. */
export function flattenSuggestions(groups: readonly SuggestionGroup[]): readonly Suggestion[] {
  return groups.flatMap((group) => group.options);
}

/** The option with that id, or null — including when the id belongs to a list that has changed. */
export function suggestionAt(
  groups: readonly SuggestionGroup[],
  id: string | null,
): Suggestion | null {
  if (id === null) {
    return null;
  }

  return flattenSuggestions(groups).find((option) => option.id === id) ?? null;
}

/**
 * The id the highlight moves to, **wrapping** at both ends.
 *
 * Wrapping rather than stopping, and the choice is written down because it has to be consistent:
 * `ArrowDown` from the last option returns to the first, `ArrowUp` from the first goes to the last,
 * and `ArrowUp` with nothing highlighted goes to the last option — which is how a shopper reaches
 * the bottom of the list in one keystroke. Returns null when there is nothing to highlight.
 */
export function nextSuggestionId(
  groups: readonly SuggestionGroup[],
  currentId: string | null,
  delta: number,
): string | null {
  const options = flattenSuggestions(groups);
  if (options.length === 0) {
    return null;
  }

  const current = options.findIndex((option) => option.id === currentId);
  if (current < 0) {
    return delta > 0 ? options[0].id : options[options.length - 1].id;
  }

  const next = (current + delta + options.length) % options.length;
  return options[next].id;
}

/** One group, or nothing when it has no options — an empty heading is worse than no heading. */
export function group(
  id: string,
  prefix: string,
  label: string,
  options: readonly Suggestion[],
): readonly SuggestionGroup[] {
  return options.length === 0
    ? []
    : [{ id: `${prefix}-${id}`, headingId: `${prefix}-${id}-heading`, label, options }];
}

/** Walks a category tree, handing each node the path that reaches it. */
function walk(
  categories: readonly CategoryNodeDto[],
  parentPath: string,
  visit: (category: CategoryNodeDto, path: string) => void,
): void {
  if (!Array.isArray(categories)) {
    return;
  }

  for (const category of categories) {
    const path = `${parentPath}/${category.slug}`;
    visit(category, path);
    walk(category.children, path, visit);
  }
}
