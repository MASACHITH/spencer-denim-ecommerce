import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { AdminProductApi } from '../../core/products/admin-product-api';
import {
  AdminProductListItem,
  BADGES,
  PagedProducts,
  ProductFormOptions,
} from '../../core/products/admin-product.model';

/** What the filter bar is asking for. One object, so one change cannot be half-applied. */
interface Filters {
  readonly term: string;
  readonly departmentId: number | null;
  readonly published: 'all' | 'published' | 'unpublished';
  readonly sort: string;
}

const EMPTY_FILTERS: Filters = { term: '', departmentId: null, published: 'all', sort: 'recentlyUpdated' };

/**
 * ADM-10: the product list — the screen the client has been waiting for, because until now they could
 * create departments and categories and not a single product.
 *
 * **It shows unpublished products, and that is the whole point.** The storefront's listing drops them; an
 * unpublished product is the only kind there is before it is finished, so a screen built on the shop's own
 * query could never reach the product it exists to publish.
 *
 * The API is the security boundary (doc 03 §3a). A user without `Products.View` reaches this page — the
 * route only requires a session, like the rest of the admin shell — and is shown the refusal rather than an
 * empty screen.
 */
@Component({
  selector: 'sda-products-list-page',
  imports: [FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="products">
      <div class="products__head">
        <h1 class="products__title">Products</h1>
        <a class="adm-btn--primary" routerLink="/products/new" data-testid="add-product">Add a product</a>
      </div>

      <p class="sr-only" role="status" data-testid="announcement">{{ announcement() }}</p>

      @if (failure(); as problem) {
        <div class="adm-state adm-state--error" role="alert" data-testid="page-error">
          <p>{{ problem.message }}</p>
          @if (problem.kind !== 'forbidden') {
            <button class="adm-btn" type="button" (click)="load()">Try again</button>
          }
        </div>
      }

      <form class="products__filters adm-panel" (ngSubmit)="search()" data-testid="filters">
        <div class="adm-field">
          <label class="adm-label" for="product-term">Search</label>
          <input
            class="adm-input"
            id="product-term"
            name="term"
            type="search"
            autocomplete="off"
            placeholder="Name, product code or SKU"
            [ngModel]="filters().term"
            (ngModelChange)="patch({ term: $event })"
            data-testid="filter-term"
          />
        </div>

        <div class="adm-field">
          <label class="adm-label" for="product-department">Department</label>
          <select
            class="adm-input"
            id="product-department"
            name="departmentId"
            [ngModel]="filters().departmentId"
            (ngModelChange)="patch({ departmentId: $event === null || $event === '' ? null : +$event })"
            data-testid="filter-department"
          >
            <option [ngValue]="null">Every department</option>
            @for (department of options()?.departments ?? []; track department.id) {
              <option [ngValue]="department.id">{{ department.name }}</option>
            }
          </select>
        </div>

        <div class="adm-field">
          <label class="adm-label" for="product-published">On the shop</label>
          <select
            class="adm-input"
            id="product-published"
            name="published"
            [ngModel]="filters().published"
            (ngModelChange)="patch({ published: $event })"
            data-testid="filter-published"
          >
            <option value="all">Published and not</option>
            <option value="published">Published only</option>
            <option value="unpublished">Not published only</option>
          </select>
        </div>

        <div class="adm-field">
          <label class="adm-label" for="product-sort">Order</label>
          <select
            class="adm-input"
            id="product-sort"
            name="sort"
            [ngModel]="filters().sort"
            (ngModelChange)="patch({ sort: $event })"
            data-testid="filter-sort"
          >
            <option value="recentlyUpdated">Recently edited</option>
            <option value="name">Name</option>
            <option value="productCode">Product code</option>
            <option value="priceAscending">Price, lowest first</option>
            <option value="priceDescending">Price, highest first</option>
          </select>
        </div>

        <div class="adm-actions products__filter-actions">
          <button class="adm-btn--primary" type="submit" data-testid="apply-filters">Search</button>
          <button class="adm-btn" type="button" (click)="reset()" data-testid="reset-filters">Clear</button>
        </div>
      </form>

      @if (loading()) {
        <p class="adm-state" data-testid="loading">Loading products…</p>
      } @else if (page(); as results) {
        @if (results.items.length === 0) {
          <p class="adm-state" data-testid="empty">
            No products match that. Clear the search, or add a product.
          </p>
        } @else {
          <p class="adm-muted" data-testid="count">
            {{ results.totalCount }} {{ results.totalCount === 1 ? 'product' : 'products' }}, showing
            page {{ results.page }} of {{ results.totalPages }}
          </p>

          <ul class="products__list" data-testid="product-list">
            @for (product of results.items; track product.id) {
              <li class="products__row" [attr.data-testid]="'product-' + product.id">
                <div class="products__thumb" aria-hidden="true">
                  @if (product.cardImageUrl) {
                    <img [src]="product.cardImageUrl" alt="" width="56" height="56" loading="lazy" />
                  } @else {
                    <span class="products__thumb-empty">No photo</span>
                  }
                </div>

                <div class="products__detail">
                  <a class="products__name" [routerLink]="['/products', product.id]">{{ product.name }}</a>
                  <p class="adm-muted products__meta">
                    {{ product.productCode }} · {{ product.departmentName }} ›
                    {{ product.primaryCategoryName }} · Rs. {{ product.basePrice.toFixed(2) }}
                  </p>
                  <p class="adm-muted products__meta">
                    {{ product.colourCount }} {{ product.colourCount === 1 ? 'colour' : 'colours' }},
                    {{ product.variantCount }}
                    <!-- "on sale" is the Sale badge's phrase; this is a count of sizes offered. -->
                    {{ product.variantCount === 1 ? 'size available' : 'sizes available' }}
                    @if (product.hasVideo) {
                      · has a video
                    }
                  </p>
                </div>

                <div class="products__status">
                  @if (product.isPublished) {
                    <span class="adm-pill" [attr.data-testid]="'state-' + product.id">On the shop</span>
                  } @else {
                    <span class="adm-pill adm-pill--hidden" [attr.data-testid]="'state-' + product.id">
                      Not published
                    </span>
                  }
                  @if (badgeLabel(product); as badge) {
                    <span class="adm-pill">{{ badge }}</span>
                  }
                </div>

                <div class="adm-actions products__row-actions">
                  <a class="adm-btn" [routerLink]="['/products', product.id]">Edit</a>

                  <!-- aria-disabled rather than disabled: disabling the button that currently has focus
                       makes the browser drop focus to <body>, so a keyboard user loses their place on the
                       screen as a result of their own keystroke (the ADM-22 defect). The guard that
                       actually stops a second request is in setPublished. -->
                  <button
                    class="adm-btn"
                    type="button"
                    [attr.aria-disabled]="busy() ? 'true' : null"
                    (click)="setPublished(product, !product.isPublished)"
                    [attr.data-testid]="'toggle-' + product.id"
                  >
                    {{ product.isPublished ? 'Take off the shop' : 'Put on the shop' }}
                  </button>
                </div>
              </li>
            }
          </ul>

          <div class="adm-actions products__paging">
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="!results.hasPreviousPage || busy() ? 'true' : null"
              (click)="goToPage(results.page - 1)"
              data-testid="previous-page"
            >
              Previous
            </button>
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="!results.hasNextPage || busy() ? 'true' : null"
              (click)="goToPage(results.page + 1)"
              data-testid="next-page"
            >
              Next
            </button>
          </div>
        }
      }
    </section>
  `,
  styles: `
    .products {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-2) var(--gutter) var(--space-4);
    }

    .products__head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: center;
      justify-content: space-between;
    }

    .products__title {
      font-size: var(--text-h2);
      font-weight: var(--weight-regular);
    }

    /* One column at 320 px, two from 768 px, four from 1280 px: the filter bar is four controls, and four
       across a phone would be four unreadable columns. */
    .products__filters {
      display: grid;
      gap: var(--space-2);
      grid-template-columns: 1fr;
    }

    .products__filter-actions {
      align-items: end;
    }

    .products__list {
      display: grid;
      gap: var(--space-1);
      list-style: none;
    }

    /* The row stacks on a phone and becomes columns from 768 px. No horizontal scroll at any width: the
       long values (the name, the code, the category path) wrap instead. */
    .products__row {
      display: grid;
      gap: var(--space-1);
      grid-template-columns: auto 1fr;
      align-items: start;
      padding: var(--space-1-5);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }

    .products__thumb {
      width: 56px;
      height: 56px;
      display: grid;
      place-items: center;
      overflow: hidden;
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }

    .products__thumb img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }

    .products__thumb-empty {
      font-size: var(--text-label);
      color: var(--color-text-muted);
      text-align: center;
    }

    .products__detail {
      display: grid;
      gap: 2px;
      min-width: 0;
    }

    .products__name {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }

    .products__meta {
      font-size: var(--text-small);
      overflow-wrap: anywhere;
    }

    .products__status,
    .products__row-actions {
      grid-column: 1 / -1;
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: center;
    }

    .products [aria-disabled='true'] {
      opacity: 0.55;
      cursor: not-allowed;
    }

    @media (min-width: 768px) {
      .products__filters {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .products__row {
        grid-template-columns: auto minmax(0, 1fr) auto auto;
        align-items: center;
      }

      .products__status,
      .products__row-actions {
        grid-column: auto;
      }
    }

    @media (min-width: 1280px) {
      .products__filters {
        grid-template-columns: repeat(4, minmax(0, 1fr)) auto;
      }
    }
  `,
})
export class ProductsListPage {
  private readonly api = inject(AdminProductApi);

  private readonly results = signal<PagedProducts | null>(null);
  private readonly formOptions = signal<ProductFormOptions | null>(null);
  private readonly isLoading = signal(true);
  private readonly isBusy = signal(false);
  private readonly problem = signal<CatalogFailure | null>(null);
  private readonly said = signal('');
  private readonly applied = signal<Filters>(EMPTY_FILTERS);
  private readonly draft = signal<Filters>(EMPTY_FILTERS);
  private readonly currentPage = signal(1);

  protected readonly page = this.results.asReadonly();
  protected readonly options = this.formOptions.asReadonly();
  protected readonly loading = this.isLoading.asReadonly();
  protected readonly busy = this.isBusy.asReadonly();
  protected readonly failure = this.problem.asReadonly();
  protected readonly announcement = this.said.asReadonly();
  protected readonly filters = this.draft.asReadonly();

  /** The staff-assigned badge's label, or null when the product has none. */
  protected badgeLabel(product: AdminProductListItem): string | null {
    return BADGES.find((badge) => badge.value === product.badgeOverride)?.label ?? null;
  }

  constructor() {
    // The departments come from the product form's own options endpoint rather than from a second one:
    // the filter needs exactly the list the form offers, and two sources would be two things to keep
    // in step.
    this.api.formOptions().subscribe({
      next: (options) => this.formOptions.set(options),
      error: () => {
        // A missing department list is a narrower filter bar, not a broken screen.
      },
    });

    this.load();
  }

  load(): void {
    this.isLoading.set(true);
    this.problem.set(null);
    this.read();
  }

  protected patch(change: Partial<Filters>): void {
    this.draft.update((current) => ({ ...current, ...change }));
  }

  protected search(): void {
    this.applied.set(this.draft());
    this.currentPage.set(1);
    this.said.set('Searching…');
    this.read();
  }

  protected reset(): void {
    this.draft.set(EMPTY_FILTERS);
    this.applied.set(EMPTY_FILTERS);
    this.currentPage.set(1);
    this.said.set('Search cleared.');
    this.read();
  }

  protected goToPage(page: number): void {
    const results = this.results();
    if (this.isBusy() || !results || page < 1 || page > results.totalPages) {
      return;
    }

    this.currentPage.set(page);
    this.read();
  }

  /**
   * Puts a product on the shop or takes it off.
   *
   * **The list is re-read rather than patched in place**, because publishing moves the row version — and
   * because a refused publish is the interesting case: doc 11 §7 G-01's checklist can say the product has
   * no photograph, and the only honest answer is the sentence the API sends, which is shown as the page
   * error and announced.
   *
   * The guard is here rather than on the button's `disabled` attribute, for the reason the template gives.
   */
  protected setPublished(product: AdminProductListItem, isPublished: boolean): void {
    if (this.isBusy()) {
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(isPublished ? `Putting ${product.name} on the shop…` : `Taking ${product.name} off the shop…`);

    this.api.setPublished(product.id, isPublished, product.rowVersion).subscribe({
      next: () => {
        this.isBusy.set(false);
        this.said.set(
          isPublished ? `${product.name} is on the shop.` : `${product.name} is no longer on the shop.`,
        );

        // Not load(): that replaces the list with "Loading products…", which destroys the button the
        // admin pressed and drops their focus to <body> (the ADM-22 defect).
        this.read();
      },
      error: (error: unknown) => {
        this.isBusy.set(false);
        this.said.set('');
        this.problem.set(toCatalogFailure(error));
      },
    });
  }

  /**
   * Reads the page without taking the screen down.
   *
   * `load()` sets `isLoading`, and the template replaces the whole list while it is set. Using it after a
   * write would destroy the row the admin was working in and lose their keyboard place — the ADM-22 defect
   * that `settings-page` records in the same words.
   */
  private read(): void {
    const filters = this.applied();

    this.api
      .list({
        term: filters.term,
        departmentId: filters.departmentId,
        published:
          filters.published === 'all' ? null : filters.published === 'published',
        sort: filters.sort,
        page: this.currentPage(),
      })
      .subscribe({
        next: (results) => {
          this.results.set(results);
          this.isLoading.set(false);
        },
        error: (error: unknown) => {
          this.problem.set(toCatalogFailure(error));
          this.isLoading.set(false);
        },
      });
  }
}
