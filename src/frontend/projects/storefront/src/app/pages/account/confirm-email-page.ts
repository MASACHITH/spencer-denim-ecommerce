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
import { AccountApi, accountErrorMessage, accountFailure } from '../../core/account/account-api';
import { AccountStore } from '../../core/account/account.store';
import { privatePage } from '../../core/platform/private-page';

/**
 * Confirm an email address, and show the guest orders it brought with it (ACC-01, doc 11 P11 step A.4,
 * doc 08 §3.1 D5).
 *
 * ## It posts in the browser and never on the server, and this one is not a preference
 *
 * Two independent reasons, either of which would be enough:
 *
 * 1. **Confirming needs the shopper's own session, and only the browser has it.** The server confirms
 *    only for a browser already signed in as the account the link names, and it issues no session of
 *    its own (W3-01 Blocker 2). The first version did sign the link's holder in, on the reasoning that
 *    the holder of the link is the owner — and the independent tester showed why that is wrong: somebody
 *    who registers *your* address before you do chooses the password, and the moment you click the
 *    link, your guest orders were theirs and your browser was signed in to their account. The link
 *    proves the mailbox; the session proves the password; linking now needs both.
 * 2. **The token is single-use.** A render is not an intention: a link prefetch, a crawler, a preview
 *    card or a duplicated SSR pass would spend the token and the shopper would open their own link to be
 *    told it had already been used. Which is also why it is not posted twice in the browser — see
 *    {@link redeemed}.
 *
 * So the server renders the heading and "Confirming your email address…", and the browser does the work
 * in `afterNextRender` — the same shape `wishlist-page.ts` uses for its own browser-only read, and
 * guarded by `isPlatformBrowser` as well as by the framework's own skipping of `afterNextRender` on the
 * server, so "the server never redeems a token" is a property of this file that a unit test can render
 * on the server platform and see.
 *
 * ## The linked orders are the whole point of the page
 *
 * Doc 11 P11's own example: "Customer registers with the email used for 3 guest orders → after
 * confirming, all 3 appear in My account." The response names them, so the page names them. **Empty is
 * the ordinary case** — most people register before they order — and it is said as a fact rather than as
 * a disappointment, because the confirmation still worked.
 *
 * ## One opaque refusal for every bad link
 *
 * Already used, expired, wrong, or for an address the account no longer uses all answer the same 400 with
 * the same sentence, because saying "expired" rather than "wrong" tells a holder the token was once real.
 * The page prints the server's sentence and offers the way to a new link, which is the only thing a
 * shopper can act on.
 *
 * The token is read once from the route snapshot and the address bar is rewritten without it — see
 * `reset-password-page.ts`, which makes the same decision for the same reason.
 */
