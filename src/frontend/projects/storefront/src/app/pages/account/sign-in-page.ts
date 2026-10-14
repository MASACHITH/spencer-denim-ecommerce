import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { AccountNotice } from './account-notice';
import { AccountApi, accountErrorMessage, fieldErrorsOf } from '../../core/account/account-api';
import { ACCOUNT_FIELD_LENGTHS } from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { returnPath } from '../../core/account/signed-in.guard';
import { CartStore } from '../../core/cart/cart.store';
import { privatePage } from '../../core/platform/private-page';

/**
 * Sign in (ACC-01, doc 11 P11 step B).
 *
 * ## What renders on the server, and what does not
 *
 * **The whole page renders on the server**, because all of it is the same for every visitor: a heading,
 * two empty boxes and two links. Nothing here is personal until something is typed, so there is no
 * reason to make a shopper wait for JavaScript to see the form — and no `afterNextRender` read, because
 * there is nothing to read.
 *
 * It still answers `no-store`, `private`, `Vary: Cookie` and `noindex` (see {@link privatePage}). The
 * cache headers are belt and braces on *this* page and are set anyway for the reason `wishlist-page.ts`
 * gives: which account pages are impersonal is a fact about a rendering decision in a component, and a
 * later task that puts a name in this markup must not also have to remember the header. `noindex` is not
 * belt and braces — a sign-in form in a search result is a phishing surface and a wasted crawl.
 *
 * The one thing it reads in the browser is {@link AccountStore.load}, and only to answer "are you
 * already signed in"; the note that follows is not a redirect, deliberately. A page a shopper
 * deliberately opened that bounces them somewhere else fights the back button, and the honest answer to
 * "I opened the sign-in page while signed in" is to say so and offer the way on.
 *
 * ## The live region is rendered before there is anything to announce
 *
 * **This page re-renders on submit** — same form, same values, one new sentence — which is precisely the
 * case the project has shipped wrong twice. {@link AccountNotice} is in the markup from the server's
 * first render and only its *text* changes; the refusal is never a node that arrives with its own
 * message. Focus moves into it after a refused submit, on `afterNextRender` and never `queueMicrotask`
 * (a signal write schedules change detection as a task, so a microtask runs before the node exists —
 * which passes Vitest and fails a real browser, measured twice on this project).
 *
 * ## No `[disabled]` on the submit button
 *
 * #109. A `disabled` attribute applied to the control that currently has focus throws focus to `<body>`
 * and fires no `focusin`, so the press of the button throws the shopper's place away — and pressing
 * Enter in a field is a press of this button. It carries `aria-disabled` while a request is in flight
 * and {@link submit} refuses a second press, so the state is visible to assistive technology, the
 * control stays reachable, and the shopper can still read why.
 *
 * ## One refusal, whatever went wrong
 *
 * The server answers one 401 for a wrong password, an unknown address, a staff address and a deactivated
 * account alike, each at the same password-hash cost (doc 08 §3.1 DB10, #101). This page must not undo
 * that at the last step, so it prints the server's sentence and adds nothing: no "we don't recognise
 * that email", and no field-level mark on `email` rather than `password`. A 423 is different and is
 * allowed to be — the lock is a fact about an account the holder of its password can see anyway, and the
 * server's own sentence says it clears by itself.
 */
