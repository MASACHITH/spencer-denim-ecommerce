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
import { HttpClient } from '@angular/common/http';

import { SITE_CONFIG, whatsAppUrl } from '../../core/config/site-config';
import { SiteSettingsService } from '../../core/site-settings/site-settings.service';
import { contactErrorMessage, contactFieldErrors } from '../../core/content/content-page.service';
import { BRAND } from '@spencer-denim/shared';

/** The form's fields. The keys are the API's field names, so an error lands on its own input. */
interface EnquiryForm {
  name: string;
  email: string;
  phone: string;
  subject: string;
  message: string;
  orderNumber: string;
  /** The honeypot. Never shown to a person; see the template. */
  website: string;
}

const EMPTY_FORM: EnquiryForm = {
  name: '',
  email: '',
  phone: '',
  subject: '',
  message: '',
  orderNumber: '',
  website: '',
};

/** `content.ContactSubmissions.Message` is `nvarchar(4000)`; the counter and the API agree on it. */
const MESSAGE_MAX = 4000;

/**
 * The Contact page's own content (GLB-10): the enquiry form, the contact details, the opening hours,
 * the map and the WhatsApp link.
 *
 * **None of it is page body.** Doc 11 P13 step 13 puts the store's contact details, opening hours, map,
 * WhatsApp number and social links in **Settings**, which is why this component reads
 * `SiteSettingsService` rather than anything the content editor typed — and why the panel appears on the
 * page whose `systemKey` is `contact` however that page's body and slug have been edited.
 *
 * **Nothing here is hardcoded and nothing is a placeholder.** Every detail renders only when the client
 * has supplied it. ADM-22's first slice removed three placeholder social links because "there is no
 * placeholder value for this shop's Instagram that is not a lie"; an address, a telephone number and a
 * set of opening hours are the same case and sharper, because a shopper can drive to an invented
 * address. The one exception is the WhatsApp number, which keeps the documented placeholder in
 * `SITE_CONFIG` — that predates this task and is deliberate (GLB-08's button is on every page, and
 * removing it over an unfilled setting would take a scoped feature off the site).
 *
 * **No email is sent and none is promised.** Doc 11 P15's notifications are "none in v1" and there is
 * no mail transport in this build. The confirmation says the message has arrived and that the team will
 * be in touch, which is true of a staff member reading the inbox. Checkout shipped a promise of an email
 * nothing could send and it was a Major finding.
 */
