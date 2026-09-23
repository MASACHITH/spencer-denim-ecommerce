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
  SIZE_OPTIONS,
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

/** A group of checkboxes, so the eleven groups are declared as data rather than as markup. */
interface CheckboxGroup {
  readonly key: ListFilterKey;
  readonly legend: string;
  readonly options: readonly FilterOption[];
  readonly swatches: boolean;
  readonly open: boolean;
}

let nextPanelId = 0;

/**
 * The PLP-02 filter controls, used twice: as the desktop sidebar and inside the mobile drawer.
 *
 * It owns no state. Every control reports the state it would produce through {@link changed}, and
 * the page turns that into a URL — which is the only place the listing's state lives. That is
 * what makes a filtered listing shareable and what makes Back undo a filter.
 *
 * Department and category are radio groups rather than checkboxes because the API takes one of
 * each (`ProductListQuery.DepartmentSlug`, `CategorySlug` are single values), and because they
 * are the page's own address: choosing one navigates.
 *
 * Native `fieldset`/`legend` and real `input` elements throughout: a checkbox that is a `div`
 * has to have its name, its state and its keyboard behaviour rebuilt by hand, and this page has
 * over a hundred of them.
 */
@Component({
  selector: 'sd-listing-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="filters">
      @if (departments().length > 0) {
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

      @if (categories().length > 0) {
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

      @for (group of checkboxGroups; track group.key) {
        <details class="filters__group" [open]="group.open || isGroupActive(group.key)">
          <summary class="filters__summary">{{ group.legend }}</summary>
          <fieldset class="filters__set" [class.filters__set--swatches]="group.swatches">
            <legend class="sr-only">{{ group.legend }}</legend>
            @for (option of group.options; track option.value + ':' + (option.group ?? '')) {
              @if (startsGroup(group.options, $index)) {
                <p class="filters__subhead">{{ option.group }}</p>
              }
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
      /* PLP-02 is a touch surface first: every row of this panel is a 44px target. */
      min-height: var(--tap);
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
      list-style-position: inside;
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
    .filters__subhead {
      grid-column: 1 / -1;
      padding-top: var(--space-1);
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
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

  /** The state the shopper's last change would produce. The page decides what to do with it. */
  readonly changed = output<ListingState>();

  /** Radio groups need a name that is unique per instance: this panel is rendered twice. */
  protected readonly id = `sd-filters-${nextPanelId++}`;

  protected readonly priceBands = PRICE_BANDS;
  protected readonly ratingOptions = RATING_OPTIONS;
  protected readonly discountOptions = DISCOUNT_OPTIONS;

  protected readonly checkboxGroups: readonly CheckboxGroup[] = [
    {
      key: 'sizes',
      legend: FILTER_GROUP_LABEL.sizes,
      options: SIZE_OPTIONS,
      swatches: false,
      open: true,
    },
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

  /** True when this option opens a new named block, so the sub-heading is drawn once. */
  protected startsGroup(options: readonly FilterOption[], index: number): boolean {
    const current = options[index]?.group;
    return current !== undefined && current !== options[index - 1]?.group;
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
