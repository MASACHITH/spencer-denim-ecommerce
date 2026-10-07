import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { AdminContentApi } from '../../core/content/admin-content-api';
import {
  AdminContactEnquiries,
  AdminContactEnquiry,
  ContactEnquiryStatus,
  ENQUIRY_STATUSES,
  SETTABLE_ENQUIRY_STATUSES,
} from '../../core/content/admin-content.model';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/** How many enquiries a page of the inbox holds. The API clamps anything larger. */
const PAGE_SIZE = 25;

/**
 * The contact-enquiry inbox (GLB-10, doc 11 P15 step 7).
 *
 * **Replying happens outside the system**, by email or telephone — doc 11 P15 says so and doc 11's
 * notifications for v1 are "none". So there is no reply box here and the API has nowhere to send one:
 * marking an enquiry **Replied** records that somebody dealt with it and who, which is all the inbox
 * can honestly claim. A reply field that stored a message nothing sends would be the promise checkout
 * was marked Major for making.
 *
 * **Opening an enquiry marks it Read**, because doc 11 P15 makes reading it the thing that moves the
 * status rather than a button. The two calls go out together; the GET does not write, because a GET
 * that changes a row is a row a link checker or a prefetch can change.
 *
 * **The message is rendered as text, never as markup.** It is a visitor's free text on an authenticated
 * screen, so binding it as HTML would be a stored-XSS surface aimed at staff — the mirror image of the
 * surface the content-page sanitiser closes.
 *
 * **The list carries only a short preview of the message**, and that is doc 11 P8 rather than a size
 * optimisation: a customer may paste bank account details into it, and a list is the payload most
 * likely to be cached, screenshotted into a support ticket or widened into an export later.
 */
