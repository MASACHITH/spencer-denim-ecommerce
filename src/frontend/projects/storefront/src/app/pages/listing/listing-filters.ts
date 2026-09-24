import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';
import {
  AGE_GROUP_OPTIONS,
  COLOUR_OPTIONS,
  DISCOUNT_OPTIONS,
  FILTER_GROUP_LABEL,
  FIT_OPTIONS,
  FilterOption,
  MATERIAL_OPTIONS,
  OCCASION_OPTIONS,
  PRICE_BANDS,
  RATING_OPTIONS,
  SizeScale,
  sizeScalesFor,
} from '../../core/catalog/filter-options';
import {
  ListFilterKey,
  ListingState,
  matchedPriceBand,
  toggleListValue,
  withFilter,
} from '../../core/catalog/listing-state';

/** One category the panel offers, flattened out of the tree with its depth kept for indenting. */
interface CategoryChoice {
  readonly slug: string;
  readonly name: string;
  readonly depth: number;
}

/** A group of checkboxes, so the filter groups are declared as data rather than as markup. */
interface CheckboxGroup {
  readonly key: ListFilterKey;
  readonly legend: string;
  readonly options: readonly FilterOption[];
  readonly swatches: boolean;
  readonly open: boolean;
}

let nextPanelId = 0;

/**
 * The PLP-02 filter controls: the desktop sidebar and the contents of the mobile drawer, which
 * are the same component rendered once (the page decides which container it goes in).
 *
 * It owns no state. Every control reports the state it would produce through {@link changed}, and
 * the page turns that into a URL — which is the only place the listing's state lives. That is
 * what makes a filtered listing shareable and what makes Back undo a filter.
 *
 * Department and category are radio groups rather than checkboxes because the API takes one of
 * each (`ProductListQuery.DepartmentSlug`, `CategorySlug` are single values), and because they
 * are the page's own address: choosing one navigates. On New Arrivals and Sale neither is
 * offered, because those listings have no department to be in.
 *
 * Native `fieldset`/`legend` and real `input` elements throughout: a checkbox that is a `div`
 * has to have its name, its state and its keyboard behaviour rebuilt by hand, and this page has
 * dozens of them.
 */
