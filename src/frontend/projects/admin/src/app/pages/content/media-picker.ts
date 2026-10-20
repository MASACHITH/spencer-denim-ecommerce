import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';

import { AdminSiteContentApi, toContentFailure } from '../../core/content/admin-site-content-api';

/** What the picker shows and hands back: the asset's public id plus enough to draw a thumbnail. */
export interface PickedMedia {
  readonly publicId: string;
  /** The image, or for a video the video file itself. */
  readonly url: string | null;
  readonly altText: string | null;
  /** A video's still; null for an image. */
  readonly posterUrl: string | null;
}

let nextPickerId = 0;

/**
 * A media field for the content screens (#192): the current image (or video) with its alt text, an
 * upload with a **required** description, and Remove.
 *
 * The upload goes to the media library first (`POST /api/admin/media/images`, or `/videos` with a
 * poster) and the picker hands the resulting public id to the form, which sends it with its own save —
 * the two-step flow the logo setting established, so a refused upload changes nothing.
 *
 * Alt text is required here, unlike the product form: everything these screens place is public, and a
 * public image needs a description before the content using it goes live (doc 11 P13, GLB-12).
 */
@Component({
  selector: 'sda-media-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="picker" [attr.aria-describedby]="error() ? pid + '-error' : null">
      <legend class="adm-label">
        {{ label() }}
        @if (required()) {
          <span class="adm-muted">(required)</span>
        } @else {
          <span class="adm-muted">(optional)</span>
        }
      </legend>

      @if (media(); as current) {
        <div class="picker__current" [attr.data-testid]="testId() + '-preview'">
          @if (kind() === 'video') {
            @if (current.posterUrl) {
              <img class="picker__thumb" [src]="current.posterUrl" [alt]="'Poster of the chosen video'" />
            }
            <p class="adm-panel__hint">A video is chosen.</p>
          } @else {
            @if (current.url) {
              <img class="picker__thumb" [src]="current.url" [alt]="current.altText ?? ''" />
            }
            <p class="adm-panel__hint">
              @if (current.altText) {
                Description: {{ current.altText }}
              } @else {
                <span class="adm-field-error">This image has no description. Upload it again with one.</span>
              }
            </p>
          }
          <div class="adm-actions">
            <button
              class="adm-btn"
              type="button"
              [attr.data-testid]="testId() + '-remove'"
              (click)="remove()"
            >
              Remove {{ kind() === 'video' ? 'the video' : 'the image' }}
            </button>
          </div>
        </div>
      } @else {
        <p class="adm-muted" [attr.data-testid]="testId() + '-empty'">
          {{ kind() === 'video' ? 'No video chosen.' : 'No image chosen.' }}
        </p>
      }

      <div class="adm-fields">
        <label class="adm-field" [for]="pid + '-file'">
          <span class="adm-label">{{ kind() === 'video' ? 'Video file (MP4)' : 'Image file' }}</span>
          <input
            class="adm-input"
            type="file"
            [id]="pid + '-file'"
            [accept]="kind() === 'video' ? 'video/mp4' : 'image/jpeg,image/png,image/webp'"
            [attr.data-testid]="testId() + '-file'"
            (change)="chooseFile($event)"
          />
        </label>
        @if (kind() === 'video') {
          <label class="adm-field" [for]="pid + '-poster'">
            <span class="adm-label">Poster image (shown before it plays)</span>
            <input
              class="adm-input"
              type="file"
              [id]="pid + '-poster'"
              accept="image/jpeg,image/png,image/webp"
              [attr.data-testid]="testId() + '-poster'"
              (change)="choosePoster($event)"
            />
          </label>
        }
        <label class="adm-field" [for]="pid + '-alt'">
          <span class="adm-label">Description for screen readers</span>
          <input
            class="adm-input"
            type="text"
            maxlength="250"
            autocomplete="off"
            [id]="pid + '-alt'"
            [value]="altText()"
            [attr.data-testid]="testId() + '-alt'"
            (input)="typeAlt($event)"
          />
        </label>
      </div>

      <div class="adm-actions">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="uploading() ? 'true' : null"
          [attr.data-testid]="testId() + '-upload'"
          (click)="upload()"
        >
          {{ uploading() ? 'Uploading…' : kind() === 'video' ? 'Upload video' : 'Upload image' }}
        </button>
      </div>

      @if (uploadProblem(); as message) {
        <p class="adm-field-error" role="alert" [attr.data-testid]="testId() + '-upload-error'">{{ message }}</p>
      }
      @if (error(); as message) {
        <p class="adm-field-error" [id]="pid + '-error'" role="alert" [attr.data-testid]="testId() + '-error'">
          {{ message }}
        </p>
      }
    </fieldset>
  `,
  styles: `
    .picker {
      display: grid;
      gap: var(--space-1);
      border: 1px solid var(--color-border);
      padding: var(--space-1-5) var(--space-2);
      margin: 0;
      min-width: 0;
    }
    .picker legend {
      padding-inline: var(--space-0-5);
    }
    .picker__current {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .picker__thumb {
      display: block;
      max-width: min(100%, 12rem);
      max-height: 8rem;
      object-fit: contain;
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }
  `,
})
export class MediaPicker {
  private readonly api = inject(AdminSiteContentApi);

  readonly kind = input<'image' | 'video'>('image');
  readonly label = input.required<string>();
  readonly testId = input.required<string>();
  readonly media = input<PickedMedia | null>(null);
  readonly required = input(false);
  /** The server's message for this field, when the last save refused it. */
  readonly error = input<string | null>(null);

  readonly mediaChange = output<PickedMedia | null>();

  protected readonly pid = `media-picker-${++nextPickerId}`;
  protected readonly altText = signal('');
  protected readonly uploading = signal(false);
  protected readonly uploadProblem = signal<string | null>(null);

  private file: File | null = null;
  private poster: File | null = null;

  protected typeAlt(event: Event): void {
    this.altText.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected chooseFile(event: Event): void {
    this.file = (event.target as HTMLInputElement | null)?.files?.[0] ?? null;
  }

  protected choosePoster(event: Event): void {
    this.poster = (event.target as HTMLInputElement | null)?.files?.[0] ?? null;
  }

  protected remove(): void {
    this.mediaChange.emit(null);
  }

  protected upload(): void {
    if (this.uploading()) {
      return;
    }

    const altText = this.altText().trim();
    const video = this.kind() === 'video';

    if (this.file === null) {
      this.uploadProblem.set(video ? 'Choose a video file first.' : 'Choose an image file first.');
      return;
    }

    if (video && this.poster === null) {
      this.uploadProblem.set('Choose a poster image too. A video cannot be added without one.');
      return;
    }

    if (altText.length === 0) {
      this.uploadProblem.set(
        'Describe it first, in a few words. Shoppers who use a screen reader hear this instead of seeing it.',
      );
      return;
    }

    this.uploading.set(true);
    this.uploadProblem.set(null);

    const request = video
      ? this.api.uploadVideo(this.file, this.poster!, altText)
      : this.api.uploadImage(this.file, altText);

    request.subscribe({
      next: (asset) => {
        this.uploading.set(false);
        this.altText.set('');
        this.file = null;
        this.poster = null;
        this.mediaChange.emit({
          publicId: asset.publicId,
          url: asset.url,
          altText: asset.altText ?? altText,
          posterUrl: asset.posterUrl ?? null,
        });
      },
      error: (error: unknown) => {
        this.uploading.set(false);
        const failure = toContentFailure(error);
        this.uploadProblem.set(
          failure.kind === 'forbidden'
            ? 'Your role cannot upload to the media library. Ask an administrator for the Media permission.'
            : (Object.values(failure.fields)[0] ?? failure.message),
        );
      },
    });
  }
}

/** A media reference from the API, as the picker shows it. */
export function pickedImage(
  ref: { readonly publicId: string; readonly url: string | null; readonly altText: string | null } | null,
): PickedMedia | null {
  return ref === null ? null : { publicId: ref.publicId, url: ref.url, altText: ref.altText, posterUrl: null };
}

export function pickedVideo(
  ref: { readonly publicId: string; readonly url: string | null; readonly posterUrl: string | null } | null,
): PickedMedia | null {
  return ref === null ? null : { publicId: ref.publicId, url: ref.url, altText: null, posterUrl: ref.posterUrl };
}
