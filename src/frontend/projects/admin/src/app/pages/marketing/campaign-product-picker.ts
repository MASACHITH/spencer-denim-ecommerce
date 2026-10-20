import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { Subject, of, timer } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';

import { AdminProductApi } from '../../core/products/admin-product-api';
import { AdminProductListItem } from '../../core/products/admin-product.model';
import { toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/** How many products one page of results shows. Small, because the list sits inside a form. */
export const PRODUCT_PICKER_PAGE_SIZE = 10;

/** How long the picker waits after the last keystroke before it asks the server. */
export const PRODUCT_PICKER_DEBOUNCE_MS = 250;

/** One page of the search, or why there is none. */
interface SearchPage {
  readonly term: string;
  readonly page: number;
  readonly items: readonly AdminProductListItem[];
  readonly totalCount: number;
  readonly problem: string | null;
}

/**
 * The product picker on the sale-campaign form (#191, ADM-14).
 *
 * **A search, not a dropdown.** The catalogue is thousands of rows, so the list is asked for one page at a
 * time from `GET /api/admin/products` — the product list's own endpoint, which already matches a name, a
 * product code, a slug or any variant SKU, pages on the server and leaves out removed products. Reusing it
 * means the picker and the Products screen cannot disagree about what a search finds, and no new route
 * needed a permission of its own: it is `Products.View`, which every seeded role holding `Promotions.*`
 * also holds. A custom role without it is told which permission is missing.
 *
 * **The ARIA 1.2 combobox pattern** (APG "editable combobox with list autocomplete"): the text box is the
 * `combobox`, it owns a `listbox` through `aria-controls`, says whether it is open with `aria-expanded`,
 * and points at the highlighted option with `aria-activedescendant` while focus stays in the box. Up and
 * Down move the highlight, Enter adds the highlighted product, Page Up and Page Down turn the page,
 * Escape closes the list and a second Escape clears the box. A pointer can click an option; the
 * `mousedown` is cancelled so focus never leaves the box.
 *
 * **Choosing adds at once and leaves the list open**, so a staff member putting six shirts on a sale
 * types once and presses Enter six times. A product already on the campaign is marked and refused, with
 * the reason, rather than added twice for the server to reject.
 *
 * Unpublished products are offered and labelled: a campaign may be set up for a product that goes on sale
 * the day it is published, and the API accepts any product that has not been removed.
 */
@Component({
  selector: 'sda-campaign-product-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="picker">
      <label class="adm-label" for="campaign-product-search">Find a product</label>
      <p class="adm-panel__hint picker__hint" id="campaign-product-hint">
        Type part of a name, a product code or a SKU. Use the up and down arrow keys to move through the
        results and Enter to add one; Page Up and Page Down show more.
      </p>
      <input
        id="campaign-product-search"
        class="adm-input"
        type="text"
        role="combobox"
        autocomplete="off"
        spellcheck="false"
        aria-autocomplete="list"
        aria-controls="campaign-product-results"
        aria-describedby="campaign-product-hint campaign-product-status"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-activedescendant]="activeOptionId()"
        [value]="term()"
        data-testid="product-search"
        (input)="onInput($event)"
        (keydown)="onKeydown($event)"
      />

      <!-- Always rendered: a status written into a node the same update creates is not announced. -->
      <p class="adm-muted" id="campaign-product-status" role="status" data-testid="product-search-status">
        {{ status() }}
      </p>

      <div class="picker__popup" [hidden]="!isOpen()" data-testid="product-results-popup">
        <ul
          id="campaign-product-results"
          class="picker__list"
          role="listbox"
          aria-label="Matching products"
          data-testid="product-results"
        >
          @for (item of results(); track item.id; let index = $index) {
            <!-- An option in an aria-activedescendant combobox is not focusable and carries no key
                 handler of its own: focus stays in the text box, whose keydown moves the highlight and
                 chooses with Enter. tabindex="-1" adds no tab stop. The rule disabled below is written
                 for the other half of the pattern, a widget that moves focus into its list; the
                 storefront's header search makes the same call. Driven for real in
                 e2e/admin-sale-campaigns.e2e.ts. -->
            <!-- eslint-disable-next-line @angular-eslint/template/click-events-have-key-events -->
            <li
              role="option"
              tabindex="-1"
              class="picker__option"
              [id]="optionId(item.id)"
              [class.picker__option--active]="index === active()"
              [attr.aria-selected]="index === active()"
              [attr.data-testid]="'product-option-' + item.id"
              (mousedown)="$event.preventDefault()"
              (click)="choose(index)"
            >
              <span class="picker__name">{{ item.name }}</span>
              <span class="picker__meta">
                {{ item.productCode }} · {{ item.departmentName }} › {{ item.primaryCategoryName }}
                @if (!item.isPublished) {
                  · not published
                }
                @if (isChosen(item.id)) {
                  · already on this campaign
                }
              </span>
            </li>
          }
        </ul>

        @if (pageCount() > 1) {
          <div class="adm-actions">
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="page() <= 1 ? 'true' : null"
              data-testid="product-results-previous"
              (mousedown)="$event.preventDefault()"
              (click)="turn(-1)"
            >
              Previous {{ pageSize }}
            </button>
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="page() >= pageCount() ? 'true' : null"
              data-testid="product-results-next"
              (mousedown)="$event.preventDefault()"
              (click)="turn(1)"
            >
              Next {{ pageSize }}
            </button>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
      grid-column: 1 / -1;
      min-width: 0;
    }
    .picker {
      display: grid;
      gap: var(--space-0-5);
      grid-column: 1 / -1;
      min-width: 0;
    }
    .picker__hint {
      margin: 0;
    }
    .picker__popup {
      display: grid;
      gap: var(--space-1);
      border: 1px solid var(--color-border-strong);
      background: var(--color-bg);
      padding: var(--space-1);
      min-width: 0;
    }
    .picker__popup[hidden] {
      display: none;
    }
    .picker__list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      min-width: 0;
    }
    .picker__option {
      display: grid;
      gap: 2px;
      min-height: var(--tap);
      padding: var(--space-1);
      cursor: pointer;
      border-left: 3px solid transparent;
      overflow-wrap: anywhere;
    }
    .picker__option--active {
      background: var(--color-surface);
      border-left-color: var(--color-text);
      outline: 2px solid var(--color-text);
      outline-offset: -2px;
    }
    .picker__name {
      font-weight: var(--weight-semibold);
    }
    .picker__meta {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
  `,
})
export class CampaignProductPicker {
  private readonly api = inject(AdminProductApi);

  /** The ids of the products already on the campaign, so the list can mark them and refuse a repeat. */
  readonly chosen = input<readonly number[]>([]);

  /** A product the staff member chose. The form adds it as a target and announces it. */
  readonly picked = output<AdminProductListItem>();

  /** A sentence the form should announce on its own live region: the picker refused something. */
  readonly refused = output<string>();

  protected readonly pageSize = PRODUCT_PICKER_PAGE_SIZE;

  protected readonly term = signal('');
  protected readonly results = signal<readonly AdminProductListItem[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly page = signal(1);
  protected readonly active = signal(-1);
  protected readonly isOpen = signal(false);
  protected readonly status = signal('');

  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.totalCount() / PRODUCT_PICKER_PAGE_SIZE)),
  );

  protected readonly activeOptionId = computed(() => {
    const item = this.results()[this.active()];
    return this.isOpen() && item !== undefined ? this.optionId(item.id) : null;
  });

  /** Every request, typed or paged. `switchMap` drops the answer to a search that has been overtaken. */
  private readonly requests = new Subject<{ readonly term: string; readonly page: number; readonly typed: boolean }>();

  constructor() {
    const subscription = this.requests
      .pipe(
        // One switchMap around the wait *and* the request, so a keystroke cancels both: the pending
        // pause and any search already in flight. Typing waits for a pause; turning a page does not.
        switchMap((request) =>
          (request.typed ? timer(PRODUCT_PICKER_DEBOUNCE_MS) : of(0)).pipe(
            switchMap(() =>
              this.api.list({
                term: request.term,
                page: request.page,
                pageSize: PRODUCT_PICKER_PAGE_SIZE,
                sort: 'name',
              }),
            ),
            map(
              (result): SearchPage => ({
                term: request.term,
                page: result.page,
                items: result.items,
                totalCount: result.totalCount,
                problem: null,
              }),
            ),
            catchError((error: unknown) =>
              of<SearchPage>({
                term: request.term,
                page: request.page,
                items: [],
                totalCount: 0,
                problem: describeFailure(error),
              }),
            ),
          ),
        ),
      )
      .subscribe((answer) => this.show(answer));

    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  protected optionId(productId: number): string {
    return `campaign-product-option-${productId}`;
  }

  protected isChosen(productId: number): boolean {
    return this.chosen().includes(productId);
  }

  protected onInput(event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    this.term.set(value);

    if (value.trim().length === 0) {
      this.close();
      this.results.set([]);
      this.totalCount.set(0);
      this.status.set('');
      return;
    }

    // The list on screen belongs to the previous term, so it goes while the new search waits: an Enter
    // pressed during the pause must not add a product from a list the staff member has typed past.
    this.results.set([]);
    this.totalCount.set(0);
    this.close();
    this.status.set('Searching…');
    this.requests.next({ term: value.trim(), page: 1, typed: true });
  }

  protected onKeydown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.step(1);
        return;
      case 'ArrowUp':
        event.preventDefault();
        this.step(-1);
        return;
      case 'PageDown':
        if (this.isOpen()) {
          event.preventDefault();
          this.turn(1);
        }
        return;
      case 'PageUp':
        if (this.isOpen()) {
          event.preventDefault();
          this.turn(-1);
        }
        return;
      case 'Enter':
        // Always swallowed: the box is inside the campaign form, and Enter in it must never submit a save
        // the staff member did not ask for.
        event.preventDefault();
        if (this.isOpen() && this.active() >= 0) {
          this.choose(this.active());
        }
        return;
      case 'Escape':
        if (this.isOpen()) {
          event.preventDefault();
          this.close();
        } else if (this.term().length > 0) {
          event.preventDefault();
          this.term.set('');
          this.results.set([]);
          this.totalCount.set(0);
          this.status.set('');
        }
        return;
      default:
        return;
    }
  }

  /** Adds the product at this position, unless it is already on the campaign. */
  protected choose(index: number): void {
    const item = this.results()[index];

    if (item === undefined) {
      return;
    }

    this.active.set(index);

    if (this.isChosen(item.id)) {
      this.refused.emit(`${item.name} is already on this campaign.`);
      return;
    }

    this.picked.emit(item);
  }

  /** Shows the next or previous page of the same search. */
  protected turn(direction: 1 | -1): void {
    const next = this.page() + direction;

    if (next < 1 || next > this.pageCount() || this.term().trim().length === 0) {
      return;
    }

    this.status.set('Loading more products…');
    this.requests.next({ term: this.term().trim(), page: next, typed: false });
  }

  /** Moves the highlight, opening the list first when there are results to open it on. */
  private step(direction: 1 | -1): void {
    const count = this.results().length;

    if (count === 0) {
      return;
    }

    if (!this.isOpen()) {
      this.isOpen.set(true);
      this.active.set(direction === 1 ? 0 : count - 1);
      return;
    }

    // Wraps at both ends, which is what the APG listbox examples do and what a keyboard user expects
    // from a short list.
    this.active.set((this.active() + direction + count) % count);
  }

  private close(): void {
    this.isOpen.set(false);
    this.active.set(-1);
  }

  private show(answer: SearchPage): void {
    // A page that arrives after the box was cleared or changed belongs to a search nobody is looking at.
    if (answer.term !== this.term().trim()) {
      return;
    }

    if (answer.problem !== null) {
      this.results.set([]);
      this.totalCount.set(0);
      this.close();
      this.status.set(answer.problem);
      return;
    }

    this.results.set(answer.items);
    this.totalCount.set(answer.totalCount);
    this.page.set(answer.page);

    if (answer.totalCount === 0) {
      this.close();
      this.status.set(`No product matches “${answer.term}”.`);
      return;
    }

    this.isOpen.set(true);
    this.active.set(0);

    const first = (answer.page - 1) * PRODUCT_PICKER_PAGE_SIZE + 1;
    const last = first + answer.items.length - 1;
    const noun = answer.totalCount === 1 ? 'product matches' : 'products match';

    this.status.set(
      answer.totalCount <= PRODUCT_PICKER_PAGE_SIZE
        ? `${answer.totalCount} ${noun}.`
        : `${answer.totalCount} ${noun}. Showing ${first} to ${last}.`,
    );
  }
}

/** A failed search, in words a staff member can act on. */
function describeFailure(error: unknown): string {
  const failure = toCatalogFailure(error);

  return failure.kind === 'forbidden'
    ? 'You can manage campaigns but not read the product list, so products cannot be searched. Ask for '
      + 'the "Products" permission as well.'
    : `The products could not be searched. ${failure.message}`;
}