@Component({
  selector: 'sd-listing-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="filters">
      @if (showsPlacement() && departments().length > 0) {
        <details class="filters__group" open>
          <summary class="filters__summary">Department</summary>
          <fieldset class="filters__set">
            <legend class="sr-only">Department</legend>
            @for (department of departments(); track department.slug) {
              <label class="opt">
                <input
                  type="radio"
                  [name]="id + '-department'"
                  [value]="department.slug"
                  [checked]="state().department === department.slug"
                  (change)="chooseDepartment(department.slug)"
                />
                <span>{{ department.name }}</span>
              </label>
            }
          </fieldset>
        </details>
      }

      @if (showsPlacement() && categories().length > 0) {
        <details class="filters__group" open>
          <summary class="filters__summary">Category</summary>
          <fieldset class="filters__set">
            <legend class="sr-only">Category</legend>
            <label class="opt">
              <input
                type="radio"
                [name]="id + '-category'"
                value=""
                [checked]="state().category === null"
                (change)="chooseCategory(null)"
              />
              <span>All {{ departmentName() }}</span>
            </label>
            @for (category of categories(); track category.slug) {
              <label class="opt" [class.opt--child]="category.depth > 1">
                <input
                  type="radio"
                  [name]="id + '-category'"
                  [value]="category.slug"
                  [checked]="state().category === category.slug"
                  (change)="chooseCategory(category.slug)"
                />
                <span>{{ category.name }}</span>
              </label>
            }
          </fieldset>
        </details>
      }

      <details class="filters__group" open>
        <summary class="filters__summary">Price</summary>
        <fieldset class="filters__set">
          <legend class="sr-only">Price</legend>
          <label class="opt">
            <input
              type="radio"
              [name]="id + '-price'"
              value=""
              [checked]="priceBand() === null"
              (change)="choosePrice(null)"
            />
            <span>Any price</span>
          </label>
          @for (band of priceBands; track band.id) {
            <label class="opt">
              <input
                type="radio"
                [name]="id + '-price'"
                [value]="band.id"
                [checked]="priceBand() === band.id"
                (change)="choosePrice(band.id)"
              />
              <span>{{ band.label }}</span>
            </label>
          }
        </fieldset>
      </details>

      <!-- Sizes are grouped by scale, each its own fieldset: "36" is a women's waist and a shoe,
           and a flat list of labels says which is which to nobody. -->
      <details class="filters__group" open>
        <summary class="filters__summary">{{ sizeLegend }}</summary>
        <fieldset class="filters__set filters__set--scales">
          <legend class="sr-only">{{ sizeLegend }}</legend>
          @for (scale of sizeScales(); track scale.name) {
            <fieldset class="filters__scale">
              <legend class="filters__scale-legend">{{ scale.name }}</legend>
              <div class="filters__scale-options">
                @for (option of scale.options; track option.value) {
                  <label class="opt">
                    <input
                      type="checkbox"
                      [checked]="isChecked('sizes', option.value)"
                      (change)="toggle('sizes', option.value)"
                    />
                    <span>{{ option.label }}</span>
                  </label>
                }
              </div>
            </fieldset>
          }
        </fieldset>
      </details>

      @for (group of checkboxGroups; track group.key) {
        <details class="filters__group" [open]="group.open || isGroupActive(group.key)">
          <summary class="filters__summary">{{ group.legend }}</summary>
          <fieldset class="filters__set" [class.filters__set--swatches]="group.swatches">
            <legend class="sr-only">{{ group.legend }}</legend>
            @for (option of group.options; track option.value) {
              <label class="opt" [class.opt--swatch]="group.swatches">
                <input
                  type="checkbox"
                  [checked]="isChecked(group.key, option.value)"
                  (change)="toggle(group.key, option.value)"
                />
                @if (group.swatches) {
                  <span class="opt__dot" [style.--dot]="option.hex" aria-hidden="true"></span>
                }
                <span>{{ option.label }}</span>
              </label>
            }
          </fieldset>
        </details>
      }

      <details class="filters__group" [open]="state().minRating !== null">
        <summary class="filters__summary">Rating</summary>
        <fieldset class="filters__set">
          <legend class="sr-only">Minimum rating</legend>
          <label class="opt">
            <input
              type="radio"
              [name]="id + '-rating'"
              value=""
              [checked]="state().minRating === null"
              (change)="chooseRating(null)"
            />
            <span>Any rating</span>
          </label>
          @for (option of ratingOptions; track option.value) {
            <label class="opt">
              <input
                type="radio"
                [name]="id + '-rating'"
                [value]="option.value"
                [checked]="state().minRating === +option.value"
                (change)="chooseRating(+option.value)"
              />
              <span>{{ option.label }}</span>
            </label>
          }
        </fieldset>
      </details>

      <details class="filters__group" [open]="state().inStock">
        <summary class="filters__summary">Availability</summary>
        <fieldset class="filters__set">
          <legend class="sr-only">Availability</legend>
          <label class="opt">
            <input type="checkbox" [checked]="state().inStock" (change)="toggleInStock()" />
            <span>In stock only</span>
          </label>
        </fieldset>
      </details>

      @if (showsDiscount()) {
        <details class="filters__group" [open]="state().onSale">
          <summary class="filters__summary">Discount</summary>
          <fieldset class="filters__set">
            <legend class="sr-only">Discount</legend>
            <label class="opt">
              <input
                type="radio"
                [name]="id + '-discount'"
                value=""
                [checked]="!state().onSale"
                (change)="chooseDiscount(null)"
              />
              <span>Any</span>
            </label>
            @for (option of discountOptions; track option.value) {
              <label class="opt">
                <input
                  type="radio"
                  [name]="id + '-discount'"
                  [value]="option.value"
                  [checked]="discountChoice() === option.value"
                  (change)="chooseDiscount(+option.value)"
                />
                <span>{{ option.label }}</span>
              </label>
            }
          </fieldset>
        </details>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .filters__group {
      border-bottom: 1px solid var(--color-border);
    }
    .filters__summary {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1);
      /* PLP-02 is a touch surface first: every row of this panel is a 44px target. */
      min-height: var(--tap);
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    /* display:flex drops the native disclosure triangle, so the chevron is drawn back on:
       a group that collapses has to look like one. */
    .filters__summary::marker,
    .filters__summary::-webkit-details-marker {
      display: none;
      content: '';
    }
    .filters__summary::after {
      content: '';
      flex: none;
      width: 8px;
      height: 8px;
      margin-inline-end: 4px;
      border-right: 1.5px solid currentColor;
      border-bottom: 1.5px solid currentColor;
      transform: translateY(-2px) rotate(45deg);
      transition: transform var(--duration) var(--ease);
    }
    .filters__group[open] > .filters__summary::after {
      transform: translateY(2px) rotate(-135deg);
    }
    .filters__set {
      display: grid;
      padding: 0 0 var(--space-1-5);
      border: 0;
      min-width: 0;
    }
    .filters__set--swatches {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0 var(--space-1);
    }
    .filters__scale {
      border: 0;
      padding: 0;
      min-width: 0;
    }
    .filters__scale-legend {
      padding: var(--space-1) 0 0;
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .filters__scale-options {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0 var(--space-1);
    }
    .opt {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      font-size: var(--text-small);
      cursor: pointer;
      min-width: 0;
    }
    .opt > span {
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .opt--child {
      padding-inline-start: var(--space-2);
      color: var(--color-text-muted);
    }
    .opt input {
      flex: none;
      width: 18px;
      height: 18px;
      accent-color: var(--color-primary);
      cursor: pointer;
    }
    .opt__dot {
      flex: none;
      width: 14px;
      height: 14px;
      border-radius: var(--radius-round);
      background: var(--dot);
      /* The strong edge unconditionally: white and cream are invisible on a white panel. */
      border: 1px solid var(--color-border-strong);
    }
  `,
})
export class ListingFilters {
  readonly state = input.required<ListingState>();
  readonly departments = input.required<readonly DepartmentDto[]>();

  /**
   * Which filter this listing's address already fixes, if any. New Arrivals and Sale have no
   * department, and their own filter is not something a shopper can take off here.
   */
  readonly fixedFilter = input<'newArrivals' | 'discount' | null>(null);

  /** The state the shopper's last change would produce. The page decides what to do with it. */
  readonly changed = output<ListingState>();

  /** Radio groups need a name that is unique per instance. */
  protected readonly id = `sd-filters-${nextPanelId++}`;

  protected readonly priceBands = PRICE_BANDS;
  protected readonly ratingOptions = RATING_OPTIONS;
  protected readonly discountOptions = DISCOUNT_OPTIONS;
  protected readonly sizeLegend = FILTER_GROUP_LABEL.sizes;

  protected readonly checkboxGroups: readonly CheckboxGroup[] = [
    {
      key: 'colours',
      legend: FILTER_GROUP_LABEL.colours,
      options: COLOUR_OPTIONS,
      swatches: true,
      open: true,
    },
    {
      key: 'ageGroups',
      legend: FILTER_GROUP_LABEL.ageGroups,
      options: AGE_GROUP_OPTIONS,
      swatches: false,
      open: false,
    },
    {
      key: 'materials',
      legend: FILTER_GROUP_LABEL.materials,
      options: MATERIAL_OPTIONS,
      swatches: false,
      open: false,
    },
    { key: 'fits', legend: FILTER_GROUP_LABEL.fits, options: FIT_OPTIONS, swatches: false, open: false },
    {
      key: 'occasions',
      legend: FILTER_GROUP_LABEL.occasions,
      options: OCCASION_OPTIONS,
      swatches: false,
      open: false,
    },
  ];

  /** A listing that is a department shows where it sits; New Arrivals and Sale do not. */
  protected readonly showsPlacement = computed(() => this.fixedFilter() === null);

  /** Sale is already the discount filter; offering it again would let a shopper unset the page. */
  protected readonly showsDiscount = computed(() => this.fixedFilter() !== 'discount');

  protected readonly sizeScales = computed<readonly SizeScale[]>(() =>
    sizeScalesFor(this.state().department),
  );

  private readonly department = computed(() =>
    this.departments().find((candidate) => candidate.slug === this.state().department),
  );

  protected readonly departmentName = computed(() => this.department()?.name ?? 'products');

  protected readonly categories = computed<readonly CategoryChoice[]>(() =>
    flatten(this.department()?.categories ?? []),
  );

  protected readonly priceBand = computed(() => matchedPriceBand(this.state()));

  /** Which discount radio is on: "0" is the bare sale flag, the rest are real minimums. */
  protected readonly discountChoice = computed(() => {
    const state = this.state();
    if (!state.onSale) {
      return null;
    }

    return String(state.minDiscountPercent ?? 0);
  });

  protected isChecked(key: ListFilterKey, value: string): boolean {
    return this.state()[key].includes(value);
  }

  protected isGroupActive(key: ListFilterKey): boolean {
    return this.state()[key].length > 0;
  }

  protected toggle(key: ListFilterKey, value: string): void {
    this.changed.emit(toggleListValue(this.state(), key, value));
  }

  protected toggleInStock(): void {
    this.changed.emit(withFilter(this.state(), { inStock: !this.state().inStock }));
  }

  /** A different department has different categories, so the category cannot come along. */
  protected chooseDepartment(slug: string): void {
    this.changed.emit(withFilter(this.state(), { department: slug, category: null }));
  }

  protected chooseCategory(slug: string | null): void {
    this.changed.emit(withFilter(this.state(), { category: slug }));
  }

  protected choosePrice(bandId: string | null): void {
    const band = PRICE_BANDS.find((candidate) => candidate.id === bandId);
    this.changed.emit(
      withFilter(this.state(), { minPrice: band?.min ?? null, maxPrice: band?.max ?? null }),
    );
  }

  protected chooseRating(value: number | null): void {
    this.changed.emit(withFilter(this.state(), { minRating: value }));
  }

  /** `null` clears the sale filter; 0 is "on sale", anything above it a minimum discount. */
  protected chooseDiscount(percent: number | null): void {
    this.changed.emit(
      withFilter(this.state(), {
        onSale: percent !== null,
        minDiscountPercent: percent !== null && percent > 0 ? percent : null,
      }),
    );
  }
}

/** Depth-first, so a child category is listed under its parent and indented by its depth. */
function flatten(categories: readonly CategoryNodeDto[]): readonly CategoryChoice[] {
  return categories.flatMap((category) => [
    { slug: category.slug, name: category.name, depth: category.depth },
    ...flatten(category.children),
  ]);
}
