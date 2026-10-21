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

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { OverseasDeliveryApi } from '../../core/delivery/overseas-delivery-api';
import {
  CountryDeliveryRow,
  DeliveryRegionRow,
  OVERSEAS_PERMISSIONS,
  OverseasDeliveryScreen,
  daysProblem,
  feeProblem,
  numberOrNull,
  refusalText,
} from '../../core/delivery/overseas-delivery.model';
import { money } from '../../core/orders/order-formatting';

/** The editor's boxes for one region, as typed. */
interface RegionDraft {
  readonly fee: string;
  readonly min: string;
  readonly max: string;
  readonly enabled: boolean;
}

/** The editor's boxes for one country, as typed. Blank means "use the region's". */
interface CountryDraft {
  readonly fee: string;
  readonly min: string;
  readonly max: string;
  readonly enabled: boolean;
}

/**
 * Overseas delivery (owner decision 2026-10-21: Sri Lanka plus worldwide delivery).
 *
 * Three things, in the order a staff member needs them: the International delivery switch (every overseas order
 * at once), the regions (a fee in LKR and an estimate each), and the countries (each one's own switch and an
 * optional fee and estimate of its own). Sri Lanka is not here: it is priced by district.
 *
 * **What a shopper is charged is shown beside every country**, worked out on the server by the same function
 * checkout prices with, so the screen and checkout cannot disagree. A country that cannot be ordered to says
 * why.
 *
 * `Settings.View` to look, `Settings.Edit` to change. The guard asks only for a session, as every admin route
 * does: the API is the security boundary, and the page says so to a role without `Settings.View` rather than
 * bouncing it.
 */
