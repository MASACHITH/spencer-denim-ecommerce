import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

import {
  AdminProductApi,
  IMAGE_ACCEPT,
  MAX_IMAGE_BYTES,
} from '../../core/products/admin-product-api';
import { AdminSaleCampaignBanner } from '../../core/marketing/admin-campaign.model';
import { toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/**
 * The campaign banner picker (#191, ADM-14; doc 11 P3 step 1's optional banner).
 *
 * **The media picker the rest of the admin uses, not a new one.** A picture goes to the media library
 * through `POST /api/admin/media/images` — the same upload the product form and the logo setting make,
 * through the same `AdminProductApi.uploadImage` — and the campaign refers to it by the `publicId` the
 * library answers with. The library's checksum index means uploading a picture it already holds answers
 * with that asset rather than a copy, and the campaign's save writes the `media.MediaUsages` row that
 * stops the library deleting a picture the Sale page is drawing.
 *
 * **A description is asked for before the upload.** It is what the Sale page reads aloud, and the
 * description lives on the library's asset, so it has to travel with the file. The server accepts a
 * banner without one and warns; this form does not offer that path for a new upload.
 *
 * **Nothing is uploaded until the button is pressed**, so choosing the wrong file costs nothing, and the
 * campaign itself does not change until the form is saved — the picture is only *chosen* here.
 */
@Component({
  selector: 'sda-campaign-banner-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="banner">
      <h3 class="banner__title">Banner <span class="adm-muted">(optional)</span></h3>
      <p class="adm-panel__hint" id="campaign-banner-hint">
        Shown across the top of the Sale page while this campaign is live, and only then. A wide picture
        works best, about three times as wide as it is tall; its edges may be cropped to fit.
      </p>

      @if (banner(); as chosen) {
        <figure class="banner__preview" data-testid="banner-preview">
          @if (chosen.url) {
            <img [src]="chosen.url" [alt]="chosen.altText ?? ''" width="320" height="100" />
          }
          <figcaption>
            @if (chosen.altText) {
              Read aloud as “{{ chosen.altText }}”.
            } @else {
              <span class="adm-field-error" data-testid="banner-no-alt">
                This picture has no description, so people using a screen reader will not hear what it
                says. Upload it again with a description if it carries words or an offer.
              </span>
            }
          </figcaption>
        </figure>
        <div class="adm-actions">
          <button class="adm-btn" type="button" data-testid="banner-remove" (click)="remove()">
            Remove the banner
          </button>
        </div>
      } @else {
        <p class="adm-muted" data-testid="banner-empty">
          No banner. The Sale page shows its heading and the products only.
        </p>
      }

      <div class="adm-fields">
        <label class="adm-field adm-field--wide">
          <span class="adm-label">Describe the picture</span>
          <input
            class="adm-input"
            type="text"
            maxlength="250"
            autocomplete="off"
            aria-describedby="campaign-banner-alt-hint"
            [value]="altText()"
            [attr.aria-invalid]="altMissing() ? 'true' : null"
            data-testid="banner-alt"
            (input)="typeAlt($event)"
          />
          <span class="adm-panel__hint" id="campaign-banner-alt-hint">
            Read aloud to people using a screen reader. Say what the picture says, such as “Linen Week:
            20% off shirts”.
          </span>
        </label>

        <label class="adm-field adm-field--wide">
          <span class="adm-label">{{ banner() ? 'Replace the picture' : 'Choose a picture' }}</span>
          <input
            #file
            class="adm-input"
            type="file"
            [accept]="accept"
            aria-describedby="campaign-banner-hint"
            data-testid="banner-file"
            (change)="chooseFile($event)"
          />
        </label>
      </div>

      @if (problem(); as message) {
        <p class="adm-field-error" role="alert" data-testid="banner-error">{{ message }}</p>
      }

      <div class="adm-actions">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="busy() ? 'true' : null"
          data-testid="banner-upload"
          (click)="upload()"
        >
          {{ busy() ? 'Uploading…' : 'Upload the picture' }}
        </button>
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
      margin-block-end: var(--space-2);
    }
    .banner {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
    }
    .banner__title {
      font-size: var(--text-data);
      font-weight: var(--weight-semibold);
      margin-block: var(--space-2) 0;
    }
    .banner__preview {
      display: grid;
      gap: var(--space-1);
      margin: 0;
      min-width: 0;
      font-size: var(--text-small);
      overflow-wrap: anywhere;
    }
    .banner__preview img {
      width: 100%;
      max-width: 480px;
      height: auto;
      aspect-ratio: 16 / 5;
      object-fit: cover;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
    }
  `,
})
export class CampaignBannerPicker {
  private readonly api = inject(AdminProductApi);
  private readonly injector = inject(Injector);
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('file');

  /** The banner the form currently holds, or null. */
  readonly banner = input<AdminSaleCampaignBanner | null>(null);

  /** The staff member chose a picture, or removed it. */
  readonly bannerChange = output<AdminSaleCampaignBanner | null>();

  /** A sentence for the form's own live region. */
  readonly said = output<string>();

  protected readonly accept = IMAGE_ACCEPT;
  protected readonly altText = signal('');
  protected readonly altMissing = signal(false);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);

  private file: File | null = null;

  protected typeAlt(event: Event): void {
    this.altText.set((event.target as HTMLInputElement | null)?.value ?? '');
    this.altMissing.set(false);
  }

  protected chooseFile(event: Event): void {
    this.file = (event.target as HTMLInputElement | null)?.files?.[0] ?? null;
    this.problem.set(null);
  }

  /** Sends the picture to the media library and, once it is there, makes it this campaign's banner. */
  protected upload(): void {
    if (this.busy()) {
      return;
    }

    const file = this.file;
    const description = this.altText().trim();

    if (file === null) {
      this.refuse('Choose a picture first.');
      return;
    }

    if (description.length === 0) {
      this.altMissing.set(true);
      this.refuse('Describe the picture first. The Sale page reads the description aloud.');
      return;
    }

    if (file.size > MAX_IMAGE_BYTES) {
      this.refuse('That picture is larger than 8 MB. Save it smaller and choose it again.');
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    this.said.emit('Uploading the banner…');

    this.api.uploadImage(file, description).subscribe({
      next: (asset) => {
        this.busy.set(false);
        this.file = null;
        this.altText.set('');

        const input = this.fileInput()?.nativeElement;
        if (input) {
          input.value = '';
        }

        this.bannerChange.emit({
          publicId: asset.publicId,
          url: asset.url,
          altText: asset.altText ?? null,
        });
        this.said.emit('The banner is chosen. Save the campaign to keep it.');
      },
      error: (error: unknown) => {
        this.busy.set(false);

        const failure = toCatalogFailure(error);

        this.refuse(
          failure.kind === 'forbidden'
            ? 'You can manage campaigns but not upload pictures. Ask for the "Media library" permission '
              + 'as well.'
            : failure.message,
        );
      },
    });
  }

  /** Takes the banner off the campaign. Nothing is deleted from the media library. */
  protected remove(): void {
    this.bannerChange.emit(null);
    this.said.emit('The banner is removed. Save the campaign to keep the change.');

    // The Remove button goes with the preview, so focus is put on the file box rather than left to fall
    // to <body> (#109's shape).
    afterNextRender(() => this.fileInput()?.nativeElement.focus(), { injector: this.injector });
  }

  /** Shows why nothing happened. The line is `role="alert"`, so it is not announced a second time. */
  private refuse(message: string): void {
    this.problem.set(message);
  }
}
