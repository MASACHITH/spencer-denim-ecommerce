/**
 * The admin catalogue structure payloads (ADM-11, W1-12), field for field as
 * `/api/admin/catalog` sends them.
 *
 * These are **not** the storefront's `DepartmentDto` / `CategoryNodeDto`. That reader drops every row an
 * admin has switched off — `IsActive` is the switch doc 08 §7.1 gives them — so a screen built on it could
 * only ever show the rows that are already live and could never switch one back on.
 */
export interface AdminDepartment {
  readonly id: number;
  readonly name: string;
  readonly slug: string;
  readonly description: string | null;
  readonly heroImageAssetId: number | null;
  readonly sortOrder: number;
  readonly isActive: boolean;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  /** Base64 `rowversion`. Sent back with every write; a stale one is refused (doc 08 §1.5). */
  readonly rowVersion: string;
  readonly categoryCount: number;
  /** Published products this department puts in the shop when it is on. */
  readonly storefrontProductCount: number;
}

/** One node of the admin category tree, active or not. Soft-deleted categories are not in it. */
export interface AdminCategoryNode {
  readonly id: number;
  readonly departmentId: number;
  readonly parentCategoryId: number | null;
  /** The materialised path, e.g. `/3/17/` — the one value that cannot be rebuilt from the screen. */
  readonly path: string;
  readonly depth: number;
  readonly name: string;
  readonly slug: string;
  readonly description: string | null;
  readonly imageAssetId: number | null;
  readonly bannerAssetId: number | null;
  readonly bannerLinkUrl: string | null;
  readonly ageGroupId: number | null;
  readonly sizeGuideId: number | null;
  readonly sortOrder: number;
  readonly showInMegaMenu: boolean;
  readonly megaMenuColumn: number | null;
  readonly isActive: boolean;
  readonly seoTitle: string | null;
  readonly seoDescription: string | null;
  readonly rowVersion: string;
  /** Products linked here, published or not. This is what the remove guard counts. */
  readonly productCount: number;
  /** Products whose primary category this is. Deactivating hides exactly these. */
  readonly primaryProductCount: number;
  readonly children: readonly AdminCategoryNode[];
}

export interface AdminCategoryTree {
  readonly departmentId: number;
  readonly departmentName: string;
  readonly departmentSlug: string;
  readonly departmentIsActive: boolean;
  readonly categories: readonly AdminCategoryNode[];
}

/**
 * What switching a category off will do, read before the button is pressed.
 *
 * The three numbers are three different things. `IsActive` is per row and the storefront listing checks
 * only a product's *own* primary category, so deactivating a parent does not take its children's products
 * out of the shop — it takes every menu route to them away instead. That is
 * `descendantProductsLosingTheirMenuRoute`, and it is the consequence an admin is least likely to predict.
 */
export interface CategoryDeactivationImpact {
  readonly categoryId: number;
  readonly name: string;
  readonly isActive: boolean;
  readonly productsLeavingTheStorefront: number;
  readonly descendantCategories: number;
  readonly descendantProductsLosingTheirMenuRoute: number;
  readonly examples: readonly string[];
}

/**
 * The body of a create, and — with `rowVersion` — of an edit.
 *
 * **It carries every writable column, including the ones the screen has no control for.** `PUT` replaces
 * the row, so a field the body leaves out is a field the API is told to clear. The tile image and the
 * banner have no picker yet (ADM-18's media library has no listing endpoint), and leaving them out of this
 * type is how a save silently wiped them — with an audit row recording a member of staff doing it. A
 * missing control is a reason to round-trip a value, never a reason to drop it.
 */
export interface CategoryFormValues {
  parentCategoryId: number | null;
  name: string;
  slug: string;
  description: string;
  /** No picker yet (ADM-18). Carried through so a save cannot clear it. */
  imageAssetId: number | null;
  /** No picker yet (ADM-18). Carried through so a save cannot clear it. */
  bannerAssetId: number | null;
  bannerLinkUrl: string;
  ageGroupId: number | null;
  sizeGuideId: number | null;
  showInMegaMenu: boolean;
  megaMenuColumn: number | null;
  seoTitle: string;
  seoDescription: string;
}

