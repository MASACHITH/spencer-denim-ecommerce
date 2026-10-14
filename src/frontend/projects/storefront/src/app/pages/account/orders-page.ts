import { isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  PLATFORM_ID,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { AccountNav } from './account-nav';
import { AccountNotice } from './account-notice';
import { AccountApi, accountErrorMessage } from '../../core/account/account-api';
import {
  CustomerOrderSummaryDto,
  EMPTY_ORDER_PAGE,
  accountDate,
} from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { formatLkr } from '../../core/catalog/money';
import { privatePage } from '../../core/platform/private-page';

/**
 * My account's order history (ACC-02, doc 11 P11 step 12) — **the gap W3-01 closes**.
 *
 * Before this page, a shopper who placed an order had no way to look it up again unless they still had
 * the emailed link, and the tracking number existed only in the admin panel. Putting the tracking on the
 * *list* rather than only on the detail page means the answer to "where is my parcel" is one page and not
 * two.
 *
 * ## The shell renders on the server; the orders do not
 *
 * The session cookie is `sd_customer_auth` and `cartCookieInterceptor` forwards **only `sd_cart`** — on
 * purpose, because the same browser may carry `sd_admin_auth` and relaying a staff session from the SSR
 * process to the API on a shopper's page render would hand the storefront an authenticated identity it
 * has no business holding. So the server has no way to read this list, and does not pretend to: it renders
 * the heading, the navigation and "Loading your orders…", and the browser reads the list in
 * `afterNextRender` — the shape `wishlist-page.ts` uses, and guarded by `isPlatformBrowser` as well as by
 * the framework's own skipping, so "the server never reads an order history" is a property of this file
 * that a unit test can render on the server platform and see.
 *
 * **Which costs this page nothing it would have had.** It answers `no-store`, `private`, `Vary: Cookie`
 * and `noindex` ({@link privatePage}), so it is never in a shared cache and never in a search result —
 * the two things server rendering buys. The bag went the other way because doc 08 §3.5 gives the bag four
 * reasons to be server-side and the cookie it needs *is* forwarded.
 *
 * ## It gates on having asked, not on the list being empty
 *
 * "I have not looked yet" and "you have no orders" are the same empty array, and telling a customer with
 * thirty orders that they have none — for the whole of the server's response and the browser's first
 * frame — is a confident wrong answer rather than a slow one. Two red `main` branches on this project were
 * built on exactly that distinction, so {@link state} is what the template asks and the empty state is
 * drawn only once a read has actually succeeded.
 *
 * ## Paging lives in the address bar
 *
 * `?page=2`, bound by `withComponentInputBinding()`. That makes the back button work, and it is what makes
 * the sign-in guard's `returnTo` carry a page number: a shopper sent to sign in from page 3 of their
 * history and returned to page 1 would read it as having lost their place, which is why
 * `customerSignedIn` captures `state.url` rather than rebuilding the path.
 *
 * **`pageSize` is never sent and never written here.** The server's default is ten, it clamps anything
 * outside 1–50 back to ten, and it echoes the size back — so the page prints what it was given and cannot
 * disagree with the server about how many rows a page holds.
 *
 * ## Why neither paging control is ever removed from the page
 *
 * Previous on page 1 and Next on the last page carry `aria-disabled` and are refused by their handlers;
 * they are not `disabled` and not hidden. Three reasons, in order of how badly each has bitten this
 * project: `disabled` on the control that has focus throws focus to `<body>` and fires no `focusin`
 * (#109), a removed control does the same thing by another route (W2-08's three removal controls), and a
 * control nobody can reach cannot tell anyone why it will not work. The payoff here is concrete: because
 * Next survives its own press, a keyboard shopper paging through a long history keeps their place and the
 * page change is announced rather than focused — see {@link said}.
 *
 * ## Confirmation, and why some orders are missing
 *
 * Guest orders placed with the same address appear **only after the address is confirmed** (doc 08 §3.1
 * D5). That is specified behaviour and not a gap, so an unconfirmed customer is told it on this page —
 * {@link AccountNav} draws the note from `emailConfirmed` on `GET /api/account/me` — rather than left to
 * wonder why a list is short. Confirmation gates nothing else: doc 11 §7 G-24 lets them sign in, shop and
 * check out.
 */
@Component({
  selector: 'sd-orders-page',
  imports: [AccountNav, AccountNotice, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc">
      <h1 class="acc__title">Your orders</h1>

      <sd-account-nav />
      <sd-account-notice [alert]="error()" [status]="said()" testId="orders" />

      @if (phase() === 'waiting') {
        <!-- The server's answer, and the browser's for one frame. **Not an empty state**: the server
             cannot read this list, and "You have not ordered anything yet" would be a claim rather than
             a wait. -->
        <p class="acc__muted" role="status" data-testid="orders-loading">Loading your orders…</p>
      } @else if (phase() === 'failed' && orders().length === 0) {
        <!-- The read failed and there is nothing in hand, so there is nothing true to draw. **Not the
             empty state**, which would tell a customer with thirty orders that they have none because
             the shop was briefly unreachable. The sentence is in the alert region above; this is the
             way to try again. 'wishlist-page.ts' makes the same distinction, and keeps the rows it
             already had — which the branch below does too, by not existing. -->
        <p class="acc-actions">
          <button class="btn btn--secondary" type="button" data-testid="orders-retry" (click)="retry()">
            Try again
          </button>
        </p>
      } @else if (orders().length === 0) {
        <div class="acc-empty">
          <p class="acc__muted" data-testid="orders-empty">You have not placed an order yet.</p>
          <a class="btn btn--primary" routerLink="/">Start shopping</a>
        </div>
      } @else {
        <p class="acc__muted" data-testid="orders-count">{{ rangeLabel() }}</p>

        <ul class="acc-orders" data-testid="orders-list">
          @for (order of orders(); track order.token) {
            <li class="acc-order" [attr.data-order]="order.orderNumber">
              <!-- A second link to the same place, taken out of the accessibility tree and out of the
                   tab order — the shape 'bag-page.ts' uses. A picture is a bigger target than a line of
                   text on a telephone, and a screen reader hearing the same destination twice is noise. -->
              <a
                class="acc-order__thumb"
                [routerLink]="['/order', order.token]"
                tabindex="-1"
                aria-hidden="true"
              >
                @if (order.firstLineThumbnailUrl; as thumbnail) {
                  <img
                    [src]="thumbnail"
                    alt=""
                    width="72"
                    height="96"
                    loading="lazy"
                    decoding="async"
                  />
                }
              </a>

              <div class="acc-order__detail">
                <!-- The order number is the link, and it is the accessible name of the row's
                     destination: "SD-2026-00041" is what the shopper quotes to the shop and what they
                     are looking for when they scan the list. -->
                <a
                  class="acc-order__number"
                  [routerLink]="['/order', order.token]"
                  data-testid="order-link"
                  >{{ order.orderNumber }}</a
                >

                <dl class="acc-order__facts">
                  <div>
                    <dt>Placed</dt>
                    <dd data-testid="order-date">{{ date(order) }}</dd>
                  </div>
                  <div>
                    <dt>Status</dt>
                    <!-- The server's own label, never 'status' lower-cased: doc 11 P9's pickup variant
                         reads 'Shipped' as "Ready for collection". -->
                    <dd data-testid="order-status">{{ order.statusLabel }}</dd>
                  </div>
                  <div>
                    <dt>Items</dt>
                    <dd data-testid="order-items">{{ itemsLabel(order) }}</dd>
                  </div>
                  <div>
                    <dt>Total</dt>
                    <dd data-testid="order-total">{{ money(order.grandTotal) }}</dd>
                  </div>
                </dl>

                @if (order.tracking; as tracking) {
                  <!-- **Doc 11 P11 step 12's courier tracking, on the list.** The block is absent, not
                       null-filled, until the order ships — and a store-pickup order never has one,
                       because there is no courier. -->
                  <p class="acc-order__tracking" data-testid="order-tracking">
                    <span class="acc-order__courier">{{ tracking.courierName }}</span>
                    @if (tracking.trackingUrl; as url) {
                      <!-- 'rel="noopener noreferrer"' because the template this was built from is
                           admin-editable (doc 08 §3.5), so the destination is not the shop's own; and
                           the new tab is announced, because WCAG 2.2 SC 3.2.5 asks for a warning rather
                           than a surprise. -->
                      <a
                        class="link acc-inline-action"
                        [href]="url"
                        target="_blank"
                        rel="noopener noreferrer"
                        data-testid="order-tracking-link"
                        >{{ tracking.trackingNumber }}<span class="sr-only"> (opens in a new tab)</span></a
                      >
                    } @else {
                      <!-- The courier has no online tracking, or has been deleted since. The number is
                           still the thing the shopper needs, so it is printed as text to quote. -->
                      <span data-testid="order-tracking-number">{{ tracking.trackingNumber }}</span>
                    }
                  </p>
                }
              </div>
            </li>
          }
        </ul>

        @if (totalPages() > 1) {
          <nav class="acc-pager" aria-label="Order history pages">
            <!-- Neither control is ever removed or 'disabled'. See the class docblock. -->
            <button
              class="btn btn--secondary"
              type="button"
              [attr.aria-disabled]="pageNumber() <= 1 ? 'true' : null"
              data-testid="orders-previous"
              (click)="goTo(pageNumber() - 1)"
            >
              Previous
            </button>

            <p class="acc-pager__at" data-testid="orders-pager-at">
              Page {{ pageNumber() }} of {{ totalPages() }}
            </p>

            <button
              class="btn btn--secondary"
              type="button"
              [attr.aria-disabled]="hasMore() ? null : 'true'"
              data-testid="orders-next"
              (click)="goTo(pageNumber() + 1)"
            >
              Next
            </button>
          </nav>
        }
      }
    </div>
  `,
  styles: `
    .acc-empty {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      padding-block: var(--space-4);
    }
    .acc-orders {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding-top: var(--space-2);
      min-width: 0;
    }
    /* The thumbnail beside the detail at every width, because a 72 px picture does not need a
       breakpoint to fit next to text at 320 px: 16 px gutter + 72 + 16 gap leaves 216 px, and the
       facts list below wraps inside it. 'minmax(0, 1fr)' is what keeps a long order number or a long
       courier name from pushing the row — and therefore the page — wider than the viewport. */
    .acc-order {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      gap: var(--space-2);
      min-width: 0;
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
    }
    /* The box is reserved before the bytes arrive (CLS), and an order whose first line has no stored
       thumbnail leaves a tinted gap rather than a collapsed column that moves the text. */
    .acc-order__thumb {
      display: block;
      width: 72px;
      height: 96px;
      background: var(--color-bg-alt);
    }
    .acc-order__detail {
      display: grid;
      gap: var(--space-0-5);
      align-content: start;
      min-width: 0;
    }
    /* Full tap height, because it is the row's primary control (GLB-09's 44 px floor), and
       'overflow-wrap' because an order number is a long unbroken string. */
    .acc-order__number {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      font-weight: var(--weight-medium);
      overflow-wrap: anywhere;
    }
    .acc-order__facts {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-0-5) var(--space-3);
      font-size: var(--text-small);
      min-width: 0;
    }
    .acc-order__facts > div {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .acc-order__facts dt {
      color: var(--color-text-muted);
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
    }
    .acc-order__tracking {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-0-5) var(--space-1);
      font-size: var(--text-small);
      min-width: 0;
    }
    .acc-order__courier {
      color: var(--color-text-muted);
    }
    /* **A tracking number is one unbroken word of whatever length the courier chose**, and a flex item's
       automatic minimum width is its longest word, so a 60-character number pushed the page 127 / 100 /
       92 px sideways at 320 / 375 / 390 (independent tester, W3-01). 'min-width: 0' lets the item be
       narrower than that word and 'overflow-wrap: anywhere' lets the word break; 'anywhere' rather than
       'break-word' because only 'anywhere' lowers the min-content size that flex layout reads. */
    .acc-order__tracking > * {
      min-width: 0;
      max-width: 100%;
      overflow-wrap: anywhere;
    }
    .acc-pager {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1) var(--space-2);
      padding-top: var(--space-4);
    }
    .acc-pager__at {
      font-size: var(--text-small);
    }
  `,
})
export class OrdersPage {
  /**
   * `?page=`, as the address bar has it.
   *
   * A string, because that is what a query parameter is, and because a hand-typed `?page=banana` has to
   * be a page number this component decides rather than a `NaN` it passes on. {@link page} is the
   * decision.
   */
  readonly page = input<string | undefined>();

  private readonly api = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly result = signal(EMPTY_ORDER_PAGE);
  private readonly failure = signal<string | null>(null);
  private readonly announcement = signal<string | null>(null);

  /**
   * What the page knows: `waiting` until an answer arrives, then `ready` or `failed`.
   *
   * `waiting` is what the server renders and what the browser's first frame shows, and it is **not** the
   * same as an empty list — see the class docblock. A failed read moves to `failed` and **keeps whatever
   * rows were already in hand**, so paging onto a page the shop could not answer leaves the previous
   * page on screen with the reason beside it rather than an empty list.
   */
  private readonly state = signal<'waiting' | 'ready' | 'failed'>('waiting');

  /**
   * Whether the browser has taken over from the server.
   *
   * False on the server for everyone, and false in the browser until `afterNextRender`.
   */
  private readonly asked = signal(false);

  /** Guards against an answer from a page the shopper has already paged away from. */
  private ticket = 0;

  protected readonly phase = this.state.asReadonly();
  protected readonly error = this.failure.asReadonly();
  protected readonly said = this.announcement.asReadonly();
  protected readonly orders = computed(() => this.result().orders);
  protected readonly hasMore = computed(() => this.result().hasMore);

  /**
   * Which page is asked for: a whole number of at least 1.
   *
   * Anything else — absent, blank, `banana`, `-3`, `2.5`, `1e9` — is page 1. The server clamps too, and
   * both are needed: this one decides what the controls say and what is asked for, the server's decides
   * what is answered.
   */
  protected readonly pageNumber = computed(() => {
    const raw = this.page();
    const parsed = Number(raw);

    return raw !== undefined && Number.isSafeInteger(parsed) && parsed >= 1 ? parsed : 1;
  });

  /**
   * How many pages there are, from the server's own `totalCount` and `pageSize`.
   *
   * Zero before anything has been read (`pageSize` is 0 in {@link EMPTY_ORDER_PAGE}, deliberately, so
   * there is no page size written in the front end at all), which is why the pager is drawn only once
   * this is above 1.
   */
  protected readonly totalPages = computed(() => {
    const { totalCount, pageSize } = this.result();

    return pageSize > 0 ? Math.ceil(totalCount / pageSize) : 0;
  });

  /** "Orders 11 to 20 of 34", or "4 orders" when they all fit on one page. */
  protected readonly rangeLabel = computed(() => {
    const { orders, page, pageSize, totalCount } = this.result();

    if (this.totalPages() <= 1) {
      return totalCount === 1 ? '1 order.' : `${totalCount} orders.`;
    }

    const first = (page - 1) * pageSize + 1;

    return `Orders ${first} to ${first + orders.length - 1} of ${totalCount}.`;
  });

  constructor() {
    privatePage('Your orders', 'Your orders, their status and their courier tracking.');

    // The signed-in guard has already resolved the session for a browser navigation, and this shares its
    // answer rather than making a second request (`AccountStore.load` is single-flight). It is here so
    // that `AccountNav`'s "confirm your email" note has something to draw from on a cold load, where the
    // header's own read and this one are the same request.
    inject(AccountStore).load().subscribe();

    if (this.isBrowser) {
      // **One effect for both the first read and every page change**, rather than a read in
      // `afterNextRender` and a second path for later navigations. It depends on exactly two things —
      // has the browser taken over, and which page is asked for — and reads everything else untracked,
      // so it fires once on hydration and once per page change and never on an answer arriving.
      effect(() => {
        const ready = this.asked();
        const page = this.pageNumber();

        untracked(() => {
          if (ready) {
            this.read(page);
          }
        });
      });

      // The earliest honest moment: the browser has hydrated the markup the server sent, so anything
      // added now is an ordinary update rather than a node Angular never sent.
      afterNextRender(() => this.asked.set(true));
    }
  }

  protected money(amount: number): string {
    return formatLkr(amount);
  }

  /**
   * When the order was placed, or null.
   *
   * **`currency` on the row is deliberately not used.** The shop prices in rupees only — multi-currency
   * pricing is out of scope (doc 11 §1.3) — and `formatLkr` is the one formatter the catalogue, the bag,
   * checkout and the order page all print through, by hand rather than with `Intl` so the server's render
   * and the browser's hydration cannot disagree by a separator. The field is echoed by the server for the
   * day the shop has a second currency; printing `order.currency` beside a hard-coded `Rs.` would be two
   * currencies in one line today.
   */
  protected date(order: CustomerOrderSummaryDto): string | null {
    return accountDate(order.placedAtUtc);
  }

  /** "3 items", counting quantities, which is what the server's `itemCount` already is. */
  protected itemsLabel(order: CustomerOrderSummaryDto): string {
    return order.itemCount === 1 ? '1 item' : `${order.itemCount} items`;
  }

  /**
   * Goes to another page, by navigating — so the address bar, the back button and `returnTo` all agree.
   *
   * **It refuses rather than being disabled** (#109): a request for page 0 or for one past the end is the
   * press of an `aria-disabled` control and is answered by doing nothing, which leaves focus exactly
   * where the shopper put it. `hasMore` is the server's own flag rather than arithmetic here.
   */
  protected goTo(page: number): void {
    if (page < 1 || (page > this.pageNumber() && !this.hasMore())) {
      return;
    }

    void this.router.navigate([], {
      // `page: null` for page 1 so the first page's address is `/account/orders` and not
      // `/account/orders?page=1`: two addresses for one page is two entries in the back button.
      queryParams: { page: page === 1 ? null : page },
      queryParamsHandling: 'merge',
    });
  }

  private read(page: number): void {
    const ticket = ++this.ticket;

    this.failure.set(null);

    this.api.orders(page).subscribe({
      next: (result) => {
        if (ticket !== this.ticket) {
          return;
        }

        this.result.set(result);
        this.state.set('ready');

        // `role="status"` announces a *change*, so setting the region to the text it already holds is
        // silence — which is why this is the range sentence and not "Loaded". The page number is in it,
        // so paging forward and back always says something new.
        this.announcement.set(
          result.totalCount === 0 ? null : `Showing ${this.rangeLabel().replace(/\.$/, '')}.`,
        );
      },
      error: (error: unknown) => {
        if (ticket !== this.ticket) {
          return;
        }

        this.failure.set(accountErrorMessage(error));
        this.state.set('failed');
      },
    });
  }

  /** Asks again for the page the address bar names. The only control the failed state offers. */
  protected retry(): void {
    this.read(this.pageNumber());
  }
}
