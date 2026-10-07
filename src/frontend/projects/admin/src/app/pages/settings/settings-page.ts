import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BRAND } from '@spencer-denim/shared';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminLogo,
  AdminSettings,
  BankDetailValues,
  ContactAndSocialValues,
  SETTING_GROUPS,
  SETTING_KEYS,
} from '../../core/settings/admin-settings.model';

/** The largest logo file this screen will send. The media library's own limit is higher. */
const MAX_LOGO_BYTES = 2 * 1024 * 1024;

/** What the media library takes (checked there by file signature, not by name). */
const LOGO_ACCEPT = 'image/png,image/jpeg,image/webp';

/**
 * ADM-22 and GLB-02: the shop's logo, the WhatsApp number and the social profile addresses.
 *
 * **Why this screen exists now.** The client's delivered logo files are JPEGs with a mockup background, so
 * they cannot go in the header, and nobody has sent the social profile addresses. Both had been blocking for
 * days. The owner's decision was to stop waiting: the client uploads the logo themselves and replaces it
 * whenever they like, and the same screen takes the addresses when they arrive.
 *
 * **Two forms, two saves, deliberately.** Changing the logo changes every page of the shop instantly, so it
 * is its own act with its own audit row — never a side effect of saving a form whose hidden field was stale.
 * It is the same argument `SetCategoryActive` makes for not being a checkbox on the category form.
 *
 * **The shop's *name* is not on this screen and must not be added to it.** It is a compile-time constant in
 * one place per application (`BRAND` here, `StoreBrand` in the back end), by the owner's decision of
 * 2026-10-02 (docs/02 §I2, W1-15), with a convention test that fails if it is ever written out again. ADM-22
 * says "store info" and a name box is the obvious way to finish that — the logo is an image a client
 * uploads, the name is part of the build, and a settings row would give it two sources of truth, one of which
 * the server renders the storefront from before any database is touched.
 *
 * The API is the security boundary (doc 03 §3a). A user without `Settings.Edit` reaches this page — the route
 * only requires a session, like the rest of the admin shell — and is shown the refusal rather than an empty
 * screen.
 */
