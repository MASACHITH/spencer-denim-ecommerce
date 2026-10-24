import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { OverseasDeliveryApi } from '../../core/delivery/overseas-delivery-api';
import {
  CountryDeliveryRow,
  OVERSEAS_PERMISSIONS,
  OverseasDeliveryScreen,
} from '../../core/delivery/overseas-delivery.model';

/**
 * Owner decision 2026-10-23: open-by-default model — tick = blocked, untick = open.
 *
 * A dedicated page for blocking and unblocking individual countries. Every toggle fires the existing
 * `PUT /api/admin/delivery/overseas/countries/{code}` immediately; there is no Save button. The server
 * returns the whole screen on each write, so local state is always what the server holds.
 *
 * `Settings.View` to look, `Settings.Edit` to change.
 */
@Component({
  selector: 'sda-blocked-countries-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <p><a class="adm-link" routerLink="/settings">Settings</a></p>
      <h1 class="adm-panel__title">Blocked countries</h1>
      <p class="adm-panel__hint">
        Countries you block are refused at checkout. Tick a country to block it; untick to open it
        again. All countries are open unless you block them.
      </p>
    </section>

    <!-- tabindex="-1" so focus can land here after an announcement redraws. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="bc-said">{{ said() }}</p>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="bc-problem">
        <p>{{ failure.kind === 'forbidden' ? 'You do not have permission to see blocked countries.' : failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions">
            <button class="adm-btn" type="button" (click)="load()">Reload</button>
          </div>
        }
      </div>
    }

    @if (loading()) {
      <p class="adm-muted" data-testid="bc-loading">Loading countries…</p>
    } @else if (screen(); as loaded) {
      <section class="adm-panel" aria-labelledby="bc-panel-title" data-testid="bc-panel">
        <h2 class="adm-panel__title" id="bc-panel-title">All overseas countries</h2>
        <p class="adm-panel__hint" data-testid="bc-blocked-summary">
          @if (blocked().length === 0) {
            All countries are open.
          } @else {
            Currently blocked: {{ blocked().map(c => c.code).join(', ') }}
          }
        </p>
        <div class="adm-fields">
          <label class="adm-field">
            <span class="adm-label">Search countries</span>
            <input
              class="adm-input"
              type="search"
              autocomplete="off"
              placeholder="Country name…"
              [value]="search()"
              data-testid="bc-search"
              (input)="search.set(text($event))"
            />
          </label>
        </div>
        <p class="adm-muted" data-testid="bc-count">{{ shown().length }} of {{ loaded.countries.length }} countries</p>
        <ul class="bc-list" data-testid="bc-countries">
          @for (country of shown(); track country.code) {
            <li
              class="bc-row"
              [class.bc-row--blocked]="!country.isShippingEnabled"
              [attr.data-testid]="'bc-country-' + country.code"
            >
              <span class="bc-row__info">
                <span class="bc-row__name" [attr.data-testid]="'bc-name-' + country.code">{{ country.name }}</span>
                <span class="adm-muted bc-region" [attr.data-testid]="'bc-region-' + country.code">{{ regionName(country.regionCode) }}</span>
                @if (!country.isShippingEnabled) {
                  <span class="adm-pill adm-pill--hidden" [attr.data-testid]="'bc-blocked-badge-' + country.code">Blocked</span>
                }
              </span>
              <span class="bc-row__controls">
                @if (savingRow(country.code)) {
                  <span class="adm-muted bc-spinner" [attr.data-testid]="'bc-spinner-' + country.code" aria-live="polite">Saving…</span>
                }
                @if (rowError(country.code); as message) {
                  <span class="adm-field-error" role="alert" [attr.data-testid]="'bc-error-' + country.code">{{ message }}</span>
                }
                @if (canEdit()) {
                  <span class="bc-check">
                    <input
                      type="checkbox"
                      [id]="'bc-block-' + country.code"
                      [checked]="!country.isShippingEnabled"
                      [attr.aria-disabled]="savingRow(country.code) ? 'true' : null"
                      [attr.data-testid]="'bc-toggle-' + country.code"
                      (change)="toggle(country, $event)"
                    />
                    <label [for]="'bc-block-' + country.code">Block {{ country.name }}</label>
                  </span>
                }
              </span>
            </li>
          } @empty {
            <li class="bc-row bc-row--empty">
              <span class="bc-row__info">No countries match "{{ search() }}".</span>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-3); padding: var(--gutter); min-width: 0; }

    .bc-list { display: grid; gap: var(--space-1); list-style: none; padding: 0; margin: 0; min-width: 0; }

    .bc-row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1) var(--space-2);
      padding: var(--space-1) var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }

    .bc-row--blocked { background: var(--color-surface-error, #fff5f5); }

    .bc-row--empty { color: var(--color-muted); font-style: italic; }

    .bc-row__info {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      min-width: 0;
      overflow-wrap: anywhere;
      flex: 1 1 0;
    }

    .bc-row__name { font-weight: var(--weight-semibold); overflow-wrap: anywhere; }

    .bc-region { font-size: var(--text-small); }

    .bc-row__controls {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      flex-wrap: wrap;
      flex: 0 0 auto;
    }

    .bc-spinner { font-size: var(--text-small); }

    .bc-check { display: flex; align-items: center; gap: var(--space-1); min-height: var(--tap); }

    .bc-check input[type='checkbox'] { width: 24px; height: 24px; flex: 0 0 auto; margin: 0; cursor: pointer; }

    .bc-check label {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      cursor: pointer;
      overflow-wrap: anywhere;
    }
  `,
})
export class BlockedCountriesPage {
  private readonly api = inject(OverseasDeliveryApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly injector = inject(Injector);

  protected readonly screen = signal<OverseasDeliveryScreen | null>(null);
  protected readonly loading = signal(true);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');
  protected readonly canEdit = signal(false);
  protected readonly search = signal('');

  private readonly savingRows = signal<ReadonlySet<string>>(new Set<string>());
  private readonly rowErrors = signal<Readonly<Record<string, string>>>({});

  /** Countries where `isShippingEnabled = false`. */
  protected readonly blocked = computed(() =>
    (this.screen()?.countries ?? []).filter((c) => !c.isShippingEnabled),
  );

  /** Countries visible after the search term is applied. */
  protected readonly shown = computed(() => {
    const term = this.search().trim().toLowerCase();

    return (this.screen()?.countries ?? []).filter(
      (c) => term === '' || c.name.toLowerCase().includes(term) || c.code.toLowerCase() === term,
    );
  });

  constructor() {
    this.auth.whoAmI().subscribe({
      next: (session) =>
        this.canEdit.set(session.grantsAllPermissions || session.permissions.includes(OVERSEAS_PERMISSIONS.edit)),
      error: () => this.canEdit.set(false),
    });

    this.load();
  }

  protected load(keepProblem = false): void {
    this.loading.set(true);
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
    return (event.target as HTMLInputElement | null)?.value ?? '';
  }

  protected regionName(code: string): string {
    return this.screen()?.regions.find((r) => r.code === code)?.name ?? code;
  }

  protected savingRow(code: string): boolean {
    return this.savingRows().has(code);
  }

  protected rowError(code: string): string | null {
    return this.rowErrors()[code] ?? null;
  }

  /**
   * Fires immediately on checkbox change. Blocks a second toggle while the first is in flight.
   * Reverts the checkbox and shows a row-level error on failure.
   */
  protected toggle(country: CountryDeliveryRow, event: Event): void {
    const box = event.target as HTMLInputElement | null;

    if (this.savingRow(country.code)) {
      if (box) {
        box.checked = country.isShippingEnabled;  // revert to pending first-toggle's visual
      }

      return;
    }

    const wantBlocked = box?.checked ?? !country.isShippingEnabled;
    const wantEnabled = !wantBlocked;

    this.savingRows.update((s) => {
      const next = new Set(s);
      next.add(country.code);
      return next;
    });
    this.rowErrors.update((all) => this.without(all, country.code));
    this.announce(wantBlocked ? `Blocking ${country.name}…` : `Unblocking ${country.name}…`);

    this.api
      .updateCountry(country.code, {
        isShippingEnabled: wantEnabled,
        deliveryFeeOverride: country.deliveryFeeOverride,
        estimatedDaysMinOverride: country.estimatedDaysMinOverride,
        estimatedDaysMaxOverride: country.estimatedDaysMaxOverride,
        // The endpoint replaces every field on every save, so the Express overrides travel back unchanged: leaving
        // them out would clear a country's Express fee and estimate each time it is blocked or reopened.
        expressFeeOverride: country.expressFeeOverride,
        expressDaysMinOverride: country.expressDaysMinOverride,
        expressDaysMaxOverride: country.expressDaysMaxOverride,
        rowVersion: country.rowVersion,
      })
      .subscribe({
        next: (screen) => {
          this.savingRows.update((s) => {
            const next = new Set(s);
            next.delete(country.code);
            return next;
          });
          this.accept(screen);
          this.announce(wantBlocked ? `${country.name} is now blocked.` : `${country.name} is now open.`);
        },
        error: (error: unknown) => {
          this.savingRows.update((s) => {
            const next = new Set(s);
            next.delete(country.code);
            return next;
          });

          if (box) {
            box.checked = !country.isShippingEnabled;
          }

          const failure = toCatalogFailure(error);

          if (failure.kind === 'conflict') {
            this.problem.set({ ...failure, message: `${failure.message} The page has been reloaded with the latest values.` });
            this.announce(failure.message);
            this.load(true);
            return;
          }

          this.rowErrors.update((all) => ({ ...all, [country.code]: failure.message }));
          this.announce(failure.message);
        },
      });
  }

  private accept(screen: OverseasDeliveryScreen): void {
    this.screen.set(screen);
    this.loading.set(false);
  }

  private without<T>(all: Readonly<Record<string, T>>, key: string): Record<string, T> {
    const copy = { ...all };
    delete copy[key];
    return copy;
  }

  /** @see OverseasDeliveryPage.announce — a repeated identical announcement has to re-fire. */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
