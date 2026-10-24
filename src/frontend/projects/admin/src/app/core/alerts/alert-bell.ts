import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs';

import { colomboTime } from '../orders/order-formatting';
import { AdminAlertsApi } from './admin-alerts-api';
import { AdminAlert, alertAdvice, severityLabel } from './admin-alert.model';

/** One alert that this user has already cleared, kept on screen with its outcome. */
interface Cleared {
  atUtc: string;
  byUs: boolean;
}

/**
 * The alert **bell** in the admin header (UAT feedback, 2026-10-24). It is the persistent alert banner of doc 08
 * §3.8 N2 / doc 11 §4.3, §6.1 step 1, drawn as a bell with a red count and a panel that opens under it.
 *
 * **Why a bell and not the full-width strip it replaced.** The strip was a yellow block that pushed every page
 * down and, with several alerts, took most of the first screen; the client's reviewers asked for the alerts to
 * live somewhere of their own. The bell keeps the two properties the strip was built for: it is in the shell,
 * so it is on every admin screen, and the red count is visible without opening anything. Everything below that
 * paragraph — where the component lives, the five accessibility rules, the silence — was written for the strip
 * and is unchanged, except that "the banner" now means the panel the bell opens.
 *
 * **The bell is drawn only for a user who may have alerts.** A 403 or a failed load draws nothing at all. An
 * empty list is different: the bell is drawn without a count and the panel says nothing needs attention, so a
 * Super Admin can tell "no alerts" from "not allowed to see alerts".
 *
 * **Opening is a disclosure, not a dialog.** The button has `aria-expanded` and `aria-controls`; the panel is a
 * named region after the button in the tab order. Escape closes it and returns focus to the button, a press
 * anywhere else on the page closes it, and so does navigating. Acknowledging an alert does not close it
 * (rule 1 below).
 *
 * The rest of this comment is the original, and says "banner" where it now means the panel.
 *
 * **It lives in the admin shell and not on the dashboard, and that is this task's one real design
 * decision.** Doc 08 calls it "the dashboard banner" and ADM-04's dashboard does not exist: the route at
 * `/` is W0-09's scaffold placeholder, listed as `planned`, 0 % on the client's progress page. Putting the
 * banner there would mean a dead email raises a banner a Super Admin sees only if they happen to go back
 * to the home screen — and doc 11 §6.1's whole morning routine starts from these banners. In the shell it
 * is on every admin screen, which is what "persistent until acknowledged" means in practice, and it is
 * *more* than doc 08 asks for rather than less.
 *
 * **ADM-04 adopts this component rather than building a second one.** It is self-contained — it fetches,
 * renders and acknowledges on its own and takes no inputs — so a screen could render `<sda-alert-bell />`
 * inside a widget and the shell can drop it, or both can keep it, with no change here. What ADM-04 must not
 * do is write a second implementation: two banners would be two wordings of "this does not fix anything".
 *
 * **Five accessibility rules, each of them a defect this project has already paid for.**
 *
 * 1. **An acknowledged alert is not torn out from under the focus.** It stays on screen with its outcome
 *    and goes when the next page load re-reads the list — the rule `bank-transfers-page.ts` follows for a
 *    decided row. Removing the row would destroy the node holding the focus, which is the same harm as
 *    disabling it (issue #109).
 * 2. **Nothing is ever `disabled`.** Every button carries `aria-disabled` and the handler refuses while a
 *    write is in flight (issue #109). The count of instances is left out on purpose: the comments
 *    already in this repository say six and the brief for this task says seven, and I have not read the
 *    issue.
 * 3. **The confirmation is written where it survives.** The live region is the component's first child and
 *    is outside every `@if`, so it exists whether or not there are alerts. A branch last week put a
 *    refusal inside the panel its own refusal destroyed, and a live region inside a block that success
 *    removed — and a blind customer pressed Cancel and heard nothing at all. (Reported to this task by
 *    the owner; the branch is not named here because I have not checked which one it was.)
 * 4. **An identical announcement re-fires.** `role="status"` announces a *change*, so acknowledging two
 *    alerts in a row would be read out once; the second gets a trailing space (ADM-22).
 * 5. **The banner's title is not a heading.** It is a `<p>` that names the landmark through
 *    `aria-labelledby`, because this component renders *above* every admin page's `<h1>` — and an `<h2>`
 *    there would be the defect #76 and #20's SH11 were: a screen reader met the mega menu's headings
 *    "before the page said what it was", and "axe reports nothing, because nested heading order is a
 *    best-practice rule rather than a WCAG failure, which is exactly why it survived"
 *    (`accessibility.e2e.ts`). Nothing is lost: a `region` with an accessible name is how a
 *    screen-reader user finds a banner, and that file's own rule — the first heading in the DOM is the
 *    `h1` — stays true on every admin screen. `admin-alerts.e2e.ts` asserts it on three of them.
 *
 * **It is silent about itself.** No alerts, no permission, no API — all three render nothing. A banner that
 * said "could not load alerts" on every admin screen for a user who is not allowed any would be a
 * permanent error message about a feature that is not theirs.
 */
