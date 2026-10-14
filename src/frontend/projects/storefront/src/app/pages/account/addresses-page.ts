import { isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  PLATFORM_ID,
  afterNextRender,
  afterRenderEffect,
  computed,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';

import { AccountNav } from './account-nav';
import { AccountNotice } from './account-notice';
import {
  AccountApi,
  accountErrorMessage,
  accountFailure,
  fieldErrorsOf,
} from '../../core/account/account-api';
import {
  ACCOUNT_FIELD_LENGTHS,
  CustomerAddressDto,
  CustomerAddressEdit,
  SAVED_ADDRESS_LIMIT,
  blankToNull,
} from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { CheckoutApi } from '../../core/checkout/checkout-api';
import { DistrictDto } from '../../core/checkout/checkout.model';
import { privatePage } from '../../core/platform/private-page';

/** The form's own state, which is a draft and not an address until the server has taken it. */
interface AddressDraft {
  readonly label: string;
  readonly recipientName: string;
  readonly phone: string;
  readonly line1: string;
  readonly line2: string;
  readonly city: string;
  readonly districtId: string;
  readonly postalCode: string;
  readonly landmark: string;
  readonly isDefault: boolean;
}

/**
 * Every box empty, every tick off.
 *
 * **Nothing in here is an example.** No "123 Galle Road", no placeholder that reads like data, no seeded
 * recipient: a form that pretends to hold an address is a form a shopper submits without reading. The
 * one field that is not a box — the district — starts unselected with a "Choose a district" option that
 * carries no value, so a shopper who does not choose is refused by the server rather than given a
 * district they never picked.
 */
const EMPTY_DRAFT: AddressDraft = {
  label: '',
  recipientName: '',
  phone: '',
  line1: '',
  line2: '',
  city: '',
  districtId: '',
  postalCode: '',
  landmark: '',
  isDefault: false,
};

/**
 * The saved address book (ACC-02, doc 11 P11 step 11): list, add, edit, remove, set default, up to ten.
 *
 * ## The shell renders on the server; the book does not
 *
 * The same reasoning as `orders-page.ts`, for the same reason: `sd_customer_auth` is not forwarded to the
 * API during SSR — `cartCookieInterceptor` forwards `sd_cart` and nothing else, on purpose — so the
 * server cannot read this and does not pretend to. It renders the heading, the navigation and "Loading
 * your addresses…", and the browser reads in `afterNextRender`. The page answers `no-store`, `private`,
 * `Vary: Cookie` and `noindex`, which matters more here than almost anywhere in the shop: this markup
 * holds somebody's home address and telephone number.
 *
 * ## Every write answers the whole book, and the page re-renders from it
 *
 * That is the server's decision and it is load-bearing: setting a default clears the previous one, and
 * deleting the default promotes the newest remaining address, so a page that merged one returned row into
 * a local list would show two defaults or none. So there is **no local mutation of the list anywhere in
 * this file** — every one of the five writes ends with `this.book.set(answer)`.
 *
 * ## The districts come from checkout
 *
 * `GET /api/checkout` answers `districts` whether or not the visitor has a bag
 * (`GetCheckoutQueryHandler`: "three things that need the reference data whether or not there is a bag"),
 * and `CheckoutApi` is already the client for it. A second districts endpoint — or a second client for
 * the same one — would be a second list of the shop's districts to keep in step with
 * `ref.Districts.IsActive`, which an admin edits; and the list checkout will actually quote for is the
 * one this form should be offering. The cost is a payload wider than this page needs, which is a few
 * unused fields and not an extra request.
 *
 * **A district that is no longer served is still named on the card it was saved with.** The server joins
 * `districtName` in and sends `districtIsActive`, so the card says so rather than silently offering an
 * address checkout will not quote for; the *select* offers only active districts, so a shopper editing
 * such an address has to choose a new one, which is the honest consequence.
 *
 * ## Removing an address destroys the button that removed it
 *
 * Which throws focus to `<body>` exactly as `[disabled]` does — the defect W2-08 shipped on all three of
 * the wishlist's removal controls, after that page had already banned the attribute. So this page does
 * what `WishlistPage.rehomeFocus` does: it remembers where the removed card was and puts focus on the
 * card that took its place, or on Add an address when the book is empty, in an `afterRenderEffect`, which
 * is the first moment the replacement exists. It will not take focus from somewhere else.
 *
 * Removal asks first, inline — `window.confirm` cannot be styled, is announced badly, and does not exist
 * during a server render — and the question names the address, because "Remove this address?" in a book
 * of ten is not a question anybody can answer.
 *
 * ## No `[disabled]` anywhere, and the cap is the server's
 *
 * Every control carries `aria-disabled` at most and refuses in its handler (#109). The Add form is not
 * hidden at ten addresses: it says the cap has been reached and why, because a form that vanishes is a
 * form a shopper looks for. The cap itself is counted on the server inside the transaction that inserts,
 * so two tabs cannot both add an eleventh, and the refusal a shopper reads is the server's own sentence
 * with the server's own number in it.
 */
@Component({
  selector: 'sd-addresses-page',
  imports: [AccountNav, AccountNotice],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc">
      <h1 class="acc__title">Your addresses</h1>

      <sd-account-nav />
      <sd-account-notice [alert]="error()" [status]="said()" testId="addresses" />

      @if (phase() === 'waiting') {
        <p class="acc__muted" role="status" data-testid="addresses-loading">
          Loading your addresses…
        </p>
      } @else if (phase() === 'failed' && book().length === 0) {
        <p class="acc-actions">
          <button
            class="btn btn--secondary"
            type="button"
            data-testid="addresses-retry"
            (click)="read()"
          >
            Try again
          </button>
        </p>
      } @else {
        @if (book().length === 0) {
          <p class="acc__muted" data-testid="addresses-empty">
            You have not saved an address yet. Saving one fills in your delivery details at checkout.
          </p>
        } @else {
          <p class="acc__muted" data-testid="addresses-count">{{ countLabel() }}</p>

          <ul class="acc-grid" data-testid="addresses-list">
            @for (address of book(); track address.id) {
              <li class="acc-card" [attr.data-address]="address.id">
                @if (address.label; as label) {
                  <p class="acc-card__title">{{ label }}</p>
                }
                @if (address.isDefault) {
                  <!-- A word, not a colour: the default has to be tellable without seeing one
                       (WCAG 1.4.1), and a screen reader reads it in order with the card. -->
                  <p class="acc-card__flag" data-testid="address-default-flag">Default</p>
                }

                <div class="acc-card__lines">
                  <p>{{ address.recipientName }}</p>
                  <p>{{ address.phone }}</p>
                  <p>{{ address.line1 }}</p>
                  @if (address.line2; as line2) {
                    <p>{{ line2 }}</p>
                  }
                  <p>{{ address.city }}, {{ address.districtName }}</p>
                  @if (address.postalCode; as postalCode) {
                    <p>{{ postalCode }}</p>
                  }
                  @if (address.landmark; as landmark) {
                    <p>{{ landmark }}</p>
                  }
                </div>

                @if (!address.districtIsActive) {
                  <p class="acc-error" data-testid="address-inactive-district">
                    We no longer deliver to {{ address.districtName }}. Edit this address and choose
                    another district before using it at checkout.
                  </p>
                }

                @if (confirmingId() === address.id) {
                  <!-- Inline rather than `window.confirm`, which cannot be styled, is announced badly
                       and does not exist during a server render. It names the address, because "Remove
                       this address?" in a book of ten is not a question anybody can answer.

                       **No `role` on it, and that is deliberate.** This row replaces the row holding
                       the Remove button that was just pressed, so that button is destroyed — which
                       throws focus to `<body>` exactly as `[disabled]` does. Focus is moved into "Yes,
                       remove it" instead, which announces the question by reading it, and a live region
                       created in the same update as its own text is the thing that does not get
                       announced. `wishlist-page.ts` makes the same two-step confirmation and does not
                       move focus on the asking step; this does. -->
                  <p class="acc-card__actions">
                    <span>{{ removeQuestion(address) }}</span>
                    <button
                      class="link acc-inline-action"
                      type="button"
                      data-testid="address-remove-yes"
                      (click)="remove(address)"
                    >
                      Yes, remove it
                    </button>
                    <button
                      class="link acc-inline-action"
                      type="button"
                      data-testid="address-remove-no"
                      (click)="cancelRemove(address)"
                    >
                      Cancel
                    </button>
                  </p>
                } @else {
                  <p class="acc-card__actions">
                    <button
                      class="link acc-inline-action"
                      type="button"
                      [attr.aria-label]="'Edit ' + describe(address)"
                      data-testid="address-edit"
                      (click)="beginEdit(address)"
                    >
                      Edit
                    </button>

                    @if (!address.isDefault) {
                      <button
                        class="link acc-inline-action"
                        type="button"
                        [attr.aria-label]="'Make ' + describe(address) + ' my default address'"
                        data-testid="address-make-default"
                        (click)="makeDefault(address)"
                      >
                        Make default
                      </button>
                    }

                    <button
                      class="link acc-inline-action"
                      type="button"
                      [attr.aria-label]="'Remove ' + describe(address)"
                      data-testid="address-remove"
                      (click)="askToRemove(address)"
                    >
                      Remove
                    </button>
                  </p>
                }
              </li>
            }
          </ul>
        }

        <section class="acc__section" aria-labelledby="addresses-form-title">
          <h2 class="acc__subtitle" id="addresses-form-title" tabindex="-1" #formTitle>
            {{ editingId() === null ? 'Add an address' : 'Edit this address' }}
          </h2>

          @if (atLimit() && editingId() === null) {
            <!-- The form is not hidden at the cap: a form that vanishes is a form a shopper looks for.
                 The number is the server's, from {@link SAVED_ADDRESS_LIMIT}, which exists only to say
                 this in advance — the refusal itself quotes the number the server enforced. -->
            <p class="acc-error" data-testid="addresses-at-limit">
              You have the {{ LIMIT }} addresses we can save. Remove one you no longer use to add
              another.
            </p>
          }

          <form class="acc-form" novalidate (submit)="save($event)">
            <div class="acc-field">
              <label class="acc-label" for="ad-label">
                Name for this address <span class="acc-optional">(optional)</span>
              </label>
              <input
                class="acc-input"
                id="ad-label"
                name="label"
                type="text"
                [attr.maxlength]="LENGTHS.label"
                [value]="draft().label"
                [attr.aria-invalid]="messageFor('label') ? 'true' : null"
                [attr.aria-describedby]="
                  messageFor('label') ? 'ad-label-error ad-label-hint' : 'ad-label-hint'
                "
                data-testid="address-field-label"
                (input)="set('label', $event)"
              />
              <p class="acc-hint" id="ad-label-hint">So you can tell your addresses apart.</p>
              @if (messageFor('label'); as message) {
                <p class="acc-error" id="ad-label-error">{{ message }}</p>
              }
            </div>

            <div class="acc-row">
              <div class="acc-field">
                <label class="acc-label" for="ad-recipientName">Recipient name</label>
                <input
                  class="acc-input"
                  id="ad-recipientName"
                  name="recipientName"
                  type="text"
                  autocomplete="name"
                  required
                  [attr.maxlength]="LENGTHS.recipientName"
                  [value]="draft().recipientName"
                  [attr.aria-invalid]="messageFor('recipientName') ? 'true' : null"
                  [attr.aria-describedby]="
                    messageFor('recipientName') ? 'ad-recipientName-error' : null
                  "
                  data-testid="address-field-recipient"
                  (input)="set('recipientName', $event)"
                />
                @if (messageFor('recipientName'); as message) {
                  <p class="acc-error" id="ad-recipientName-error">{{ message }}</p>
                }
              </div>

              <div class="acc-field">
                <label class="acc-label" for="ad-phone">Phone</label>
                <input
                  class="acc-input"
                  id="ad-phone"
                  name="phone"
                  type="tel"
                  inputmode="tel"
                  autocomplete="tel"
                  required
                  [attr.maxlength]="LENGTHS.phone"
                  [value]="draft().phone"
                  [attr.aria-invalid]="messageFor('phone') ? 'true' : null"
                  [attr.aria-describedby]="
                    messageFor('phone') ? 'ad-phone-error ad-phone-hint' : 'ad-phone-hint'
                  "
                  data-testid="address-field-phone"
                  (input)="set('phone', $event)"
                />
                <p class="acc-hint" id="ad-phone-hint">The courier calls this number.</p>
                @if (messageFor('phone'); as message) {
                  <p class="acc-error" id="ad-phone-error">{{ message }}</p>
                }
              </div>
            </div>

            <div class="acc-field">
              <label class="acc-label" for="ad-line1">Address line 1</label>
              <input
                class="acc-input"
                id="ad-line1"
                name="line1"
                type="text"
                autocomplete="address-line1"
                required
                [attr.maxlength]="LENGTHS.line"
                [value]="draft().line1"
                [attr.aria-invalid]="messageFor('line1') ? 'true' : null"
                [attr.aria-describedby]="messageFor('line1') ? 'ad-line1-error' : null"
                data-testid="address-field-line1"
                (input)="set('line1', $event)"
              />
              @if (messageFor('line1'); as message) {
                <p class="acc-error" id="ad-line1-error">{{ message }}</p>
              }
            </div>

            <div class="acc-field">
              <label class="acc-label" for="ad-line2">
                Address line 2 <span class="acc-optional">(optional)</span>
              </label>
              <input
                class="acc-input"
                id="ad-line2"
                name="line2"
                type="text"
                autocomplete="address-line2"
                [attr.maxlength]="LENGTHS.line"
                [value]="draft().line2"
                [attr.aria-invalid]="messageFor('line2') ? 'true' : null"
                [attr.aria-describedby]="messageFor('line2') ? 'ad-line2-error' : null"
                data-testid="address-field-line2"
                (input)="set('line2', $event)"
              />
              @if (messageFor('line2'); as message) {
                <p class="acc-error" id="ad-line2-error">{{ message }}</p>
              }
            </div>

            <div class="acc-row">
              <div class="acc-field">
                <label class="acc-label" for="ad-city">Town or city</label>
                <input
                  class="acc-input"
                  id="ad-city"
                  name="city"
                  type="text"
                  autocomplete="address-level2"
                  required
                  [attr.maxlength]="LENGTHS.city"
                  [value]="draft().city"
                  [attr.aria-invalid]="messageFor('city') ? 'true' : null"
                  [attr.aria-describedby]="messageFor('city') ? 'ad-city-error' : null"
                  data-testid="address-field-city"
                  (input)="set('city', $event)"
                />
                @if (messageFor('city'); as message) {
                  <p class="acc-error" id="ad-city-error">{{ message }}</p>
                }
              </div>

              <div class="acc-field">
                <label class="acc-label" for="ad-districtId">District</label>
                <select
                  class="acc-input"
                  id="ad-districtId"
                  name="districtId"
                  required
                  [attr.aria-invalid]="messageFor('districtId') ? 'true' : null"
                  [attr.aria-describedby]="
                    messageFor('districtId')
                      ? 'ad-districtId-error ad-districtId-hint'
                      : 'ad-districtId-hint'
                  "
                  data-testid="address-field-district"
                  (change)="set('districtId', $event)"
                >
                  <!-- No value, so an unchosen district is sent as nothing and refused by the server
                       with "Choose a district." rather than quietly becoming district 1. -->
                  <option value="" [selected]="draft().districtId === ''">Choose a district</option>
                  @for (district of districts(); track district.id) {
                    <option [value]="district.id" [selected]="district.id === chosenDistrictId()">
                      {{ district.name }}
                    </option>
                  }
                </select>
                <p class="acc-hint" id="ad-districtId-hint">
                  The delivery fee and the estimate depend on the district.
                </p>
                @if (messageFor('districtId'); as message) {
                  <p class="acc-error" id="ad-districtId-error">{{ message }}</p>
                }
              </div>
            </div>

            <div class="acc-row">
              <div class="acc-field">
                <label class="acc-label" for="ad-postalCode">
                  Postal code <span class="acc-optional">(optional)</span>
                </label>
                <input
                  class="acc-input"
                  id="ad-postalCode"
                  name="postalCode"
                  type="text"
                  inputmode="numeric"
                  autocomplete="postal-code"
                  [attr.maxlength]="LENGTHS.postalCode"
                  [value]="draft().postalCode"
                  [attr.aria-invalid]="messageFor('postalCode') ? 'true' : null"
                  [attr.aria-describedby]="messageFor('postalCode') ? 'ad-postalCode-error' : null"
                  data-testid="address-field-postal"
                  (input)="set('postalCode', $event)"
                />
                @if (messageFor('postalCode'); as message) {
                  <p class="acc-error" id="ad-postalCode-error">{{ message }}</p>
                }
              </div>

              <div class="acc-field">
                <label class="acc-label" for="ad-landmark">
                  Landmark <span class="acc-optional">(optional)</span>
                </label>
                <input
                  class="acc-input"
                  id="ad-landmark"
                  name="landmark"
                  type="text"
                  [attr.maxlength]="LENGTHS.landmark"
                  [value]="draft().landmark"
                  [attr.aria-invalid]="messageFor('landmark') ? 'true' : null"
                  [attr.aria-describedby]="
                    messageFor('landmark') ? 'ad-landmark-error ad-landmark-hint' : 'ad-landmark-hint'
                  "
                  data-testid="address-field-landmark"
                  (input)="set('landmark', $event)"
                />
                <p class="acc-hint" id="ad-landmark-hint">Anything that helps the courier find you.</p>
                @if (messageFor('landmark'); as message) {
                  <p class="acc-error" id="ad-landmark-error">{{ message }}</p>
                }
              </div>
            </div>

            <!-- The whole row is the label, so the tap target is the row rather than the 16 px box —
                 checkout's own lesson, and doc 06's L4 on where a focus ring is visible. -->
            <label class="acc-check">
              <input
                type="checkbox"
                [checked]="draft().isDefault"
                data-testid="address-field-default"
                (change)="setDefaultTick($event)"
              />
              <span>Use this as my default address</span>
            </label>

            <div class="acc-actions">
              <button
                class="btn btn--primary"
                type="submit"
                [attr.aria-disabled]="canSave() ? null : 'true'"
                data-testid="address-save"
              >
                {{ saveLabel() }}
              </button>

              @if (editingId() !== null) {
                <button
                  class="link acc-inline-action"
                  type="button"
                  data-testid="address-cancel-edit"
                  (click)="cancelEdit()"
                >
                  Cancel
                </button>
              }
            </div>
          </form>
        </section>
      }
    </div>
  `,
  styles: `
    /* The row is the target, and the box keeps its own 44 px so a pointer landing on it is not a
       smaller target than the words beside it. */
    .acc-check {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      font-size: var(--text-small);
      cursor: pointer;
    }
    .acc-check input {
      width: 20px;
      height: 20px;
      flex: none;
      margin: 0;
    }
  `,
})
export class AddressesPage {
  protected readonly LENGTHS = ACCOUNT_FIELD_LENGTHS;
  protected readonly LIMIT = SAVED_ADDRESS_LIMIT;

  private readonly api = inject(AccountApi);
  private readonly checkout = inject(CheckoutApi);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly notice = viewChild.required(AccountNotice);
  private readonly formTitle = viewChild<ElementRef<HTMLElement>>('formTitle');

  private readonly addresses = signal<readonly CustomerAddressDto[]>([]);
  private readonly districtList = signal<readonly DistrictDto[]>([]);
  private readonly state = signal<'waiting' | 'ready' | 'failed'>('waiting');
  private readonly failure = signal<string | null>(null);
  private readonly announcement = signal<string | null>(null);
  private readonly form = signal<AddressDraft>(EMPTY_DRAFT);
  private readonly editing = signal<number | null>(null);
  private readonly confirming = signal<number | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly pending = signal(false);

  /**
   * Where the last removal was in the list, and a ticket so one is never answered twice.
   *
   * The shape `WishlistStore.lastRemoval` uses, for the same reason: the control that caused the removal
   * has been destroyed by the time focus can be re-homed, so the *index* is the only thing left that says
   * where the shopper was.
   */
  private readonly removal = signal<{ readonly index: number; readonly ticket: number } | null>(null);
  private removalTicket = 0;
  private answeredRemoval = 0;

  protected readonly book = this.addresses.asReadonly();
  protected readonly districts = this.districtList.asReadonly();
  protected readonly phase = this.state.asReadonly();
  protected readonly error = this.failure.asReadonly();
  protected readonly said = this.announcement.asReadonly();
  protected readonly draft = this.form.asReadonly();
  protected readonly editingId = this.editing.asReadonly();
  protected readonly confirmingId = this.confirming.asReadonly();

  protected readonly atLimit = computed(() => this.addresses().length >= SAVED_ADDRESS_LIMIT);

  /**
   * The chosen district as a number, or null.
   *
   * The draft holds the select's own string, because that is what a `<select>` reads and writes; this is
   * the one place it becomes a number, so the template compares like with like and {@link save} sends a
   * number or `null` rather than `''` or `NaN`.
   */
  protected readonly chosenDistrictId = computed(() => {
    const raw = this.form().districtId;

    return raw === '' ? null : Number(raw);
  });

  /**
   * Whether Save would do anything.
   *
   * False while a write is in flight, and false when the book is full and this is an Add rather than an
   * Edit — an edit of an existing address does not add one, so the cap does not apply to it. It drives
   * `aria-disabled` and is re-checked in {@link save}, so the button's state and its behaviour come from
   * one expression and cannot disagree.
   */
  protected readonly canSave = computed(
    () => !this.pending() && !(this.atLimit() && this.editing() === null),
  );

  protected readonly saveLabel = computed(() => {
    if (this.pending()) {
      return 'Saving…';
    }

    return this.editing() === null ? 'Save address' : 'Save changes';
  });

  protected readonly countLabel = computed(() => {
    const count = this.addresses().length;
    const kept = count === 1 ? '1 saved address' : `${count} saved addresses`;

    return `${kept} of ${SAVED_ADDRESS_LIMIT}.`;
  });

  constructor() {
    privatePage(
      'Your addresses',
      'The delivery addresses saved to your account.',
    );

    inject(AccountStore).load().subscribe();

    if (isPlatformBrowser(inject(PLATFORM_ID))) {
      // **Focus, after a removal has destroyed the control that caused it.** See the class docblock.
      // `afterRenderEffect` rather than an `effect`, because the card that takes the removed one's place
      // does not exist until the render has happened: an ordinary effect would focus the card before it,
      // or nothing. It depends on `removal` alone and reads the rest untracked, so it fires once per
      // removal and not on every change to the book.
      afterRenderEffect(() => {
        const removal = this.removal();
        untracked(() => this.rehomeFocus(removal));
      });

      afterNextRender(() => {
        this.read();
        this.readDistricts();
      });
    }
  }

  /** Reads the book. The only control the failed state offers, so it is callable from the template. */
  protected read(): void {
    this.failure.set(null);

    this.api.addresses().subscribe({
      next: (book) => {
        this.addresses.set(book);
        this.state.set('ready');
      },
      error: (error: unknown) => {
        this.failure.set(accountErrorMessage(error));
        this.state.set('failed');
      },
    });
  }

  protected set(field: keyof AddressDraft, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value;

    this.form.update((draft) => ({ ...draft, [field]: value }));
  }

  protected setDefaultTick(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;

    this.form.update((draft) => ({ ...draft, isDefault: checked }));
  }

  protected messageFor(field: string): string | null {
    return this.serverErrors().get(field) ?? null;
  }

  /**
   * How to refer to one address in a control's accessible name.
   *
   * **Three identical "Remove" buttons in a book of three addresses are three identical controls**, and a
   * screen reader reading them out gives a shopper no way to tell which is which. The label, the
   * recipient and the first line are what distinguishes them, in that order of usefulness.
   */
  protected describe(address: CustomerAddressDto): string {
    return address.label ?? `${address.recipientName}, ${address.line1}`;
  }

  protected removeQuestion(address: CustomerAddressDto): string {
    return `Remove ${this.describe(address)}?`;
  }

  protected beginEdit(address: CustomerAddressDto): void {
    this.editing.set(address.id);
    this.confirming.set(null);
    this.serverErrors.set(new Map());
    this.failure.set(null);
    this.form.set({
      label: address.label ?? '',
      recipientName: address.recipientName,
      phone: address.phone,
      line1: address.line1,
      line2: address.line2 ?? '',
      city: address.city,
      // **Blank when the saved district is no longer served**, so the shopper has to choose one that is:
      // the select offers only active districts, so keeping the old id would show "Choose a district"
      // over a value the form would then submit unchanged.
      districtId: this.districts().some((district) => district.id === address.districtId)
        ? String(address.districtId)
        : '',
      postalCode: address.postalCode ?? '',
      landmark: address.landmark ?? '',
      isDefault: address.isDefault,
    });

    // The form is below the list, and on a telephone it is below the fold: a shopper who pressed Edit
    // and saw nothing change would press it again. Focus moves to the form's own heading, which is why
    // that heading carries `tabindex="-1"`, and `afterNextRender` because the heading's text changes in
    // the same update ("Add an address" becomes "Edit this address").
    afterNextRender(() => this.formTitle()?.nativeElement.focus(), { injector: this.injector });
  }

  protected cancelEdit(): void {
    this.editing.set(null);
    this.form.set(EMPTY_DRAFT);
    this.serverErrors.set(new Map());
    afterNextRender(() => this.formTitle()?.nativeElement.focus(), { injector: this.injector });
  }

  /**
   * Asks before removing, and moves focus to the question.
   *
   * The Remove button that was pressed is destroyed by this update, so without the focus move a keyboard
   * shopper would be dropped on `<body>` with a question on screen they never heard — the #109 failure
   * arriving by the other route, which W2-08 shipped on all three of the wishlist's removal controls.
   */
  protected askToRemove(address: CustomerAddressDto): void {
    this.confirming.set(address.id);
    this.focusInCard(address.id, 'address-remove-yes');
  }

  /** Dismisses the question and gives focus back to the Remove button it replaced. */
  protected cancelRemove(address: CustomerAddressDto): void {
    this.confirming.set(null);
    this.focusInCard(address.id, 'address-remove');
  }

  /**
   * Focuses one control inside one address's card, after the render that creates it.
   *
   * `afterNextRender` and **never `queueMicrotask`**: a signal write schedules change detection as a
   * task, so a microtask runs before the node exists — which passes Vitest, where the fixture has
   * already called `detectChanges`, and fails in a real browser. Measured twice on this project. An
   * `Injector` is passed because the callers are event handlers rather than injection contexts.
   */
  private focusInCard(addressId: number, testId: string): void {
    afterNextRender(
      () =>
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-address="${addressId}"] [data-testid="${testId}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  protected save(event: Event): void {
    event.preventDefault();

    if (!this.canSave()) {
      return;
    }

    const draft = this.form();
    const edit: CustomerAddressEdit = {
      label: blankToNull(draft.label),
      recipientName: draft.recipientName.trim(),
      phone: draft.phone.trim(),
      line1: draft.line1.trim(),
      line2: blankToNull(draft.line2),
      city: draft.city.trim(),
      // **`null` and not `0` for an unchosen district.** The server's request type makes it nullable for
      // exactly this reason: an omitted number binds `0` on a non-nullable property, and `0` is
      // indistinguishable from "they sent zero", so the validator could not tell a missing district from
      // an invalid one and would give the wrong message.
      districtId: this.chosenDistrictId(),
      postalCode: blankToNull(draft.postalCode),
      landmark: blankToNull(draft.landmark),
      isDefault: draft.isDefault,
    };

    const editingId = this.editing();

    this.pending.set(true);
    this.failure.set(null);
    this.serverErrors.set(new Map());

    const request =
      editingId === null ? this.api.addAddress(edit) : this.api.updateAddress(editingId, edit);

    request.subscribe({
      next: (book) => {
        this.pending.set(false);
        this.addresses.set(book);
        this.state.set('ready');
        this.editing.set(null);
        this.form.set(EMPTY_DRAFT);
        this.announcement.set(
          editingId === null ? 'Address saved.' : 'Changes to your address saved.',
        );
      },
      error: (error: unknown) => {
        this.pending.set(false);
        this.serverErrors.set(fieldErrorsOf(error));
        this.failure.set(accountErrorMessage(error));

        // A 409 means another tab filled the book while this form was open, so the page's idea of how
        // many addresses there are is out of date and the cap notice above the form is wrong. Re-reading
        // is the only way the shopper sees why Save stopped working.
        if (accountFailure(error) === 'addressLimit') {
          this.read();
        }

        afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
      },
    });
  }

  protected makeDefault(address: CustomerAddressDto): void {
    if (this.pending()) {
      return;
    }

    this.pending.set(true);
    this.failure.set(null);

    this.api.setDefaultAddress(address.id).subscribe({
      next: (book) => {
        this.pending.set(false);
        this.addresses.set(book);
        this.announcement.set(`${this.describe(address)} is now your default address.`);
      },
      error: (error: unknown) => this.writeFailed(error),
    });
  }

  protected remove(address: CustomerAddressDto): void {
    if (this.pending()) {
      return;
    }

    // Taken **before** the write, because afterwards the card is gone and so is its position.
    const index = this.addresses().findIndex((candidate) => candidate.id === address.id);

    this.pending.set(true);
    this.confirming.set(null);
    this.failure.set(null);

    this.api.deleteAddress(address.id).subscribe({
      next: (book) => {
        this.pending.set(false);
        this.addresses.set(book);
        this.announcement.set(`${this.describe(address)} removed.`);

        // If the removed address was being edited, the form is holding something that no longer exists.
        if (this.editing() === address.id) {
          this.editing.set(null);
          this.form.set(EMPTY_DRAFT);
        }

        this.removal.set({ index, ticket: ++this.removalTicket });
      },
      error: (error: unknown) => this.writeFailed(error),
    });
  }

  private writeFailed(error: unknown): void {
    this.pending.set(false);
    this.failure.set(accountErrorMessage(error));

    // A 404 means it was already removed, elsewhere or in another tab, so the book in hand is stale.
    if (accountFailure(error) === 'notFound') {
      this.read();
    }

    afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
  }

  private readDistricts(): void {
    // `CheckoutApi.get(null, null)` is a quote with no district and no method chosen, which is what this
    // page wants: the reference data and no delivery arithmetic. A failure is quiet — the rest of the
    // page still works, and the select says so by having nothing in it but "Choose a district", which is
    // a state the server would refuse rather than a wrong district silently saved.
    this.checkout.get(null, null).subscribe({
      next: (session) => this.districtList.set(session.districts),
      error: () => this.districtList.set([]),
    });
  }

  /**
   * Puts focus where the shopper was after a removal destroyed the button they pressed.
   *
   * - one card gone → the Remove button of the card that took its place, or of the last card when the
   *   removed one was last;
   * - nothing left → the form's heading, which is the only thing left to do.
   *
   * **It will not take focus from somewhere else.** A removal can only be started from this page, but the
   * shopper may have moved to the header or the footer while the request was in flight, and moving focus
   * then would be a worse surprise than the one being fixed. The node that was pressed is already gone,
   * so focus sitting on `<body>` is the signal that it was ours; anything else inside this page is ours
   * too. `WishlistPage.rehomeFocus` is where this shape comes from, and W2-08 is where it was learned.
   */
  private rehomeFocus(removal: { readonly index: number; readonly ticket: number } | null): void {
    if (!removal || removal.ticket === this.answeredRemoval) {
      return;
    }

    this.answeredRemoval = removal.ticket;

    const host = this.host.nativeElement;
    const active = host.ownerDocument.activeElement;
    if (active !== host.ownerDocument.body && !host.contains(active)) {
      return;
    }

    const buttons = [...host.querySelectorAll<HTMLElement>('[data-testid="address-remove"]')];

    if (buttons.length === 0) {
      this.formTitle()?.nativeElement.focus();

      return;
    }

    buttons[Math.min(Math.max(removal.index, 0), buttons.length - 1)].focus();
  }
}