@Component({
  selector: 'sd-confirm-email-page',
  imports: [AccountNotice, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container acc acc--narrow">
      <h1 class="acc__title">Confirm your email address</h1>

      <sd-account-notice [alert]="error()" [status]="outcome()" testId="confirm-email" />

      @if (!hasToken()) {
        <p class="acc__lead" data-testid="confirm-email-no-token">
          This page needs the link from your confirmation message. Open that link again, or ask for a new
          one from your profile.
        </p>
      } @else if (phase() === 'waiting') {
        <!-- **The page gates on having an answer, not on 'linked()' being empty.** The server cannot
             redeem the token, so its markup and the browser's first frame are this: a wait, not an
             outcome. Rendering the refusal branch here instead — which the first version of this
             template did, because "no linked orders yet" and "refused" are both falsy — would tell a
             shopper their own live link had failed, every time, for one frame and for the whole of the
             server's response. That is the confident wrong value 'wishlist-page.ts' records two red
             branches having been built on. -->
        <p class="acc__lead" role="status" data-testid="confirm-email-working">
          Confirming your email address…
        </p>
      } @else if (phase() === 'confirmed') {
        @if (linked(); as orders) {
          @if (orders.length > 0) {
          <!-- Doc 11 P11's own example, answered. A heading rather than a bare list, and 'h2' because
               the page's 'h1' is above it: no level is skipped. -->
            <h2 class="acc__subtitle">
              {{
                orders.length === 1
                  ? 'One earlier order is now yours'
                  : orders.length + ' earlier orders are now yours'
              }}
            </h2>
            <p class="acc__lead">
              These were placed with this email address before you had an account. They are in your
              order list now.
            </p>
            <ul class="acc-card__lines" data-testid="confirm-email-linked">
              @for (orderNumber of orders; track orderNumber) {
                <li>{{ orderNumber }}</li>
              }
            </ul>
          } @else {
            <p class="acc__lead" data-testid="confirm-email-none-linked">
              There were no earlier orders placed with this address to add.
            </p>
          }
        }

        <p class="acc-actions">
          <a class="btn btn--primary" routerLink="/account/orders" data-testid="confirm-email-orders"
            >See your orders</a
          >
        </p>
      } @else if (phase() === 'sign-in') {
        <!-- Not signed in. The server answered before looking at the token, so the link is still good:
             sign in and come straight back to it. "Forgot password" is the route for somebody who never
             chose a password for this address, which is exactly the person a pre-registered address
             would strand — resetting proves the mailbox and replaces whatever password was set. -->
        <p class="acc-actions">
          <a
            class="btn btn--primary"
            routerLink="/account/sign-in"
            [queryParams]="{ returnTo: returnHere() }"
            data-testid="confirm-email-sign-in"
            >Sign in</a
          >
          <a
            class="link acc-inline-action"
            routerLink="/account/forgot-password"
            data-testid="confirm-email-forgot"
            >Forgot password</a
          >
        </p>
      } @else {
        <!-- Refused. The sentence is in the alert region above, which has had focus moved into it. -->
        <p class="acc-actions">
          <a class="btn btn--primary" routerLink="/account/profile">Ask for a new link</a>
          <a class="link acc-inline-action" routerLink="/account/sign-in">Sign in</a>
        </p>
      }
    </div>
  `,
})
export class ConfirmEmailPage {
  private readonly api = inject(AccountApi);
  private readonly store = inject(AccountStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly notice = viewChild.required(AccountNotice);

  /** Read once, synchronously, before the address bar is rewritten. */
  private readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token');

  /**
   * Whether the post has been made.
   *
   * **A plain field and not a signal**, because nothing renders from it: it exists so the token is spent
   * once however many times a render phase runs. `afterNextRender` runs once per component instance
   * today, so this is a guard against a future change rather than against an observed double post —
   * stated that way rather than claimed as measured.
   */
  private redeemed = false;

  /**
   * What the page knows.
   *
   * `waiting` is the state the server renders in and the browser's first frame, and it is **not** the
   * same as `refused`: see the template. It only ever moves forward, and only in the browser.
   */
  private readonly state = signal<'waiting' | 'confirmed' | 'sign-in' | 'refused'>('waiting');

  private readonly failure = signal<string | null>(null);
  private readonly confirmed = signal<string | null>(null);
  private readonly linkedOrders = signal<readonly string[]>([]);

  protected readonly error = this.failure.asReadonly();

  /** The server's own sentence for a successful confirmation, or null. */
  protected readonly outcome = this.confirmed.asReadonly();

  protected readonly linked = this.linkedOrders.asReadonly();
  protected readonly phase = this.state.asReadonly();

  protected readonly hasToken = computed(() => (this.token ?? '').trim().length > 0);

  /**
   * This page with its token, for the sign-in form's `returnTo`, so signing in lands back here and the
   * confirmation runs. The address bar no longer has the token in it, so it is rebuilt from the copy read
   * at construction. `returnPath` on the sign-in page accepts it because it is a same-origin path.
   */
  protected readonly returnHere = computed(
    () => `/account/confirm-email?token=${encodeURIComponent(this.token ?? '')}`,
  );

  constructor() {
    privatePage(
      'Confirm your email address',
      'Confirm your email address to add the orders you placed as a guest to your account.',
    );

    if (isPlatformBrowser(inject(PLATFORM_ID)) && this.hasToken()) {
      afterNextRender(() => {
        // `scroll: 'manual'`: this only takes the token out of the address bar, on the page the shopper is already on.
        void this.router.navigate([], { queryParams: {}, replaceUrl: true, scroll: 'manual' });
        this.confirm();
      });
    }
  }

  private confirm(): void {
    if (this.redeemed) {
      return;
    }

    this.redeemed = true;

    this.api.confirmEmail(this.token ?? '').subscribe({
      next: (result) => {
        this.linkedOrders.set(result.linkedOrderNumbers);
        this.state.set('confirmed');

        // Told rather than asked: the shopper was already signed in as this customer, and the reply
        // carries the session with `emailConfirmed` now true, so the header and the profile agree with
        // the page in the same frame. Confirming merges no bag and issues no cookie, so the bag is not
        // re-read.
        this.store.accept(result.customer);

        // **The server's wording and not ours.** The only sentence written here is the one the server
        // does not send: a 200 carries the customer and the order numbers but no `message`, so this page
        // owns this line — and it is careful about the two things the server is careful about. It claims
        // nothing about an email and reveals nothing about any other address.
        this.confirmed.set('Your email address is confirmed.');

        afterNextRender(() => this.notice().focusStatus(), { injector: this.injector });
      },
      error: (error: unknown) => {
        this.state.set(accountFailure(error) === 'signInRequired' ? 'sign-in' : 'refused');
        this.failure.set(accountErrorMessage(error));

        afterNextRender(() => this.notice().focusAlert(), { injector: this.injector });
      },
    });
  }
}
