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
                    [disabled]="busy()"
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
                  <button
                    class="adm-btn--danger"
                    type="button"
                    [disabled]="busy()"
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
                <button
                  class="adm-btn--primary"
                  type="submit"
                  [disabled]="busy()"
                  data-testid="save-contact-social"
                >
                  {{ busy() ? 'Saving…' : 'Save' }}
                </button>
              </div>
            </form>
          </section>
        }

        @if (!canEditStore() && !canEditContact()) {
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
  `,
})
export class SettingsPage {
  /** The shop's name, from the one place it is written down — never a field on this screen (W1-15). */
  protected readonly brand = BRAND.name;

  protected readonly accept = LOGO_ACCEPT;

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

  private readonly api = inject(AdminSettingsApi);
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('logoFile');

  private readonly loaded = signal<AdminSettings | null>(null);
  private readonly isLoading = signal(true);
  private readonly isBusy = signal(false);
  private readonly problem = signal<CatalogFailure | null>(null);
  private readonly logoProblem = signal<string | null>(null);
  private readonly values = signal<ContactAndSocialValues>(EMPTY_FORM);
  private readonly said = signal('');

  protected readonly settings = this.loaded.asReadonly();
  protected readonly loading = this.isLoading.asReadonly();
  protected readonly busy = this.isBusy.asReadonly();
  protected readonly failure = this.problem.asReadonly();
  protected readonly logoError = this.logoProblem.asReadonly();
  protected readonly form = this.values.asReadonly();
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

  constructor() {
    this.load();
  }

  load(): void {
    this.isLoading.set(true);
    this.problem.set(null);

    this.api.settings().subscribe({
      next: (settings) => {
        this.loaded.set(settings);
        this.values.set(toForm(settings));
        this.logoAltText.set(settings.logo?.altText?.trim() || BRAND.name);
        this.isLoading.set(false);
      },
      error: (error: unknown) => {
        this.problem.set(toCatalogFailure(error));
        this.isLoading.set(false);
      },
    });
  }

  protected patch<K extends keyof ContactAndSocialValues>(
    field: K,
    value: ContactAndSocialValues[K],
  ): void {
    this.values.update((current) => ({ ...current, [field]: value }));

    // The message about this field is now about a value nobody is looking at any more.
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

    this.isBusy.set(true);
    this.problem.set(null);

    // Every field, every save: the API replaces the group, so a field this object omitted would be a field
    // the API is told to clear (W1-12's blocker).
    this.api.updateContactAndSocial(this.values(), group).subscribe({
      next: () => this.afterWrite('Contact and social settings saved.'),
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  protected onLogoChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];

    this.logoProblem.set(null);

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

    this.isBusy.set(true);
    this.problem.set(null);

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

    this.isBusy.set(true);
    this.problem.set(null);

    this.api.setStoreLogo(null, group).subscribe({
      next: () => this.afterWrite("The logo was removed. The header shows the shop's name as text."),
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  /**
   * After any successful write: say so, clear the file box, and reload.
   *
   * **The reload is not laziness.** Every save moves the group's row version, so the token on screen is
   * stale the moment the save succeeds — keeping it would make the admin's *next* save fail with a conflict
   * they did nothing to cause. Re-reading also brings back the logo's URL and dimensions, which only the
   * server can produce.
   */
  private afterWrite(message: string): void {
    this.isBusy.set(false);
    this.said.set(message);
    this.clearFileInput();
    this.load();
  }

  private afterFailure(error: unknown): void {
    this.isBusy.set(false);
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

/** Re-exported for the spec, which asserts the preview is drawn from what the server sent. */
export type { AdminLogo };
