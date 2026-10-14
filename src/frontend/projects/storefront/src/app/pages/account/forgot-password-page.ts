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
import { ACCOUNT_FIELD_LENGTHS } from '../../core/account/account.model';
import { privatePage } from '../../core/platform/private-page';

/**
 * Ask for a password-reset link (ACC-01, doc 11 P11 step C.8).
 *
 * **One field, one sentence, and the sentence is the server's.** 202 for every address: only an active
 * customer match mints a token, a staff address and an unknown address send nothing, and all three answer
 * identically (doc 08 §3.1 DB10). The server even accepts a badly-shaped address rather than refusing it,
 * because "that is not a valid address" tells a caller which strings are worth trying — so this page does
 * no shape validation of its own either, and its `type="email"` with `novalidate` on the form is a
 * keyboard hint rather than a gate.
 *
 * The server's reply says what it queued and offers the shop as a fallback if nothing arrives; it does
 * **not** say "check your inbox", because that is a promise about delivery and doc 08 §3.8's transport is
 * unconfigured. This page prints that reply and writes nothing of its own.
 *
 * SSR: the whole page, because none of it is personal. The live regions are in the server's markup and
 * only their text changes ({@link AccountNotice}) — **this page re-renders on submit**, which is the case
 * the rule exists for. The submit button carries `aria-disabled` and never `disabled` (#109).
 */
@Component({
  selector: 'sd-forgot-password-page',
  imports: [AccountNotice, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc acc--narrow">
      <h1 class="acc__title">Reset your password</h1>
      <p class="acc__lead">Tell us the email address you sign in with and we will queue a link to it.</p>

      <sd-account-notice [alert]="error()" [status]="reply()" testId="forgot-password" />

      <form class="acc-form" novalidate (submit)="submit($event)">
        <div class="acc-field">
          <label class="acc-label" for="fp-email">Email</label>
          <input
            class="acc-input"
            id="fp-email"
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
            [attr.aria-describedby]="messageFor('email') ? 'fp-email-error' : null"
            data-testid="forgot-password-email"
            (input)="email.set(value($event))"
          />
          @if (messageFor('email'); as message) {
            <p class="acc-error" id="fp-email-error">{{ message }}</p>
          }
        </div>

        <div class="acc-actions">
          <button
            class="btn btn--primary"
            type="submit"
            [attr.aria-disabled]="busy() ? 'true' : null"
            data-testid="forgot-password-submit"
          >
            {{ busy() ? 'Sending…' : 'Send a reset link' }}
          </button>
          <a class="link acc-inline-action" routerLink="/account/sign-in">Back to sign in</a>
        </div>
      </form>
    </div>
  `,
})
export class ForgotPasswordPage {
  protected readonly LENGTHS = ACCOUNT_FIELD_LENGTHS;

  protected readonly email = signal('');

  private readonly api = inject(AccountApi);
  private readonly injector = inject(Injector);
  private readonly notice = viewChild.required(AccountNotice);

  private readonly failure = signal<string | null>(null);
  private readonly accepted = signal<string | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly pending = signal(false);

  protected readonly error = this.failure.asReadonly();
  protected readonly reply = this.accepted.asReadonly();
  protected readonly busy = this.pending.asReadonly();

  constructor() {
    privatePage(
      'Reset your password',
      'Ask for a link to set a new password for your account.',
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

    this.api.forgotPassword(this.email()).subscribe({
      next: (reply) => {
        this.pending.set(false);
        this.accepted.set(reply.message);

        // Emptied, so a second press cannot ask again for the same address and spend one of the five
        // reset links the server allows per /64 per ten minutes.
        this.email.set('');

        afterNextRender(() => this.notice().focusStatus(), { injector: this.injector });
      },
      error: (error: unknown) => {
        this.pending.set(false);
        this.serverErrors.set(fieldErrorsOf(error));
        this.failure.set(accountErrorMessage(error));

        afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
      },
    });
  }
}
