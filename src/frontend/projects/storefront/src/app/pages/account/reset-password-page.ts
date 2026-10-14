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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { AccountNotice } from './account-notice';
import { AccountApi, accountErrorMessage, fieldErrorsOf } from '../../core/account/account-api';
import { ACCOUNT_FIELD_LENGTHS, PASSWORD_RULE } from '../../core/account/account.model';
import { privatePage } from '../../core/platform/private-page';

/**
 * Set a new password from a reset link (ACC-01, doc 11 P11 step C.9).
 *
 * ## The token comes out of the address bar and then leaves it
 *
 * It arrives as `?token=`, is read **once** from the route snapshot, and the address bar is rewritten
 * without it on the first render in the browser — `replaceUrl`, so the entry carrying it is not left in
 * history either. `CustomerTokenRequest`'s own docblock on the server is the other half of the same
 * decision: the token travels in the request *body* although it arrived in the page's URL, because a
 * token in a request URL lands in the API's access log and in any proxy's, where the page's own address
 * does not.
 *
 * It is read from the **snapshot** rather than from the `queryParamMap` observable, and that is the
 * narrower, more honest contract: this page is opened once, by one link, with one token. Subscribing
 * would mean reacting to the very rewrite below and reading `null` over the captured value — which is the
 * bug that shape invites.
 *
 * ## A missing token is a page state, not a crash
 *
 * Someone who opens `/account/reset-password` with no token at all — a truncated email, a copied link
 * missing its tail — gets the explanation and a way to ask for a new link, not an empty form that would
 * refuse whatever they typed. The form is only rendered when there is a token to spend.
 *
 * ## No session is issued, and the page says the true next step
 *
 * The server deliberately does not sign the shopper in: the link was opened by whatever browser read the
 * email, and handing a session to a mailbox reader is the one thing a reset must not do. Its reply
 * already says to sign in with the new password and that every other browser has been signed out, so
 * this page prints that reply and offers the sign-in link. It writes no sentence of its own.
 *
 * **A refused password does not spend the token**, so the same link still works — which is why the form
 * stays on screen after a 400 on `password`, and only a spent or wrong link (`invalid_link`) replaces it.
 *
 * ## SSR, the live region and the button
 *
 * The server renders the heading, the explanation and the form; it posts nothing, because redeeming a
 * one-time token during a page render would spend it on a prefetch or a crawl. The live regions are in
 * that markup from the first byte and only their text changes ({@link AccountNotice}) — **this page
 * re-renders on submit**, the case the brief singles out. `aria-disabled` and never `disabled` on the
 * button (#109).
 */
@Component({
  selector: 'sd-reset-password-page',
  imports: [AccountNotice, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc acc--narrow">
      <h1 class="acc__title">Choose a new password</h1>

      <sd-account-notice [alert]="error()" [status]="reply()" testId="reset-password" />

      @if (!hasToken()) {
        <p class="acc__lead" data-testid="reset-password-no-token">
          This page needs the link from your reset message. Open that link again, or ask for a new one.
        </p>
        <p class="acc-actions">
          <a class="btn btn--primary" routerLink="/account/forgot-password">Ask for a new link</a>
        </p>
      } @else if (done()) {
        <!-- The token is spent and the server issued no session, so the only thing left to do is sign
             in. The sentence above this is the server's; this is the control for it. -->
        <p class="acc-actions">
          <a class="btn btn--primary" routerLink="/account/sign-in" data-testid="reset-password-sign-in"
            >Sign in</a
          >
        </p>
      } @else {
        <form class="acc-form" novalidate (submit)="submit($event)">
          <div class="acc-field">
            <label class="acc-label" for="rp-password">New password</label>
            <input
              class="acc-input"
              id="rp-password"
              name="password"
              type="password"
              autocomplete="new-password"
              required
              [attr.maxlength]="LENGTHS.password"
              [value]="password()"
              [attr.aria-invalid]="messageFor('password') ? 'true' : null"
              [attr.aria-describedby]="
                messageFor('password')
                  ? 'rp-password-error rp-password-rule'
                  : 'rp-password-rule'
              "
              data-testid="reset-password-password"
              (input)="password.set(value($event))"
            />
            @if (messageFor('password'); as message) {
              <!-- The refusal is the server's own wording. The rule below it is a pinned copy of
                   the server's constant - see PASSWORD_RULE - so the two cannot disagree. -->
              <p class="acc-error" id="rp-password-error">{{ message }}</p>
            }
            <p class="acc-hint" id="rp-password-rule" data-testid="reset-password-rule">
              {{ PASSWORD_RULE }}
            </p>
          </div>

          <div class="acc-actions">
            <button
              class="btn btn--primary"
              type="submit"
              [attr.aria-disabled]="busy() ? 'true' : null"
              data-testid="reset-password-submit"
            >
              {{ busy() ? 'Saving…' : 'Set password' }}
            </button>
          </div>
        </form>
      }
    </div>
  `,
})
export class ResetPasswordPage {
  protected readonly LENGTHS = ACCOUNT_FIELD_LENGTHS;

  /**
   * The one sentence the server's validator and this form both state.
   *
   * Pinned to `CustomerPasswordPolicy.Requirement` by a C# convention test; see
   * {@link PASSWORD_RULE}.
   */
  protected readonly PASSWORD_RULE = PASSWORD_RULE;

  protected readonly password = signal('');

  private readonly api = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly notice = viewChild.required(AccountNotice);

  /** Read once, synchronously, before anything can rewrite the address bar. See the docblock. */
  private readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token');

  private readonly failure = signal<string | null>(null);
  private readonly accepted = signal<string | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly pending = signal(false);

  protected readonly error = this.failure.asReadonly();
  protected readonly reply = this.accepted.asReadonly();
  protected readonly busy = this.pending.asReadonly();

  /** Whether there is a token to spend at all. A blank `?token=` counts as none. */
  protected readonly hasToken = computed(() => (this.token ?? '').trim().length > 0);

  /** True once the password has been set. The form goes and the sign-in link takes its place. */
  protected readonly done = computed(() => this.accepted() !== null);

  constructor() {
    privatePage('Choose a new password', 'Set a new password for your account.');

    if (isPlatformBrowser(inject(PLATFORM_ID))) {
      // **After the first render, so the token has certainly been captured** — it is read above in the
      // field initialiser, which runs before this, so the order is a property of the class rather than
      // of the scheduler. `replaceUrl` so the entry holding the token is not left in history, and
      // `queryParams: {}` with the default `handling` so the whole query string goes rather than being
      // merged with.
      afterNextRender(() => {
        if (this.hasToken()) {
          void this.router.navigate([], { queryParams: {}, replaceUrl: true });
        }
      });
    }
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected messageFor(field: string): string | null {
    return this.serverErrors().get(field) ?? null;
  }

  protected submit(event: Event): void {
    event.preventDefault();

    if (this.pending() || !this.hasToken()) {
      return;
    }

    this.pending.set(true);
    this.failure.set(null);
    this.serverErrors.set(new Map());

    this.api.resetPassword(this.token ?? '', this.password()).subscribe({
      next: (reply) => {
        this.pending.set(false);
        this.accepted.set(reply.message);
        this.password.set('');

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