@Component({
  selector: 'sda-enquiries-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Contact enquiries</h1>
      <p class="adm-panel__hint">
        Messages from the Contact page, newest first. Reply by email or telephone, then mark the message
        <strong>Replied</strong> so the rest of the team can see it is dealt with.
      </p>
      <p class="adm-panel__hint">
        @if (inbox(); as page) {
          {{ page.newCount }} unread of {{ page.total }} shown.
        }
      </p>

      <fieldset class="adm-filter">
        <legend class="adm-label">Show</legend>
        <div class="adm-actions">
          <button
            class="adm-btn"
            type="button"
            [attr.aria-pressed]="status() === null"
            (click)="filter(null)"
          >
            All
          </button>
          @for (candidate of statuses; track candidate) {
            <button
              class="adm-btn"
              type="button"
              [attr.aria-pressed]="status() === candidate"
              [attr.data-testid]="'filter-' + candidate"
              (click)="filter(candidate)"
            >
              {{ candidate }}
            </button>
          }
        </div>
      </fieldset>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
      </div>
    }

    <!-- tabindex="-1" so 'focusStatus' can land here after a row is destroyed: focus would otherwise
         fall to <body> (#109, read from the other direction). -1 keeps it out of the tab order. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="enquiry-status">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading…</p>
    } @else if (isEmpty()) {
      <p class="adm-muted" data-testid="enquiries-empty">
        No enquiries{{ status() ? ' with that status' : '' }} yet.
      </p>
    } @else {
      <ul class="adm-list" data-testid="enquiries">
        @for (item of inbox()!.items; track item.id) {
          <li class="adm-row" [attr.data-testid]="'enquiry-' + item.id">
            <div class="adm-row__head">
              <button
                class="adm-row__open"
                type="button"
                [attr.aria-expanded]="opened()?.id === item.id"
                [attr.data-testid]="'open-' + item.id"
                (click)="open(item.id)"
              >
                <span class="adm-row__who">{{ item.name }}</span>
                <span class="adm-muted">{{ item.subject || 'No subject' }}</span>
              </button>
              <span class="adm-pill" [class.adm-pill--hidden]="item.status !== 'New'">
                {{ item.status }}
              </span>
            </div>
            <p class="adm-muted adm-row__preview">{{ item.preview }}</p>
            @if (item.orderNumber) {
              <p class="adm-muted">Order {{ item.orderNumber }}</p>
            }
            @if (item.handledByUserEmail) {
              <p class="adm-muted">{{ item.status }} by {{ item.handledByUserEmail }}</p>
            }

            @if (opened(); as enquiry) {
              @if (enquiry.id === item.id) {
                <div class="adm-detail" [attr.id]="'enquiry-detail-' + item.id" tabindex="-1">
                  <dl class="adm-detail__facts">
                    <dt>Email</dt>
                    <dd><a [href]="'mailto:' + enquiry.email">{{ enquiry.email }}</a></dd>
                    @if (enquiry.phone) {
                      <dt>Telephone</dt>
                      <dd><a [href]="'tel:' + enquiry.phone">{{ enquiry.phone }}</a></dd>
                    }
                    <dt>Arrived</dt>
                    <dd>{{ enquiry.createdAtUtc }}</dd>
                    <dt>Sent from</dt>
                    <dd>
                      <!-- Null means either that the server could not see an address or that the
                           ninety-day retention step has cleared it (doc 08 §8.4). The screen cannot
                           tell the two apart, so it says so rather than drawing a blank. -->
                      {{ enquiry.ipAddress ?? 'not recorded, or cleared after 90 days' }}
                    </dd>
                  </dl>

                  <!-- Interpolated, never bound as HTML. See the component's own documentation. -->
                  <p class="adm-detail__message" data-testid="enquiry-message">{{ enquiry.message }}</p>

                  <div class="adm-actions">
                    @for (candidate of settable; track candidate) {
                      @if (candidate !== enquiry.status) {
                        <button
                          class="adm-btn"
                          type="button"
                          [attr.aria-disabled]="isBusy() ? 'true' : null"
                          [attr.data-testid]="'set-' + candidate + '-' + enquiry.id"
                          (click)="setStatus(enquiry, candidate)"
                        >
                          Mark {{ candidate }}
                        </button>
                      }
                    }
                  </div>

                  <details class="adm-delete">
                    <summary>Delete this message for good</summary>
                    <p class="adm-panel__hint">
                      Use this when the message holds card or bank details — those must be removed, not
                      just hidden. It cannot be undone. The reason is recorded in the audit log; the
                      message itself is not.
                    </p>
                    <label class="adm-field">
                      <span class="adm-label">Why</span>
                      <input
                        class="adm-input"
                        type="text"
                        maxlength="500"
                        [value]="reason()"
                        [attr.data-testid]="'reason-' + enquiry.id"
                        (input)="setReason($event)"
                      />
                    </label>
                    <button
                      class="adm-btn--danger"
                      type="button"
                      [attr.aria-disabled]="isBusy() ? 'true' : null"
                      [attr.data-testid]="'delete-' + enquiry.id"
                      (click)="remove(enquiry)"
                    >
                      Delete for good
                    </button>
                  </details>
                </div>
              }
            }
          </li>
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .adm-filter {
      border: 0;
      padding: 0;
      display: grid;
      gap: var(--space-1);
    }
    .adm-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
    }
    .adm-row {
      display: grid;
      gap: var(--space-0-5);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
    }
    .adm-row__head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1);
    }
    /* The whole heading is the control, so the 44 px floor is met by the row rather than by a small
       chevron beside it — the lesson W1-03 records about controls that are large enough and buried,
       read the other way round: make the target the thing a person is aiming at. */
    .adm-row__open {
      display: grid;
      gap: 2px;
      min-height: var(--tap);
      padding: 0;
      border: 0;
      background: none;
      text-align: left;
      cursor: pointer;
      font: inherit;
      color: inherit;
    }
    .adm-row__who {
      font-weight: var(--weight-semibold);
    }
    .adm-row__preview {
      /* Two lines, then clipped. The preview is already short on the wire; this stops a message with
         no spaces in it widening the row past the panel. */
      overflow-wrap: anywhere;
    }
    .adm-detail {
      display: grid;
      gap: var(--space-2);
      padding-top: var(--space-2);
      border-top: 1px solid var(--color-border);
    }
    .adm-detail__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
    }
    .adm-detail__facts dt {
      color: var(--color-text-muted);
    }
    .adm-detail__message {
      /* A visitor's text, so it keeps its own line breaks and cannot widen the panel. */
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      padding: var(--space-2);
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }
    .adm-detail a {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      text-decoration: underline;
    }
    .adm-delete summary {
      min-height: var(--tap);
      display: flex;
      align-items: center;
      cursor: pointer;
      color: var(--color-sale);
    }
  `,
})
export class EnquiriesPage {
  private readonly api = inject(AdminContentApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly statuses = ENQUIRY_STATUSES;
  protected readonly settable = SETTABLE_ENQUIRY_STATUSES;

  protected readonly inbox = signal<AdminContactEnquiries | null>(null);
  protected readonly opened = signal<AdminContactEnquiry | null>(null);
  protected readonly status = signal<ContactEnquiryStatus | null>(null);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly reason = signal('');
  protected readonly said = signal('');

  /** Whether the inbox has anything unread, for the heading. */
  protected readonly hasUnread = computed(() => (this.inbox()?.newCount ?? 0) > 0);

  /**
   * Whether this filter has nothing in it.
   *
   * A computed rather than `inbox()?.items.length === 0` in the template: that expression is
   * `undefined` before the first answer, which reads as "not empty" and so renders the list branch over
   * a null — and `strictTemplates` says so, which is how this was caught.
   */
  protected readonly isEmpty = computed(() => (this.inbox()?.items.length ?? -1) === 0);

  constructor() {
    this.load();
  }

  protected filter(status: ContactEnquiryStatus | null): void {
    this.status.set(status);
    this.opened.set(null);
    this.isLoading.set(true);
    this.load();
  }

  protected setReason(event: Event): void {
    this.reason.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  /**
   * Opens one enquiry and marks it Read.
   *
   * The two calls are separate on purpose (see the component's own documentation) and the read is fired
   * whatever the list said the status was: the list may be a minute old, and `markRead` is a no-op for
   * anything that is not New, so the worst case is a wasted request rather than a wrong status.
   */
  protected open(submissionId: number): void {
    if (this.opened()?.id === submissionId) {
      this.opened.set(null);
      return;
    }

    this.reason.set('');
    this.said.set('Opening the message…');

    this.api.enquiry(submissionId).subscribe({
      next: (enquiry) => {
        this.opened.set(enquiry);
        this.said.set(`Message from ${enquiry.name}, ${enquiry.status.toLowerCase()}.`);
        this.focusDetail(submissionId);

        this.api.markRead(submissionId).subscribe({
          next: () => this.refresh(),

          // A failed mark-as-read is not worth interrupting the staff member for: they can read the
          // message, which is what they came for, and the badge count is the only thing that is wrong.
          //
          // **It used to hide something worse than a transient failure.** The endpoint required
          // `Customers.Edit`, which `Order Staff` does not have, so for that role every open answered
          // 403, this handler ate it, and the New badge never cleared — on the screen that role is
          // most likely to live in. The endpoint now requires `Customers.View`, the permission to read
          // the message at all, so this is back to covering only what it claims to cover.
          error: () => undefined,
        });
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  protected setStatus(enquiry: AdminContactEnquiry, status: ContactEnquiryStatus): void {
    if (this.isBusy()) {
      return;
    }

    this.begin(`Marking the message ${status}…`);

    this.api.setStatus(enquiry.id, status).subscribe({
      next: () => {
        this.said.set(`Marked ${status}.`);
        this.opened.set({ ...enquiry, status });
        this.refresh();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  /**
   * Deletes an enquiry for good (doc 08 §3.7).
   *
   * **Focus is re-homed**, because this destroys the row holding the button being pressed — the same
   * harm as `[disabled]` (#109) from the other direction. Focus goes to the live region's own sentence,
   * which is where the next thing to read is.
   */
  protected remove(enquiry: AdminContactEnquiry): void {
    if (this.isBusy()) {
      return;
    }

    const reason = this.reason().trim();
    if (reason.length < 3) {
      this.say('Say why this message is being deleted. It is recorded in the audit log.');
      return;
    }

    this.begin('Deleting the message…');

    this.api.deleteEnquiry(enquiry.id, reason).subscribe({
      next: () => {
        this.opened.set(null);
        this.reason.set('');
        this.said.set('The message has been deleted. It cannot be recovered.');
        this.refresh(() => this.focusStatus());
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  private load(then?: () => void): void {
    this.api.enquiries(this.status(), 0, PAGE_SIZE).subscribe({
      next: (page) => {
        this.inbox.set(page);
        this.isLoading.set(false);
        this.isBusy.set(false);
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.fail(error);
      },
    });
  }

  private refresh(then?: () => void): void {
    this.load(then);
  }

  private begin(message: string): void {
    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(message);
  }

  private fail(error: unknown): void {
    this.isBusy.set(false);
    const failure = toCatalogFailure(error);
    this.problem.set(failure);
    this.said.set(failure.message);
  }

  /**
   * Moves focus onto something the change just created, <b>after the view has caught up</b>.
   *
   * **Both of these used `queueMicrotask` and neither of them worked.** A signal write schedules change
   * detection as a *task*; a microtask runs before it, so `getElementById` found nothing and focus
   * stayed where it was — which for `focusDetail` means on the row button, with the panel it just
   * opened never announced, and for `focusStatus` means on a control that is about to be replaced. The
   * contact form two projects away hit the same thing and was fixed with `setTimeout(0)`; the W2-11
   * tester pointed out that this is the load-sensitive version of the same idea, because it races the
   * scheduler rather than waiting for it, and that `afterNextRender` is the API that actually means
   * "once the DOM reflects this".
   *
   * It needs the injector explicitly: these run from an event handler, which is not an injection
   * context.
   */
  private focusDetail(submissionId: number): void {
    this.focusAfterRender(() =>
      this.document.getElementById(`enquiry-detail-${submissionId}`),
    );
  }

  private focusStatus(): void {
    this.focusAfterRender(() =>
      this.document.querySelector<HTMLElement>('[data-testid="enquiry-status"]'),
    );
  }

  private focusAfterRender(find: () => HTMLElement | null): void {
    afterNextRender(() => find()?.focus(), { injector: this.injector });
  }

  /**
   * Puts a sentence in the live region, <b>and makes it speak even when it is the same sentence</b>.
   *
   * `role="status"` announces a *change*. Setting the region to the text it already holds is not one, so
   * a staff member who pressed Delete twice with a reason too short to accept was told why once and then
   * got silence — on the second press, which is the one where they are already confused. ADM-22 hit this
   * and fixed it by giving the save two genuinely different sentences ("…saving", then the result); this
   * path has no in-progress state to describe, so there is no second true sentence to use.
   *
   * So the region is emptied and the sentence put back once the view has caught up, which is a real
   * mutation of real text. **Not a counter or a zero-width character:** a screen reader reads what is
   * there, and "(2)" tells the listener nothing about their message.
   */
  private say(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
