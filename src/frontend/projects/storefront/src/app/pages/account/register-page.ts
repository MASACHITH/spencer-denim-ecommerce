import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';

import { AccountNotice } from './account-notice';
import { AccountApi, accountErrorMessage, fieldErrorsOf } from '../../core/account/account-api';
import {
  ACCOUNT_FIELD_LENGTHS,
  PASSWORD_RULE,
  blankToNull,
} from '../../core/account/account.model';
import { privatePage } from '../../core/platform/private-page';

/**
 * Create an account (ACC-01, doc 11 P11 step A).
 *
 * ## The reply is the server's sentence, word for word
 *
 * The server answers **202 and one sentence for every address** — free, already a customer's, or a staff
 * member's — because doc 08 §3.1 DB10 says the site must never reveal whether an address is registered.
 * The sentence it sends also says "**queued**" rather than "sent", because doc 08 §3.8's mail transport
 * is unconfigured until the client's mail credentials arrive. Both properties are destroyed by a page
 * that writes its own confirmation, so this page writes none: it prints `message` and nothing else. W3-02
 * removed "we will email your order number" from checkout for the second reason, and
 * `checkout-page.spec.ts` has a live test asserting that page does not match `/email (you|your order)/i`.
 *
 * It is also why there is **no "you may already have an account" hint** anywhere on this form, and no
 * check as the shopper types: a duplicate-address check is an enumeration oracle however politely it is
 * worded.
 *
 * ## The form clears itself on success, and the heading does not change
 *
 * A 202 means the shopper's part is done, so the boxes are emptied — leaving a password in a filled form
 * behind a success message invites a second submit, which the server's rate limit would then refuse (five
 * registrations per /64 per ten minutes). Focus moves to the confirmation, because the sentence *is* the
 * outcome and reading it is the next thing to do.
 *
 * ## No password rule is printed, and that is the server's decision rather than an omission
 *
 * `CustomerPasswordPolicy` holds the minimum length and the sentence that describes it, and its own
 * remarks say "the front end reads it from the registration endpoint's problem details rather than
 * holding a second copy" — because #126 shipped a `maxlength` of 120 against a 100-character column, and
 * a form promising eight characters against an API refusing nine is the same defect. So a refused
 * password shows the server's own sentence, which names every rule actually enforced.
 *
 * **The cost is real and is in the task report**: a shopper learns the rule from a refusal rather than
 * before typing, which is a round trip and arguably thin against WCAG 2.2 SC 3.3.2. The fix is a server
 * that states the policy — the endpoint's own `WithDescription` already does, for a developer — and not a
 * number typed into this file.
 *
 * ## SSR, the live region, and the submit button
 *
 * Everything here is the same for every visitor, so the server renders all of it and the page reads
 * nothing in `afterNextRender`. The two live regions are in that server markup from the first byte and
 * only their text ever changes ({@link AccountNotice}); **this page re-renders on submit**, so a region
 * that arrived with its message would be a message nothing was watching. The submit button carries
 * `aria-disabled` and never `disabled` (#109): disabling the focused control throws focus to `<body>`,
 * and pressing Enter in a field is a press of this button.
 */
