import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { focusEditForm } from '../../core/shell/focus-edit-form';
import { AdminOrderApi } from '../../core/orders/admin-order-api';
import { AdminCourier, TRACKING_NUMBER_PLACEHOLDER } from '../../core/orders/admin-order.model';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/**
 * Couriers and their tracking addresses (ADM-09 / ADM-22, doc 11 P9's precondition).
 *
 * **Nothing is seeded and nothing on this screen names a courier.** Which carriers the shop uses, and what
 * their tracking URLs look like, is the client's information — the same class of fact as their bank
 * account. W3-03 shipped a real bank's name and the client's own legal name as placeholder text and both
 * were Major findings, so there is no example carrier here: not in a seeder, not as a default, and **not
 * as a form placeholder that renders**. The only example shown is the obviously-fake
 * `{trackingNumber}` placeholder, which is a shape rather than a value.
 *
 * **The consequence is that a new installation cannot mark a delivery order as shipped, and that is
 * correct.** What the shop is owed is the sentence saying so, which the order detail prints in place of a
 * dead button.
 *
 * **There is no delete.** An order that shipped keeps the courier's *name* as a snapshot, so deleting the
 * row would not corrupt a past dispatch note — but the tracking *link* is rendered from the courier's
 * current template (doc 08 §3.5), so deleting one turns every live parcel's link into plain text with no
 * explanation. Deactivating takes it out of the picker and leaves the links working.
 */
