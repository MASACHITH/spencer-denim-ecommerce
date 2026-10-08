import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { RouterLink } from '@angular/router';

import { AdminOrderApi } from '../../core/orders/admin-order-api';
import {
  AdminOrderListPage,
  AdminOrderSort,
  ORDER_STATUS_TABS,
  OrderStatusName,
} from '../../core/orders/admin-order.model';
import { money, timeLeft } from '../../core/orders/order-formatting';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/** How many orders a page holds. The API clamps anything larger (its own ceiling is 100). */
const PAGE_SIZE = 25;

/**
 * The admin order list (ADM-09, doc 11 P9 step 1).
 *
 * **This is the screen the shop cannot run without, and the search box is the reason.** Doc 11 §6.6 is a
 * whole section about a customer telephoning about an order: what they have is the order number, or their
 * email address, or their telephone number. Before this screen existed the only order screen was the
 * bank-transfer approval queue, and an order left it the moment it was paid — so a customer who rang up
 * could not be helped at all.
 *
 * **One search box, not four.** Which of those three things the caller is about to say is not a question
 * anybody can answer, so the server classifies the term (`AdminOrderSearch`) and matches exactly one
 * column: the order number first, because that is what gets quoted.
 *
 * **The sort comes from the server.** Doc 11 P9 step 1 works the Paid filter oldest first; the API chooses
 * that when the screen expresses no preference and says which order it used, and this page labels the list
 * from the answer rather than from its own assumption.
 *
 * **Nothing here promises an email.** `ops.EmailOutbox` is W3-06 and unbuilt, so no sentence on this
 * screen or the detail says a customer has been or will be notified.
 */