@Component({
  selector: 'sd-contact-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="contact">
      <section class="contact__form-wrap" aria-labelledby="contact-form-heading">
        <h2 class="contact__heading" id="contact-form-heading">Send us a message</h2>

        @if (sent()) {
          <!-- The form is replaced rather than cleared, so nobody re-sends the same enquiry by
               pressing Enter again, and the confirmation is what receives focus. -->
          <div class="contact__done" id="contact-sent" data-testid="contact-sent" tabindex="-1">
            <p class="contact__done-title">{{ confirmation() }}</p>
            <!--
              **No reply time, and that is a correction to this file.** It read "We usually reply within
              one working day", which is a service-level commitment the client never made, on the
              component whose own docblock says nothing here is hardcoded — and nothing in this system
              sends anything, so there is no process behind the promise at all. Raised by the W2-11
              tester. WhatsApp is still offered because it is a real route to a real person; what is
              gone is the claim about how fast anybody answers.
            -->
            @if (whatsApp()) {
              <p class="t-small muted">
                If it is urgent, WhatsApp is the fastest way to reach us.
              </p>
            }
            <button class="btn btn--secondary contact__again" type="button" (click)="startAgain()">
              Send another message
            </button>
          </div>
        } @else {
          <form class="contact__form" novalidate (submit)="submit($event)">
            <div class="contact__field">
              <label class="contact__label" for="contact-name">Your name</label>
              <input
                class="contact__input"
                id="contact-name"
                name="name"
                type="text"
                autocomplete="name"
                maxlength="120"
                required
                [value]="form().name"
                [attr.aria-invalid]="errorFor('name') ? 'true' : null"
                [attr.aria-describedby]="errorFor('name') ? 'contact-name-error' : null"
                (input)="set('name', $event)"
              />
              @if (errorFor('name'); as message) {
                <p class="contact__fielderror" id="contact-name-error">{{ message }}</p>
              }
            </div>

            <div class="contact__row">
              <div class="contact__field">
                <label class="contact__label" for="contact-email">Email address</label>
                <input
                  class="contact__input"
                  id="contact-email"
                  name="email"
                  type="email"
                  inputmode="email"
                  autocomplete="email"
                  maxlength="256"
                  required
                  [value]="form().email"
                  [attr.aria-invalid]="errorFor('email') ? 'true' : null"
                  [attr.aria-describedby]="errorFor('email') ? 'contact-email-error' : null"
                  (input)="set('email', $event)"
                />
                @if (errorFor('email'); as message) {
                  <p class="contact__fielderror" id="contact-email-error">{{ message }}</p>
                }
              </div>

              <div class="contact__field">
                <label class="contact__label" for="contact-phone">
                  Telephone <span class="contact__optional">(optional)</span>
                </label>
                <input
                  class="contact__input"
                  id="contact-phone"
                  name="phone"
                  type="tel"
                  inputmode="tel"
                  autocomplete="tel"
                  maxlength="20"
                  [value]="form().phone"
                  [attr.aria-invalid]="errorFor('phone') ? 'true' : null"
                  [attr.aria-describedby]="errorFor('phone') ? 'contact-phone-error' : null"
                  (input)="set('phone', $event)"
                />
                @if (errorFor('phone'); as message) {
                  <p class="contact__fielderror" id="contact-phone-error">{{ message }}</p>
                }
              </div>
            </div>

            <div class="contact__row">
              <div class="contact__field">
                <label class="contact__label" for="contact-subject">What is it about?</label>
                <input
                  class="contact__input"
                  id="contact-subject"
                  name="subject"
                  type="text"
                  maxlength="150"
                  [value]="form().subject"
                  [attr.aria-invalid]="errorFor('subject') ? 'true' : null"
                  [attr.aria-describedby]="errorFor('subject') ? 'contact-subject-error' : null"
                  (input)="set('subject', $event)"
                />
                @if (errorFor('subject'); as message) {
                  <p class="contact__fielderror" id="contact-subject-error">{{ message }}</p>
                }
              </div>

              <div class="contact__field">
                <label class="contact__label" for="contact-order">
                  Order number <span class="contact__optional">(optional)</span>
                </label>
                <input
                  class="contact__input"
                  id="contact-order"
                  name="orderNumber"
                  type="text"
                  maxlength="20"
                  autocapitalize="characters"
                  [value]="form().orderNumber"
                  [attr.aria-describedby]="'contact-order-hint'"
                  (input)="set('orderNumber', $event)"
                />
                <p class="contact__hint t-small muted" id="contact-order-hint">
                  If your message is about an order, this helps us find it straight away.
                </p>
              </div>
            </div>

            <div class="contact__field">
              <label class="contact__label" for="contact-message">How can we help?</label>
              <textarea
                class="contact__input contact__textarea"
                id="contact-message"
                name="message"
                rows="6"
                [attr.maxlength]="MESSAGE_MAX"
                required
                [value]="form().message"
                [attr.aria-invalid]="errorFor('message') ? 'true' : null"
                [attr.aria-describedby]="
                  errorFor('message') ? 'contact-message-error contact-message-count' : 'contact-message-count'
                "
                (input)="set('message', $event)"
              ></textarea>
              <p class="contact__hint t-small muted" id="contact-message-count">
                {{ remaining() }} characters left. Please do not send card or bank details here.
              </p>
              @if (errorFor('message'); as message) {
                <p class="contact__fielderror" id="contact-message-error">{{ message }}</p>
              }
            </div>

            <!--
              The honeypot (doc 11 §7 G-06). It is not 'type="hidden"', because a script reads the type
              as readily as the name: it is a real text input moved out of the viewport and taken out of
              the tab order and the accessibility tree, so a person never meets it and a form-filler
              does. 'autocomplete="off"' matters here more than anywhere else on the form — a browser
              that helpfully filled it would make a genuine visitor look like a bot.
            -->
            <div class="contact__honeypot" aria-hidden="true">
              <label for="contact-website">Website</label>
              <input
                id="contact-website"
                name="website"
                type="text"
                tabindex="-1"
                autocomplete="off"
                [value]="form().website"
                (input)="set('website', $event)"
              />
            </div>

            <div class="contact__actions">
              <!--
                'aria-disabled' and a guard in the handler, never '[disabled]' (#109). Disabling the
                control that has focus moves focus to <body>, which is six recorded instances in this
                project. Playwright's 'toBeDisabled()' honours 'aria-disabled'.
              -->
              <button
                class="btn btn--primary contact__submit"
                type="submit"
                data-testid="contact-submit"
                [attr.aria-disabled]="busy() ? 'true' : null"
              >
                {{ busy() ? 'Sending…' : 'Send message' }}
              </button>

              @if (whatsApp(); as href) {
                <a class="btn btn--secondary contact__whatsapp" [href]="href" rel="noopener">
                  Or message us on WhatsApp
                </a>
              }
            </div>

            <!-- Always mounted, so a screen reader is watching before there is anything to say. The
                 handler sets "Sending…" on the way in and the outcome on the way out, so a second
                 identical failure still speaks twice — a live region announces a *change*, and setting
                 it to the text it already holds is not one (ADM-22). -->
            <p class="contact__status" role="alert" data-testid="contact-status">{{ said() }}</p>
          </form>
        }
      </section>

      @if (contact().hasAny || whatsApp()) {
        <aside class="contact__details" aria-labelledby="contact-details-heading">
          <h2 class="contact__heading" id="contact-details-heading">Find us</h2>

          @if (contact().addressLines.length > 0) {
            <address class="contact__address">
              @for (line of contact().addressLines; track line) {
                <span class="contact__address-line">{{ line }}</span>
              }
            </address>
          }

          <ul class="contact__list">
            @if (contact().phoneHref; as href) {
              <li>
                <a class="contact__link" [href]="'tel:' + href">{{ contact().phone }}</a>
              </li>
            }
            @if (contact().email; as email) {
              <li>
                <a class="contact__link" [href]="'mailto:' + email">{{ email }}</a>
              </li>
            }
            @if (whatsApp(); as href) {
              <li>
                <a class="contact__link" [href]="href" rel="noopener">WhatsApp</a>
              </li>
            }
          </ul>

          @if (contact().openingHours.length > 0) {
            <div class="contact__hours">
              <h3 class="contact__subheading label">Opening hours</h3>
              <ul class="contact__list">
                @for (line of contact().openingHours; track line) {
                  <li>{{ line }}</li>
                }
              </ul>
            </div>
          }

          @if (contact().mapEmbedUrl; as map) {
            <!--
              'sandbox="allow-scripts"', and the one token is the whole point.

              **This said 'sandbox=""' and the map could not render.** An empty sandbox applies *every*
              restriction, scripts included, and both providers the host allow-list permits — Google
              Maps and OpenStreetMap — draw their tiles with JavaScript. So a visitor got a blank 20rem
              bordered box, on a feature named in GLB-10's own scope line, and an e2e test asserted the
              empty value and locked it in. Measured by the W2-11 tester against the live endpoints:
              with 'sandbox=""', 0 images and 0 tiles from either provider and no body text at all; with
              'allow-scripts', 36 images and 7 tiles and "Map data ©2026" from Google, 1 tile and
              "© OpenStreetMap contributors" from OSM.

              'allow-scripts' alone, and nothing else. Without 'allow-same-origin' the frame runs in an
              opaque origin, so the provider's script reaches neither this page nor its own cookies and
              storage; without 'allow-top-navigation' it cannot move the shop's address bar; without
              'allow-forms' and 'allow-popups' it can neither submit nor open anything. That leaves a
              map that draws itself and can do nothing else, which is what a map embed needs.

              'referrerpolicy' so the shop's own addresses are not handed to the map provider on every
              page view, and 'loading="lazy"' so a page on a phone does not pay for a map nobody
              scrolled to. The 'src' has been through the host allow-list three times by this point — on
              write, on read, and in 'toMapEmbedUrl' here — which is what makes 'allow-scripts' safe to
              grant: the script that runs is Google's or OpenStreetMap's, not an editor's.
            -->
            <iframe
              class="contact__map"
              [src]="map"
              title="Map of the shop's location"
              sandbox="allow-scripts"
              referrerpolicy="no-referrer"
              loading="lazy"
            ></iframe>
          }
        </aside>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      padding-top: var(--space-6);
    }
    .contact {
      display: grid;
      gap: var(--space-6);
    }
    @media (min-width: 1024px) {
      /* The form first and wider, because it is what most visitors came for. The details column is
         narrower and does not stretch, so a map in it stays a reasonable size on a 1920 px screen. */
      .contact {
        grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr);
        gap: var(--space-8);
        align-items: start;
      }
    }
    .contact__heading {
      font-size: var(--text-h3);
      padding-bottom: var(--space-2);
    }
    .contact__subheading {
      padding-bottom: var(--space-1);
    }
    .contact__form {
      display: grid;
      gap: var(--space-3);
    }
    .contact__row {
      display: grid;
      gap: var(--space-3);
    }
    @media (min-width: 768px) {
      .contact__row {
        grid-template-columns: 1fr 1fr;
      }
    }
    .contact__field {
      display: grid;
      gap: var(--space-0-5);
      /* 'minmax(0, …)' on the parent is not enough on its own: a grid item's default 'min-width: auto'
         lets a long unbroken value in an input push the column wider than the page. The checkout form
         records the same fix for the same reason (GLB-09: no horizontal scroll). */
      min-width: 0;
      /* 'align-content: start', because a grid container's default is 'stretch' and it stretches the
         implicit rows to fill whatever height the cell has. From 768px up, "What is it about?" shares a
         row with "Order number", which carries a two-line hint underneath it — so the subject field's
         cell was as tall as its neighbour and the *input* absorbed the difference: measured at 71-103px
         against 48 for every sibling. Nothing looked broken enough to notice, and the tap-target scan
         could not see it, because a control being too tall is not a question that scan asks. Raised by
         the W2-11 tester. */
      align-content: start;
    }
    .contact__label {
      font-weight: 600;
      font-size: var(--text-small);
    }
    .contact__optional {
      font-weight: 400;
      color: var(--color-text-muted);
    }
    .contact__input {
      width: 100%;
      min-height: 48px;
      padding: var(--space-1) var(--space-1-5);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      background: var(--color-surface);
      color: var(--color-text);
      font: inherit;
    }
    .contact__textarea {
      min-height: 10rem;
      resize: vertical;
      line-height: 1.6;
    }
    .contact__input[aria-invalid='true'] {
      border-color: var(--color-sale);
    }
    .contact__fielderror {
      color: var(--color-sale);
      font-size: var(--text-small);
    }
    .contact__hint {
      margin: 0;
    }
    /*
      Taken out of the viewport rather than hidden with 'display: none', because some form-fillers skip
      a field they can see is not rendered. 'aria-hidden' on the wrapper and 'tabindex="-1"' on the
      input keep it away from a screen reader and the keyboard, so no person can reach it — which is the
      property that makes a filled value mean "not a person".
    */
    .contact__honeypot {
      position: absolute;
      left: -9999px;
      width: 1px;
      height: 1px;
      overflow: hidden;
    }
    /*
      Left-aligned and 'fit-content', which is a deliberate choice about the bottom-right corner rather
      than a style preference. 'docs/04' §4 (owner, 2026-10-06) gives the floating WhatsApp button the
      bottom-right corner site-wide, and a full-width submit button would run under it at some scroll
      offsets. A left-aligned button of its own width never enters that column at any of the eight
      project widths, so there is nothing for the float to cover. 'contact.e2e.ts' checks it with
      'document.elementFromPoint' across scroll offsets rather than trusting this comment.
    */
    .contact__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      padding-top: var(--space-1);
    }
    .contact__submit,
    .contact__whatsapp,
    .contact__again {
      width: fit-content;
      min-width: 11rem;
      min-height: var(--tap);
    }
    .contact__submit[aria-disabled='true'] {
      opacity: 0.6;
      cursor: progress;
    }
    .contact__status:empty {
      display: none;
    }
    .contact__status {
      color: var(--color-sale);
      font-size: var(--text-small);
    }
    .contact__done {
      display: grid;
      justify-items: start;
      gap: var(--space-1-5);
      border: 1px solid var(--color-border);
      padding: var(--space-3);
      background: var(--color-bg-alt);
    }
    .contact__done-title {
      font-weight: 600;
    }
    .contact__details {
      display: grid;
      gap: var(--space-3);
      align-content: start;
    }
    .contact__address {
      display: grid;
      font-style: normal;
    }
    .contact__list {
      display: grid;
      gap: var(--space-0-5);
      list-style: none;
    }
    /* A standalone link, so the 44 px floor applies — the lesson the 404 page's own link records. */
    .contact__link {
      display: inline-flex;
      align-items: center;
      width: fit-content;
      min-height: var(--tap);
      color: var(--color-primary);
      text-decoration: underline;
    }
    .contact__map {
      width: 100%;
      /* A height rather than an aspect ratio: an embed is a fixed-size map rather than an image, and a
         ratio on a 1920 px column gives a map two screens tall. */
      height: 20rem;
      border: 1px solid var(--color-border);
      /* Reserves the box before the frame loads, which is what keeps a lazily-loaded map out of the
         page's CLS budget (docs/06 §4: CLS < 0.1). */
      display: block;
    }
  `,
})
export class ContactPanel {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SITE_CONFIG);
  private readonly siteSettings = inject(SiteSettingsService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly MESSAGE_MAX = MESSAGE_MAX;

  protected readonly form = signal<EnquiryForm>({ ...EMPTY_FORM });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly said = signal('');
  protected readonly confirmation = signal('');
  protected readonly fieldErrors = signal<Readonly<Record<string, string>>>({});

  /** The contact details the client has supplied — nothing invented. */
  protected readonly contact = this.siteSettings.contact;

  /** The `wa.me` link (GLB-08), from the setting when it is filled in and the placeholder otherwise. */
  protected readonly whatsApp = computed(() =>
    whatsAppUrl(this.config, this.siteSettings.whatsAppNumber()),
  );

  protected readonly remaining = computed(() =>
    Math.max(0, MESSAGE_MAX - this.form().message.length),
  );

  protected errorFor(field: keyof EnquiryForm): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  protected set(field: keyof EnquiryForm, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement | null)?.value ?? '';

    this.form.update((current) => ({ ...current, [field]: value }));

    // The error for the field being corrected goes away as it is corrected, rather than surviving until
    // the next submit — a message that contradicts what the visitor is looking at is noise.
    if (this.fieldErrors()[field]) {
      this.fieldErrors.update((errors) => {
        const next = { ...errors };
        delete next[field];
        return next;
      });
    }
  }

  /**
   * Sends the enquiry.
   *
   * **The guard is here rather than on the button's `disabled` attribute** (#109): disabling the control
   * that has focus moves focus to `<body>`, which is six recorded instances in this project. The button
   * carries `aria-disabled` so assistive technology and Playwright both see the state, and this refuses
   * the activation.
   */
  protected submit(event: Event): void {
    event.preventDefault();

    if (this.busy()) {
      return;
    }

    const values = this.form();

    this.busy.set(true);
    this.fieldErrors.set({});

    // "Sending…" first, so the outcome is always a *change* to the live region and a second identical
    // failure still speaks (ADM-22).
    this.said.set('Sending…');

    this.http
      .post<{ message: string }>('/api/content/contact', {
        name: values.name,
        email: values.email,
        phone: values.phone,
        subject: values.subject,
        message: values.message,
        orderNumber: values.orderNumber,
        website: values.website,
      })
      .subscribe({
        next: (response) => {
          this.busy.set(false);
          this.fieldErrors.set({});

          // The API's own sentence, which deliberately promises no email. If it ever arrives empty, the
          // fallback says the same thing rather than nothing.
          this.confirmation.set(
            response?.message?.trim() ||
              `Thank you — your message has reached ${BRAND.name}. Our team will be in touch.`,
          );

          // The status region is cleared as the form is replaced: the confirmation is a heading-level
          // statement in the page now, and leaving an error-coloured duplicate underneath it would be
          // read twice.
          this.said.set('');
          this.sent.set(true);

          // **Re-homing focus, and it is necessary rather than polite.** The submit button the visitor
          // has just pressed is inside the block this replaces, so destroying it would drop focus to
          // <body> — the same harm as `[disabled]` (#109), arrived at from the other direction, and the
          // rule that the project records as "necessary and not sufficient". The confirmation carries
          // `tabindex="-1"` so it can receive focus without becoming a tab stop, and a screen reader
          // then reads the sentence that replaced the form rather than silence.
          this.focusConfirmation();
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.fieldErrors.set(contactFieldErrors(error));
          this.said.set(contactErrorMessage(error));
        },
      });
  }

  /**
   * Back to an empty form after a successful send.
   *
   * **It re-homes focus, which the rule about destroying the focused node makes necessary.** "Send
   * another message" is the control being pressed and it is inside the block this removes, so focus
   * would land on `<body>` — the same harm as `[disabled]`, from the other direction. The name field is
   * both the first thing the visitor needs and a sensible place to be.
   */
  protected startAgain(): void {
    this.form.set({ ...EMPTY_FORM });
    this.fieldErrors.set({});
    this.said.set('');
    this.sent.set(false);

    // After the next render, because the field does not exist until the form is back.
    this.focusAfterRender('contact-name');
  }

  private focusConfirmation(): void {
    this.focusAfterRender('contact-sent');
  }

  /**
   * Moves focus to an element that the change just made, after the view has caught up.
   *
   * Through `DOCUMENT` rather than the global, because this component renders on the server too and
   * there is no global `document` there.
   *
   * **Not `queueMicrotask`, which is what the first version used and what did not work.** A signal
   * write schedules change detection as a task, not a microtask, so a microtask runs *before* the view
   * has been re-rendered and the element it is looking for does not exist yet. Focus then stayed on
   * `<body>` — the exact harm the re-homing exists to prevent, arrived at by trying to prevent it.
   * Measured by `content-pages.e2e.ts`, which reads `document.activeElement` after the form is
   * replaced; a jsdom test would not have caught it, because `fixture.detectChanges()` is synchronous
   * there.
   *
   * **And not `setTimeout(0)` either, which is what replaced it.** That waits for *a* task rather than
   * for *this* render, so on a slow phone or under a loaded CPU the callback can still win the race and
   * the bug comes back intermittently — which is the worst form for it to take, because it passes in
   * every test run that matters. `afterNextRender` is the API that means what this needs: run once the
   * DOM reflects the change. Raised by the W2-11 tester.
   *
   * The injector is passed explicitly because this runs from a form submission, which is not an
   * injection context.
   */
  private focusAfterRender(elementId: string): void {
    afterNextRender(() => this.document.getElementById(elementId)?.focus(), {
      injector: this.injector,
    });
  }
}
