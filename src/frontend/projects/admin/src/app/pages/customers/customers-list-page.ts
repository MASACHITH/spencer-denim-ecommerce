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

import { AdminCustomerApi } from '../../core/customers/admin-customer-api';
import {
  AdminCountryOption,
  AdminCustomerListPage,
  AdminDistrictOption,
} from '../../core/customers/admin-customer.model';
import { colomboDate, money } from '../../core/orders/order-formatting';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/** How many customers a page holds. The API clamps anything above 100. */
const PAGE_SIZE = 25;

/**
 * The admin customer list (ADM-15, doc 11 P11 step 16; `Customers.View`).
 *
 * **Registered accounts only.** A guest who never registered has no account to list; they are found on
 * the Orders screen by their email address. The hint says so, because "why is this person not here" is
 * the question a staff member on the telephone will ask.
 *
 * **Search and paging are the server's**, because this list holds every customer the shop ever has. The
 * box searches on Search, not as you type, for the order list's reason: the server reads an email
 * address, a telephone number and a name differently, so a half-typed term is a different query.
 *
 * **Read only.** Nothing on this screen changes a customer; anonymising one is doc 11 P17, not built here.
 */
@Component({
  selector: 'sda-customers-list-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Customers</h1>
      <p class="adm-panel__hint">
        Registered customer accounts. A guest who checked out without an account is not listed here — find
        their orders on the Orders screen by email address.
      </p>

      <form class="customers-search" (submit)="submitSearch($event)" role="search">
        <label class="adm-field adm-field--wide">
          <span class="adm-label">Find a customer</span>
          <input
            class="adm-input"
            type="search"
            name="search"
            autocomplete="off"
            maxlength="100"
            [value]="searchText()"
            aria-describedby="customer-search-hint"
            data-testid="customer-search"
            (input)="typeSearch($event)"
          />
        </label>
        <p class="adm-panel__hint" id="customer-search-hint">
          An email address (from its start), a telephone number, or part of a name.
        </p>

        <label class="adm-field adm-field--wide">
          <span class="adm-label">Country</span>
          <select
            class="adm-input"
            name="country"
            aria-describedby="customer-country-hint"
            data-testid="customer-country"
            (change)="chooseCountry($event)"
          >
            <option value="" [selected]="countryCode() === null">Every country</option>
            @for (country of countryChoices(); track country.code) {
              <option [value]="country.code" [selected]="countryCode() === country.code">
                {{ country.name }}
              </option>
            }
          </select>
        </label>
        <p class="adm-panel__hint" id="customer-country-hint">
          The country of the customer's default saved address. A customer with no saved address is in no country.
        </p>

        @if (districtApplies()) {
          <label class="adm-field adm-field--wide">
            <span class="adm-label">Default district</span>
            <select
              class="adm-input"
              name="district"
              data-testid="customer-district"
              (change)="chooseDistrict($event)"
            >
              <option value="" [selected]="districtId() === null">Every district</option>
              @for (district of districts(); track district.id) {
                <option [value]="district.id" [selected]="districtId() === district.id">
                  {{ district.name }}
                </option>
              }
            </select>
          </label>
        } @else {
          <p class="adm-panel__hint" data-testid="customer-district-note">
            Districts are Sri Lankan, so the district filter is hidden while another country is chosen.
          </p>
        }

        <div class="adm-actions">
          <button class="adm-btn--primary" type="submit" data-testid="customer-search-submit">Search</button>
          @if (searchApplied() || districtId() !== null || countryCode() !== null) {
            <button class="adm-btn" type="button" data-testid="customer-search-clear" (click)="clear()">
              Clear
            </button>
          }
        </div>
      </form>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="customers-retry" (click)="reload()">
              Try again
            </button>
          </div>
        }
      </div>
    }

    <!-- tabindex="-1" so focus can land here when a search destroys the row that had it (#109). -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="customers-status">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading customers…</p>
    } @else if (page() === null) {
      <!-- A failed read; the refusal is shown above. -->
    } @else if (isEmpty()) {
      <p class="adm-muted" data-testid="customers-empty">{{ emptyMessage() }}</p>
    } @else {
      <ul class="adm-list" data-testid="customers">
        @for (row of page()!.rows; track row.customerId) {
          <li class="customers-row" [attr.data-testid]="'customer-' + row.customerId">
            <a
              class="customers-row__open"
              [routerLink]="['/customers', row.customerId]"
              [attr.data-testid]="'open-customer-' + row.customerId"
            >
              <span class="customers-row__name">{{ row.name }}</span>
              <span class="adm-muted customers-row__email">{{ row.email }}</span>
            </a>
            @if (!row.emailConfirmed || !row.isActive) {
              <p class="customers-row__flags">
                @if (!row.emailConfirmed) {
                  <span class="adm-pill" [attr.data-testid]="'unconfirmed-' + row.customerId">
                    Email not confirmed
                  </span>
                }
                @if (!row.isActive) {
                  <span class="adm-pill adm-pill--hidden">Disabled</span>
                }
              </p>
            }
            <dl class="customers-row__facts">
              <dt>Telephone</dt>
              <dd>{{ row.phone ?? 'Not given' }}</dd>
              <dt>District</dt>
              <dd [attr.data-testid]="'district-' + row.customerId">{{ row.districtName ?? 'Not chosen' }}</dd>
              <dt>Country</dt>
              <dd [attr.data-testid]="'country-' + row.customerId">{{ row.countryName ?? 'No saved address' }}</dd>
              <dt>Orders</dt>
              <dd [attr.data-testid]="'orders-' + row.customerId">{{ row.orderCount }}</dd>
              <dt>Lifetime spend</dt>
              <dd [attr.data-testid]="'spend-' + row.customerId">{{ cash(row.lifetimeSpend, row.currency) }}</dd>
              <dt>Last order</dt>
              <dd>{{ row.lastOrderAtUtc ? day(row.lastOrderAtUtc) : 'None yet' }}</dd>
              <dt>Registered</dt>
              <dd>{{ day(row.registeredAtUtc) }}</dd>
            </dl>
          </li>
        }
      </ul>

      <nav class="adm-actions customers-paging" aria-label="Pages">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="page()!.page <= 1 ? 'true' : null"
          data-testid="customers-prev"
          (click)="go(page()!.page - 1)"
        >
          Previous
        </button>
        <p class="adm-muted" data-testid="customers-paging">
          {{ firstOnPage() }}–{{ lastOnPage() }} of {{ page()!.totalCount }}
        </p>
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="lastOnPage() >= page()!.totalCount ? 'true' : null"
          data-testid="customers-next"
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
    .customers-search {
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
    .customers-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .customers-row__open {
      display: grid;
      gap: 2px;
      min-height: var(--tap);
      align-content: center;
      text-decoration: none;
      color: inherit;
      min-width: 0;
    }
    .customers-row__name {
      font-weight: var(--weight-semibold);
      text-decoration: underline;
      overflow-wrap: anywhere;
    }
    .customers-row__email {
      overflow-wrap: anywhere;
    }
    .customers-row__flags {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      margin: 0;
    }
    .customers-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .customers-row__facts dt {
      color: var(--color-text-muted);
    }
    .customers-row__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .customers-paging {
      align-items: center;
    }
  `,
})
export class CustomersListPage {
  private readonly api = inject(AdminCustomerApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly page = signal<AdminCustomerListPage | null>(null);

  /** The filter's options, kept across a failed read so the select does not empty itself. */
  protected readonly districts = signal<readonly AdminDistrictOption[]>([]);
  protected readonly countries = signal<readonly AdminCountryOption[]>([]);

  protected readonly districtId = signal<number | null>(null);
  /** The chosen country's ISO code, or null for every country. */
  protected readonly countryCode = signal<string | null>(null);

  /** The server's countries, plus the chosen one if a later answer no longer lists it, so the box never says "Every country" while one is still sent. */
  protected readonly countryChoices = computed<readonly AdminCountryOption[]>(() => {
    const chosen = this.countryCode();
    const listed = this.countries();

    return chosen === null || listed.some((country) => country.code === chosen)
      ? listed
      : [...listed, { code: chosen, name: chosen }];
  });

  /** A district belongs to Sri Lanka: it is offered for every country and for Sri Lanka, never for another. */
  protected readonly districtApplies = computed(() => this.countryCode() === null || this.countryCode() === 'LK');
  protected readonly searchText = signal('');
  protected readonly searchApplied = signal('');

  protected readonly isLoading = signal(true);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  protected readonly isEmpty = computed(() => (this.page()?.rows.length ?? -1) === 0);

  protected readonly firstOnPage = computed(() => {
    const answered = this.page();
    return answered === null || answered.totalCount === 0
      ? 0
      : (answered.page - 1) * answered.pageSize + 1;
  });

  protected readonly lastOnPage = computed(() => {
    const answered = this.page();
    return answered === null ? 0 : Math.min(answered.page * answered.pageSize, answered.totalCount);
  });

  protected readonly emptyMessage = computed(() => {
    const term = this.searchApplied();
    const district = this.districtName();
    const country = this.countryName();
    const where = district ?? country;

    if (term.length > 0) {
      return `No customer matches “${term}”${where ? ` in ${where}` : ''}. A guest who never `
        + 'registered is not a customer account — search the Orders screen by email instead.';
    }

    if (district !== null) {
      return `No customer has chosen ${district} as their default district.`;
    }

    return country === null
      ? 'No customer has registered yet.'
      : `No customer's default saved address is in ${country}.`;
  });

  constructor() {
    this.load(1);
  }

  protected cash(amount: number, currency: string): string {
    return money(amount, currency);
  }

  protected day(utc: string): string {
    return colomboDate(utc);
  }

  protected typeSearch(event: Event): void {
    this.searchText.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected submitSearch(event: Event): void {
    event.preventDefault();

    const term = this.searchText().trim();
    this.searchApplied.set(term);
    this.load(1, term.length === 0 ? 'Showing every customer.' : `Searching for “${term}”…`);
  }

  /** A district applies at once: unlike the search box, a choice from a list is never half-made. */
  protected chooseDistrict(event: Event): void {
    const value = (event.target as HTMLSelectElement | null)?.value ?? '';
    this.districtId.set(value === '' ? null : Number(value));

    const district = this.districtName();
    this.load(1, district === null ? 'Showing every district.' : `Showing customers in ${district}.`);
  }

  /**
   * A country applies at once, for the district's reason. Choosing another country than Sri Lanka also drops
   * the district, which no longer applies to it and would otherwise keep filtering unseen.
   */
  protected chooseCountry(event: Event): void {
    const value = (event.target as HTMLSelectElement | null)?.value ?? '';
    this.countryCode.set(value === '' ? null : value);

    if (!this.districtApplies()) {
      this.districtId.set(null);
    }

    const country = this.countryName();
    this.load(1, country === null ? 'Showing every country.' : `Showing customers in ${country}.`);
  }

  protected clear(): void {
    this.searchText.set('');
    this.searchApplied.set('');
    this.districtId.set(null);
    this.countryCode.set(null);
    this.load(1, 'Search cleared.');
  }

  protected go(page: number): void {
    const answered = this.page();

    // The refusal `[disabled]` would have given, which is why neither paging button is disabled: a
    // disabled button whose state changes under the user throws focus to <body> (#109).
    if (answered === null || page < 1 || (page - 1) * answered.pageSize >= answered.totalCount) {
      return;
    }

    this.load(page, `Page ${page}.`);
  }

  protected reload(): void {
    this.load(this.page()?.page ?? 1);
  }

  private countryName(): string | null {
    const code = this.countryCode();
    return code === null ? null : (this.countries().find((country) => country.code === code)?.name ?? null);
  }

  private districtName(): string | null {
    const id = this.districtId();
    return id === null ? null : (this.districts().find((district) => district.id === id)?.name ?? null);
  }

  private load(page: number, announcement?: string): void {
    // Captured before the list is replaced: the only moment the focused element can still be identified.
    const focused = this.document.activeElement as HTMLElement | null;

    this.isLoading.set(true);
    this.problem.set(null);

    if (announcement !== undefined) {
      this.announce(announcement);
    }

    this.api
      .list({
        search: this.searchApplied().length === 0 ? null : this.searchApplied(),
        districtId: this.districtId(),
        countryCode: this.countryCode(),
        page,
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (answered) => {
          this.page.set(answered);
          this.districts.set(answered.districts);
          this.countries.set(answered.countries);
          this.isLoading.set(false);
          this.announce(this.summarise(answered));
          this.rehomeFocusIfLost(focused);
        },
        error: (error: unknown) => {
          this.isLoading.set(false);
          this.page.set(null);
          const failure = toCatalogFailure(error);
          this.problem.set(failure);
          this.announce(failure.message);
          this.rehomeFocusIfLost(focused);
        },
      });
  }

  private summarise(answered: AdminCustomerListPage): string {
    if (answered.totalCount === 0) {
      return this.emptyMessage();
    }

    const of = answered.totalCount === 1 ? 'customer' : 'customers';

    return `${answered.totalCount} ${of}, newest first. Showing ${this.firstOnPage()} to ${this.lastOnPage()}.`;
  }

  /**
   * Re-homes focus only if the load destroyed the element that had it — the order list's rule and its
   * reason. **`afterNextRender`, never `queueMicrotask`**: a microtask runs before change detection, so
   * every node would still look connected.
   */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="customers-status"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /** Puts a sentence in the live region, re-firing it when it is the same sentence (ADM-22's pattern). */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