@Component({
  selector: 'sda-alert-bell',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Rule 3: outside every @if, so acknowledging the last alert cannot destroy the node the
         confirmation is written into. -->
    <p class="sr-only" role="status" data-testid="alert-announcement">{{ announcement() }}</p>

    @if (available()) {
      <div class="bell">
        <button
          class="bell__button"
          type="button"
          aria-controls="alert-panel"
          [attr.aria-expanded]="open()"
          (click)="toggle()"
          #bellButton
          data-testid="alert-bell"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
            <path d="M6 9a6 6 0 1 1 12 0c0 5 2 6.5 2 6.5H4S6 14 6 9Z" />
            <path d="M10 19a2 2 0 0 0 4 0" />
          </svg>
          @if (waiting() > 0) {
            <span class="bell__count" aria-hidden="true" data-testid="alert-count">{{ countText() }}</span>
          }
          <span class="sr-only">{{ buttonName() }}</span>
        </button>

        @if (open()) {
          <!-- A landmark with a name, and deliberately **not** a heading: see rule 5 above. -->
          <section class="alerts" id="alert-panel" aria-labelledby="alerts-heading" data-testid="alert-panel">
            <p class="alerts__heading" id="alerts-heading">
              {{ heading() }}
            </p>

            @if (alerts().length === 0) {
              <p class="alerts__hint" data-testid="alert-none">Nothing needs your attention right now.</p>
            } @else {
              <p class="alerts__hint">
                Acknowledging an alert records that you have seen it. It does not fix the problem and does not
                send, retry or adjust anything.
              </p>

              <ul class="alerts__list">
                @for (alert of alerts(); track alert.id) {
                  <li
                    class="alerts__item"
                    [class.is-critical]="alert.severity === 3"
                    [attr.data-testid]="'alert-' + alert.id"
                  >
                    <p class="alerts__severity">{{ severityOf(alert) }}</p>
                    <p class="alerts__title">{{ alert.title }}</p>
                    <p class="alerts__advice">{{ adviceFor(alert) }}</p>

                    <!-- Both, not just the id. A public id with no order number is an alert whose order was
                         not in the database when it was raised (see AdminAlert.orderNumber), so the link would
                         404 - and alertAdvice says so instead of sending somebody to it. Orders are never
                         deleted (doc 08 1.6), so the order cannot turn up later either. No backticks in here:
                         this comment is inside the component's template literal. -->
                    @if (alert.orderPublicId && alert.orderNumber) {
                      <a
                        class="adm-link"
                        [routerLink]="['/orders', alert.orderPublicId]"
                        [attr.data-testid]="'alert-order-' + alert.id"
                      >
                        Open order {{ alert.orderNumber }}
                      </a>
                    }

                    @if (cleared().get(alert.id); as outcome) {
                      <!-- Rule 1: the acknowledged alert keeps its place, and this is the node focus moves to. -->
                      <p
                        class="alerts__done"
                        tabindex="-1"
                        [attr.data-testid]="'alert-done-' + alert.id"
                        #outcomeMessage
                      >
                        {{ outcomeText(outcome) }}
                      </p>
                    } @else {
                      <!-- No note field. Doc 08 §3.8 gives the table an AcknowledgementNote column and
                           nothing writes it (owner's decision, 2026-10-09): ops.AdminAlerts is not in doc 08
                           §3.1 step 2's anonymisation deletion list and doc 11 §4 says these rows are never
                           scrubbed, so a free-text box beside a customer's order number was the one place in
                           this feature where a telephone number could be pasted into a table nothing can
                           clean. Nothing read it back either. -->
                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="busy() !== null ? 'true' : null"
                        (click)="acknowledge(alert)"
                        [attr.data-testid]="'alert-ack-' + alert.id"
                      >
                        {{ busy() === alert.id ? 'Recording…' : 'I have seen this' }}
                      </button>
                    }

                    @if (failure().get(alert.id); as message) {
                      <p class="alerts__error" role="alert" [attr.data-testid]="'alert-error-' + alert.id">
                        {{ message }}
                      </p>
                    }
                  </li>
                }
              </ul>
            }
          </section>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      margin-inline-start: auto;
    }
    .bell {
      position: relative;
    }
    .bell__button {
      position: relative;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: var(--tap);
      height: var(--tap);
      border: 0;
      background: transparent;
      color: inherit;
      cursor: pointer;
    }
    .bell__button svg {
      width: 24px;
      height: 24px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .bell__button:focus-visible {
      outline: 2px solid var(--color-on-primary);
      outline-offset: -4px;
    }
    .bell__button[aria-expanded='true'] {
      background: color-mix(in srgb, var(--color-on-primary) 18%, transparent);
    }
    /* White on the shop's sale red (--color-sale, #b3261e): 6.5:1. The ring separates it from the dark header. */
    .bell__count {
      position: absolute;
      top: 0;
      inset-inline-end: -6px;
      min-width: 16px;
      height: 16px;
      padding-inline: 4px;
      border-radius: 10px;
      border: 2px solid var(--color-primary);
      box-sizing: content-box;
      background: var(--color-sale);
      color: #fff;
      font-size: 0.6875rem;
      font-weight: var(--weight-semibold);
      line-height: 16px;
      text-align: center;
    }
    .alerts {
      position: absolute;
      inset-block-start: 100%;
      inset-inline-end: 0;
      z-index: 30;
      display: grid;
      gap: var(--space-1);
      width: min(30rem, calc(100vw - 2 * var(--gutter)));
      max-height: min(36rem, calc(100vh - 6rem));
      overflow-y: auto;
      padding: var(--space-2);
      background: var(--color-surface);
      color: var(--color-text);
      border: 1px solid var(--color-border-strong);
      border-top: 3px solid var(--color-warning);
      box-shadow: 0 12px 32px rgba(0, 0, 0, 0.22);
      font-size: var(--text-data);
      text-align: start;
    }
    .alerts__heading {
      font-size: var(--text-h3);
      font-weight: var(--weight-semibold);
    }
    .alerts__hint {
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .alerts__list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .alerts__item {
      display: grid;
      gap: var(--space-0-5);
      justify-items: start;
      padding: var(--space-2);
      background: var(--color-surface);
      border: 1px solid var(--color-border-strong);
      min-width: 0;
    }
    .alerts__item.is-critical {
      border-color: var(--color-sale);
    }
    .alerts__severity {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .alerts__title {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .alerts__advice,
    .alerts__done {
      color: var(--color-text-muted);
      overflow-wrap: anywhere;
    }
    .alerts__error {
      color: var(--color-sale);
    }
    .alerts__done:focus-visible {
      outline: 2px solid var(--color-focus, currentColor);
      outline-offset: 2px;
    }
  `,
})
export class AlertBell {
  private readonly api = inject(AdminAlertsApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);

  /**
   * The outcome lines currently in the view, so focus can be moved to one by reading the view rather than
   * by querying the whole document — the shape `bank-transfers-page.ts` uses for the same job.
   */
  private readonly outcomeMessages = viewChildren<ElementRef<HTMLElement>>('outcomeMessage');

  private readonly bellButton = viewChild<ElementRef<HTMLButtonElement>>('bellButton');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly alerts = signal<AdminAlert[]>([]);
  protected readonly moreThanShown = signal(false);

  /** Whether this user may have alerts at all: false after a 403, a 401 or any failed load, and the bell is not drawn. */
  protected readonly available = signal(false);
  protected readonly open = signal(false);

  /** Alerts this user has not yet acknowledged on this screen. The red count. */
  protected readonly waiting = computed(
    () => this.alerts().filter((alert) => !this.cleared().has(alert.id)).length,
  );
  protected readonly countText = computed(() => {
    const waiting = this.waiting();
    const shown = waiting > 99 ? '99' : String(waiting);

    return waiting > 99 || this.moreThanShown() ? `${shown}+` : shown;
  });
  protected readonly buttonName = computed(() => {
    const waiting = this.waiting();
    const more = this.moreThanShown() ? ' or more' : '';

    return waiting === 0
      ? 'Alerts: nothing needs attention'
      : `Alerts: ${waiting}${more} ${waiting === 1 && more === '' ? 'needs' : 'need'} attention`;
  });
  protected readonly cleared = signal<Map<number, Cleared>>(new Map());
  protected readonly failure = signal<Map<number, string>>(new Map());

  /** The alert whose acknowledgement is in flight, or null. One at a time. */
  protected readonly busy = signal<number | null>(null);

  private readonly said = signal('');

  /** The address of the last finished navigation, so a first or repeated one does not shut the panel. */
  private lastUrl: string | null = null;

  protected readonly announcement = this.said.asReadonly();

  constructor() {
    this.load();

    // Re-read on every navigation, which is what clears the alerts this user acknowledged on the last
    // screen. **There is no poller**, and that is a decision rather than an omission: an admin panel that
    // polls every thirty seconds is a change to how often every staff browser talks to the API, and doc 02
    // does not ask for one. A dead email is acted on in the morning routine (doc 11 §6.1), and every
    // navigation is a refresh.
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => {
        // A new screen starts with the panel shut. It also re-reads the list, which clears what was acknowledged.
        //
        // **Only when the address changed.** The first NavigationEnd arrives when the page has finished loading,
        // which can be after the bell is already drawn and pressed (a slow screen chunk); shutting the panel
        // then would close it under the staff member's finger. Found by the browser suite, which pressed the bell
        // in that window about one run in three.
        const url = event.urlAfterRedirects;
        if (this.lastUrl !== null && this.lastUrl !== url) {
          this.open.set(false);
        }

        this.lastUrl = url;
        this.load();
      });
  }

  protected toggle(): void {
    this.open.update((current) => !current);
  }

  /** A press anywhere outside the bell and its panel shuts the panel. Focus is left where the press put it. */
  @HostListener('document:click', ['$event'])
  protected closeOnOutsideClick(event: Event): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) {
      this.open.set(false);
    }
  }

  /**
   * Escape shuts the panel and puts focus back on the bell, so a keyboard user is not left on a node that vanished.
   * Heard on the whole document, not only inside the panel: a press on the panel's own text sends focus to the page,
   * and Escape would then have done nothing (tester, #256).
   */
  @HostListener('document:keydown.escape')
  protected closeOnEscape(): void {
    if (!this.open()) {
      return;
    }

    this.open.set(false);
    this.bellButton()?.nativeElement.focus();
  }

  /**
   * Tabbing out of the panel shuts it, so focus never lands on a field hidden underneath it (WCAG 2.2 SC 2.4.11).
   * Only when focus goes to something else that can take it: a press on plain text sends focus nowhere
   * (`relatedTarget` null), and that case is the outside-press rule's.
   */
  @HostListener('focusout', ['$event'])
  protected closeWhenFocusLeaves(event: FocusEvent): void {
    const next = event.relatedTarget;

    if (this.open() && next instanceof Node && !this.host.nativeElement.contains(next)) {
      this.open.set(false);
    }
  }

  protected heading(): string {
    const shown = this.waiting();

    if (shown === 0) {
      return this.alerts().length === 0 ? 'No alerts' : 'Everything here is acknowledged';
    }

    const count = shown === 1 ? '1 alert needs attention' : `${shown} alerts need attention`;

    // "And more are waiting" rather than a number: the API cannot give an exact total without a second
    // statement that scans the table (see `AdminAlertBanner.moreThanShown`). It still says the one thing
    // doc 11 §6.1 step 6 needs it to — that there is more to open after these.
    return this.moreThanShown() ? `${count} · more are waiting after these` : count;
  }

  protected severityOf(alert: AdminAlert): string {
    return severityLabel(alert.severity);
  }

  protected adviceFor(alert: AdminAlert): string {
    return alertAdvice(alert);
  }

  protected outcomeText(outcome: Cleared): string {
    const at = colomboTime(outcome.atUtc);

    return outcome.byUs
      ? `Acknowledged at ${at}. The problem itself is not resolved.`
      : `Somebody else acknowledged this at ${at}. The problem itself is not resolved.`;
  }

  /**
   * Records that this user has seen the alert.
   *
   * **Rule 2: the refusal is here and not in a `disabled` attribute.** A second press while the first is
   * in flight does nothing, and the button stays focusable throughout.
   */
  protected acknowledge(alert: AdminAlert): void {
    if (this.busy() !== null || this.cleared().has(alert.id)) {
      return;
    }

    this.busy.set(alert.id);
    this.clearFailure(alert.id);

    this.api
      .acknowledge(alert.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (reply) => {
          this.busy.set(null);
          this.cleared.update((map) =>
            new Map(map).set(alert.id, {
              atUtc: reply.acknowledgedAtUtc,
              byUs: !reply.alreadyAcknowledged,
            }),
          );

          this.announce(
            reply.alreadyAcknowledged
              ? `Somebody else had already acknowledged this alert. It is still not resolved: ${alert.title}`
              : `Acknowledged. This records that you have seen it and does not resolve it: ${alert.title}`,
          );

          this.focusOutcome(alert.id);
        },
        error: (error: unknown) => {
          this.busy.set(null);

          const message =
            error instanceof HttpErrorResponse && error.status === 404
              ? 'That alert is no longer there. Reload the page.'
              : 'That could not be recorded. Try again.';

          this.failure.update((map) => new Map(map).set(alert.id, message));

          // Announced as well as shown: the message is in a `role="alert"` node inside the item, which is
          // enough on its own, and the live region carries it too because a staff member who pressed the
          // button is listening to the region they were last told something in.
          this.announce(message);
        },
      });
  }

  /**
   * Loads the banner, and renders nothing for every reason it might not be this user's.
   *
   * A 401 is the sign-in page; a 403 is a staff member R10 gives no path to (doc 08 §3.8); anything else
   * is the API being unreachable, which the dashboard's own status panel is the place to report. In all
   * three the banner empties rather than explaining itself on every screen.
   */
  private load(): void {
    if (this.onSignInPage()) {
      // No session yet, so the call could only ever be a 401. Skipped rather than made and swallowed.
      //
      // **And whatever the last session left is cleared.** Signing out is a navigation inside the page, not a reload, so
      // the previous user's count and panel (alert titles, order numbers, links) stayed on the sign-in screen.
      this.available.set(false);
      this.open.set(false);
      this.alerts.set([]);
      this.moreThanShown.set(false);
      this.cleared.set(new Map());
      this.failure.set(new Map());
      return;
    }

    this.api
      .open()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (banner) => {
          this.available.set(true);
          this.alerts.set(banner.alerts);
          this.moreThanShown.set(banner.moreThanShown);
          this.cleared.set(new Map());
          this.failure.set(new Map());
        },
        error: () => {
          this.available.set(false);
          this.open.set(false);
          this.alerts.set([]);
          this.moreThanShown.set(false);
        },
      });
  }

  /**
   * Puts a sentence in the live region, **re-firing an identical one**.
   *
   * `role="status"` announces a change, and setting a region to the text it already holds is not a change
   * — so a Super Admin clearing two alerts whose titles happen to match would hear it once (ADM-22). A
   * trailing space is invisible and makes it a change; the same mechanism as `bank-transfers-page.ts` and
   * `WishlistStore.announce`. **Here it is the guarantee and not a backstop**: unlike the bank queue there
   * is no "…ing" sentence on the way in to alternate with, so two identical outcomes really are two
   * identical writes.
   */
  private announce(sentence: string): void {
    this.said.update((previous) => (previous === sentence ? `${sentence} ` : sentence));
  }

  /**
   * Moves focus to the outcome line, which has replaced the button the staff member just pressed.
   *
   * **`afterNextRender`, and `queueMicrotask` is the one that looks right and is not.** The signal write a
   * line earlier is what makes the outcome node exist, and the microtask queue is where change detection
   * is *scheduled* — so a microtask can run before the render and find nothing, which a browser test
   * caught on `bank-transfers-page.ts` with focus still on `<body>`.
   */
  private focusOutcome(alertId: number): void {
    const testId = `alert-done-${alertId}`;

    afterNextRender(
      () => {
        this.outcomeMessages()
          .map((message) => message.nativeElement)
          .find((element) => element.dataset['testid'] === testId)
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  /**
   * Whether this is one of the sign-in screens, where there is no session to ask about.
   *
   * **The router cannot answer this on the load that matters, and the browser can.** This component is
   * built during bootstrap, before the initial navigation resolves, so `router.url` is `'/'` the first
   * time `load()` runs — and a guard that read only the router let the sign-in page fire an
   * unauthenticated `/api/admin/alerts` on every cold load. Measured in a browser before this line
   * existed. The router is still read because it is the authority once navigation has started, and
   * `/admin/` is the base href so the document's path carries it.
   */
  private onSignInPage(): boolean {
    return (
      this.router.url.startsWith('/login') ||
      this.document.location?.pathname.includes('/login') === true
    );
  }

  private clearFailure(alertId: number): void {
    if (this.failure().has(alertId)) {
      this.failure.update((map) => {
        const next = new Map(map);
        next.delete(alertId);
        return next;
      });
    }
  }
}