@Component({
  selector: 'sda-settings-page',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="settings">
      <h1 class="settings__title">Settings</h1>
      <p class="sr-only" role="status" data-testid="announcement">{{ announcement() }}</p>

      @if (failure(); as problem) {
        <div class="adm-state adm-state--error" role="alert" data-testid="page-error">
          <p>{{ problem.message }}</p>
          @if (problem.kind !== 'forbidden') {
            <button class="adm-btn" type="button" (click)="load()">Try again</button>
          }
        </div>
      }

      @if (loading()) {
        <p class="adm-state">Loading settings…</p>
      } @else if (settings(); as loaded) {
        @if (canEditStore()) {
          <section class="adm-panel" aria-labelledby="branding-title">
            <h2 class="adm-panel__title" id="branding-title">Logo</h2>
            <p class="adm-panel__hint">
              Shown in the header on every page. With no logo the header shows the shop's name as text,
              which is a perfectly good header — so take your time over the artwork. PNG or WebP with a
              transparent background works best; a JPEG will show its background colour as a rectangle.
            </p>

            <div class="settings__logo">
              @if (loaded.logo; as mark) {
                <div class="settings__preview" data-testid="logo-preview">
                  @if (mark.url) {
                    <img class="settings__preview-img" [src]="mark.url" [alt]="mark.altText ?? ''" />
                  }
                  <p class="adm-panel__hint">
                    {{ mark.originalFileName }}
                    @if (mark.width && mark.height) {
                      · {{ mark.width }}×{{ mark.height }}
                    }
                  </p>
                  @if (!mark.altText) {
                    <p class="adm-field-error" data-testid="logo-no-alt">
                      This image has no alternative text, so a screen reader cannot read the header. Upload
                      it again with a description.
                    </p>
                  }
                </div>
              } @else {
                <p class="adm-state" data-testid="logo-empty">
                  No logo yet. The header is showing “{{ brand }}” as text.
                </p>
              }

              <div class="adm-fields">
                <label class="adm-field adm-field--wide">
                  <span class="adm-label">Describe the logo (alternative text)</span>
                  <input
                    class="adm-input"
                    type="text"
                    name="logoAltText"
                    maxlength="200"
                    [ngModel]="logoAltText()"
                    (ngModelChange)="logoAltText.set($event)"
                    data-testid="logo-alt-input"
                  />
                  <span class="adm-panel__hint">
                    What someone who cannot see the image should hear in its place. “{{ brand }}” is
                    usually right.
                  </span>
                </label>

                <label class="adm-field adm-field--wide">
                  <span class="adm-label">{{ loaded.logo ? 'Replace the logo' : 'Upload a logo' }}</span>
                  <input
                    class="adm-input"
                    type="file"
                    name="logoFile"
                    [accept]="accept"
                    [attr.aria-disabled]="busy() ? 'true' : null"
                    (change)="onLogoChosen($event)"
                    data-testid="logo-file"
                    #logoFile
                  />
                  @if (logoError(); as message) {
                    <span class="adm-field-error" data-testid="logo-error">{{ message }}</span>
                  }
                </label>
              </div>

              @if (loaded.logo) {
                <div class="adm-actions">
                  <!-- Also aria-disabled, for the reason the Save button gives. This one differs in what
                       happens *after*: a successful removal takes loaded.logo away, so this button is
                       gone from the screen by design and focus cannot stay on it. removeLogo therefore
                       hands focus to the upload box, which is the control that replaces it in the same
                       panel and the obvious next step. -->
                  <button
                    class="adm-btn--danger"
                    type="button"
                    [attr.aria-disabled]="busy() ? 'true' : null"
                    (click)="removeLogo()"
                    data-testid="logo-remove"
                  >
                    {{ busy() ? 'Working…' : 'Remove the logo' }}
                  </button>
                </div>
                <p class="adm-panel__hint">
                  Removing it puts the shop's name back in the header and releases the image, so you can
                  then delete it from the media library if you want to.
                </p>
              }
            </div>
          </section>
        }

        @if (canEditContact()) {
          <section class="adm-panel" aria-labelledby="contact-title">
            <h2 class="adm-panel__title" id="contact-title">Contact and social</h2>
            <p class="adm-panel__hint">
              Leave a box empty and that icon does not appear on the shop at all. That is better than a
              link to the wrong place — a social icon that does not go to your page makes the shop look
              abandoned.
            </p>

            <form class="adm-fields" (ngSubmit)="saveContactAndSocial()" novalidate>
              @if (hasContact()) {
              <label class="adm-field">
                <span class="adm-label">WhatsApp number</span>
                <input
                  class="adm-input"
                  type="text"
                  name="whatsApp"
                  inputmode="numeric"
                  autocomplete="off"
                  placeholder="94771234567"
                  [attr.aria-invalid]="fieldError('whatsApp') ? 'true' : null"
                  [ngModel]="form().whatsApp"
                  (ngModelChange)="patch('whatsApp', $event)"
                  data-testid="field-whatsApp"
                />
                <span class="adm-panel__hint">
                  International form, digits only — country code first, no plus sign and no leading zero.
                </span>
                @if (fieldError('whatsApp'); as message) {
                  <span class="adm-field-error" data-testid="error-whatsApp">{{ message }}</span>
                }
              </label>
              @for (field of contactFields; track field.name) {
                <label class="adm-field">
                  <span class="adm-label">{{ field.label }}</span>
                  @if (field.multiline) {
                    <textarea
                      class="adm-input adm-input--multiline"
                      rows="4"
                      [name]="field.name"
                      autocomplete="off"
                      [placeholder]="field.placeholder"
                      [attr.aria-invalid]="fieldError(field.name) ? 'true' : null"
                      [ngModel]="form()[field.name]"
                      (ngModelChange)="patch(field.name, $event)"
                      [attr.data-testid]="'field-' + field.name"
                    ></textarea>
                  } @else {
                    <input
                      class="adm-input"
                      [type]="field.type"
                      [name]="field.name"
                      autocomplete="off"
                      [placeholder]="field.placeholder"
                      [attr.aria-invalid]="fieldError(field.name) ? 'true' : null"
                      [ngModel]="form()[field.name]"
                      (ngModelChange)="patch(field.name, $event)"
                      [attr.data-testid]="'field-' + field.name"
                    />
                  }
                  <span class="adm-panel__hint">{{ field.hint }}</span>
                  @if (fieldError(field.name); as message) {
                    <span class="adm-field-error" [attr.data-testid]="'error-' + field.name">
                      {{ message }}
                    </span>
                  }
                </label>
              }
              }

              <!-- Only the boxes whose group the server actually sent.
                   A box for a group this user cannot read would be a box they can type into and never save,
                   and — before the toForm fix — one whose empty value would have cleared the client's value on
                   the next save. The screen asks the payload, which is the same question the server answers. -->
              @if (hasSocial()) {
              @for (field of socialFields; track field.name) {
                <label class="adm-field">
                  <span class="adm-label">{{ field.label }}</span>
                  <input
                    class="adm-input"
                    type="url"
                    [name]="field.name"
                    autocomplete="off"
                    [placeholder]="field.placeholder"
                    [attr.aria-invalid]="fieldError(field.name) ? 'true' : null"
                    [ngModel]="form()[field.name]"
                    (ngModelChange)="patch(field.name, $event)"
                    [attr.data-testid]="'field-' + field.name"
                  />
                  @if (fieldError(field.name); as message) {
                    <span class="adm-field-error" [attr.data-testid]="'error-' + field.name">
                      {{ message }}
                    </span>
                  }
                </label>
              }
              }

              <div class="adm-actions adm-field--wide">
                <!-- aria-disabled rather than disabled, and that is the whole point of it.
                     Disabling a button that currently has focus makes the browser drop focus to <body>,
                     so the admin lost their place on the screen the moment they pressed Save — by their
                     own keystroke. aria-disabled tells assistive technology the control is unavailable
                     while leaving it focusable, and saveContactAndSocial already refuses to start a
                     second write while one is in flight, so the button is inert either way. -->
                <button
                  class="adm-btn--primary"
                  type="submit"
                  [attr.aria-disabled]="busy() ? 'true' : null"
                  data-testid="save-contact-social"
                >
                  {{ busy() ? 'Saving…' : 'Save' }}
                </button>
              </div>
            </form>
          </section>
        }

        @if (canEditBank()) {
          <section class="adm-panel" aria-labelledby="bank-title">
            <h2 class="adm-panel__title" id="bank-title">Bank transfer</h2>
            <p class="adm-panel__hint">
              Where customers send the money. Until the bank name, account name and account number are
              all filled in, a customer who chooses bank transfer is told the details are being confirmed
              — which is better than showing half of them. Check the account number twice: every customer
              who chooses bank transfer sees it.
            </p>

            <form class="adm-fields" (ngSubmit)="saveBankDetails()" novalidate>
              @for (field of bankFields; track field.name) {
                <div class="adm-field" [class.adm-field--wide]="field.wide">
                  <!-- An explicit for/id pair rather than a wrapping label. The control differs by
                       field (one of them is a textarea), and a label that wraps an @if is a label the
                       template linter cannot associate with anything — which is also a fair description
                       of what a screen reader has to work out. No backticks in this comment: the whole
                       template is a template literal and one would end it. -->
                  <label class="adm-label" [attr.for]="'bank-' + field.name">{{ field.label }}</label>
                  @if (field.multiline) {
                    <textarea
                      class="adm-input"
                      rows="3"
                      [id]="'bank-' + field.name"
                      [name]="field.name"
                      autocomplete="off"
                      [placeholder]="field.placeholder"
                      [attr.aria-invalid]="fieldError(field.name) ? 'true' : null"
                      [attr.aria-describedby]="'bank-hint-' + field.name"
                      [ngModel]="bankForm()[field.name]"
                      (ngModelChange)="patchBank(field.name, $event)"
                      [attr.data-testid]="'field-' + field.name"
                    ></textarea>
                  } @else {
                    <input
                      class="adm-input"
                      type="text"
                      [id]="'bank-' + field.name"
                      [name]="field.name"
                      autocomplete="off"
                      [placeholder]="field.placeholder"
                      [attr.aria-invalid]="fieldError(field.name) ? 'true' : null"
                      [attr.aria-describedby]="'bank-hint-' + field.name"
                      [ngModel]="bankForm()[field.name]"
                      (ngModelChange)="patchBank(field.name, $event)"
                      [attr.data-testid]="'field-' + field.name"
                    />
                  }
                  <span class="adm-panel__hint" [id]="'bank-hint-' + field.name">{{ field.hint }}</span>
                  @if (fieldError(field.name); as message) {
                    <span class="adm-field-error" [attr.data-testid]="'error-' + field.name">
                      {{ message }}
                    </span>
                  }
                </div>
              }

              <div class="adm-actions adm-field--wide">
                <!-- aria-disabled, not disabled: disabling the button under the admin's own finger throws
                     their focus to <body>. saveBankDetails refuses a second write while one is in flight,
                     so the control is inert either way. -->
                <button
                  class="adm-btn--primary"
                  type="submit"
                  [attr.aria-disabled]="busy() ? 'true' : null"
                  data-testid="save-bank"
                >
                  {{ busy() ? 'Saving…' : 'Save bank details' }}
                </button>
              </div>
            </form>
          </section>
        }

        @if (!canEditStore() && !canEditContact() && !canEditBank()) {
          <p class="adm-state" data-testid="nothing-to-edit">
            There are no settings you can change here.
          </p>
        }
      }
    </section>
  `,
  styles: `
    .settings {
      display: grid;
      gap: var(--space-3, 1.5rem);
    }
    .settings__title {
      font-size: 1.5rem;
      font-weight: 500;
    }
    .settings__logo {
      display: grid;
      gap: var(--space-2, 1rem);
    }
    /* The three write controls say aria-disabled rather than disabled, so that pressing one does not throw
       the admin's focus to the document body. That leaves the visual unavailable state to be drawn here,
       which :disabled was drawing before. Opacity only, and no pointer-events: none, because a control that
       cannot be hovered cannot show a cursor at all, and the press is already a no-op.
       (No backticks in this block: it is a template literal, and one would end it.) */
    .settings [aria-disabled='true'] {
      opacity: 0.6;
      cursor: progress;
    }
    .settings__preview {
      display: grid;
      gap: 0.5rem;
      justify-items: start;
    }
    /* A chequerboard behind the preview, so a logo with a white or transparent background is visible as
       the shape it is. The client's delivered artwork is a JPEG on a mockup background and this is where
       that becomes obvious before it reaches the storefront rather than after. */
    .settings__preview-img {
      max-width: min(100%, 320px);
      max-height: 96px;
      object-fit: contain;
      padding: 0.5rem;
      background-color: #fff;
      background-image:
        linear-gradient(45deg, #e9e9e9 25%, transparent 25%, transparent 75%, #e9e9e9 75%),
        linear-gradient(45deg, #e9e9e9 25%, transparent 25%, transparent 75%, #e9e9e9 75%);
      background-position: 0 0, 8px 8px;
      background-size: 16px 16px;
      border: 1px solid var(--adm-border, #d6d6d6);
    }
    /* The address and the opening hours are multi-line (GLB-10, W2-11). 'resize: vertical' only, so a
       staff member can grow the box without being able to drag it wider than the panel. */
    .adm-input--multiline {
      min-height: 5.5rem;
      resize: vertical;
      font: inherit;
      line-height: 1.5;
    }
  `,
})
export class SettingsPage {
  /** The shop's name, from the one place it is written down — never a field on this screen (W1-15). */
  protected readonly brand = BRAND.name;

  protected readonly accept = LOGO_ACCEPT;

  /**
   * The Contact page's own details (GLB-10, W2-11, doc 11 P13 step 13), as data for the same reason the
   * social boxes are: the template cannot then spell a field name differently from the API, which is
   * the pairing `AdminSettingsShapeTests` pins on the server side.
   *
   * **Every hint says what the shop does when the box is empty**, because that is the question a client
   * filling this screen in actually has, and the answer is always "nothing is shown" rather than
   * "a placeholder appears". Nothing on the Contact page is invented.
   */
  protected readonly contactFields = [
    {
      name: 'phone' as const,
      label: 'Telephone number',
      type: 'tel',
      multiline: false,
      placeholder: '+94 11 234 5678',
      hint: 'As you want shoppers to read it. Tapping it dials. Leave it empty to show no number.',
    },
    {
      name: 'email' as const,
      label: 'Contact email address',
      type: 'email',
      multiline: false,
      placeholder: 'hello@yourshop.lk',
      hint: 'Where shoppers write to. The shop never sends email from this address.',
    },
    {
      name: 'address' as const,
      label: 'Postal address',
      type: 'text',
      multiline: true,
      placeholder: 'Your shop\nColombo 03\nSri Lanka',
      hint: 'One line per line. Leave it empty and the Contact page shows no address at all.',
    },
    {
      name: 'openingHours' as const,
      label: 'Opening hours',
      type: 'text',
      multiline: true,
      placeholder: 'Monday to Friday, 9am – 6pm\nSaturday, 9am – 1pm',
      hint: 'One line per line. Leave it empty to show no hours.',
    },
    {
      name: 'mapEmbedUrl' as const,
      label: 'Map embed address',
      type: 'url',
      multiline: false,
      placeholder: 'https://www.google.com/maps/embed?pb=…',
      hint:
        'In Google Maps use Share → Embed a map, then copy the address out of the src=\"…\" part. ' +
        'Only Google Maps and OpenStreetMap are accepted, because the map is shown inside the shop’s ' +
        'own page.',
    },
  ];

  /** The three social boxes, as data, so the template cannot spell a field name differently from the API. */
  protected readonly socialFields = [
    {
      name: 'facebookUrl' as const,
      label: 'Facebook page',
      placeholder: 'https://www.facebook.com/yourshop',
    },
    {
      name: 'instagramUrl' as const,
      label: 'Instagram profile',
      placeholder: 'https://www.instagram.com/yourshop',
    },
    { name: 'tikTokUrl' as const, label: 'TikTok profile', placeholder: 'https://www.tiktok.com/@yourshop' },
  ];

  /**
   * The five bank boxes, as data, so the template cannot spell a field name differently from the API.
   *
   * The names match the request body's exactly, which is also what makes `fieldError` work: the API
   * returns its validation errors keyed by the field name it refused.
   *
   * **The placeholders are obviously fake, and the first version's were not.** They read "Commercial Bank
   * of Ceylon PLC", "Kollupitiya", "Spencer Denim Industries (Pvt) Ltd" and "8001 2345 6789" — a real Sri
   * Lankan bank, the client's own legal entity and a plausibly-formatted account number, rendered in the
   * production bundle on the one screen where a Super Admin types the real values, with real values in the
   * sections above and below. At a glance that reads as data rather than as a hint, and the thing it would
   * be mistaken for is where a shopper's money goes. The social fields two lists up have always said
   * `yourshop`; this is the same convention, which is why the shape is kept (the spacing in an account
   * number is the hint that matters) while the identity is not.
   */
  protected readonly bankFields = [
    {
      name: 'bankName' as const,
      label: 'Bank name',
      placeholder: 'Example Bank PLC',
      hint: 'Exactly as it appears in online banking, so a customer can find it in their payee list.',
      wide: true,
      multiline: false,
    },
    {
      name: 'branch' as const,
      label: 'Branch (optional)',
      placeholder: 'Your branch',
      hint: 'A branch deposit slip asks for it; online banking usually does not.',
      wide: false,
      multiline: false,
    },
    {
      name: 'accountName' as const,
      label: 'Account name',
      placeholder: 'Your Company (Pvt) Ltd',
      hint: "The name the account is held in. A customer's bank may check it against the number.",
      wide: true,
      multiline: false,
    },
    {
      name: 'accountNumber' as const,
      label: 'Account number',
      placeholder: '0000 0000 0000',
      hint: 'Digits, spaces and dashes. Type the spacing your bank prints — customers copy it.',
      wide: false,
      multiline: false,
    },
    {
      name: 'instructions' as const,
      label: 'Extra instructions (optional)',
      placeholder: 'Cash-machine deposits can take a working day to appear.',
      hint: 'Shown under the details on the order page. Plain text; line breaks are kept.',
      wide: true,
      multiline: true,
    },
  ];

  private readonly api = inject(AdminSettingsApi);
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('logoFile');

  private readonly loaded = signal<AdminSettings | null>(null);
  private readonly isLoading = signal(true);
  private readonly isBusy = signal(false);
  private readonly problem = signal<CatalogFailure | null>(null);
  private readonly logoProblem = signal<string | null>(null);
  private readonly values = signal<ContactAndSocialValues>(EMPTY_FORM);
  private readonly bankValues = signal<BankDetailValues>(EMPTY_BANK_FORM);
  private readonly said = signal('');

  /**
   * The fields typed into since the last write was sent, which the re-read after that write must not
   * overwrite. Cleared when a save goes out, because the save carries the whole form and the server is
   * about to agree with it.
   *
   * Deliberately not a signal: nothing renders from it, and it is only ever read inside the subscription
   * callbacks that apply a payload.
   */
  private readonly editedSinceWrite = new Set<string>();

  protected readonly settings = this.loaded.asReadonly();
  protected readonly loading = this.isLoading.asReadonly();
  protected readonly busy = this.isBusy.asReadonly();
  protected readonly failure = this.problem.asReadonly();
  protected readonly logoError = this.logoProblem.asReadonly();
  protected readonly form = this.values.asReadonly();
  protected readonly bankForm = this.bankValues.asReadonly();
  protected readonly announcement = this.said.asReadonly();

  /**
   * The alt text the next upload will carry, defaulting to the shop's name.
   *
   * Typed `string` explicitly: `BRAND` is `as const`, so inference would narrow this to that one string
   * literal and nothing the admin typed could be set into it.
   */
  protected readonly logoAltText = signal<string>(BRAND.name);

  /**
   * Whether the API returned the group at all.
   *
   * **The screen asks the payload, not the permission list.** A group this user may not read is absent
   * from the response, so "can I edit the logo" and "did the server send me the Store group" are the same
   * question — which means the screen cannot disagree with the server about who may do what. Hiding a
   * panel is still only convenience; the API refuses regardless (doc 03 §3a).
   */
  protected readonly canEditStore = computed(() => this.hasGroup(SETTING_GROUPS.store));

  protected readonly canEditContact = computed(
    () => this.hasGroup(SETTING_GROUPS.contact) || this.hasGroup(SETTING_GROUPS.social),
  );

  /** Whether the server sent the Contact group, and so whether the WhatsApp box belongs on screen. */
  protected readonly hasContact = computed(() => this.hasGroup(SETTING_GROUPS.contact));

  /** <inheritdoc cref="hasContact" /> — for the three social boxes. */
  protected readonly hasSocial = computed(() => this.hasGroup(SETTING_GROUPS.social));

  /**
   * Whether the server sent the `Bank` group, and so whether the bank panel belongs on screen.
   *
   * **The first group in this build that most admins will not see.** It needs `Settings.Payments`, which
   * only a Super Admin holds, so a Store Manager opening this screen gets the logo and the contact panels
   * and no bank panel at all — which is the read model's per-row filter working, not a bug.
   */
  protected readonly canEditBank = computed(() => this.hasGroup(SETTING_GROUPS.bank));

  constructor() {
    this.load();
  }

  /**
   * Reads the settings and puts the screen into its loading state while it waits.
   *
   * This is the *first* read, and the one behind "Try again" — the two cases where there is genuinely
   * nothing on screen worth keeping. The re-read after a write goes through `refresh()` instead.
   */
  load(): void {
    this.read({ takeScreenDown: true });
  }

  /**
   * Re-reads the settings **without taking the screen down**.
   *
   * `load()` sets `isLoading`, and the template replaces the whole panel with "Loading settings…" while it
   * is set. Using it after a write meant every save destroyed the form — which dropped the admin's keyboard
   * focus to `<body>`, so a keyboard user lost their place on the screen every time they saved, and Tab
   * started again at the top of the document.
   *
   * Leaving the form mounted keeps the control the user pressed alive across the whole operation, so focus
   * is never *moved* and never has to be restored. The cost is that the form is now on screen and typeable
   * while the re-read is in flight, which is what `editedSinceWrite` exists to make safe.
   */
  private refresh(): void {
    this.read({ takeScreenDown: false });
  }

  private read({ takeScreenDown }: { takeScreenDown: boolean }): void {
    if (takeScreenDown) {
      this.isLoading.set(true);
    }

    this.problem.set(null);

    this.api.settings().subscribe({
      next: (settings) => {
        this.applySettings(settings, { keepUnsavedEdits: !takeScreenDown });
        this.isLoading.set(false);
      },
      error: (error: unknown) => {
        this.problem.set(toCatalogFailure(error));
        this.isLoading.set(false);
      },
    });
  }

  /**
   * Puts a freshly read payload onto the screen.
   *
   * **Why `keepUnsavedEdits` is not optional politeness.** A re-read that followed a write used to land
   * while the form was off screen, so it could not overwrite anything the admin was typing. Now that the
   * form stays mounted — which is what keeps their focus — the re-read's response can arrive *after* they
   * have started on the next field, and setting the whole form from the server would silently wipe those
   * keystrokes and reset the control to pristine, giving no hint it had happened.
   *
   * Values the server normalised are still adopted, which is the point of re-reading: only the fields
   * touched since the write was sent are held back.
   */
  private applySettings(settings: AdminSettings, { keepUnsavedEdits }: { keepUnsavedEdits: boolean }): void {
    this.loaded.set(settings);

    const fromServer: Record<string, string | null> = { ...toForm(settings) };

    if (keepUnsavedEdits) {
      const current = this.values() as Record<string, string | null | undefined>;

      for (const field of this.editedSinceWrite) {
        // Only a field the server actually sent. `toForm` leaves an unreadable group's keys *absent* on
        // purpose, so that the next save omits them rather than telling the API to clear them (W1-12);
        // putting one back here would undo that.
        if (field in fromServer) {
          fromServer[field] = current[field] ?? null;
        }
      }
    } else {
      this.editedSinceWrite.clear();
    }

    this.values.set(fromServer as ContactAndSocialValues);

    const bankFromServer: Record<string, string | null> = { ...toBankForm(settings) };

    if (keepUnsavedEdits) {
      const currentBank = this.bankValues() as Record<string, string | null | undefined>;

      for (const field of this.editedSinceWrite) {
        if (field in bankFromServer) {
          bankFromServer[field] = currentBank[field] ?? null;
        }
      }
    }

    this.bankValues.set(bankFromServer as BankDetailValues);
    this.logoAltText.set(settings.logo?.altText?.trim() || BRAND.name);
  }

  protected patch<K extends keyof ContactAndSocialValues>(
    field: K,
    value: ContactAndSocialValues[K],
  ): void {
    this.editedSinceWrite.add(field);
    this.values.update((current) => ({ ...current, [field]: value }));

    // The message about this field is now about a value nobody is looking at any more.
    this.problem.update((current) =>
      current?.kind === 'validation' && current.fieldErrors?.[field] ? null : current,
    );
  }

  protected patchBank<K extends keyof BankDetailValues>(field: K, value: BankDetailValues[K]): void {
    this.editedSinceWrite.add(field);
    this.bankValues.update((current) => ({ ...current, [field]: value }));

    this.problem.update((current) =>
      current?.kind === 'validation' && current.fieldErrors?.[field] ? null : current,
    );
  }

  protected fieldError(field: string): string | null {
    const failure = this.problem();

    return failure?.kind === 'validation' ? (failure.fieldErrors?.[field]?.[0] ?? null) : null;
  }

  protected saveContactAndSocial(): void {
    const group = this.groupVersion(SETTING_GROUPS.contact) ?? this.groupVersion(SETTING_GROUPS.social);
    if (group === null || this.isBusy()) {
      return;
    }

    this.beforeWrite('Saving the contact and social settings…');

    // Every field, every save: the API replaces the group, so a field this object omitted would be a field
    // the API is told to clear (W1-12's blocker).
    this.api.updateContactAndSocial(this.values(), group).subscribe({
      next: () => this.afterWrite('Contact and social settings saved.'),
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  protected saveBankDetails(): void {
    const group = this.groupVersion(SETTING_GROUPS.bank);
    if (group === null || this.isBusy()) {
      return;
    }

    this.beforeWrite('Saving the bank details…');

    // Only the fields the server sent, which is what `toBankForm` produced. A field this object omits is a
    // field the API leaves alone (W1-12).
    this.api.updateBankDetails(this.bankValues(), group).subscribe({
      next: () =>
        this.afterWrite(
          'Bank details saved. Customers who choose bank transfer will see them on their order.',
        ),
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  protected onLogoChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];

    this.logoProblem.set(null);

    // The box carries `aria-disabled` rather than `disabled` while a write is in flight, for the reason the
    // Save button gives — a disabled control under the cursor throws focus to `<body>`. So the guard the
    // attribute used to provide has to be here instead, and the box is cleared so no filename is left on
    // screen implying a file was taken.
    if (this.isBusy()) {
      this.clearFileInput();
      return;
    }

    if (!file) {
      return;
    }

    // Checked here as well as on the server, because a 2 MB refusal after a slow upload on a Sri Lankan
    // mobile connection is a worse experience than a sentence before it starts. The server's check is the
    // one that matters — it reads the file signature, so a renamed file does not get through.
    if (file.size > MAX_LOGO_BYTES) {
      this.logoProblem.set('That file is larger than 2 MB. A logo should be well under that.');
      this.clearFileInput();
      return;
    }

    const group = this.groupVersion(SETTING_GROUPS.store);
    if (group === null) {
      return;
    }

    const alt = this.logoAltText().trim() || BRAND.name;

    this.beforeWrite('Uploading the logo…');

    // Upload first, then point the setting at it. A failed upload therefore changes nothing, and the
    // previous logo is still the live one.
    this.api.uploadLogo(file, alt).subscribe({
      next: (asset) =>
        this.api.setStoreLogo(asset.publicId, group).subscribe({
          next: () => this.afterWrite('The logo was updated. Every page shows it now.'),
          error: (error: unknown) => this.afterFailure(error),
        }),
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  protected removeLogo(): void {
    const group = this.groupVersion(SETTING_GROUPS.store);
    if (group === null || this.isBusy()) {
      return;
    }

    this.beforeWrite('Removing the logo…');

    this.api.setStoreLogo(null, group).subscribe({
      next: () => {
        this.afterWrite("The logo was removed. The header shows the shop's name as text.");

        // The only write whose own control does not survive it: with the logo gone, `loaded.logo` is null
        // and the Remove button leaves the screen, so focus has to be given somewhere deliberately. The
        // upload box is the control that takes its place in the same panel. It is safe to focus now rather
        // than after the re-read because it is never `disabled` — see `onLogoChosen`.
        this.fileInput()?.nativeElement.focus();
      },
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  /**
   * Before any write: mark the screen busy and **say that the write has started**.
   *
   * **Why the live region has to speak here and not only at the end.** `role="status"` announces a
   * *change*. Every save used to finish by setting the region to the same sentence it already held, and
   * setting a region to the text it already has is not a change — so an admin who saved twice was told
   * once and then never again, which on a screen whose whole purpose is repeated edits is most of the time.
   *
   * Saying "…saving" on the way in is the honest fix rather than a differentiator: the screen really does
   * have an in-progress state — the button has always read "Saving…" — and the region was simply the one
   * place not reflecting it. It gives assistive technology two real mutations per save with two different,
   * true sentences, instead of one sentence stamped with a counter that tells the listener nothing.
   */
  private beforeWrite(message: string): void {
    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(message);

    // The save carries the whole form, so the server is about to hold exactly what is on screen: nothing
    // typed before this moment needs holding back from the re-read that follows.
    this.editedSinceWrite.clear();
  }

  /**
   * After any successful write: say so, clear the file box, and re-read.
   *
   * **The re-read is not laziness.** Every save moves the group's row version, so the token on screen is
   * stale the moment the save succeeds — keeping it would make the admin's *next* save fail with a conflict
   * they did nothing to cause. Re-reading also brings back the logo's URL and dimensions, and any value the
   * API normalised, which only the server can produce.
   *
   * It goes through `refresh()` rather than `load()` so the form is not destroyed under the admin's focus.
   */
  private afterWrite(message: string): void {
    this.isBusy.set(false);
    this.said.set(message);
    this.clearFileInput();
    this.refresh();
  }

  /**
   * After a refused write.
   *
   * **The announcement is cleared rather than left alone.** `beforeWrite` has just put "…saving" into the
   * live region; leaving it there would have the screen quietly insisting it was still saving something it
   * has already given up on. The refusal itself is announced by the `role="alert"` message, which is where
   * a failure belongs — this only stops the status region from contradicting it.
   */
  private afterFailure(error: unknown): void {
    this.isBusy.set(false);
    this.said.set('');
    this.problem.set(toCatalogFailure(error));
    this.clearFileInput();
  }

  /**
   * Empties the file box after a write, successful or not.
   *
   * Without it the browser keeps the chosen filename on screen, so after a refused upload the screen shows
   * a file that is not the logo beside an error about it — and choosing the *same* file again fires no
   * `change` event at all, so the admin's obvious next move would do nothing.
   */
  private clearFileInput(): void {
    const input = this.fileInput()?.nativeElement;
    if (input) {
      input.value = '';
    }
  }

  private hasGroup(groupKey: string): boolean {
    return this.groupVersion(groupKey) !== null;
  }

  private groupVersion(groupKey: string): string | null {
    return this.loaded()?.groups.find((group) => group.groupKey === groupKey)?.rowVersion ?? null;
  }
}

/** Before the payload arrives, the screen is writing nothing — so it carries no fields at all. */
const EMPTY_FORM: ContactAndSocialValues = {};

/** <inheritdoc cref="EMPTY_FORM" /> */
const EMPTY_BANK_FORM: BankDetailValues = {};

/**
 * The form's values, read out of the payload by key — **only the keys the payload contained**.
 *
 * A key the server did not send is *absent* from the result, so `JSON.stringify` drops it from the request body
 * and the API leaves that setting alone. A key the server sent as `null` is present and null, which is how a
 * client clears one.
 *
 * **The first version returned `null` for an absent key and this comment claimed that was the safe choice.**
 * It was the opposite: `PUT` replaces, so four nulls tell the API to clear four keys. A caller who cannot read
 * `Social.*` — which is exactly what #53 adds — would have wiped the client's three social addresses with their
 * first save of the WhatsApp number. W1-12's Blocker, one feature over, and the comment is what the next
 * person would have trusted.
 */
export function toForm(settings: AdminSettings): ContactAndSocialValues {
  const byKey = new Map(
    settings.groups.flatMap((group) => group.settings.map((setting) => [setting.key, setting.value] as const)),
  );

  const form: Record<string, string | null> = {};

  for (const [field, key] of [
    ['whatsApp', SETTING_KEYS.whatsApp],
    ['phone', SETTING_KEYS.phone],
    ['email', SETTING_KEYS.email],
    ['address', SETTING_KEYS.address],
    ['openingHours', SETTING_KEYS.openingHours],
    ['mapEmbedUrl', SETTING_KEYS.mapEmbedUrl],
    ['facebookUrl', SETTING_KEYS.facebookUrl],
    ['instagramUrl', SETTING_KEYS.instagramUrl],
    ['tikTokUrl', SETTING_KEYS.tikTokUrl],
  ] as const) {
    if (byKey.has(key)) {
      form[field] = byKey.get(key) ?? null;
    }
  }

  return form as ContactAndSocialValues;
}

/**
 * The bank form's values, read out of the payload by key — **only the keys the payload contained**.
 *
 * Exactly {@link toForm}'s rule, and the group it matters most on: these five keys need
 * `Settings.Payments`, so an admin who cannot read them receives none of them, and a request carrying five
 * nulls would clear the shop's bank account number on their first save of anything.
 */
export function toBankForm(settings: AdminSettings): BankDetailValues {
  const byKey = new Map(
    settings.groups.flatMap((group) => group.settings.map((setting) => [setting.key, setting.value] as const)),
  );

  const form: Record<string, string | null> = {};

  for (const [field, key] of [
    ['bankName', SETTING_KEYS.bankName],
    ['branch', SETTING_KEYS.branch],
    ['accountName', SETTING_KEYS.accountName],
    ['accountNumber', SETTING_KEYS.accountNumber],
    ['instructions', SETTING_KEYS.instructions],
  ] as const) {
    if (byKey.has(key)) {
      form[field] = byKey.get(key) ?? null;
    }
  }

  return form as BankDetailValues;
}

/** Re-exported for the spec, which asserts the preview is drawn from what the server sent. */
export type { AdminLogo };