export function emptyCategoryForm(parentCategoryId: number | null = null): CategoryFormValues {
  return {
    parentCategoryId,
    name: '',
    slug: '',
    description: '',
    imageAssetId: null,
    bannerAssetId: null,
    bannerLinkUrl: '',
    ageGroupId: null,
    sizeGuideId: null,
    // The column's default (doc 08 §3.2): a new category is listed unless the admin says otherwise.
    showInMegaMenu: true,
    megaMenuColumn: null,
    seoTitle: '',
    seoDescription: '',
  };
}

export function categoryFormFrom(node: AdminCategoryNode): CategoryFormValues {
  return {
    parentCategoryId: node.parentCategoryId,
    name: node.name,
    slug: node.slug,
    description: node.description ?? '',
    imageAssetId: node.imageAssetId,
    bannerAssetId: node.bannerAssetId,
    bannerLinkUrl: node.bannerLinkUrl ?? '',
    ageGroupId: node.ageGroupId,
    sizeGuideId: node.sizeGuideId,
    showInMegaMenu: node.showInMegaMenu,
    megaMenuColumn: node.megaMenuColumn,
    seoTitle: node.seoTitle ?? '',
    seoDescription: node.seoDescription ?? '',
  };
}

/**
 * The department edit form.
 *
 * Doc 08 §3.2: there is no create and no delete — the nav is fixed. **And no name and no slug**, by the
 * owner's decision of 2026-10-02 confirming doc 11 P1 step 1: staff edit "their description, hero image,
 * sort order and SEO" and nothing else. A department slug is the first segment of every listing URL in its
 * department and redirects are deferred, so an editable one is a third of the shop's links waiting to
 * break. The W1-02 follow-up it appeared to serve needs no editable field: `Department.Slug` has had a
 * write path since W1-02 — the seeder — and `CatalogSlug` now guards it in the property setter with
 * `CK_Departments_Slug` behind that.
 *
 * `heroImageAssetId` is here with no control to set it, for the reason {@link CategoryFormValues} gives.
 */
export interface DepartmentFormValues {
  description: string;
  /** No picker yet (ADM-18). Carried through so a save cannot clear it. */
  heroImageAssetId: number | null;
  seoTitle: string;
  seoDescription: string;
}

export function departmentFormFrom(department: AdminDepartment): DepartmentFormValues {
  return {
    description: department.description ?? '',
    heroImageAssetId: department.heroImageAssetId,
    seoTitle: department.seoTitle ?? '',
    seoDescription: department.seoDescription ?? '',
  };
}

/** One choice in a picker: the id to send back and the name to show. */
export interface CatalogOption {
  readonly id: number;
  readonly name: string;
}

/**
 * The lists a category form chooses from (doc 11 P1 step 2).
 *
 * Not deferred with the image pickers, because the argument for deferring those does not reach these: a
 * tile image needs ADM-18's media library, while an age group is four seeded rows and a size guide is
 * three. Without them a Kids category made here could never be given an age group, and a new category
 * would silently get no size guide — which breaks PDP-04 for every product in it with no override.
 */
export interface CategoryFormOptions {
  readonly ageGroups: readonly CatalogOption[];
  readonly sizeGuides: readonly CatalogOption[];
}

/** The RFC 9457 validation body the API returns for a rejected field, keyed by the field's own name. */
export interface ValidationProblemDetails {
  readonly title?: string;
  readonly errors?: Record<string, string[]>;
}

/** Every node of a tree, flattened depth-first, so a screen can walk it without recursion. */
export function flatten(nodes: readonly AdminCategoryNode[]): AdminCategoryNode[] {
  const all: AdminCategoryNode[] = [];

  for (const node of nodes) {
    all.push(node);
    all.push(...flatten(node.children));
  }

  return all;
}

/** The node with that id, anywhere in the tree, or null. */
export function findNode(
  nodes: readonly AdminCategoryNode[],
  id: number,
): AdminCategoryNode | null {
  for (const node of nodes) {
    if (node.id === id) {
      return node;
    }

    const found = findNode(node.children, id);
    if (found) {
      return found;
    }
  }

  return null;
}
