import { isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, Signal, computed, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, shareReplay, tap } from 'rxjs';

import { AccountApi, accountFailure } from './account-api';
import { CustomerSessionDto } from './account.model';

/**
 * What the shell knows about who is signed in.
 *
 * - `unknown` — nobody has asked yet. **This is the state the server renders in, for everyone**, and it
 *   is not the same as `signedOut`: the difference is the whole of why the header does not draw a
 *   "Sign in" link on the server and then replace it with a name.
 * - `loading` — `GET /api/account/me` is in flight. Still not `signedOut`.
 * - `signedIn` / `signedOut` — the answer came back.
 */
export type AccountPhase = 'unknown' | 'loading' | 'signedIn' | 'signedOut';

/**
 * The signed-in customer (ACC-01, ACC-02, doc 11 P11).
 *
 * Provided in root, because the header, the guard and six account pages are views of one fact: the
 * header greeting and the page that greets you must agree in the same frame, and two stores would be two
 * answers to "am I signed in".
 *
 * ## It never asks the server during a server render, and that is a decision with a reason
 *
 * The session cookie is `sd_customer_auth` and `cartCookieInterceptor` forwards **only `sd_cart`** —
 * deliberately, because the same browser may be carrying `sd_admin_auth` and relaying a staff session
 * from the SSR process to the API on a shopper's page render would hand the storefront an authenticated
 * identity it has no business holding. So during SSR this store has no way to know who the visitor is,
 * and the honest thing is not to guess: {@link load} answers `null` on the server without making a
 * request, the phase stays `unknown`, and the markup the server emits is the same for every visitor.
 *
 * **Which is also what keeps the rest of the site cacheable.** This store feeds the site header, and the
 * header is on the home page, every listing and every product page. If the server rendered a customer's
 * first name into it, every page in the shop would have to answer `no-store` with `Vary: Cookie` — the
 * treatment `/bag` and `/checkout` get for one page each — and the storefront would lose shared caching
 * everywhere to greet one shopper half a second sooner. Reading it in the browser costs a greeting that
 * appears a frame late on the first load of a session and nothing else.
 *
 * **The alternative was considered and is written down rather than hidden.** Forwarding the session
 * cookie during SSR would let an account page render its own data on the server. It would need the
 * interceptor to learn a second cookie and a second allowed path set, it would put a name and an order
 * history into markup that a mis-set cache header could share, and it buys nothing a shopper can see on
 * a page that is `noindex` and `no-store` anyway. The brief asked for justification at the same length as
 * the interceptor's; this is it, and the answer is no.
 *
 * ## One read per application load, shared by everyone who asks
 *
 * {@link load} is **single-flight**: the first caller starts the request, every later caller gets the
 * same `Observable` through `shareReplay(1)`, and callers after it resolves get the cached answer with
 * no request at all. That is not a micro-optimisation — it is what stops the defect the brief names.
 * On a cold load the signed-in guard runs *during the first navigation* and the header's own read is
 * scheduled by `afterNextRender`, so without this there would be two `GET /api/account/me` calls racing
 * each other, and the loser's answer would arrive after the winner's and overwrite it. With it there is
 * exactly one request however many callers there are, and `onSameUrlNavigation` is not involved: the
 * router's default is `'ignore'`, so re-navigating to the same URL does not re-run the guard, and the
 * guard would be answered from the cache if it did.
 *
 * {@link refresh} is the deliberate escape hatch, for the two moments the cached answer is known to be
 * stale: a sign-in and a confirmation, both of which hand back a fresh session of their own.
 */
@Injectable({ providedIn: 'root' })
export class AccountStore {
  private readonly api = inject(AccountApi);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly current = signal<CustomerSessionDto | null>(null);
  private readonly phase = signal<AccountPhase>('unknown');

  /** The one read in flight, or the one that has already answered. See the class docblock. */
  private inFlight: Observable<CustomerSessionDto | null> | null = null;

  /** The signed-in customer, or null. Null while the phase is `unknown`, which is not "signed out". */
  readonly customer: Signal<CustomerSessionDto | null> = this.current.asReadonly();

  readonly status: Signal<AccountPhase> = this.phase.asReadonly();

  /** True only once the server has said so. False on the server and while the read is outstanding. */
  readonly isSignedIn = computed(() => this.phase() === 'signedIn');

