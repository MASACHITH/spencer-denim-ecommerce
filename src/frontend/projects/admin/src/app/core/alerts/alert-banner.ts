import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
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
 * The persistent alert banner (doc 08 §3.8 N2's "dashboard banner"; doc 11 §4.3, §6.1 step 1).
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
 * renders and acknowledges on its own and takes no inputs — so the dashboard can render `<sda-alert-banner />`
 * inside a widget and the shell can drop it, or both can keep it, with no change here. What ADM-04 must not
 * do is write a second implementation: two banners would be two wordings of "this does not fix anything".
 *
 * **Four accessibility rules, each of them a defect this project has already paid for.**
 *
 * 1. **An acknowledged alert is not torn out from under the focus.** It stays on screen with its outcome
 *    and goes when the next page load re-reads the list — the rule `bank-transfers-page.ts` follows for a
 *    decided row. Removing the row would destroy the node holding the focus, which is the same harm as
 *    disabling it (issue #109).
 * 2. **Nothing is ever `disabled`.** Every button carries `aria-disabled` and the handler refuses while a
 *    write is in flight (issue #109, seven instances).
 * 3. **The confirmation is written where it survives.** The live region is the component's first child and
 *    is outside every `@if`, so it exists whether or not there are alerts — W3-05 put a refusal inside the
 *    panel its own refusal destroyed and a live region inside a block that success removed, and a blind
 *    customer pressed Cancel and heard nothing at all.
 * 4. **An identical announcement re-fires.** `role="status"` announces a *change*, so acknowledging two
 *    alerts in a row would be read out once; the second gets a trailing space (ADM-22).
 *
 * **It is silent about itself.** No alerts, no permission, no API — all three render nothing. A banner that
 * said "could not load alerts" on every admin screen for a user who is not allowed any would be a
 * permanent error message about a feature that is not theirs.
 */
@Component({
  selector: 'sda-alert-banner',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Rule 3: outside every @if, so acknowledging the last alert cannot destroy the node the
         confirmation is written into. -->
    <p class="sr-only" role="status" data-testid="alert-announcement">{{ announcement() }}</p>

    @if (alerts().length > 0) {
      <section class="alerts" aria-labelledby="alerts-heading" data-testid="alert-banner">
        <h2 class="alerts__heading" id="alerts-heading">
          {{ heading() }}
        </h2>

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

              @if (alert.orderPublicId; as orderPublicId) {
                <a
                  class="adm-link"
                  [routerLink]="['/orders', orderPublicId]"
                  [attr.data-testid]="'alert-order-' + alert.id"
                >
                  Open order {{ alert.orderNumber ?? orderPublicId }}
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
                <label class="alerts__note" [attr.for]="'alert-note-' + alert.id">
                  <span class="adm-label">Note (optional)</span>
                  <input
                    class="adm-input"
                    type="text"
                    [attr.id]="'alert-note-' + alert.id"
                    [attr.maxlength]="noteMaxLength"
                    [value]="noteFor(alert.id)"
                    (input)="noteChanged(alert.id, $event)"
                    [attr.data-testid]="'alert-note-' + alert.id"
                  />
                </label>

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
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .alerts {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      padding-inline: var(--gutter);
      background: color-mix(in srgb, var(--color-warning) 12%, var(--color-surface));
      border-bottom: 2px solid var(--color-warning);
      font-size: var(--text-data);
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
    .alerts__note {
      display: grid;
      gap: var(--space-0-5);
      width: 100%;
      max-width: 32rem;
    }
    .alerts__done:focus-visible {
      outline: 2px solid var(--color-focus, currentColor);
      outline-offset: 2px;
    }
  `,
})
export class AlertBanner {
  /** `ops.AdminAlerts.AcknowledgementNote` is `nvarchar(500)` (doc 08 §3.8). */
  protected readonly noteMaxLength = 500;

  private readonly api = inject(AdminAlertsApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  /**
   * The outcome lines currently in the view, so focus can be moved to one by reading the view rather than
   * by querying the whole document — the shape `bank-transfers-page.ts` uses for the same job.
   */
  private readonly outcomeMessages = viewChildren<ElementRef<HTMLElement>>('outcomeMessage');

  protected readonly alerts = signal<AdminAlert[]>([]);
  protected readonly totalOpen = signal(0);
  protected readonly cleared = signal<Map<number, Cleared>>(new Map());
  protected readonly failure = signal<Map<number, string>>(new Map());

  /** The alert whose acknowledgement is in flight, or null. One at a time. */
  protected readonly busy = signal<number | null>(null);

  private readonly said = signal('');

  protected readonly announcement = this.said.asReadonly();

  private readonly notes = new Map<number, string>();

  constructor() {
    this.load();

    // Re-read on every navigation, which is what clears the alerts this user acknowledged on the last
    // screen. **There is no poller**, and that is a decision rather than an omission: an admin panel that
    // polls every thirty seconds is a change to how often every staff browser talks to the API, and doc 02
    // does not ask for one. A dead email is acted on in the morning routine (doc 11 §6.1), and every
    // navigation is a refresh.
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.load());
  }

  protected heading(): string {
    const shown = this.alerts().length;
    const total = this.totalOpen();

    if (total > shown) {
      return `${total} alerts need attention · showing the ${shown} newest`;
    }

    return total === 1 ? '1 alert needs attention' : `${total} alerts need attention`;
  }

  protected severityOf(alert: AdminAlert): string {
    return severityLabel(alert.severity);
  }

  protected adviceFor(alert: AdminAlert): string {
    return alertAdvice(alert);
  }

  protected noteFor(alertId: number): string {
    return this.notes.get(alertId) ?? '';
  }

  protected noteChanged(alertId: number, event: Event): void {
    this.notes.set(alertId, (event.target as HTMLInputElement).value);
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

    const typed = (this.notes.get(alert.id) ?? '').trim();

    this.api
      .acknowledge(alert.id, typed.length === 0 ? null : typed)
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
    if (this.router.url.startsWith('/login')) {
      // No session yet, so the call could only ever be a 401. Skipped rather than made and swallowed.
      return;
    }

    this.api
      .open()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (banner) => {
          this.alerts.set(banner.alerts);
          this.totalOpen.set(banner.totalOpen);
          this.cleared.set(new Map());
          this.failure.set(new Map());
          this.notes.clear();
        },
        error: () => {
          this.alerts.set([]);
          this.totalOpen.set(0);
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