@Component({
  selector: 'sd-sign-in-page',
  imports: [AccountNotice, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc acc--narrow">
      <h1 class="acc__title">Sign in</h1>

      <sd-account-notice [alert]="error()" [status]="signedInNote()" testId="sign-in" />

      <form class="acc-form" novalidate (submit)="submit($event)">
        <div class="acc-field">
          <label class="acc-label" for="si-email">Email</label>
          <input
            class="acc-input"
            id="si-email"
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
            [attr.aria-describedby]="messageFor('email') ? 'si-email-error' : null"
            data-testid="sign-in-email"
            (input)="email.set(value($event))"
          />
          @if (messageFor('email'); as message) {
            <p class="acc-error" id="si-email-error">{{ message }}</p>
          }
        </div>

        <div class="acc-field">
          <label class="acc-label" for="si-password">Password</label>
          <input
            class="acc-input"
            id="si-password"
            name="password"
            type="password"
            autocomplete="current-password"
            required
            [attr.maxlength]="LENGTHS.password"
            [value]="password()"
            [attr.aria-invalid]="messageFor('password') ? 'true' : null"
            [attr.aria-describedby]="messageFor('password') ? 'si-password-error' : null"
            data-testid="sign-in-password"
            (input)="password.set(value($event))"
          />
          @if (messageFor('password'); as message) {
            <p class="acc-error" id="si-password-error">{{ message }}</p>
          }
        </div>

        <div class="acc-actions">
          <!-- 'aria-disabled', never 'disabled': see the class docblock and #109. -->
          <button
            class="btn btn--primary"
            type="submit"
            [attr.aria-disabled]="busy() ? 'true' : null"
            data-testid="sign-in-submit"
          >
            {{ busy() ? 'Signing in…' : 'Sign in' }}
          </button>
          <a class="link acc-inline-action" routerLink="/account/forgot-password"
            >Forgotten your password?</a
          >
        </div>
      </form>

      <p class="acc__lead acc__section">
        New here?
        <a class="link acc-inline-action" routerLink="/account/register" data-testid="sign-in-register"
          >Create an account</a
        >
      </p>
    </div>
  `,
  styles: `
    /* The one rule this page needs that the shared account stylesheet does not already give: the
       "New here?" line sits well below the form rather than looking like part of it. Everything else
       is '.acc-*' in 'storefront/src/styles.css', which exists because eight account pages drawing
       the same field from eight copies is how one of them ends up 36 px tall. */
    .acc__section {
      padding-top: var(--space-4);
    }
  `,
})
export class SignInPage {
  protected readonly LENGTHS = ACCOUNT_FIELD_LENGTHS;

  /**
   * Where to go after signing in, from `?returnTo=`.
   *
   * Bound from the query string by `withComponentInputBinding()` (`app.config.ts`). It is washed through
   * {@link returnPath} before it is ever used, because a value in the address bar is a string a stranger
   * can write and an unchecked one turns this form into an open redirect — the one real security decision
   * on these pages.
   */
  readonly returnTo = input<string | undefined>();

  protected readonly email = signal('');
  protected readonly password = signal('');

  private readonly api = inject(AccountApi);
  private readonly store = inject(AccountStore);
  private readonly cart = inject(CartStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly notice = viewChild.required(AccountNotice);

  private readonly failure = signal<string | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly pending = signal(false);

  protected readonly error = this.failure.asReadonly();
  protected readonly busy = this.pending.asReadonly();

  /**
   * "You are already signed in", or null.
   *
   * Only ever non-null once the server has answered, so it is null on the server for everyone and null
   * in the browser until `GET /api/account/me` comes back. It is a `status` and not an `alert`: being
   * signed in is not a problem.
   */
  protected readonly signedInNote = computed(() => {
    const customer = this.store.customer();

    return customer === null
      ? null
      : `You are already signed in as ${customer.firstName}. Signing in again will replace that session.`;
  });

  constructor() {
    privatePage(
      'Sign in',
      'Sign in to see your orders, their tracking and your saved addresses.',
    );

    // The only thing this page asks the shop before a submit, and only so it can say "you are already
    // signed in". Single-flight in the store, so this shares whatever request the header or a guard has
    // already started rather than racing it. A no-op on the server.
    this.store.load().subscribe();
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** The server's message for one field, or null. There is no client-side validation on this form. */
  protected messageFor(field: string): string | null {
    return this.serverErrors().get(field) ?? null;
  }

  /**
   * Signs in.
   *
   * **It refuses its own second press rather than being disabled.** `busy()` is what the button says and
   * what this checks; the two cannot disagree because there is one signal.
   */
  protected submit(event: Event): void {
    event.preventDefault();

    if (this.pending()) {
      return;
    }

    this.pending.set(true);
    this.failure.set(null);
    this.serverErrors.set(new Map());

    this.api.signIn(this.email(), this.password()).subscribe({
      next: (customer) => {
        this.pending.set(false);

        // Told rather than asked: the response *is* the session, so there is no window in which the
        // header disagrees with the page that just signed the shopper in.
        this.store.accept(customer);

        // **The server merged the guest bag into the account bag in this same request** (doc 11 P11 step
        // B.6), so the header's badge is now stale by exactly the number of lines that moved. Re-reading
        // it here is the only way the shopper sees the right count without a full page load. The
        // wishlist half of that step is **not built on the server** — a guest wishlist lives in the
        // browser (doc 08 §1.7) and there is no port to post it to — so there is nothing to re-read, and
        // the browser's own list is what the wishlist page keeps showing.
        this.cart.load();

        void this.router.navigateByUrl(returnPath(this.returnTo()));
      },
      error: (error: unknown) => {
        this.pending.set(false);
        this.serverErrors.set(fieldErrorsOf(error));
        this.failure.set(accountErrorMessage(error));

        // WCAG 2.2 4.1.3, and the defect checkout recorded: a refused form that drew red field messages,
        // announced nothing and left focus on `<body>`. `afterNextRender` because the region's text is
        // written by the signal above and does not exist until change detection has run; an `Injector` is
        // passed because this is an HTTP callback and not an injection context.
        afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
      },
    });
  }
}