  /**
   * True only once the server has said there is no session.
   *
   * **Not `!isSignedIn()`**, and the distinction is the one two red branches on this project were built
   * on: "I have not asked yet" and "there is nobody" render as the same falsy value, and a header that
   * offered a sign-in link on the strength of the first would be a confident wrong answer rather than a
   * slow one.
   */
  readonly isSignedOut = computed(() => this.phase() === 'signedOut');

  /**
   * Doc 11 §7 G-24, and the only thing confirmation governs.
   *
   * True for a signed-in customer who has not confirmed their address. It gates **nothing** — they sign
   * in, shop and check out — and it is shown so they know why their earlier guest orders are not in the
   * history yet (doc 08 §3.1 D5).
   */
  readonly needsEmailConfirmation = computed(() => {
    const customer = this.current();

    return customer !== null && !customer.emailConfirmed;
  });

  /**
   * Reads the session once per application load.
   *
   * On the server it answers `null` immediately and asks nothing — see the class docblock. In the browser
   * the first caller starts the request and everyone else shares it.
   *
   * **A 401 is an answer, not an error.** It is the ordinary state of every visitor who is not signed in,
   * so it resolves to `null` and the phase becomes `signedOut`. Anything else — the API down, a 500, the
   * browser offline — leaves the phase `unknown` rather than claiming the shopper is signed out, because
   * a guard that redirected to the sign-in page on the strength of a network blip would throw a
   * signed-in customer out of their own account.
   */
  load(): Observable<CustomerSessionDto | null> {
    if (!this.isBrowser) {
      return of(null);
    }

    if (this.inFlight) {
      return this.inFlight;
    }

    this.phase.set('loading');

    // **`tap` before `catchError`, not after**, and the first version had it the other way round. With
    // the order reversed the `tap` runs on the error branch's substituted `null` as well, so a 500 or an
    // offline browser went through `accept(null)` and became `signedOut` — the confident wrong answer
    // the `isSignedOut` docblock above is about, produced by the very code meant to avoid it.
    this.inFlight = this.api.me().pipe(
      tap((customer: CustomerSessionDto) => this.accept(customer)),
      catchError((error: unknown) => {
        // `signedOut` covers both "no cookie" and "the cookie names an account that is no longer an
        // active customer", which the server deliberately answers identically so the shell does not
        // have to guess what an empty 200 meant. It is an answer, so it is accepted as one.
        if (accountFailure(error) === 'signedOut') {
          this.accept(null);

          return of(null);
        }

        // Not an answer about the session at all. The phase goes back to `unknown` and the memo is
        // dropped, so the next caller tries again rather than inheriting a failure.
        this.inFlight = null;
        this.phase.set('unknown');

        return of(null);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    return this.inFlight;
  }

  /**
   * Forgets the cached read and takes the session again.
   *
   * For the two moments the cache is known to be stale and the caller has no session in hand: nothing
   * calls this speculatively.
   */
  refresh(): Observable<CustomerSessionDto | null> {
    this.inFlight = null;
    this.phase.set('unknown');

    return this.load();
  }

  /**
   * Takes a session the server has just handed back from somewhere other than `/me`.
   *
   * Sign-in, email confirmation and change-password all answer with the whole session, so the store is
   * told rather than asked — one fewer request, and no window in which the header disagrees with the page
   * that just signed the shopper in. It also replaces the memo, so a later {@link load} answers this
   * value instead of refetching.
   */
  accept(customer: CustomerSessionDto | null): void {
    this.current.set(customer);
    this.phase.set(customer === null ? 'signedOut' : 'signedIn');
    this.inFlight = of(customer);
  }

  /**
   * Signs out.
   *
   * **The local state is cleared whatever the server says**, including on a 401, because a 401 here means
   * the session had already gone — refusing to clear it would leave the header greeting somebody who is
   * not signed in. The caller navigates afterwards and re-reads the bag: doc 11 P11 step B.7 is explicit
   * that the guest bag is not restored, so the badge genuinely is zero and has to be told.
   */
  signOut(): Observable<boolean> {
    return this.api.signOut().pipe(
      map(() => true),
      catchError(() => of(false)),
      tap(() => this.accept(null)),
    );
  }
}
