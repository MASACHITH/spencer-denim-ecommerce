import { isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  PLATFORM_ID,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import { AccountNav } from './account-nav';
import { AccountNotice } from './account-notice';
import { AccountApi, accountErrorMessage, fieldErrorsOf } from '../../core/account/account-api';
import {
  ACCOUNT_FIELD_LENGTHS,
  CustomerProfileDto,
  blankToNull,
} from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { CheckoutApi } from '../../core/checkout/checkout-api';
import { DistrictDto } from '../../core/checkout/checkout.model';
import { privatePage } from '../../core/platform/private-page';

/**
 * The profile: name, phone, default district, password, and a new confirmation link (ACC-02, doc 11 P11
 * step 10).
 *
 * ## The shell renders on the server; the profile does not
 *
 * `sd_customer_auth` is not forwarded to the API during SSR, on purpose (`cartCookieInterceptor`), so the
 * server cannot read this page's contents and does not pretend to. It renders the heading, the navigation
 * and "Loading your details…"; the browser reads in `afterNextRender`. The page answers `no-store`,
 * `private`, `Vary: Cookie` and `noindex` — this markup holds a name and a telephone number.
 *
 * ## Three forms, three outcomes, and one pair of live regions
 *
 * The details form, the password form and the confirmation-link button each have their own result, and
 * all three write into the same {@link AccountNotice} — which is in the server's markup from the first
 * byte and whose text is the only thing that ever changes. One pair rather than three, because a screen
 * reader watching three regions on one page hears whichever changed and has no way to know which form it
 * belonged to; each message therefore names its own subject ("Your details are saved", "Your password is
 * changed"), which is what makes one region enough.
 *
 * ## The email address is read-only, and that is the server's state rather than this page's choice
 *
 * Doc 11 P11 step 10 includes "changes email (confirmation to the new address required)" and **that flow
 * is not built**: `CustomerProfileEdit` on the server has no email field, and its own remarks say why —
 * it needs the uniqueness check across both user types, a token minted against the *new* address, the old
 * address left working until the new one is confirmed, and a decision about whether the old address is
 * notified. So the address is shown, labelled as the thing you sign in with, and there is no box for it.
 * A disabled input would imply a control that could be enabled; text says what is true.
 *
 * ## A district the shop stopped serving is dropped rather than refusing the save
 *
 * The server does that deliberately: refusing the whole save would lose a name change for a reason that
 * has nothing to do with the name. The page re-renders the form from the server's answer, so a dropped
 * district shows as unset rather than as a value that was not stored — which is the only way the shopper
 * finds out.
 *
 * ## Change password signs every other browser out, and says so
 *
 * The server bumps the security stamp and reissues *this* browser's cookie, so the shopper stays signed
 * in here and is signed out everywhere else. It answers with the whole session, which is handed straight
 * to {@link AccountStore} so the header does not go stale. A wrong current password counts towards the
 * same lockout as a wrong sign-in (#146), so this form can answer 423, and the sentence for it is the
 * server's.
 *
 * ## The confirmation link, and the sentence that is not written here
 *
 * Doc 11 §7 G-24: confirmation gates **only** the linking of earlier guest orders, so this is an offer and
 * never a gate. The server mints a fresh link, which invalidates any already in the inbox, and answers 202
 * with its own wording — "queued", not "sent", because doc 08 §3.8's transport is unconfigured. This page
 * prints that wording. It is offered only to a customer who is actually unconfirmed, because the server
 * answers the same 202 either way and a button that always says the same thing teaches nothing.
 *
 * ## No `[disabled]` on anything
 *
 * #109. Every button here carries `aria-disabled` at most and refuses in its handler, and pressing Enter
 * in a field is a press of its form's button.
 */
@Component({
  selector: 'sd-profile-page',
  imports: [AccountNav, AccountNotice],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc">
      <h1 class="acc__title">Your profile</h1>

      <sd-account-nav />
      <sd-account-notice [alert]="error()" [status]="said()" testId="profile" />

      @if (phase() === 'waiting') {
        <p class="acc__muted" role="status" data-testid="profile-loading">Loading your details…</p>
      } @else if (phase() === 'failed' && profile() === null) {
        <p class="acc-actions">
          <button class="btn btn--secondary" type="button" data-testid="profile-retry" (click)="read()">
            Try again
          </button>
        </p>
      } @else if (profile(); as current) {
        <section aria-labelledby="profile-details-title">
          <h2 class="acc__subtitle" id="profile-details-title">Your details</h2>

          <form class="acc-form" novalidate (submit)="saveDetails($event)">
            <div class="acc-field">
              <p class="acc-label" id="profile-email-label">Email</p>
              <!-- Text and not a disabled box: a disabled control implies one that could be enabled,
                   and the change-email flow is not built on the server. See the class docblock. -->
              <p class="acc-card__lines" data-testid="profile-email">{{ current.email }}</p>
              <p class="acc-hint">This is what you sign in with. Get in touch with us to change it.</p>
            </div>

            <div class="acc-row">
              <div class="acc-field">
                <label class="acc-label" for="pr-firstName">First name</label>
                <input
                  class="acc-input"
                  id="pr-firstName"
                  name="firstName"
                  type="text"
                  autocomplete="given-name"
                  required
                  [attr.maxlength]="LENGTHS.name"
                  [value]="details().firstName"
                  [attr.aria-invalid]="messageFor('firstName') ? 'true' : null"
                  [attr.aria-describedby]="messageFor('firstName') ? 'pr-firstName-error' : null"
                  data-testid="profile-first-name"
                  (input)="setDetail('firstName', $event)"
                />
                @if (messageFor('firstName'); as message) {
                  <p class="acc-error" id="pr-firstName-error">{{ message }}</p>
                }
              </div>

              <div class="acc-field">
                <label class="acc-label" for="pr-lastName">
                  Last name <span class="acc-optional">(optional)</span>
                </label>
                <input
                  class="acc-input"
                  id="pr-lastName"
                  name="lastName"
                  type="text"
                  autocomplete="family-name"
                  [attr.maxlength]="LENGTHS.name"
                  [value]="details().lastName"
                  [attr.aria-invalid]="messageFor('lastName') ? 'true' : null"
                  [attr.aria-describedby]="messageFor('lastName') ? 'pr-lastName-error' : null"
                  data-testid="profile-last-name"
                  (input)="setDetail('lastName', $event)"
                />
                @if (messageFor('lastName'); as message) {
                  <p class="acc-error" id="pr-lastName-error">{{ message }}</p>
                }
              </div>
            </div>

            <div class="acc-row">
              <div class="acc-field">
                <label class="acc-label" for="pr-phone">
                  Phone <span class="acc-optional">(optional)</span>
                </label>
                <input
                  class="acc-input"
                  id="pr-phone"
                  name="phone"
                  type="tel"
                  inputmode="tel"
                  autocomplete="tel"
                  [attr.maxlength]="LENGTHS.phone"
                  [value]="details().phone"
                  [attr.aria-invalid]="messageFor('phone') ? 'true' : null"
                  [attr.aria-describedby]="messageFor('phone') ? 'pr-phone-error' : null"
                  data-testid="profile-phone"
                  (input)="setDetail('phone', $event)"
                />
                @if (messageFor('phone'); as message) {
                  <p class="acc-error" id="pr-phone-error">{{ message }}</p>
                }
              </div>

              <div class="acc-field">
                <label class="acc-label" for="pr-district">
                  Default district <span class="acc-optional">(optional)</span>
                </label>
                <select
                  class="acc-input"
                  id="pr-district"
                  name="defaultDistrictId"
                  [attr.aria-invalid]="messageFor('defaultDistrictId') ? 'true' : null"
                  [attr.aria-describedby]="
                    messageFor('defaultDistrictId')
                      ? 'pr-district-error pr-district-hint'
                      : 'pr-district-hint'
                  "
                  data-testid="profile-district"
                  (change)="setDetail('districtId', $event)"
                >
                  <!-- "No default" is also what a district the shop has **stopped serving** shows as:
                       the list offers only active districts, so an id that is not in it has no option
                       to select, and a '<select>' with nothing selected shows its first option — which
                       would read as the shopper having chosen a district they never chose. The stored id
                       is left alone until they save, and the server drops it then, which is what makes
                       the display and the stored value agree afterwards. -->
                  <option value="" [selected]="!districtIsOffered()">No default</option>
                  @for (district of districts(); track district.id) {
                    <option [value]="district.id" [selected]="district.id === chosenDistrictId()">
                      {{ district.name }}
                    </option>
                  }
                </select>
                <!-- Doc 08 §3.5 R7: the default district is what a delivery estimate is worked out from
                     before a shopper has reached checkout. -->
                <p class="acc-hint" id="pr-district-hint">
                  Used to work out delivery estimates before you reach checkout.
                </p>
                @if (messageFor('defaultDistrictId'); as message) {
                  <p class="acc-error" id="pr-district-error">{{ message }}</p>
                }
              </div>
            </div>

            <div class="acc-actions">
              <button
                class="btn btn--primary"
                type="submit"
                [attr.aria-disabled]="savingDetails() ? 'true' : null"
                data-testid="profile-save"
              >
                {{ savingDetails() ? 'Saving…' : 'Save details' }}
              </button>
            </div>
          </form>
        </section>

        <section class="acc__section" aria-labelledby="profile-password-title">
          <h2 class="acc__subtitle" id="profile-password-title">Change your password</h2>
          <p class="acc__lead">
            Changing it signs out every other browser that is signed in to this account. This one stays
            signed in.
          </p>

          <form class="acc-form acc--narrow" novalidate (submit)="changePassword($event)">
            <div class="acc-field">
              <label class="acc-label" for="pr-current">Current password</label>
              <input
                class="acc-input"
                id="pr-current"
                name="currentPassword"
                type="password"
                autocomplete="current-password"
                required
                [attr.maxlength]="LENGTHS.password"
                [value]="currentPassword()"
                [attr.aria-invalid]="messageFor('currentPassword') ? 'true' : null"
                [attr.aria-describedby]="messageFor('currentPassword') ? 'pr-current-error' : null"
                data-testid="profile-current-password"
                (input)="currentPassword.set(value($event))"
              />
              @if (messageFor('currentPassword'); as message) {
                <p class="acc-error" id="pr-current-error">{{ message }}</p>
              }
            </div>

            <div class="acc-field">
              <label class="acc-label" for="pr-new">New password</label>
              <input
                class="acc-input"
                id="pr-new"
                name="newPassword"
                type="password"
                autocomplete="new-password"
                required
                [attr.maxlength]="LENGTHS.password"
                [value]="newPassword()"
                [attr.aria-invalid]="messageFor('newPassword') ? 'true' : null"
                [attr.aria-describedby]="messageFor('newPassword') ? 'pr-new-error' : null"
                data-testid="profile-new-password"
                (input)="newPassword.set(value($event))"
              />
              @if (messageFor('newPassword'); as message) {
                <!-- The rule is the server's, which is the thing that enforces it: see
                     'CustomerPasswordPolicy', whose own remarks say the front end must not keep a
                     second copy of the number. -->
                <p class="acc-error" id="pr-new-error">{{ message }}</p>
              }
            </div>

            <div class="acc-actions">
              <button
                class="btn btn--primary"
                type="submit"
                [attr.aria-disabled]="changingPassword() ? 'true' : null"
                data-testid="profile-change-password"
              >
                {{ changingPassword() ? 'Changing…' : 'Change password' }}
              </button>
            </div>
          </form>
        </section>

        @if (!current.emailConfirmed) {
          <section class="acc__section" aria-labelledby="profile-confirm-title">
            <h2 class="acc__subtitle" id="profile-confirm-title">Confirm your email address</h2>
            <p class="acc__lead">
              Your address is not confirmed yet. You can shop and check out as usual — confirming only
              adds the orders you placed as a guest with {{ current.email }} to your order list.
            </p>

            <p class="acc-actions">
              <button
                class="btn btn--secondary"
                type="button"
                [attr.aria-disabled]="resending() ? 'true' : null"
                data-testid="profile-resend"
                (click)="resend()"
              >
                {{ resending() ? 'Sending…' : 'Send a new confirmation link' }}
              </button>
            </p>
            <p class="acc-hint">Asking for a new link stops any earlier one from working.</p>
          </section>
        }
      }
    </div>
  `,
})
export class ProfilePage {
  protected readonly LENGTHS = ACCOUNT_FIELD_LENGTHS;

  private readonly api = inject(AccountApi);
  private readonly checkout = inject(CheckoutApi);
  private readonly store = inject(AccountStore);
  private readonly injector = inject(Injector);
  private readonly notice = viewChild.required(AccountNotice);

  private readonly current = signal<CustomerProfileDto | null>(null);
  private readonly districtList = signal<readonly DistrictDto[]>([]);
  private readonly state = signal<'waiting' | 'ready' | 'failed'>('waiting');
  private readonly failure = signal<string | null>(null);
  private readonly announcement = signal<string | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());

  private readonly form = signal({ firstName: '', lastName: '', phone: '', districtId: '' });
  private readonly savingProfile = signal(false);
  private readonly changing = signal(false);
  private readonly resendInFlight = signal(false);

  protected readonly currentPassword = signal('');
  protected readonly newPassword = signal('');

  protected readonly profile = this.current.asReadonly();
  protected readonly districts = this.districtList.asReadonly();
  protected readonly phase = this.state.asReadonly();
  protected readonly error = this.failure.asReadonly();
  protected readonly said = this.announcement.asReadonly();
  protected readonly details = this.form.asReadonly();
  protected readonly savingDetails = this.savingProfile.asReadonly();
  protected readonly changingPassword = this.changing.asReadonly();
  protected readonly resending = this.resendInFlight.asReadonly();

  /** The chosen district as a number, or null — the one place the select's string becomes a number. */
  protected readonly chosenDistrictId = computed(() => {
    const raw = this.form().districtId;

    return raw === '' ? null : Number(raw);
  });

  /** Whether the chosen district is one the shop still serves, and so has an option to select. */
  protected readonly districtIsOffered = computed(() => {
    const id = this.chosenDistrictId();

    return id !== null && this.districts().some((district) => district.id === id);
  });

  constructor() {
    privatePage('Your profile', 'Your name, telephone number, default district and password.');

    this.store.load().subscribe();

    if (isPlatformBrowser(inject(PLATFORM_ID))) {
      afterNextRender(() => {
        this.read();
        this.readDistricts();
      });
    }
  }

  /** Reads the profile. Callable from the template, because the failed state offers Try again. */
  protected read(): void {
    this.failure.set(null);

    this.api.profile().subscribe({
      next: (profile) => this.accept(profile),
      error: (error: unknown) => {
        this.failure.set(accountErrorMessage(error));
        this.state.set('failed');
      },
    });
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected setDetail(field: 'firstName' | 'lastName' | 'phone' | 'districtId', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value;

    this.form.update((draft) => ({ ...draft, [field]: value }));
  }

  protected messageFor(field: string): string | null {
    return this.serverErrors().get(field) ?? null;
  }

  protected saveDetails(event: Event): void {
    event.preventDefault();

    if (this.savingProfile()) {
      return;
    }

    const draft = this.form();

    this.savingProfile.set(true);
    this.failure.set(null);
    this.announcement.set(null);
    this.serverErrors.set(new Map());

    this.api
      .saveProfile({
        firstName: draft.firstName.trim(),
        lastName: blankToNull(draft.lastName),
        phone: blankToNull(draft.phone),
        defaultDistrictId: this.chosenDistrictId(),
      })
      .subscribe({
        next: (profile) => {
          this.savingProfile.set(false);

          // **Re-seeded from the answer, not from the draft.** The server drops a district it no longer
          // serves rather than refusing the save, so the form has to show what was actually stored — a
          // page that kept the draft would show a default district the shopper does not have.
          this.accept(profile);
          this.announcement.set('Your details are saved.');

          // The header greets the shopper by their first name, so a changed name has to reach it. The
          // profile answer is not a session — it has no security stamp — so the store is asked again
          // rather than told. One request, and only when a name may have changed.
          this.store.refresh().subscribe();
        },
        error: (error: unknown) => this.writeFailed(error),
      });
  }

  protected changePassword(event: Event): void {
    event.preventDefault();

    if (this.changing()) {
      return;
    }

    this.changing.set(true);
    this.failure.set(null);
    this.announcement.set(null);
    this.serverErrors.set(new Map());

    this.api.changePassword(this.currentPassword(), this.newPassword()).subscribe({
      next: (session) => {
        this.changing.set(false);

        // Both boxes emptied: a password left in a form behind a success message is a password sitting
        // in the DOM and a second submit waiting to be refused.
        this.currentPassword.set('');
        this.newPassword.set('');

        // The server reissued this browser's cookie with the new security stamp and handed back the
        // whole session, so the store is told rather than asked.
        this.store.accept(session);
        this.announcement.set(
          'Your password is changed. Any other browser that was signed in has been signed out.',
        );
      },
      error: (error: unknown) => this.writeFailed(error),
    });
  }

  protected resend(): void {
    if (this.resendInFlight()) {
      return;
    }

    this.resendInFlight.set(true);
    this.failure.set(null);
    this.announcement.set(null);

    this.api.resendConfirmation().subscribe({
      next: (reply) => {
        this.resendInFlight.set(false);

        // The server's own sentence. It says what was queued and makes no promise about delivery —
        // see the class docblock.
        this.announcement.set(reply.message);
        afterNextRender(() => this.notice().focusStatus(), { injector: this.injector });
      },
      error: (error: unknown) => {
        this.resendInFlight.set(false);
        this.writeFailed(error);
      },
    });
  }

  /** Takes a profile and re-seeds the form from it, so the boxes always show what is stored. */
  private accept(profile: CustomerProfileDto): void {
    this.current.set(profile);
    this.state.set('ready');
    this.form.set({
      firstName: profile.firstName,
      lastName: profile.lastName ?? '',
      phone: profile.phone ?? '',
      districtId: profile.defaultDistrictId === null ? '' : String(profile.defaultDistrictId),
    });
  }

  private writeFailed(error: unknown): void {
    this.savingProfile.set(false);
    this.changing.set(false);
    this.serverErrors.set(fieldErrorsOf(error));
    this.failure.set(accountErrorMessage(error));

    // WCAG 2.2 4.1.3 and the defect checkout recorded: a refused form that drew red field messages,
    // announced nothing and left focus on `<body>`. `afterNextRender`, never `queueMicrotask`.
    afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
  }

  private readDistricts(): void {
    // From checkout's own read, which answers `districts` whether or not there is a bag — see
    // `addresses-page.ts`, which makes the same call for the same reason. A failure is quiet: the select
    // then offers only "No default", which stores nothing rather than storing a wrong district.
    this.checkout.get(null, null).subscribe({
      next: (session) => this.districtList.set(session.districts),
      error: () => this.districtList.set([]),
    });
  }
}