@Component({
  selector: 'sda-orders-list-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Orders</h1>
      <p class="adm-panel__hint">
        Search by order number first — that is what a customer quotes on the telephone. An invoice number,
        an email address, a telephone number and a customer's name are matched too.
      </p>

      <form class="orders-search" (submit)="submitSearch($event)" role="search">
        <label class="adm-field adm-field--wide">
          <span class="adm-label" id="order-search-label">Find an order</span>
          <input
            class="adm-input"
            type="search"
            name="search"
            autocomplete="off"
            maxlength="100"
            [value]="searchText()"
            [attr.aria-describedby]="'order-search-hint'"
            data-testid="order-search"
            (input)="typeSearch($event)"
          />
        </label>
        <p class="adm-panel__hint" id="order-search-hint">
          The whole number, its last digits, or part of a name.
        </p>
        <div class="adm-actions">
          <button class="adm-btn--primary" type="submit" data-testid="order-search-submit">Search</button>
          @if (searchApplied()) {
            <button class="adm-btn" type="button" data-testid="order-search-clear" (click)="clearSearch()">
              Clear
            </button>
          }
        </div>
      </form>
    </section>

    <section class="adm-panel">
      <h2 class="adm-panel__title">Show</h2>
      <div class="adm-actions" role="group" aria-label="Filter by status">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-pressed]="status() === null"
          data-testid="filter-all"
          (click)="filter(null)"
        >
          All<span class="adm-muted">&nbsp;{{ countFor(null) }}</span>
        </button>
        @for (candidate of statuses; track candidate) {
          <button
            class="adm-btn"
            type="button"
            [attr.aria-pressed]="status() === candidate"
            [attr.data-testid]="'filter-' + candidate"
            (click)="filter(candidate)"
          >
            {{ label(candidate) }}<span class="adm-muted">&nbsp;{{ countFor(candidate) }}</span>
          </button>
        }
      </div>
      <div class="adm-actions" role="group" aria-label="Sort">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-pressed]="sortInUse() === 'Oldest'"
          data-testid="sort-oldest"
          (click)="sortBy('Oldest')"
        >
          Oldest first
        </button>
        <button
          class="adm-btn"
          type="button"
          [attr.aria-pressed]="sortInUse() === 'Newest'"
          data-testid="sort-newest"
          (click)="sortBy('Newest')"
        >
          Newest first
        </button>
      </div>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        <div class="adm-actions">
          <button class="adm-btn" type="button" (click)="reload()">Try again</button>
        </div>
      </div>
    }

    <!-- tabindex="-1" so focus can land here after a filter change destroys the row it was on: focus
         would otherwise fall to <body> (#109, read from the other direction). -1 keeps it out of the tab
         order. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="orders-status">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading orders…</p>
    } @else if (page() === null) {
      <!-- A failed read, with the refusal already shown above. The null check is a defect this screen's
           own spec found: isEmpty() answers -1 === 0, which is false, so without this branch the list
           rendered the page's rows over a null and a 403 blew the screen up with a TypeError instead of
           showing the server's sentence. -->
    } @else if (isEmpty()) {
      <p class="adm-muted" data-testid="orders-empty">{{ emptyMessage() }}</p>
    } @else {
      <ul class="adm-list" data-testid="orders">
        @for (row of page()!.rows; track row.orderPublicId) {
          <li class="orders-row" [attr.data-testid]="'order-' + row.orderNumber">
            <a
              class="orders-row__open"
              [routerLink]="['/orders', row.orderPublicId]"
              [attr.data-testid]="'open-' + row.orderNumber"
            >
              <span class="orders-row__number">{{ row.orderNumber }}</span>
              <span class="adm-muted">{{ row.customerName }}</span>
            </a>
            <dl class="orders-row__facts">
              <dt>Status</dt>
              <dd>
                <span class="adm-pill" [attr.data-testid]="'status-' + row.orderNumber">
                  {{ row.statusLabel }}
                </span>
                @if (row.secondsUntilExpiry !== null) {
                  <span class="adm-muted">&nbsp;{{ left(row.secondsUntilExpiry) }}</span>
                }
              </dd>
              <dt>Total</dt>
              <dd [attr.data-testid]="'total-' + row.orderNumber">{{ cash(row.grandTotal) }}</dd>
              <dt>Placed</dt>
              <dd>{{ when(row.placedAtUtc) }}</dd>
              <dt>Delivery</dt>
              <dd>{{ row.deliveryMethodName }}</dd>
              @if (row.trackingNumber) {
                <dt>Tracking</dt>
                <dd>{{ row.courierName }} · {{ row.trackingNumber }}</dd>
              }
              @if (row.invoiceNumber) {
                <dt>Invoice</dt>
                <dd>{{ row.invoiceNumber }}</dd>
              }
            </dl>
          </li>
        }
      </ul>

      <nav class="adm-actions" aria-label="Pages">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="page()!.page <= 1 ? 'true' : null"
          data-testid="orders-prev"
          (click)="go(page()!.page - 1)"
        >
          Previous
        </button>
        <p class="adm-muted" data-testid="orders-paging">
          {{ firstOnPage() }}–{{ lastOnPage() }} of {{ page()!.totalCount }}
        </p>
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="lastOnPage() >= page()!.totalCount ? 'true' : null"
          data-testid="orders-next"
          (click)="go(page()!.page + 1)"
        >
          Next
        </button>
      </nav>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .orders-search {
      display: grid;
      gap: var(--space-1);
    }
    .adm-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .orders-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    /* The whole heading is the control, so the 44 px floor is met by the row a person is aiming at rather
       than by a small chevron beside it — W1-03's lesson about controls that are large enough and buried,
       read the other way round. */
    .orders-row__open {
      display: grid;
      gap: 2px;
      min-height: var(--tap);
      align-content: center;
      text-decoration: none;
      color: inherit;
    }
    .orders-row__number {
      font-weight: var(--weight-semibold);
      text-decoration: underline;
      overflow-wrap: anywhere;
    }
    .orders-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .orders-row__facts dt {
      color: var(--color-text-muted);
    }
    .orders-row__facts dd {
      margin: 0;
      /* A long tracking number with no spaces in it must not widen the row past the panel, which at 320 px
         is the difference between a tidy list and a page that scrolls sideways. */
      overflow-wrap: anywhere;
    }
  `,
})
export class OrdersListPage {
  private readonly api = inject(AdminOrderApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly statuses = ORDER_STATUS_TABS;

  protected readonly page = signal<AdminOrderListPage | null>(null);
  protected readonly status = signal<OrderStatusName | null>(null);

  /** What is in the box, which is not yet what the list is filtered by. */
  protected readonly searchText = signal('');

  /** What the list is actually filtered by — only changes when Search is pressed. */
  protected readonly searchApplied = signal('');

  /**
   * The sort the screen has asked for, or null to let the server choose.
   *
   * **Null is the normal state, and that is doc 11 P9 step 1.** The server defaults the Paid filter to
   * oldest first; a screen that sent its own default would move the rule into the client, where the next
   * client would have to remember it. Pressing a sort button sets this and overrides the rule.
   */
  protected readonly sortWanted = signal<AdminOrderSort | null>(null);

  protected readonly isLoading = signal(true);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  /**
   * Which sort the rows are in, from the server's own answer.
   *
   * Falls back to the requested one only before the first response, so the two buttons are never both
   * unpressed. After that it is the server's word, which is the point: on the Paid tab the screen has
   * asked for nothing and the list is oldest first, and the label has to say so.
   */
  protected readonly sortInUse = computed<AdminOrderSort>(
    () => this.page()?.sort ?? this.sortWanted() ?? 'Newest',
  );

  /**
   * Whether this filter has nothing in it.
   *
   * A computed rather than `page()?.rows.length === 0` in the template: that expression is `undefined`
   * before the first answer, which reads as "not empty" and renders the list branch over a null — and
   * `strictTemplates` says so, which is how the enquiry inbox caught the same thing.
   */
  protected readonly isEmpty = computed(() => (this.page()?.rows.length ?? -1) === 0);

  protected readonly firstOnPage = computed(() => {
    const answered = this.page();
    return answered === null || answered.totalCount === 0
      ? 0
      : (answered.page - 1) * answered.pageSize + 1;
  });

  protected readonly lastOnPage = computed(() => {
    const answered = this.page();
    return answered === null
      ? 0
      : Math.min(answered.page * answered.pageSize, answered.totalCount);
  });

  protected readonly emptyMessage = computed(() => {
    const term = this.searchApplied();
    const filtered = this.status();

    if (term.length > 0) {
      return `No order matches “${term}”${filtered ? ` with that status` : ''}. Check the number, or `
        + 'search the customer’s email address instead.';
    }

    return filtered === null
      ? 'No orders yet.'
      : `No orders are ${this.label(filtered).toLowerCase()} at the moment.`;
  });

  constructor() {
    this.load(1);
  }

  protected cash(amount: number): string {
    return money(amount);
  }

  protected left(seconds: number): string {
    return timeLeft(seconds);
  }

  /**
   * A placed-at timestamp for the list.
   *
   * Date only: the list is scanned rather than read, and a minute-precise time on every row is noise that
   * also costs width at 320 px. The detail screen shows the full timeline.
   */
  protected when(utc: string): string {
    const parsed = new Date(utc.endsWith('Z') ? utc : `${utc}Z`);

    return Number.isNaN(parsed.getTime())
      ? utc
      : parsed.toLocaleDateString('en-LK', {
          timeZone: 'Asia/Colombo',
          year: 'numeric',
          month: 'short',
          day: '2-digit',
        });
  }

  /**
   * The wording for a status tab.
   *
   * **The two that differ from the enum's own name are the two the enum gets wrong for a person.**
   * `PendingPayment` is "Awaiting payment", because nobody says pending payment out loud. The pickup
   * variants ("Ready for collection", "Collected") are *not* here: a tab filters orders of both kinds, so
   * one of the two labels would always be wrong — the server sends the right wording per row, and that is
   * where it belongs.
   */
  protected label(status: OrderStatusName): string {
    return status === 'PendingPayment' ? 'Awaiting payment' : status;
  }

  /** The count beside a tab, over the whole table rather than the current filter. */
  protected countFor(status: OrderStatusName | null): number {
    const counts = this.page()?.statusCounts;

    if (counts === undefined) {
      return 0;
    }

    return status === null
      ? Object.values(counts).reduce((total, count) => total + count, 0)
      : (counts[status] ?? 0);
  }

  protected typeSearch(event: Event): void {
    this.searchText.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  /**
   * Applies the search.
   *
   * **A form submit, so Enter works** — and `preventDefault` so the browser does not navigate. The box
   * does not search as you type: the server classifies the term and matches one column, so a half-typed
   * order number is a different query from a whole one rather than a narrower one, and a list that
   * reshuffled on every keystroke would move the row the staff member was reaching for.
   */
  protected submitSearch(event: Event): void {
    event.preventDefault();

    const term = this.searchText().trim();
    this.searchApplied.set(term);
    this.load(1, term.length === 0 ? 'Showing every order.' : `Searching for “${term}”…`);
  }

  protected clearSearch(): void {
    this.searchText.set('');
    this.searchApplied.set('');
    this.load(1, 'Search cleared.');
  }

  protected filter(status: OrderStatusName | null): void {
    this.status.set(status);

    // The requested sort is dropped with the filter, so moving to the Paid tab gets doc 11 P9 step 1's
    // oldest-first again rather than inheriting whatever the previous tab was sorted by. A staff member
    // who wants it the other way presses the button, which is one click and visibly pressed.
    this.sortWanted.set(null);

    this.load(
      1,
      status === null
        ? 'Showing every order.'
        : `Showing ${this.label(status).toLowerCase()} orders.`,
    );
  }

  protected sortBy(sort: AdminOrderSort): void {
    if (this.sortInUse() === sort) {
      return;
    }

    this.sortWanted.set(sort);
    this.load(1, sort === 'Oldest' ? 'Oldest first.' : 'Newest first.');
  }

  protected go(page: number): void {
    const answered = this.page();

    // The guard the `disabled` attribute would have provided, which is why neither paging button is
    // disabled: `[disabled]` on a button whose state changes under the user throws focus to `<body>`
    // (#109, six instances). `aria-disabled` plus this refusal is the pattern, and Playwright's
    // `toBeDisabled()` honours it.
    if (answered === null || page < 1 || (page - 1) * answered.pageSize >= answered.totalCount) {
      return;
    }

    this.load(page, `Page ${page}.`);
  }

  protected reload(): void {
    this.load(this.page()?.page ?? 1);
  }

  private load(page: number, announcement?: string): void {
    // Captured *before* the list is replaced, because that is the only moment this element can still be
    // identified. See `rehomeFocusIfLost`.
    const focused = this.document.activeElement as HTMLElement | null;

    this.isLoading.set(true);
    this.problem.set(null);

    if (announcement !== undefined) {
      this.announce(announcement);
    }

    this.api
      .list({
        search: this.searchApplied().length === 0 ? null : this.searchApplied(),
        status: this.status(),
        sort: this.sortWanted(),
        page,
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (answered) => {
          this.page.set(answered);
          this.isLoading.set(false);
          this.announce(this.summarise(answered));
          this.rehomeFocusIfLost(focused);
        },
        error: (error: unknown) => {
          this.isLoading.set(false);
          const failure = toCatalogFailure(error);
          this.problem.set(failure);
          this.announce(failure.message);
        },
      });
  }

  private summarise(answered: AdminOrderListPage): string {
    if (answered.totalCount === 0) {
      return this.emptyMessage();
    }

    const of = answered.totalCount === 1 ? 'order' : 'orders';
    const order = answered.sort === 'Oldest' ? 'oldest first' : 'newest first';

    return `${answered.totalCount} ${of}, ${order}. `
      + `Showing ${this.firstOnPage()} to ${this.lastOnPage()}.`;
  }

  /**
   * Re-homes focus **only if the load destroyed the element that had it**.
   *
   * A filter or a page change replaces every row, so a staff member who activated a link or a control
   * inside one is left with focus on a detached node — and the browser drops it to `<body>`, which is the
   * same harm `[disabled]` does (#109) arriving from the other direction. The rule is *necessary and not
   * sufficient*: re-homing unconditionally would be its own defect, because the search box survives every
   * load and yanking the caret out of it mid-sentence is worse than the thing being fixed. So the element
   * is checked for `isConnected` and left alone when it is still there.
   *
   * Focus goes to the live region's own sentence, which is where the next thing to read is and which
   * carries `tabindex="-1"` so it can take focus without entering the tab order.
   *
   * **`afterNextRender`, not `queueMicrotask`.** A signal write schedules change detection as a *task*; a
   * microtask runs before it, so the check would run against the old DOM and every node would still look
   * connected. The enquiry inbox records the same diagnosis in full.
   */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="orders-status"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /**
   * Puts a sentence in the live region, **and makes it speak even when it is the same sentence** (ADM-22).
   *
   * `role="status"` announces a *change*. Setting the region to the text it already holds is not one, so
   * the region is emptied and the sentence put back once the view has caught up, which is a real mutation
   * of real text. **Not a counter or a zero-width character:** a screen reader reads what is there, and
   * "(2)" tells the listener nothing about their order.
   *
   * **On this screen the re-fire path is currently unreachable, and that is worth saying rather than
   * claiming coverage it does not have.** Every action here announces an in-progress sentence and then a
   * result sentence, so consecutive announcements always differ — which is the same resolution ADM-22
   * itself reached ("gave the save two genuinely different sentences"). The guard stays because the next
   * action added to this screen may not have an in-progress state, and because the couriers screen's
   * validation path does reach it and is tested there.
   */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