@Component({
  selector: 'sda-couriers-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Couriers</h1>
      <p class="adm-panel__hint">
        The carriers this shop dispatches with, and the address their website uses to track a parcel.
        Nothing is set up in advance — these are your carriers, so only you can say who they are.
      </p>
      <p class="adm-panel__hint">
        An order cannot be marked as shipped until at least one courier is active here. Deactivate a
        courier to take it out of the dispatch list; every tracking link on orders already sent with it
        keeps working.
      </p>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        <div class="adm-actions">
          <button class="adm-btn" type="button" (click)="reload()">Try again</button>
        </div>
      </div>
    }

    <!-- tabindex="-1" so focus can land here when a save replaces the row that was being edited. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="couriers-said">{{ said() }}</p>

    <section class="adm-panel">
      <h2 class="adm-panel__title">{{ editingId() === null ? 'Add a courier' : 'Edit this courier' }}</h2>
      <form (submit)="save($event)">
        <div class="adm-fields">
          <label class="adm-field">
            <span class="adm-label">Name</span>
            <input
              class="adm-input"
              type="text"
              name="name"
              maxlength="80"
              autocomplete="off"
              [value]="name()"
              [attr.aria-invalid]="errorFor('name') ? 'true' : null"
              data-testid="courier-name"
              (input)="type($event, name)"
            />
            @if (errorFor('name'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-name">{{ message }}</span>
            }
          </label>
          <label class="adm-field">
            <span class="adm-label">Telephone</span>
            <input
              class="adm-input"
              type="text"
              name="phone"
              maxlength="20"
              autocomplete="off"
              [value]="phone()"
              [attr.aria-invalid]="errorFor('phone') ? 'true' : null"
              data-testid="courier-phone"
              (input)="type($event, phone)"
            />
            @if (errorFor('phone'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-phone">{{ message }}</span>
            }
          </label>
          <label class="adm-field adm-field--wide">
            <span class="adm-label">Tracking address</span>
            <input
              class="adm-input"
              type="text"
              name="trackingUrlTemplate"
              maxlength="500"
              autocomplete="off"
              inputmode="url"
              [value]="template()"
              [attr.aria-describedby]="'template-hint'"
              [attr.aria-invalid]="errorFor('trackingUrlTemplate') ? 'true' : null"
              data-testid="courier-template"
              (input)="type($event, template)"
            />
            <span class="adm-panel__hint" id="template-hint">
              The courier's own tracking address, with
              <code>{{ placeholder }}</code>
              where the number goes. Must start with https://. Leave it empty if this courier has no online
              tracking — the number is then shown as text.
            </span>
            @if (errorFor('trackingUrlTemplate'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-template">{{ message }}</span>
            }
          </label>
          <label class="adm-field">
            <span class="adm-label">Order in the list</span>
            <input
              class="adm-input"
              type="number"
              name="sortOrder"
              step="1"
              min="0"
              max="999"
              [value]="sortOrder()"
              data-testid="courier-sort"
              (input)="type($event, sortOrder)"
            />
          </label>
          <!-- A 44 px control around a 24 px box, and the label is the control. The shared adm-check
               input is 20 px square, which the browser suite measured and GLB-09's floor refuses; a
               44 px native checkbox renders as an outsized glyph, so the box is 24 px — WCAG 2.2
               SC 2.5.8's own minimum — inside a label that fills the 44 px row. The for attribute makes
               the label part of the control, so tapping anywhere on that row toggles it, and the browser
               test measures the label because that is what a person aims at. -->
          <span class="couriers-check">
            <input
              type="checkbox"
              id="courier-active"
              [checked]="isActive()"
              data-testid="courier-active"
              (change)="toggleActive($event)"
            />
            <label for="courier-active" data-testid="courier-active-field">
              Available for new dispatches
            </label>
          </span>
        </div>

        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
            data-testid="courier-save"
          >
            {{ editingId() === null ? 'Add the courier' : 'Save the courier' }}
          </button>
          @if (editingId() !== null) {
            <button class="adm-btn" type="button" data-testid="courier-cancel" (click)="startNew()">
              Cancel
            </button>
          }
        </div>
      </form>
    </section>

    @if (isLoading()) {
      <p class="adm-muted">Loading couriers…</p>
    } @else if (couriers().length === 0) {
      <p class="adm-muted" data-testid="couriers-empty">
        No courier has been added yet, so no delivery order can be marked as shipped. Add the first one
        above.
      </p>
    } @else {
      <ul class="adm-list" data-testid="couriers">
        @for (courier of couriers(); track courier.id) {
          <li class="couriers-row" [attr.data-testid]="'courier-' + courier.id">
            <p class="couriers-row__name">
              {{ courier.name }}
              @if (!courier.isActive) {
                <span class="adm-pill adm-pill--hidden">Not in use</span>
              }
            </p>
            <dl class="couriers-row__facts">
              <dt>Tracking address</dt>
              <dd>{{ courier.trackingUrlTemplate ?? 'none — the number is shown as text' }}</dd>
              @if (courier.phone) {
                <dt>Telephone</dt>
                <dd>{{ courier.phone }}</dd>
              }
              <dt>Orders sent with them</dt>
              <dd>{{ courier.ordersShipped }}</dd>
            </dl>
            <div class="adm-actions">
              <button
                class="adm-btn"
                type="button"
                [attr.data-testid]="'edit-' + courier.id"
                (click)="edit(courier)"
              >
                Edit
              </button>
            </div>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .adm-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .couriers-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .couriers-row__name {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .couriers-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .couriers-row__facts dt {
      color: var(--color-text-muted);
    }
    .couriers-row__facts dd {
      margin: 0;
      /* A tracking template is a long URL with no spaces in it. Without this it widens the panel and the
         page scrolls sideways at 320 px. */
      overflow-wrap: anywhere;
    }
    code {
      font-size: var(--text-small);
      overflow-wrap: anywhere;
    }
    .couriers-check {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      min-width: 0;
    }
    .couriers-check input[type='checkbox'] {
      /* 24 px, not the shared 20: WCAG 2.2 SC 2.5.8's floor, and the label beside it carries the
         project's 44 px. */
      width: 24px;
      height: 24px;
      flex: 0 0 auto;
      margin: 0;
      cursor: pointer;
    }
    .couriers-check label {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      cursor: pointer;
    }
  `,
})
export class CouriersPage {
  /**
   * The placeholder a tracking address must contain.
   *
   * **The only example this screen shows, and that is deliberate.** A sample address would be an invented
   * carrier's URL rendered beside the box the real one is typed into — a value mistakable for data, which
   * is the criterion both of this project's Major findings about invented client data turned on.
   */
  protected readonly placeholder = TRACKING_NUMBER_PLACEHOLDER;

  private readonly api = inject(AdminOrderApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly couriers = signal<readonly AdminCourier[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  protected readonly editingId = signal<number | null>(null);
  protected readonly name = signal('');
  protected readonly template = signal('');
  protected readonly phone = signal('');
  protected readonly sortOrder = signal('0');
  protected readonly isActive = signal(true);

  private readonly fieldErrors = signal<Readonly<Record<string, string>>>({});

  /**
   * The server's message for one field, or null.
   *
   * A method rather than a computed returning a closure: a signal read inside the method body still makes
   * the template track `fieldErrors`, and the closure form confused the template type-checker into
   * reporting "expected 0 arguments" on every call site.
   */
  protected errorFor(field: string): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  constructor() {
    this.load();
  }

  protected type(event: Event, target: { set(value: string): void }): void {
    target.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected toggleActive(event: Event): void {
    this.isActive.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  protected startNew(): void {
    this.editingId.set(null);
    this.name.set('');
    this.template.set('');
    this.phone.set('');
    this.sortOrder.set('0');
    this.isActive.set(true);
    this.fieldErrors.set({});
  }

  protected edit(courier: AdminCourier): void {
    this.editingId.set(courier.id);
    this.name.set(courier.name);
    this.template.set(courier.trackingUrlTemplate ?? '');
    this.phone.set(courier.phone ?? '');
    this.sortOrder.set(String(courier.sortOrder));
    this.isActive.set(courier.isActive);
    this.fieldErrors.set({});
    this.announce(`Editing ${courier.name}.`);
    focusEditForm(this.document, this.injector, 'courier-name');
  }

  /**
   * Saves the form.
   *
   * **Every field is sent, including the blank ones.** The endpoint refuses a body that leaves one out
   * rather than reading its absence as null — a save that did not mention a field has silently cleared it
   * four times in this project (W1-12's category images, W1-03's mapper, GLB-10's audit snapshot) — so
   * sending them all is the client's half of that guarantee. An empty box becomes `null`, which is how a
   * staff member says "this courier has no online tracking".
   */
  protected save(event: Event): void {
    event.preventDefault();

    // The guard `disabled` would have provided, which is why the button is never disabled: `[disabled]` on
    // a control whose state changes under the user throws focus to `<body>` (#109, six instances).
    if (this.isBusy()) {
      return;
    }

    const name = this.name().trim();

    if (name.length === 0) {
      this.fieldErrors.set({ name: 'Give the courier a name.' });
      this.announce('Give the courier a name.');
      return;
    }

    const sortOrder = Number.parseInt(this.sortOrder(), 10);

    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.announce(this.editingId() === null ? 'Adding the courier…' : 'Saving the courier…');

    const template = this.template().trim();
    const phone = this.phone().trim();
    const focused = this.document.activeElement as HTMLElement | null;

    this.api
      .saveCourier(this.editingId(), {
        name,
        trackingUrlTemplate: template.length === 0 ? null : template,
        phone: phone.length === 0 ? null : phone,
        isActive: this.isActive(),

        // A blank or unparseable box is 0 rather than a refusal: the field orders a picker and has no right
        // answer, so asking a staff member to fix it before they can add a carrier would be a gate in
        // front of the thing they came to do.
        sortOrder: Number.isNaN(sortOrder) ? 0 : sortOrder,
      })
      .subscribe({
        next: (saved) => {
          this.announce(`${saved.name} is saved.`);
          this.startNew();
          this.load(() => this.rehomeFocusIfLost(focused));
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  protected reload(): void {
    this.load();
  }

  private load(then?: () => void): void {
    this.api.couriers().subscribe({
      next: (list) => {
        this.couriers.set(list);
        this.isLoading.set(false);
        this.isBusy.set(false);
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.fail(error);
      },
    });
  }

  /**
   * Turns a failure into something the staff member can act on, **keeping the server's per-field
   * messages**.
   *
   * A duplicate name and a template with no placeholder both arrive as a 400 naming `name` or
   * `trackingUrlTemplate`, and those sentences are written for the staff member — "there is already a
   * courier called X", "the address must contain {trackingNumber}". Collapsing them into one banner would
   * throw away the field, which is the only thing that says where to look.
   */
  private fail(error: unknown): void {
    this.isBusy.set(false);

    const failure = toCatalogFailure(error);

    const fields = failure.fieldErrors;

    if (fields !== undefined && Object.keys(fields).length > 0) {
      this.fieldErrors.set(
        Object.fromEntries(
          Object.entries(fields).map(([field, messages]) => [field, messages.join(' ')]),
        ),
      );

      this.announce(Object.values(this.fieldErrors()).join(' '));
      return;
    }

    this.problem.set(failure);
    this.announce(failure.message);
  }

  /** @see OrdersListPage.rehomeFocusIfLost — the same rule, and the same reason. */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="couriers-said"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /** @see OrdersListPage.announce — a repeated identical announcement has to re-fire (ADM-22). */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
