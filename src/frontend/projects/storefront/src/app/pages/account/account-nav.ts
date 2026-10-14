import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

import { AccountStore } from '../../core/account/account.store';

/**
 * My account's own navigation, and the "confirm your email" note (doc 11 P11 step D, §7 G-24).
 *
 * ## It is the hub, spread across the three pages rather than put on a fourth
 *
 * `/account` redirects to `/account/orders` instead of rendering a hub, and this is what makes that
 * defensible: a hub whose entire content is three links is a page a shopper has to get past to reach
 * anything, and doc 11 P11 step 12's order list is what they signed in to look at ("where is my
 * parcel" is the question the whole task exists to answer). `account.routes.ts` records the decision;
 * this component is the part of it a shopper can see.
 *
 * ## The confirmation note says what is true and nothing more
 *
 * Doc 11 §7 G-24 is explicit: an unconfirmed customer signs in, shops and checks out. Confirmation gates
 * **only** the linking of earlier guest orders (doc 08 §3.1 D5). So this is a note and never a barrier,
 * it does not use the word "verify" as if the account were restricted, and it does not say an email has
 * arrived or is on its way — `ops.EmailOutbox`'s transport is unconfigured until the client's mail
 * credentials arrive, and the server's own wording says "queued". It points at the profile page, where
 * asking for a new link is a button and the server's sentence is what the shopper reads.
 *
 * `role="status"` rather than `role="alert"`: it is a standing condition of the account, not an
 * interruption, and it is present on three pages — an assertive region re-announcing it on every
 * navigation would be the shop shouting the same thing three times.
 *
 * ## It renders nothing on the server
 *
 * `AccountStore` never asks who is signed in during a server render, because `sd_customer_auth` is not
 * forwarded to the API by `cartCookieInterceptor` (deliberately — see that file). So the phase is
 * `unknown` there and this draws the three links, which are true for everyone, and no note, because the
 * server does not know whether to show one. The browser adds it a frame later if it belongs.
 */
@Component({
  selector: 'sd-account-nav',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="acc-nav" aria-label="Your account">
      <ul class="acc-nav__list">
        <li>
          <a
            class="acc-nav__link"
            routerLink="/account/orders"
            routerLinkActive
            ariaCurrentWhenActive="page"
            data-testid="account-nav-orders"
            >Orders</a
          >
        </li>
        <li>
          <a
            class="acc-nav__link"
            routerLink="/account/addresses"
            routerLinkActive
            ariaCurrentWhenActive="page"
            data-testid="account-nav-addresses"
            >Addresses</a
          >
        </li>
        <li>
          <a
            class="acc-nav__link"
            routerLink="/account/profile"
            routerLinkActive
            ariaCurrentWhenActive="page"
            data-testid="account-nav-profile"
            >Profile</a
          >
        </li>
      </ul>
    </nav>

    <!-- **No `role` on this one, and that is the deliberate opposite of `AccountNotice`.** It is a
         standing condition of the account rather than something that just changed, so it is page
         content a screen reader reads in order — which also means it is exempt from the rule that a
         live region must exist before its text does, instead of pretending to keep it with an `@if`
         that creates the node and its sentence in the same update. Making it a live region would also
         re-announce the same sentence on each of the three account pages. -->
    @if (needsConfirmation()) {
      <p class="acc-notice acc-notice--ok is-on" data-testid="account-confirm-note">
        Your email address is not confirmed yet. You can shop and check out as usual — confirming only
        adds the orders you placed as a guest with the same address to your order list.
        <a class="link acc-inline-action" routerLink="/account/profile">Ask for a new link</a>
      </p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
  `,
})
export class AccountNav {
  /**
   * True only for a customer the server has said is signed in and unconfirmed.
   *
   * **Not `!emailConfirmed`**: a signed-out visitor and a visitor nobody has asked about both have no
   * customer at all, and reading that as "unconfirmed" would put this note on the server's markup for
   * every visitor to every account page. `AccountStore.needsEmailConfirmation` holds the distinction.
   */
  protected readonly needsConfirmation = inject(AccountStore).needsEmailConfirmation;
}
