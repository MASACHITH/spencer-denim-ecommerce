import { CategoryNodeDto, DepartmentDto } from './catalog-departments.model';

/**
 * Placeholder navigation, in the exact shape of `GET /api/catalog/departments`.
 *
 * The names, slugs and tree depth match the catalogue seed data of docs/08 §9, so what the menu
 * shows now is what the API will return. Nothing here is fetched: see the TODO(W1-02) in
 * `NavigationService`.
 */

let nextId = 1;

function category(
  name: string,
  slug: string,
  megaMenuColumn: number | null,
  children: readonly CategoryNodeDto[] = [],
  depth = 1,
): CategoryNodeDto {
  return {
    id: nextId++,
    name,
    slug,
    depth,
    description: null,
    imageUrl: null,
    showInMegaMenu: true,
    megaMenuColumn,
    ageGroupSlug: null,
    children,
  };
}

function child(name: string, slug: string): CategoryNodeDto {
  return category(name, slug, null, [], 2);
}

export const PLACEHOLDER_DEPARTMENTS: readonly DepartmentDto[] = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: 'Everyday essentials and occasion pieces, cut for Sri Lankan weather.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category('Dresses', 'dresses', 1),
      category('Tops & T-Shirts', 'tops-t-shirts', 1),
      category('Shirts & Blouses', 'shirts-blouses', 1),
      category('Jeans', 'jeans', 2),
      category('Trousers', 'trousers', 2),
      category('Skirts', 'skirts', 2),
      category('Traditional Wear', 'traditional-wear', 3),
      category('Activewear', 'activewear', 3),
      category('Footwear', 'footwear', 4),
      category('Accessories', 'accessories', 4),
    ],
  },
  {
    id: 2,
    name: 'Men',
    slug: 'men',
    description: 'Workwear, weekend shirts and denim built to last.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category('Shirts', 'shirts', 1),
      category('T-Shirts & Polos', 't-shirts-polos', 1),
      category('Jeans', 'jeans', 2),
      category('Trousers', 'trousers', 2),
      category('Shorts', 'shorts', 2),
      category('Activewear', 'activewear', 3),
      category('Footwear', 'footwear', 4),
      category('Accessories', 'accessories', 4),
    ],
  },
  {
    id: 3,
    name: 'Kids',
    slug: 'kids',
    description: 'Girls, boys and baby — soft fabrics and room to grow.',
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [
      category('Girls', 'girls', 1, [
        child('Girls Dresses', 'girls-dresses'),
        child('Girls Tops', 'girls-tops'),
        child('Girls Bottoms', 'girls-bottoms'),
      ]),
      category('Boys', 'boys', 2, [
        child('Boys Shirts & T-Shirts', 'boys-shirts-t-shirts'),
        child('Boys Bottoms', 'boys-bottoms'),
      ]),
      category('Baby', 'baby', 3, [
        child('Baby Bodysuits & Sets', 'baby-bodysuits-sets'),
        child('Baby Accessories', 'baby-accessories'),
      ]),
      category('Footwear', 'kids-footwear', 4),
    ],
  },
];
