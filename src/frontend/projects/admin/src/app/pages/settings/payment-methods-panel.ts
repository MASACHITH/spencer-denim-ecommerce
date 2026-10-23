import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';

import { toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import {
  PaymentProviderRow,
  PaymentProvidersApi,
  PaymentProvidersPanel,
} from '../../core/settings/payment-providers-api';

/**
 * The Payment methods panel (owner decision 2026-10-21: bank transfer off, PayPal the only payment method,
 * "block only for now").
 *
 * **It fetches for itself and draws nothing for an admin the API refuses** — `Settings.Payments` is a Super
 * Admin's alone, and the settings page shows it only where it already shows the bank panel (the same
 * permission). Switching a method off never touches orders already placed: a bank-transfer order placed
 * before still has its instructions, and the bank-transfer approval queue keeps working.
 *
 * Each switch is a native checkbox, and it is never disabled while a save is in flight (#109: a control that
 * becomes disabled under the user throws focus to `<body>`); a second press while busy is simply ignored.
 */
@Component({
  selector: 'sda-payment-methods-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!hidden()) {
      <section class="adm-panel" aria-labelledby="payment-methods-title" data-testid="payment-methods-panel">
        <h2 class="adm-panel__title" id="payment-methods-title">Payment methods</h2>
        <p class="adm-panel__hint">
          Which payment methods checkout offers. A method that is switched on but not ready (for PayPal: its
          credentials and today's exchange rate) is not offered. Switching a method off does not change orders
          already placed.
        </p>

        @if (loadProblem(); as message) {
          <div class="adm-state adm-state--error" role="alert">
            <p>{{ message }}</p>
            <div class="adm-actions">
              <button class="adm-btn" type="button" (click)="load()">Try again</button>
            </div>
          </div>
        } @else if (panel(); as loaded) {
          @if (!loaded.anyOffered) {
            <p class="adm-state adm-state--error" role="alert" data-testid="payment-methods-none">
              Checkout will refuse every order until a payment method is switched on and ready.
            </p>
          }
          <ul class="pm-list">
            @for (provider of loaded.providers; track provider.code) {
              <li class="pm-row" [attr.data-testid]="'payment-method-' + provider.code">
                <span class="pm-check">
                  <input
                    type="checkbox"
                    [id]="'pm-' + provider.code"
                    [checked]="provider.isEnabled"
                    [attr.aria-describedby]="'pm-state-' + provider.code"
                    [attr.data-testid]="'payment-method-switch-' + provider.code"
                    (change)="toggle(provider, $event)"
                  />
                  <label [for]="'pm-' + provider.code">{{ provider.displayName }}</label>
                </span>
                <span class="adm-muted pm-state" [id]="'pm-state-' + provider.code" [attr.data-testid]="'payment-method-state-' + provider.code">
                  {{ stateText(provider) }}
                </span>
                @if (errors()[provider.code]; as message) {
                  <span class="adm-field-error" role="alert" [attr.data-testid]="'payment-method-error-' + provider.code">{{ message }}</span>
                }
              </li>
            }
          </ul>
        } @else {
          <p class="adm-muted">Loading payment methods…</p>
        }
        <p class="adm-muted" role="status" data-testid="payment-methods-said">{{ said() }}</p>
      </section>
    }
  `,
  styles: `
    .pm-list { display: grid; gap: var(--space-1-5); margin: 0; padding: 0; list-style: none; }
    .pm-row { display: grid; gap: var(--space-0-5); min-width: 0; }
    .pm-state { font-size: var(--text-small); overflow-wrap: anywhere; }
    .pm-check { display: flex; align-items: center; gap: var(--space-1); min-height: var(--tap); min-width: 0; }
    .pm-check input[type='checkbox'] { width: 24px; height: 24px; flex: 0 0 auto; margin: 0; cursor: pointer; }
    .pm-check label {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      font-weight: var(--weight-semibold);
      cursor: pointer;
      overflow-wrap: anywhere;
    }
  `,
})
export class PaymentMethodsPanel {
  private readonly api = inject(PaymentProvidersApi);

  protected readonly panel = signal<PaymentProvidersPanel | null>(null);
  protected readonly hidden = signal(false);
  protected readonly loadProblem = signal<string | null>(null);
  protected readonly errors = signal<Readonly<Record<string, string>>>({});
  protected readonly said = signal('');
  private busy = false;

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loadProblem.set(null);
    this.api.panel().subscribe({
      next: (panel) => this.panel.set(panel),
      error: (error: unknown) => {
        const failure = toCatalogFailure(error);
        if (failure.kind === 'forbidden') {
          this.hidden.set(true);
          return;
        }

        this.loadProblem.set(failure.message);
      },
    });
  }

  protected stateText(provider: PaymentProviderRow): string {
    if (provider.isOffered) {
      return 'On — offered at checkout.';
    }

    if (provider.isEnabled) {
      return 'On, but not ready — not offered at checkout until its settings are finished.';
    }

    return provider.isReady ? 'Off — not offered at checkout.' : 'Off — and not ready yet.';
  }

  protected toggle(provider: PaymentProviderRow, event: Event): void {
    const box = event.target as HTMLInputElement | null;
    const wanted = box?.checked ?? provider.isEnabled;

    if (this.busy) {
      if (box) {
        box.checked = provider.isEnabled;
      }

      return;
    }

    this.busy = true;
    this.errors.set({});
    this.said.set(`${wanted ? 'Switching on' : 'Switching off'} ${provider.displayName}…`);

    this.api.setEnabled(provider.code, wanted, provider.rowVersion).subscribe({
      next: (panel) => {
        this.busy = false;
        this.panel.set(panel);
        this.said.set(`${provider.displayName} is switched ${wanted ? 'on' : 'off'}.`);
      },
      error: (error: unknown) => {
        this.busy = false;

        // The box goes back to what the server holds: a refused switch must not look as if it took.
        if (box) {
          box.checked = provider.isEnabled;
        }

        const failure = toCatalogFailure(error);
        const message =
          failure.fieldErrors?.['isEnabled']?.join(' ') ??
          (failure.kind === 'conflict'
            ? `${failure.message} The panel has been reloaded.`
            : failure.message);

        this.errors.set({ [provider.code]: message });
        this.said.set(message);

        if (failure.kind === 'conflict') {
          this.load();
        }
      },
    });
  }
}