@Component({
  selector: 'sda-overseas-delivery-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <p><a class="adm-link" routerLink="/settings">Settings</a></p>
      <h1 class="adm-panel__title">Overseas delivery</h1>
      <p class="adm-panel__hint">
        What a parcel sent abroad costs, by region, and which countries you send to. Overseas parcels go by an
        international service such as Sri Lanka Post EMS or DHL. Prices stay in rupees; PayPal charges the
        US-dollar equivalent at your exchange rate. Sri Lankan delivery is priced by district and is not on
        this page.
      </p>
      <p class="adm-panel__hint" data-testid="overseas-placeholder-note">
        The fees set up at installation are placeholders for the owner to confirm — check every region before
        taking overseas orders.
      </p>
    </section>

    <!-- tabindex="-1" so focus can land here when a save redraws the row that was being edited. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="overseas-said">{{ said() }}</p>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="overseas-problem">
        <p>{{ failure.kind === 'forbidden' ? 'You do not have permission to see overseas delivery.' : failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions">
            <button class="adm-btn" type="button" (click)="load()">Reload</button>
          </div>
        }
      </div>
    }

    @if (loading()) {
      <p class="adm-muted">Loading overseas delivery…</p>
    } @else if (screen(); as loaded) {
      <section class="adm-panel" aria-labelledby="intl-title" data-testid="overseas-international">
        <h2 class="adm-panel__title" id="intl-title">International delivery</h2>
        @if (canEdit()) {
          <span class="od-check">
            <input
              type="checkbox"
              id="intl-enabled"
              [checked]="loaded.internationalEnabled"
              data-testid="overseas-international-switch"
              (change)="toggleInternational($event)"
            />
            <label for="intl-enabled">Accept orders to addresses outside Sri Lanka</label>
          </span>
        } @else {
          <p data-testid="overseas-international-state">
            {{ loaded.internationalEnabled ? 'Overseas orders are accepted.' : 'Overseas orders are switched off.' }}
          </p>
        }
        @if (!loaded.internationalEnabled) {
          <p class="adm-panel__hint">While this is off, checkout refuses every country but Sri Lanka.</p>
        }
      </section>

      <section class="adm-panel" aria-labelledby="regions-title">
        <h2 class="adm-panel__title" id="regions-title">Regions</h2>
        <p class="adm-panel__hint">
          Every country is charged its region's fee and estimate unless it has its own. A region with no fee
          cannot be ordered to. Switching a region off switches off every country in it.
        </p>
        <ul class="od-list" data-testid="overseas-regions">
          @for (region of loaded.regions; track region.code) {
            <li class="od-row" [attr.data-testid]="'region-' + region.code">
              <p class="od-row__name">
                {{ region.name }}
                @if (!region.isEnabled) {
                  <span class="adm-pill adm-pill--hidden">Switched off</span>
                }
                <span class="adm-muted od-small">{{ region.countryCount }} {{ region.countryCount === 1 ? 'country' : 'countries' }}</span>
              </p>
              @if (canEdit()) {
                @let draft = regionDraft(region);
                <form class="adm-fields" (submit)="saveRegion($event, region)">
                  <label class="adm-field">
                    <span class="adm-label">Fee (Rs.)</span>
                    <input
                      class="adm-input"
                      type="text"
                      inputmode="decimal"
                      autocomplete="off"
                      [value]="draft.fee"
                      [attr.aria-invalid]="regionError(region.code, 'fee') ? 'true' : null"
                      [attr.data-testid]="'region-fee-' + region.code"
                      (input)="editRegion(region, 'fee', $event)"
                    />
                    @if (regionError(region.code, 'fee'); as message) {
                      <span class="adm-field-error" role="alert" [attr.data-testid]="'region-error-fee-' + region.code">{{ message }}</span>
                    }
                  </label>
                  <fieldset class="adm-field od-days">
                    <legend class="adm-label">Estimated days</legend>
                    <span class="od-days__boxes">
                      <input
                        class="adm-input"
                        type="text"
                        inputmode="numeric"
                        autocomplete="off"
                        aria-label="Fewest days"
                        [value]="draft.min"
                        [attr.aria-invalid]="regionError(region.code, 'estimatedDays') ? 'true' : null"
                        [attr.data-testid]="'region-min-' + region.code"
                        (input)="editRegion(region, 'min', $event)"
                      />
                      <span aria-hidden="true">to</span>
                      <input
                        class="adm-input"
                        type="text"
                        inputmode="numeric"
                        autocomplete="off"
                        aria-label="Most days"
                        [value]="draft.max"
                        [attr.aria-invalid]="regionError(region.code, 'estimatedDays') ? 'true' : null"
                        [attr.data-testid]="'region-max-' + region.code"
                        (input)="editRegion(region, 'max', $event)"
                      />
                    </span>
                    @if (regionError(region.code, 'estimatedDays'); as message) {
                      <span class="adm-field-error" role="alert" [attr.data-testid]="'region-error-days-' + region.code">{{ message }}</span>
                    }
                  </fieldset>
                  <span class="od-check">
                    <input
                      type="checkbox"
                      [id]="'region-enabled-' + region.code"
                      [checked]="draft.enabled"
                      [attr.data-testid]="'region-enabled-' + region.code"
                      (change)="editRegionEnabled(region, $event)"
                    />
                    <label [for]="'region-enabled-' + region.code">Switched on</label>
                  </span>
                  <div class="adm-actions">
                    <button class="adm-btn--primary" type="submit" [attr.aria-disabled]="busy() ? 'true' : null" [attr.data-testid]="'region-save-' + region.code">
                      Save {{ region.name }}
                    </button>
                  </div>
                </form>
              } @else {
                <dl class="od-facts">
                  <dt>Fee</dt>
                  <dd>{{ region.fee === null ? 'Not priced' : rupees(region.fee) }}</dd>
                  <dt>Estimate</dt>
                  <dd>{{ region.estimatedDaysMin }}–{{ region.estimatedDaysMax }} days</dd>
                </dl>
              }
            </li>
          }
        </ul>
      </section>

      <section class="adm-panel" aria-labelledby="countries-title">
        <h2 class="adm-panel__title" id="countries-title">Countries</h2>
        <div class="adm-fields">
          <label class="adm-field">
            <span class="adm-label">Find a country</span>
            <input
              class="adm-input"
              type="search"
              autocomplete="off"
              [value]="search()"
              data-testid="country-search"
              (input)="search.set(text($event))"
            />
          </label>
          <label class="adm-field">
            <span class="adm-label">Region</span>
            <select class="adm-input" data-testid="country-region-filter" (change)="regionFilter.set(text($event))">
              <option value="" [selected]="regionFilter() === ''">All regions</option>
              @for (region of loaded.regions; track region.code) {
                <option [value]="region.code" [selected]="regionFilter() === region.code">{{ region.name }}</option>
              }
            </select>
          </label>
        </div>
        <p class="adm-muted" data-testid="country-count">{{ shown().length }} of {{ loaded.countries.length }} countries</p>
        <ul class="od-list" data-testid="overseas-countries">
          @for (country of shown(); track country.code) {
            <li class="od-row" [attr.data-testid]="'country-' + country.code">
              <p class="od-row__name">
                {{ country.name }}
                <span class="adm-muted od-small">{{ regionName(country.regionCode) }}</span>
              </p>
              <p class="od-small" [attr.data-testid]="'country-effective-' + country.code">
                @if (country.isAvailable) {
                  {{ rupees(country.effectiveFee ?? 0) }} · {{ country.effectiveDaysMin }}–{{ country.effectiveDaysMax }} days
                  @if (country.deliveryFeeOverride !== null || country.estimatedDaysMinOverride !== null) {
                    <span class="adm-pill">Own settings</span>
                  }
                } @else {
                  <span class="adm-pill adm-pill--hidden">Not delivered to</span> {{ refusal(country.refusal) }}
                }
              </p>
              @if (canEdit()) {
                @if (editing() === country.code) {
                  @let draft = countryDraft(country);
                  <form class="adm-fields" (submit)="saveCountry($event, country)">
                    <span class="od-check">
                      <input
                        type="checkbox"
                        [id]="'country-enabled-' + country.code"
                        [checked]="draft.enabled"
                        [attr.data-testid]="'country-enabled-' + country.code"
                        (change)="editCountryEnabled(country, $event)"
                      />
                      <label [for]="'country-enabled-' + country.code">Deliver to {{ country.name }}</label>
                    </span>
                    <label class="adm-field">
                      <span class="adm-label">Own fee (Rs.) — blank uses the region's</span>
                      <input
                        class="adm-input"
                        type="text"
                        inputmode="decimal"
                        autocomplete="off"
                        [value]="draft.fee"
                        [attr.aria-invalid]="countryError(country.code, 'deliveryFeeOverride') ? 'true' : null"
                        [attr.data-testid]="'country-fee-' + country.code"
                        (input)="editCountry(country, 'fee', $event)"
                      />
                      @if (countryError(country.code, 'deliveryFeeOverride'); as message) {
                        <span class="adm-field-error" role="alert" [attr.data-testid]="'country-error-fee-' + country.code">{{ message }}</span>
                      }
                    </label>
                    <fieldset class="adm-field od-days">
                      <legend class="adm-label">Own estimated days — blank uses the region's</legend>
                      <span class="od-days__boxes">
                        <input
                          class="adm-input"
                          type="text"
                          inputmode="numeric"
                          autocomplete="off"
                          aria-label="Fewest days"
                          [value]="draft.min"
                          [attr.aria-invalid]="countryError(country.code, 'estimatedDaysOverride') ? 'true' : null"
                          [attr.data-testid]="'country-min-' + country.code"
                          (input)="editCountry(country, 'min', $event)"
                        />
                        <span aria-hidden="true">to</span>
                        <input
                          class="adm-input"
                          type="text"
                          inputmode="numeric"
                          autocomplete="off"
                          aria-label="Most days"
                          [value]="draft.max"
                          [attr.aria-invalid]="countryError(country.code, 'estimatedDaysOverride') ? 'true' : null"
                          [attr.data-testid]="'country-max-' + country.code"
                          (input)="editCountry(country, 'max', $event)"
                        />
                      </span>
                      @if (countryError(country.code, 'estimatedDaysOverride'); as message) {
                        <span class="adm-field-error" role="alert" [attr.data-testid]="'country-error-days-' + country.code">{{ message }}</span>
                      }
                    </fieldset>
                    <div class="adm-actions">
                      <button class="adm-btn--primary" type="submit" [attr.aria-disabled]="busy() ? 'true' : null" [attr.data-testid]="'country-save-' + country.code">
                        Save {{ country.name }}
                      </button>
                      <button class="adm-btn" type="button" [attr.data-testid]="'country-cancel-' + country.code" (click)="cancelCountry(country)">Cancel</button>
                    </div>
                  </form>
                } @else {
                  <div class="adm-actions">
                    <button class="adm-btn" type="button" [attr.data-testid]="'country-edit-' + country.code" (click)="startCountry(country)">
                      Change {{ country.name }}
                    </button>
                  </div>
                }
              }
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-3); padding: var(--gutter); min-width: 0; }
    .od-list { display: grid; gap: var(--space-2); list-style: none; padding: 0; margin: 0; min-width: 0; }
    .od-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .od-row__name { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-1); font-weight: var(--weight-semibold); overflow-wrap: anywhere; margin: 0; }
    .od-small { font-size: var(--text-small); font-weight: normal; overflow-wrap: anywhere; margin: 0; }
    .od-facts { display: grid; grid-template-columns: auto 1fr; gap: 2px var(--space-2); font-size: var(--text-small); margin: 0; }
    .od-facts dd { margin: 0; }
    .od-days { border: 0; padding: 0; margin: 0; min-width: 0; }
    .od-days__boxes { display: flex; align-items: center; gap: var(--space-1); min-width: 0; }
    .od-days__boxes input { width: 5rem; min-width: 0; }
    .od-check { display: flex; align-items: center; gap: var(--space-1); min-height: var(--tap); min-width: 0; }
    .od-check input[type='checkbox'] { width: 24px; height: 24px; flex: 0 0 auto; margin: 0; cursor: pointer; }
    .od-check label { display: flex; align-items: center; min-height: var(--tap); cursor: pointer; overflow-wrap: anywhere; }
  `,
})
export class OverseasDeliveryPage {
  private readonly api = inject(OverseasDeliveryApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);

  protected readonly screen = signal<OverseasDeliveryScreen | null>(null);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');
  protected readonly canEdit = signal(false);

  protected readonly search = signal('');
  protected readonly regionFilter = signal('');
  protected readonly editing = signal<string | null>(null);

  private readonly regionDrafts = signal<Readonly<Record<string, RegionDraft>>>({});
  private readonly countryDrafts = signal<Readonly<Record<string, CountryDraft>>>({});
  private readonly fieldErrors = signal<Readonly<Record<string, Readonly<Record<string, string>>>>>({});

  /** The countries the search and the region filter leave, by name. */
  protected readonly shown = computed(() => {
    const term = this.search().trim().toLowerCase();
    const region = this.regionFilter();

    return (this.screen()?.countries ?? []).filter(
      (country) =>
        (region === '' || country.regionCode === region) &&
        (term === '' || country.name.toLowerCase().includes(term) || country.code.toLowerCase() === term),
    );
  });

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) =>
        this.canEdit.set(session.grantsAllPermissions || session.permissions.includes(OVERSEAS_PERMISSIONS.edit)),
      // The session could not be read: show the figures and let the API refuse a change.
      error: () => this.canEdit.set(false),
    });

    this.load();
  }

  protected load(keepProblem = false): void {
    if (!keepProblem) {
      this.problem.set(null);
    }

    this.api.screen().subscribe({
      next: (screen) => this.accept(screen),
      error: (error: unknown) => {
        this.loading.set(false);
        this.problem.set(toCatalogFailure(error));
      },
    });
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
  }

  protected rupees(amount: number): string {
    return money(amount, 'LKR');
  }

  protected refusal(code: string): string {
    return refusalText(code);
  }

  protected regionName(code: string): string {
    return this.screen()?.regions.find((region) => region.code === code)?.name ?? code;
  }

  protected regionError(code: string, field: string): string | null {
    return this.fieldErrors()[`region:${code}`]?.[field] ?? null;
  }

  protected countryError(code: string, field: string): string | null {
    return this.fieldErrors()[`country:${code}`]?.[field] ?? null;
  }

  protected regionDraft(region: DeliveryRegionRow): RegionDraft {
    return (
      this.regionDrafts()[region.code] ?? {
        fee: region.fee === null ? '' : String(region.fee),
        min: String(region.estimatedDaysMin),
        max: String(region.estimatedDaysMax),
        enabled: region.isEnabled,
      }
    );
  }

  protected countryDraft(country: CountryDeliveryRow): CountryDraft {
    return (
      this.countryDrafts()[country.code] ?? {
        fee: country.deliveryFeeOverride === null ? '' : String(country.deliveryFeeOverride),
        min: country.estimatedDaysMinOverride === null ? '' : String(country.estimatedDaysMinOverride),
        max: country.estimatedDaysMaxOverride === null ? '' : String(country.estimatedDaysMaxOverride),
        enabled: country.isShippingEnabled,
      }
    );
  }

  protected editRegion(region: DeliveryRegionRow, field: 'fee' | 'min' | 'max', event: Event): void {
    const draft = { ...this.regionDraft(region), [field]: this.text(event) };
    this.regionDrafts.update((all) => ({ ...all, [region.code]: draft }));
  }

  protected editRegionEnabled(region: DeliveryRegionRow, event: Event): void {
    const enabled = (event.target as HTMLInputElement | null)?.checked ?? region.isEnabled;
    this.regionDrafts.update((all) => ({ ...all, [region.code]: { ...this.regionDraft(region), enabled } }));
  }

  protected editCountry(country: CountryDeliveryRow, field: 'fee' | 'min' | 'max', event: Event): void {
    const draft = { ...this.countryDraft(country), [field]: this.text(event) };
    this.countryDrafts.update((all) => ({ ...all, [country.code]: draft }));
  }

  protected editCountryEnabled(country: CountryDeliveryRow, event: Event): void {
    const enabled = (event.target as HTMLInputElement | null)?.checked ?? country.isShippingEnabled;
    this.countryDrafts.update((all) => ({ ...all, [country.code]: { ...this.countryDraft(country), enabled } }));
  }

  protected startCountry(country: CountryDeliveryRow): void {
    this.editing.set(country.code);
    this.clearErrors(`country:${country.code}`);
    this.focusOnNextRender(`[data-testid="country-enabled-${country.code}"]`);
  }

  protected cancelCountry(country: CountryDeliveryRow): void {
    this.editing.set(null);
    this.countryDrafts.update((all) => this.without(all, country.code));
    this.clearErrors(`country:${country.code}`);
    this.focusOnNextRender(`[data-testid="country-edit-${country.code}"]`);
  }

  protected toggleInternational(event: Event): void {
    const box = event.target as HTMLInputElement | null;
    const loaded = this.screen();
    if (loaded === null) {
      return;
    }

    if (this.busy()) {
      if (box) {
        box.checked = loaded.internationalEnabled;
      }

      return;
    }

    const wanted = box?.checked ?? loaded.internationalEnabled;
    this.begin(wanted ? 'Switching International delivery on…' : 'Switching International delivery off…');

    this.api.setInternational(wanted, loaded.internationalRowVersion).subscribe({
      next: (screen) => {
        this.accept(screen);
        this.announce(`International delivery is switched ${wanted ? 'on' : 'off'}.`);
      },
      error: (error: unknown) => {
        if (box) {
          box.checked = loaded.internationalEnabled;
        }

        this.fail(error, null);
      },
    });
  }

  protected saveRegion(event: Event, region: DeliveryRegionRow): void {
    event.preventDefault();
    if (this.busy()) {
      return;
    }

    const draft = this.regionDraft(region);
    const key = `region:${region.code}`;
    const errors: Record<string, string> = {};
    const fee = feeProblem(draft.fee, true);
    const days = daysProblem(draft.min, draft.max, false);
    if (fee) {
      errors['fee'] = fee;
    }

    if (days) {
      errors['estimatedDays'] = days;
    }

    if (Object.keys(errors).length > 0) {
      this.fieldErrors.update((all) => ({ ...all, [key]: errors }));
      this.announce(Object.values(errors).join(' '));
      return;
    }

    this.begin(`Saving ${region.name}…`);
    this.clearErrors(key);

    this.api
      .updateRegion(region.code, {
        fee: numberOrNull(draft.fee),
        estimatedDaysMin: numberOrNull(draft.min),
        estimatedDaysMax: numberOrNull(draft.max),
        isEnabled: draft.enabled,
        rowVersion: region.rowVersion,
      })
      .subscribe({
        next: (screen) => {
          this.regionDrafts.update((all) => this.without(all, region.code));
          this.accept(screen);
          this.announce(`${region.name} is saved.`);
        },
        error: (error: unknown) => this.fail(error, key),
      });
  }

  protected saveCountry(event: Event, country: CountryDeliveryRow): void {
    event.preventDefault();
    if (this.busy()) {
      return;
    }

    const draft = this.countryDraft(country);
    const key = `country:${country.code}`;
    const errors: Record<string, string> = {};
    const fee = feeProblem(draft.fee, true);
    const days = daysProblem(draft.min, draft.max, true);
    if (fee) {
      errors['deliveryFeeOverride'] = fee;
    }

    if (days) {
      errors['estimatedDaysOverride'] = days;
    }

    if (Object.keys(errors).length > 0) {
      this.fieldErrors.update((all) => ({ ...all, [key]: errors }));
      this.announce(Object.values(errors).join(' '));
      return;
    }

    this.begin(`Saving ${country.name}…`);
    this.clearErrors(key);

    this.api
      .updateCountry(country.code, {
        isShippingEnabled: draft.enabled,
        deliveryFeeOverride: numberOrNull(draft.fee),
        estimatedDaysMinOverride: numberOrNull(draft.min),
        estimatedDaysMaxOverride: numberOrNull(draft.max),
        rowVersion: country.rowVersion,
      })
      .subscribe({
        next: (screen) => {
          this.countryDrafts.update((all) => this.without(all, country.code));
          this.editing.set(null);
          this.accept(screen);
          this.announce(`${country.name} is saved.`);
          this.focusOnNextRender(`[data-testid="country-edit-${country.code}"]`);
        },
        error: (error: unknown) => this.fail(error, key),
      });
  }

  private accept(screen: OverseasDeliveryScreen): void {
    this.screen.set(screen);
    this.loading.set(false);
    this.busy.set(false);
  }

  private begin(message: string): void {
    this.busy.set(true);
    this.problem.set(null);
    this.announce(message);
  }

  /**
   * A 400 keeps the server's per-field sentences beside the boxes they are about; a 409 says the row changed
   * and reloads, because the token on screen can no longer be saved over; anything else is a banner.
   */
  private fail(error: unknown, key: string | null): void {
    this.busy.set(false);
    const failure = toCatalogFailure(error);

    if (key !== null && failure.fieldErrors && Object.keys(failure.fieldErrors).length > 0) {
      const fields = Object.fromEntries(
        Object.entries(failure.fieldErrors).map(([field, messages]) => [field, messages.join(' ')]),
      );
      this.fieldErrors.update((all) => ({ ...all, [key]: fields }));
      this.announce(Object.values(fields).join(' '));
      return;
    }

    if (failure.kind === 'conflict') {
      this.regionDrafts.set({});
      this.countryDrafts.set({});
      this.editing.set(null);
      this.problem.set({ ...failure, message: `${failure.message} The page has been reloaded with the latest values.` });
      this.announce(failure.message);
      this.load(true);
      return;
    }

    this.problem.set(failure);
    this.announce(failure.message);
  }

  private clearErrors(key: string): void {
    this.fieldErrors.update((all) => this.without(all, key));
  }

  private without<T>(all: Readonly<Record<string, T>>, key: string): Record<string, T> {
    const copy = { ...all };
    delete copy[key];
    return copy;
  }

  private focusOnNextRender(selector: string): void {
    afterNextRender(
      () => {
        const target = this.document.querySelector<HTMLElement>(selector);
        target?.focus({ preventScroll: false });
      },
      { injector: this.injector },
    );
  }

  /** @see OrdersListPage.announce — a repeated identical announcement has to re-fire. */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