@Component({
  selector: 'sd-register-page',
  imports: [AccountNotice, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc acc--narrow">
      <h1 class="acc__title">Create an account</h1>
      <p class="acc__lead">
        An account keeps your orders, their tracking and your saved addresses in one place.
      </p>

      <sd-account-notice [alert]="error()" [status]="reply()" testId="register" />

      <form class="acc-form" novalidate (submit)="submit($event)">
        <div class="acc-row">
          <div class="acc-field">
            <label class="acc-label" for="rg-firstName">First name</label>
            <input
              class="acc-input"
              id="rg-firstName"
              name="firstName"
              type="text"
              autocomplete="given-name"
              required
              [attr.maxlength]="LENGTHS.name"
              [value]="firstName()"
              [attr.aria-invalid]="messageFor('firstName') ? 'true' : null"
              [attr.aria-describedby]="messageFor('firstName') ? 'rg-firstName-error' : null"
              data-testid="register-first-name"
              (input)="firstName.set(value($event))"
            />
            @if (messageFor('firstName'); as message) {
              <p class="acc-error" id="rg-firstName-error">{{ message }}</p>
            }
          </div>

          <div class="acc-field">
            <label class="acc-label" for="rg-lastName">
              Last name <span class="acc-optional">(optional)</span>
            </label>
            <input
              class="acc-input"
              id="rg-lastName"
              name="lastName"
              type="text"
              autocomplete="family-name"
              [attr.maxlength]="LENGTHS.name"
              [value]="lastName()"
              [attr.aria-invalid]="messageFor('lastName') ? 'true' : null"
              [attr.aria-describedby]="messageFor('lastName') ? 'rg-lastName-error' : null"
              data-testid="register-last-name"
              (input)="lastName.set(value($event))"
            />
            @if (messageFor('lastName'); as message) {
              <p class="acc-error" id="rg-lastName-error">{{ message }}</p>
            }
          </div>
        </div>

        <div class="acc-field">
          <label class="acc-label" for="rg-email">Email</label>
          <input
            class="acc-input"
            id="rg-email"
            name="email"
            type="email"
            inputmode="email"
            autocomplete="email"
            autocapitalize="none"
            spellcheck="false"
            required
            [attr.maxlength]="LENGTHS.email"
            [value]="email()"
            [attr.aria-invalid]="messageFor('email') ? 'true' : null"
            [attr.aria-describedby]="
              messageFor('email') ? 'rg-email-error rg-email-hint' : 'rg-email-hint'
            "
            data-testid="register-email"
            (input)="email.set(value($event))"
          />
          <!-- It says what the address is *for* and makes no claim about a message arriving: the
               transport is unconfigured (doc 08 §3.8) and the server's own wording says "queued". -->
          <p class="acc-hint" id="rg-email-hint">This is what you sign in with.</p>
          @if (messageFor('email'); as message) {
            <p class="acc-error" id="rg-email-error">{{ message }}</p>
          }
        </div>

        <div class="acc-field">
          <label class="acc-label" for="rg-password">Password</label>
          <input
            class="acc-input"
            id="rg-password"
            name="password"
            type="password"
            autocomplete="new-password"
            required
            [attr.maxlength]="LENGTHS.password"
            [value]="password()"
            [attr.aria-invalid]="messageFor('password') ? 'true' : null"
            [attr.aria-describedby]="
              messageFor('password')
                ? 'rg-password-error rg-password-rule'
                : 'rg-password-rule'
            "
            data-testid="register-password"
            (input)="password.set(value($event))"
          />
          @if (messageFor('password'); as message) {
            <p class="acc-error" id="rg-password-error">{{ message }}</p>
          }
          <p class="acc-hint" id="rg-password-rule" data-testid="register-password-rule">
            {{ PASSWORD_RULE }}
          </p>
        </div>

        <div class="acc-field">
          <label class="acc-label" for="rg-phone">
            Phone <span class="acc-optional">(optional)</span>
          </label>
          <input
            class="acc-input"
            id="rg-phone"
            name="phone"
            type="tel"
            inputmode="tel"
            autocomplete="tel"
            [attr.maxlength]="LENGTHS.phone"
            [value]="phone()"
            [attr.aria-invalid]="messageFor('phone') ? 'true' : null"
            [attr.aria-describedby]="
              messageFor('phone') ? 'rg-phone-error rg-phone-hint' : 'rg-phone-hint'
            "
            data-testid="register-phone"
            (input)="phone.set(value($event))"
          />
          <!-- Doc 11 P11 step A.1 makes it optional, and checkout asks for one per order anyway, so
               nobody is blocked here for want of it. -->
          <p class="acc-hint" id="rg-phone-hint">Only used if we need to reach you about an order.</p>
          @if (messageFor('phone'); as message) {
            <p class="acc-error" id="rg-phone-error">{{ message }}</p>
          }
        </div>

        <div class="acc-actions">
          <button
            class="btn btn--primary"
            type="submit"
            [attr.aria-disabled]="busy() ? 'true' : null"
            data-testid="register-submit"
          >
            {{ busy() ? 'Creating…' : 'Create account' }}
          </button>
          <a class="link acc-inline-action" routerLink="/account/sign-in" data-testid="register-sign-in"
            >Sign in instead</a
          >
        </div>
      </form>
    </div>
  `,
})
export class RegisterPage {
  protected readonly LENGTHS = ACCOUNT_FIELD_LENGTHS;

  /**
   * The one sentence the server's validator and this form both state.
   *
   * Pinned to `CustomerPasswordPolicy.Requirement` by a C# convention test; see
   * {@link PASSWORD_RULE}.
   */
  protected readonly PASSWORD_RULE = PASSWORD_RULE;

  protected readonly firstName = signal('');
  protected readonly lastName = signal('');
  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly phone = signal('');

  private readonly api = inject(AccountApi);
  private readonly injector = inject(Injector);
  private readonly notice = viewChild.required(AccountNotice);

  private readonly failure = signal<string | null>(null);
  private readonly accepted = signal<string | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly pending = signal(false);

  protected readonly error = this.failure.asReadonly();

  /** The server's own sentence, or null. Never written here — see the class docblock. */
  protected readonly reply = this.accepted.asReadonly();

  protected readonly busy = this.pending.asReadonly();

  constructor() {
    privatePage(
      'Create an account',
      'Create an account to keep your orders, their tracking and your saved addresses in one place.',
    );
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected messageFor(field: string): string | null {
    return this.serverErrors().get(field) ?? null;
  }

  protected submit(event: Event): void {
    event.preventDefault();

    if (this.pending()) {
      return;
    }

    this.pending.set(true);
    this.failure.set(null);
    this.accepted.set(null);
    this.serverErrors.set(new Map());

    this.api
      .register({
        firstName: this.firstName(),
        // An empty box is **null and not `''`**: the server's validator trims and treats blank as absent
        // for the optional fields, and sending `''` would store an empty string in a nullable column.
        lastName: blankToNull(this.lastName()),
        email: this.email(),
        password: this.password(),
        phone: blankToNull(this.phone()),
      })
      .subscribe({
        next: (reply) => {
          this.pending.set(false);
          this.accepted.set(reply.message);

          // Emptied on success, password included: a filled form behind a success message invites a
          // second submit, and the server's rate limit (five registrations per /64 per ten minutes)
          // would refuse it with a sentence about too many attempts that would read as a fault.
          this.firstName.set('');
          this.lastName.set('');
          this.email.set('');
          this.password.set('');
          this.phone.set('');

          afterNextRender(() => this.notice().focusStatus(), { injector: this.injector });
        },
        error: (error: unknown) => {
          this.pending.set(false);
          this.serverErrors.set(fieldErrorsOf(error));
          this.failure.set(accountErrorMessage(error));

          // See `sign-in-page.ts`: `afterNextRender`, never `queueMicrotask`.
          afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
        },
      });
  }
}
