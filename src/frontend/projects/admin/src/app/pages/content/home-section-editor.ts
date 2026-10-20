import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';

import {
  AdminSiteContentApi,
  ContentFailure,
  toContentFailure,
} from '../../core/content/admin-site-content-api';
import {
  AdminHomeDepartment,
  AdminHomeSection,
  AdminHomeSectionItem,
  HOME_SECTION_LABELS,
  HomeItemField,
  HomeItemRequest,
  HomeOverlay,
  HomeSectionField,
  HomeSectionMode,
  liveLabel,
} from '../../core/content/admin-site-content.model';
import { MediaPicker, PickedMedia, pickedImage, pickedVideo } from './media-picker';

/** The section form, as the inputs hold it (strings until the save turns them into the request). */
interface SectionDraft {
  title: string;
  subtitle: string;
  mode: string;
  count: string;
  listingUrl: string;
  linkLabel: string;
  overlay: string;
  isEnabled: boolean;
  sortOrder: string;
  startsAt: string;
  endsAt: string;
}

/** The item form. Media are kept as picked previews; the request carries only their public ids. */
interface ItemDraft {
  title: string;
  subtitle: string;
  body: string;
  buttonLabel: string;
  linkUrl: string;
  image: PickedMedia | null;
  mobileImage: PickedMedia | null;
  video: PickedMedia | null;
  departmentId: string;
  productSlug: string;
  iconKey: string;
  sortOrder: string;
  isEnabled: boolean;
  startsAt: string;
  endsAt: string;
}

type TextItemField = 'title' | 'subtitle' | 'body' | 'buttonLabel' | 'linkUrl' | 'productSlug';

/** The request keys that have a control of their own on the section form. */
const SECTION_CONTROL_KEYS = [
  'title',
  'subtitle',
  'mode',
  'count',
  'listingUrl',
  'linkLabel',
  'overlay',
  'isEnabled',
  'sortOrder',
  'startsAt',
  'endsAt',
] as const;

/** The request keys that have a control of their own on the item form. */
const ITEM_CONTROL_KEYS = [
  'title',
  'subtitle',
  'body',
  'buttonLabel',
  'linkUrl',
  'imagePublicId',
  'mobileImagePublicId',
  'videoPublicId',
  'departmentId',
  'productSlug',
  'iconKey',
  'sortOrder',
  'isEnabled',
  'startsAt',
  'endsAt',
] as const;

/**
 * One homepage block on the Homepage screen (#192, doc 11 P13 steps 6–7): its own settings form and
 * its items.
 *
 * **The block's rules drive the form.** `rules.sectionFields` and `rules.itemFields` say which fields
 * this block uses; only those are shown, and every other one is sent as null — the server refuses a
 * value for a field the block does not use. Item writes carry the **section's** row version, because
 * the section is the aggregate, and the screen reloads after every write so the next one has the new
 * token.
 *
 * Nothing is ever `disabled` (#109): a busy button carries `aria-disabled` and its handler refuses.
 */
@Component({
  selector: 'sda-home-section-editor',
  imports: [MediaPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let s = section();
    @let t = s.type;
    <section class="adm-panel block" [attr.aria-labelledby]="t + '-heading'" [attr.data-testid]="'section-' + t">
      <h2 class="adm-panel__title block__title" [id]="t + '-heading'">
        <span>{{ label() }}</span>
        <span
          class="adm-pill"
          [class.adm-pill--hidden]="!s.isLiveNow"
          [attr.data-testid]="t + '-status'"
          >{{ status() }}</span
        >
      </h2>

      <p class="adm-muted" role="status" tabindex="-1" [attr.data-testid]="t + '-said'">{{ said() }}</p>

      @if (problem(); as failure) {
        <div class="adm-state adm-state--error" role="alert" [attr.data-testid]="t + '-problem'">
          <p>{{ failure.message }}</p>
          @if (failure.stale) {
            <div class="adm-actions">
              <button class="adm-btn" type="button" [attr.data-testid]="t + '-reload'" (click)="reloadRequested.emit()">
                Reload
              </button>
            </div>
          }
        </div>
      }

      <form class="block__form" (submit)="saveSection($event)">
        <h3 class="block__subtitle">Block settings</h3>
        <div class="adm-fields">
          @if (uses('title')) {
            <label class="adm-field">
              <span class="adm-label">Title</span>
              <input
                class="adm-input"
                type="text"
                maxlength="150"
                autocomplete="off"
                [value]="draft().title"
                [attr.aria-invalid]="sectionError('title') ? 'true' : null"
                [attr.data-testid]="t + '-title'"
                (input)="setDraft('title', $event)"
              />
              @if (sectionError('title'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-title'">{{ message }}</span>
              }
            </label>
          }
          @if (uses('subtitle')) {
            <label class="adm-field">
              <span class="adm-label">Subtitle</span>
              <input
                class="adm-input"
                type="text"
                maxlength="300"
                autocomplete="off"
                [value]="draft().subtitle"
                [attr.aria-invalid]="sectionError('subtitle') ? 'true' : null"
                [attr.data-testid]="t + '-subtitle'"
                (input)="setDraft('subtitle', $event)"
              />
              @if (sectionError('subtitle'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-subtitle'">{{ message }}</span>
              }
            </label>
          }
          @if (uses('mode')) {
            <label class="adm-field">
              <span class="adm-label">Which products</span>
              <select
                class="adm-input"
                [value]="draft().mode"
                [attr.aria-invalid]="sectionError('mode') ? 'true' : null"
                [attr.data-testid]="t + '-mode'"
                (change)="setDraft('mode', $event)"
              >
                <option value="Auto">Chosen automatically from the link below</option>
                <option value="Manual">Hand-picked, as the items below</option>
              </select>
              @if (sectionError('mode'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-mode'">{{ message }}</span>
              }
            </label>
          }
          @if (uses('count')) {
            <label class="adm-field">
              <span class="adm-label">How many products (1 to 24)</span>
              <input
                class="adm-input"
                type="number"
                min="1"
                max="24"
                step="1"
                [value]="draft().count"
                [attr.aria-invalid]="sectionError('count') ? 'true' : null"
                [attr.data-testid]="t + '-count'"
                (input)="setDraft('count', $event)"
              />
              @if (sectionError('count'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-count'">{{ message }}</span>
              }
            </label>
          }
          @if (uses('listingUrl')) {
            <label class="adm-field">
              <span class="adm-label">"View all" link</span>
              <input
                class="adm-input"
                type="text"
                maxlength="500"
                autocomplete="off"
                [attr.aria-describedby]="t + '-listing-hint'"
                [value]="draft().listingUrl"
                [attr.aria-invalid]="sectionError('listingUrl') ? 'true' : null"
                [attr.data-testid]="t + '-listingUrl'"
                (input)="setDraft('listingUrl', $event)"
              />
              <span class="adm-panel__hint" [id]="t + '-listing-hint'">
                A page on this shop, starting with /, for example /women/dresses?sort=newest.
              </span>
              @if (sectionError('listingUrl'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-listingUrl'">{{ message }}</span>
              }
            </label>
          }
          @if (uses('linkLabel')) {
            <label class="adm-field">
              <span class="adm-label">"View all" label</span>
              <input
                class="adm-input"
                type="text"
                maxlength="40"
                autocomplete="off"
                [value]="draft().linkLabel"
                [attr.aria-invalid]="sectionError('linkLabel') ? 'true' : null"
                [attr.data-testid]="t + '-linkLabel'"
                (input)="setDraft('linkLabel', $event)"
              />
              @if (sectionError('linkLabel'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-linkLabel'">{{ message }}</span>
              }
            </label>
          }
          @if (uses('overlay')) {
            <label class="adm-field">
              <span class="adm-label">Shade over the picture</span>
              <select
                class="adm-input"
                [value]="draft().overlay"
                [attr.aria-invalid]="sectionError('overlay') ? 'true' : null"
                [attr.data-testid]="t + '-overlay'"
                (change)="setDraft('overlay', $event)"
              >
                <option value="">Not chosen</option>
                <option value="dark">Dark shade, light text</option>
                <option value="light">Light shade, dark text</option>
                <option value="none">No shade</option>
              </select>
              @if (sectionError('overlay'); as message) {
                <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-overlay'">{{ message }}</span>
              }
            </label>
          }
          <label class="adm-field">
            <span class="adm-label">Position on the page (0 to 999)</span>
            <input
              class="adm-input"
              type="number"
              min="0"
              max="999"
              step="1"
              [value]="draft().sortOrder"
              [attr.aria-invalid]="sectionError('sortOrder') ? 'true' : null"
              [attr.data-testid]="t + '-sortOrder'"
              (input)="setDraft('sortOrder', $event)"
            />
            @if (sectionError('sortOrder'); as message) {
              <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-sortOrder'">{{ message }}</span>
            }
          </label>
          <label class="adm-field">
            <span class="adm-label">Show from (optional)</span>
            <input
              class="adm-input"
              type="datetime-local"
              [value]="draft().startsAt"
              [attr.aria-invalid]="sectionError('startsAt') ? 'true' : null"
              [attr.data-testid]="t + '-startsAt'"
              (input)="setDraft('startsAt', $event)"
            />
            @if (sectionError('startsAt'); as message) {
              <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-startsAt'">{{ message }}</span>
            }
          </label>
          <label class="adm-field">
            <span class="adm-label">Show until (optional)</span>
            <input
              class="adm-input"
              type="datetime-local"
              [value]="draft().endsAt"
              [attr.aria-invalid]="sectionError('endsAt') ? 'true' : null"
              [attr.data-testid]="t + '-endsAt'"
              (input)="setDraft('endsAt', $event)"
            />
            @if (sectionError('endsAt'); as message) {
              <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-error-endsAt'">{{ message }}</span>
            }
          </label>
          <label class="adm-check adm-field--wide" [attr.data-testid]="t + '-enabled-field'">
            <input
              type="checkbox"
              [checked]="draft().isEnabled"
              [attr.data-testid]="t + '-enabled'"
              (change)="setDraftEnabled($event)"
            />
            <span>Show this block on the homepage</span>
          </label>
        </div>

        @if (otherSectionErrors().length > 0) {
          <ul class="adm-field-error block__errors" role="alert" [attr.data-testid]="t + '-errors'">
            @for (message of otherSectionErrors(); track message) {
              <li>{{ message }}</li>
            }
          </ul>
        }

        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
            [attr.data-testid]="t + '-save'"
          >
            Save {{ label() }}
          </button>
        </div>
      </form>

      @if (s.rules.maxItems > 0) {
        <h3 class="block__subtitle">
          Items <span class="adm-muted">({{ s.items.length }} of up to {{ s.rules.maxItems }})</span>
        </h3>

        @if (s.items.length === 0) {
          <p class="adm-muted" [attr.data-testid]="t + '-no-items'">No items yet.</p>
        } @else {
          <ul class="block__items" [attr.data-testid]="t + '-items'">
            @for (item of s.items; track item.id) {
              <li class="block__item" [attr.data-testid]="t + '-item-' + item.id">
                @if (item.image?.url; as url) {
                  <img class="block__thumb" [src]="url" [alt]="item.image?.altText ?? ''" />
                }
                <div class="block__item-text">
                  <p class="block__item-name">{{ itemSummary(item) }}</p>
                  <p class="adm-muted">
                    Position {{ item.sortOrder }} ·
                    <span [attr.data-testid]="t + '-item-status-' + item.id">{{ itemStatus(item) }}</span>
                  </p>
                </div>
                <div class="adm-actions">
                  <button
                    class="adm-btn"
                    type="button"
                    [attr.data-testid]="t + '-item-edit-' + item.id"
                    (click)="editItem(item)"
                  >
                    Edit<span class="sr-only"> {{ itemSummary(item) }}</span>
                  </button>
                  <button
                    class="adm-btn--danger"
                    type="button"
                    [attr.aria-disabled]="isBusy() ? 'true' : null"
                    [attr.data-testid]="t + '-item-remove-' + item.id"
                    (click)="removeItem(item)"
                  >
                    {{ removingId() === item.id ? 'Yes, remove it' : 'Remove' }}<span class="sr-only"> {{ itemSummary(item) }}</span>
                  </button>
                  @if (removingId() === item.id) {
                    <button class="adm-btn" type="button" [attr.data-testid]="t + '-item-keep-' + item.id" (click)="removingId.set(null)">
                      Keep it
                    </button>
                  }
                </div>
              </li>
            }
          </ul>
        }

        @if (editing() === null) {
          @if (s.items.length < s.rules.maxItems) {
            <div class="adm-actions">
              <button class="adm-btn" type="button" [attr.data-testid]="t + '-add-item'" (click)="newItem()">
                Add an item
              </button>
            </div>
          } @else {
            <p class="adm-muted" [attr.data-testid]="t + '-full'">
              This block is full. Remove an item to add another.
            </p>
          }
        } @else {
          <form class="block__item-form" [attr.data-testid]="t + '-item-form'" (submit)="saveItem($event)">
            <h4 class="block__subtitle">{{ editing() === 'new' ? 'Add an item' : 'Edit this item' }}</h4>

            @if (itemProblem(); as failure) {
              <div class="adm-state adm-state--error" role="alert" [attr.data-testid]="t + '-item-problem'">
                <p>{{ failure.message }}</p>
                @if (failure.stale) {
                  <div class="adm-actions">
                    <button class="adm-btn" type="button" (click)="reloadRequested.emit()">Reload</button>
                  </div>
                }
              </div>
            }

            <div class="adm-fields">
              @for (field of textItemFields(); track field) {
                <label class="adm-field" [class.adm-field--wide]="field === 'body'" [for]="t + '-item-input-' + field">
                  <span class="adm-label">
                    {{ itemLabel(field) }}
                    @if (requires(field)) {
                      <span class="adm-muted">(required)</span>
                    }
                  </span>
                  @if (field === 'body') {
                    <textarea
                      class="adm-input"
                      maxlength="1000"
                      [id]="t + '-item-input-body'"
                      [value]="item().body"
                      [attr.aria-invalid]="itemError('body') ? 'true' : null"
                      [attr.data-testid]="t + '-item-body'"
                      (input)="setItem('body', $event)"
                    ></textarea>
                  } @else {
                    <input
                      class="adm-input"
                      type="text"
                      autocomplete="off"
                      [id]="t + '-item-input-' + field"
                      [attr.maxlength]="itemMax(field)"
                      [value]="item()[field]"
                      [attr.aria-invalid]="itemError(field) ? 'true' : null"
                      [attr.data-testid]="t + '-item-' + field"
                      (input)="setItem(field, $event)"
                    />
                  }
                  @if (field === 'linkUrl') {
                    <span class="adm-panel__hint">
                      @if (s.rules.linkMayLeaveSite) {
                        A page on this shop starting with /, or a full https:// address.
                      } @else {
                        A page on this shop, starting with /.
                      }
                    </span>
                  }
                  @if (itemError(field); as message) {
                    <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-item-error-' + field">{{ message }}</span>
                  }
                </label>
              }

              @if (usesItem('department')) {
                <label class="adm-field">
                  <span class="adm-label">
                    Department
                    @if (requires('department')) {
                      <span class="adm-muted">(required)</span>
                    }
                  </span>
                  <select
                    class="adm-input"
                    [attr.aria-invalid]="itemError('departmentId') ? 'true' : null"
                    [attr.data-testid]="t + '-item-department'"
                    (change)="setItem('departmentId', $event)"
                  >
                    <option value="">Choose a department…</option>
                    @for (department of departments(); track department.id) {
                      <option [value]="department.id" [selected]="'' + department.id === item().departmentId">
                        {{ department.name }}{{ department.isActive ? '' : ' (hidden from the shop)' }}
                      </option>
                    }
                  </select>
                  @if (itemError('departmentId'); as message) {
                    <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-item-error-departmentId'">{{ message }}</span>
                  }
                </label>
              }

              @if (usesItem('iconKey')) {
                <label class="adm-field">
                  <span class="adm-label">
                    Icon
                    @if (requires('iconKey')) {
                      <span class="adm-muted">(required)</span>
                    }
                  </span>
                  <select
                    class="adm-input"
                    [attr.aria-invalid]="itemError('iconKey') ? 'true' : null"
                    [attr.data-testid]="t + '-item-icon'"
                    (change)="setItem('iconKey', $event)"
                  >
                    <option value="">Choose an icon…</option>
                    @for (icon of benefitIcons(); track icon) {
                      <option [value]="icon" [selected]="icon === item().iconKey">{{ icon }}</option>
                    }
                  </select>
                  @if (itemError('iconKey'); as message) {
                    <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-item-error-iconKey'">{{ message }}</span>
                  }
                </label>
              }

              <label class="adm-field">
                <span class="adm-label">Position (0 to 999)</span>
                <input
                  class="adm-input"
                  type="number"
                  min="0"
                  max="999"
                  step="1"
                  [value]="item().sortOrder"
                  [attr.aria-invalid]="itemError('sortOrder') ? 'true' : null"
                  [attr.data-testid]="t + '-item-sortOrder'"
                  (input)="setItem('sortOrder', $event)"
                />
                @if (itemError('sortOrder'); as message) {
                  <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-item-error-sortOrder'">{{ message }}</span>
                }
              </label>
              <label class="adm-field">
                <span class="adm-label">Show from (optional)</span>
                <input
                  class="adm-input"
                  type="datetime-local"
                  [value]="item().startsAt"
                  [attr.aria-invalid]="itemError('startsAt') ? 'true' : null"
                  [attr.data-testid]="t + '-item-startsAt'"
                  (input)="setItem('startsAt', $event)"
                />
                @if (itemError('startsAt'); as message) {
                  <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-item-error-startsAt'">{{ message }}</span>
                }
              </label>
              <label class="adm-field">
                <span class="adm-label">Show until (optional)</span>
                <input
                  class="adm-input"
                  type="datetime-local"
                  [value]="item().endsAt"
                  [attr.aria-invalid]="itemError('endsAt') ? 'true' : null"
                  [attr.data-testid]="t + '-item-endsAt'"
                  (input)="setItem('endsAt', $event)"
                />
                @if (itemError('endsAt'); as message) {
                  <span class="adm-field-error" role="alert" [attr.data-testid]="t + '-item-error-endsAt'">{{ message }}</span>
                }
              </label>
              <label class="adm-check adm-field--wide" [attr.data-testid]="t + '-item-enabled-field'">
                <input
                  type="checkbox"
                  [checked]="item().isEnabled"
                  [attr.data-testid]="t + '-item-enabled'"
                  (change)="setItemEnabled($event)"
                />
                <span>Show this item</span>
              </label>
              @if (section().rules.needsArtwork) {
                <p class="adm-panel__hint adm-field--wide" [attr.data-testid]="t + '-item-needs-artwork'">
                  This block is its picture: add an image before switching the item on. It can be saved
                  switched off until the picture arrives.
                </p>
              }
            </div>

            <div class="block__media">
              @if (usesItem('image')) {
                <sda-media-picker
                  [label]="usesItem('video') ? 'Still image (phones, and people who prefer less motion)' : 'Image'"
                  [testId]="t + '-item-image'"
                  [media]="item().image"
                  [required]="requires('image') || item().video !== null"
                  [error]="itemError('imagePublicId')"
                  (mediaChange)="setItemMedia('image', $event)"
                />
              }
              @if (usesItem('mobileImage')) {
                <sda-media-picker
                  label="Phone image"
                  [testId]="t + '-item-mobileImage'"
                  [media]="item().mobileImage"
                  [required]="requires('mobileImage')"
                  [error]="itemError('mobileImagePublicId')"
                  (mediaChange)="setItemMedia('mobileImage', $event)"
                />
              }
              @if (usesItem('video')) {
                <sda-media-picker
                  kind="video"
                  label="Video"
                  [testId]="t + '-item-video'"
                  [media]="item().video"
                  [required]="requires('video')"
                  [error]="itemError('videoPublicId')"
                  (mediaChange)="setItemMedia('video', $event)"
                />
              }
            </div>

            @if (otherItemErrors().length > 0) {
              <ul class="adm-field-error block__errors" role="alert" [attr.data-testid]="t + '-item-errors'">
                @for (message of otherItemErrors(); track message) {
                  <li>{{ message }}</li>
                }
              </ul>
            }

            <div class="adm-actions">
              <button
                class="adm-btn--primary"
                type="submit"
                [attr.aria-disabled]="isBusy() ? 'true' : null"
                [attr.data-testid]="t + '-item-save'"
              >
                {{ editing() === 'new' ? 'Add the item' : 'Save the item' }}
              </button>
              <button class="adm-btn" type="button" [attr.data-testid]="t + '-item-cancel'" (click)="cancelItem()">
                Cancel
              </button>
            </div>
          </form>
        }
      } @else {
        <p class="adm-muted" [attr.data-testid]="t + '-automatic'">
          This block has no items of its own: what it shows comes from the shop itself.
        </p>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .block {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    .block__title {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--space-1);
    }
    .block__subtitle {
      font-size: var(--text-data);
      font-weight: var(--weight-semibold);
      margin-block: var(--space-1) var(--space-1);
    }
    .block__form,
    .block__item-form {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    .block__item-form {
      border-top: 1px solid var(--color-border);
      padding-top: var(--space-2);
    }
    .block__items {
      display: grid;
      gap: var(--space-1);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .block__item {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1) var(--space-2);
      padding: var(--space-1) var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .block__item-text {
      flex: 1 1 12rem;
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .block__item-name {
      font-weight: var(--weight-semibold);
      margin: 0;
    }
    .block__item-text p {
      margin: 0;
    }
    .block__thumb {
      width: 4rem;
      height: 4rem;
      object-fit: cover;
      flex: 0 0 auto;
      background: var(--color-bg-alt);
    }
    .block__media {
      display: grid;
      gap: var(--space-2);
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 280px), 1fr));
      min-width: 0;
    }
    .block__errors {
      margin: 0;
      padding-left: var(--space-2);
    }
  `,
})
export class HomeSectionEditor {
  private readonly api = inject(AdminSiteContentApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  readonly section = input.required<AdminHomeSection>();
  readonly departments = input<readonly AdminHomeDepartment[]>([]);
  readonly benefitIcons = input<readonly string[]>([]);

  /** A write went through; the page reloads so every block has the current row version. */
  readonly saved = output<string>();
  /** The staff member asked to reload after a stale write. */
  readonly reloadRequested = output<void>();

  protected readonly label = computed(() => HOME_SECTION_LABELS[this.section().type] ?? this.section().type);
  protected readonly status = computed(() => liveLabel(this.section()));

  protected readonly draft = signal<SectionDraft>(emptySectionDraft());
  protected readonly isBusy = signal(false);
  protected readonly said = signal('');
  protected readonly problem = signal<ContentFailure | null>(null);
  private readonly sectionErrors = signal<Readonly<Record<string, string>>>({});

  /** Which item is open: its id, `'new'`, or null when the item form is closed. */
  protected readonly editing = signal<number | 'new' | null>(null);
  protected readonly item = signal<ItemDraft>(emptyItemDraft(0));
  protected readonly itemProblem = signal<ContentFailure | null>(null);
  private readonly itemErrors = signal<Readonly<Record<string, string>>>({});

  /** The item whose Remove has been pressed once. */
  protected readonly removingId = signal<number | null>(null);

  /** The plain-text item fields this block uses, in the order the form shows them. */
  protected readonly textItemFields = computed(() => {
    const order: readonly TextItemField[] = ['title', 'subtitle', 'body', 'buttonLabel', 'linkUrl', 'productSlug'];
    return order.filter((field) => this.usesItem(field === 'productSlug' ? 'product' : field));
  });

  protected readonly otherSectionErrors = computed(() =>
    Object.entries(this.sectionErrors())
      .filter(([key]) => !this.sectionControlShown(key))
      .map(([, message]) => message),
  );

  protected readonly otherItemErrors = computed(() =>
    Object.entries(this.itemErrors())
      .filter(([key]) => !this.itemControlShown(key))
      .map(([, message]) => message),
  );

  private lastRowVersion: string | null = null;

  constructor() {
    // The form is refilled when the block's row version moves (a save here, or a reload after someone
    // else's), and only then: another block's save reloads the page too, and must not wipe this form.
    effect(() => {
      const section = this.section();

      untracked(() => {
        if (section.rowVersion !== this.lastRowVersion) {
          this.lastRowVersion = section.rowVersion;
          this.draft.set(sectionDraftFrom(section));

          // What was refused was refused against the old version; after a reload it no longer applies.
          this.problem.set(null);
          this.sectionErrors.set({});
          this.itemProblem.set(null);
        }
      });
    });
  }

  protected uses(field: HomeSectionField): boolean {
    return this.section().rules.sectionFields.includes(field);
  }

  protected usesItem(field: HomeItemField): boolean {
    return this.section().rules.itemFields.includes(field);
  }

  protected requires(field: string): boolean {
    return this.section().rules.requiredItemFields.includes(field);
  }

  protected sectionError(field: string): string | null {
    return this.sectionErrors()[field] ?? null;
  }

  protected itemError(field: string): string | null {
    return this.itemErrors()[field] ?? null;
  }

  protected itemLabel(field: TextItemField): string {
    switch (field) {
      case 'title':
        return 'Title';
      case 'subtitle':
        return 'Subtitle';
      case 'body':
        return 'Text';
      case 'buttonLabel':
        return 'Button label';
      case 'linkUrl':
        return 'Link';
      case 'productSlug':
        return 'Product (the end of its web address, e.g. linen-shirt)';
    }
  }

  protected itemMax(field: TextItemField): number {
    switch (field) {
      case 'title':
        return 150;
      case 'subtitle':
        return 300;
      case 'buttonLabel':
        return 40;
      case 'productSlug':
        return 200;
      default:
        return 1000;
    }
  }

  protected itemSummary(item: AdminHomeSectionItem): string {
    if (item.title) {
      return item.title;
    }

    if (item.departmentId !== null) {
      const department = this.departments().find((candidate) => candidate.id === item.departmentId);
      if (department) {
        return department.name;
      }
    }

    return item.productSlug ?? item.iconKey ?? item.image?.altText ?? `Item ${item.id}`;
  }

  protected itemStatus(item: AdminHomeSectionItem): string {
    return liveLabel(item);
  }

  protected setDraft(field: Exclude<keyof SectionDraft, 'isEnabled'>, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';
    this.draft.update((current) => ({ ...current, [field]: value }));
  }

  protected setDraftEnabled(event: Event): void {
    const checked = (event.target as HTMLInputElement | null)?.checked ?? false;
    this.draft.update((current) => ({ ...current, isEnabled: checked }));
  }

  protected setItem(field: TextItemField | 'departmentId' | 'iconKey' | 'sortOrder' | 'startsAt' | 'endsAt', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | null)?.value ?? '';
    this.item.update((current) => ({ ...current, [field]: value }));
  }

  protected setItemEnabled(event: Event): void {
    const checked = (event.target as HTMLInputElement | null)?.checked ?? false;
    this.item.update((current) => ({ ...current, isEnabled: checked }));
  }

  protected setItemMedia(field: 'image' | 'mobileImage' | 'video', media: PickedMedia | null): void {
    this.item.update((current) => ({ ...current, [field]: media }));
  }

  /**
   * Saves the block's own settings. Every key is sent; a field this block does not use is null, which
   * is what the server requires of it.
   */
  protected saveSection(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const section = this.section();
    const draft = this.draft();
    const used = (field: HomeSectionField) => this.uses(field);

    this.begin(`Saving ${this.label()}…`);
    this.sectionErrors.set({});

    this.api
      .updateSection(section.id, {
        title: used('title') ? blankToNull(draft.title) : null,
        subtitle: used('subtitle') ? blankToNull(draft.subtitle) : null,
        isEnabled: draft.isEnabled,
        sortOrder: toIntegerOrNull(draft.sortOrder),
        startsAt: blankToNull(draft.startsAt),
        endsAt: blankToNull(draft.endsAt),
        mode: used('mode') ? ((blankToNull(draft.mode) as HomeSectionMode | null) ?? null) : null,
        count: used('count') ? toIntegerOrNull(draft.count) : null,
        listingUrl: used('listingUrl') ? blankToNull(draft.listingUrl) : null,
        linkLabel: used('linkLabel') ? blankToNull(draft.linkLabel) : null,
        overlay: used('overlay') ? ((blankToNull(draft.overlay) as HomeOverlay | null) ?? null) : null,
        rowVersion: section.rowVersion,
      })
      .subscribe({
        next: () => this.succeed(`${this.label()} is saved. The homepage shows it now.`),
        error: (error: unknown) => {
          const failure = toContentFailure(error, 'this block');
          this.isBusy.set(false);
          this.sectionErrors.set(failure.fields);
          this.problem.set(Object.keys(failure.fields).length > 0 ? null : failure);
          this.said.set(
            Object.keys(failure.fields).length > 0 ? Object.values(failure.fields).join(' ') : failure.message,
          );
        },
      });
  }

  protected newItem(): void {
    this.editing.set('new');
    this.item.set(emptyItemDraft(this.section().items.length));
    this.itemErrors.set({});
    this.itemProblem.set(null);
    this.removingId.set(null);
  }

  protected editItem(item: AdminHomeSectionItem): void {
    this.editing.set(item.id);
    this.item.set(itemDraftFrom(item));
    this.itemErrors.set({});
    this.itemProblem.set(null);
    this.removingId.set(null);
  }

  protected cancelItem(): void {
    this.editing.set(null);
    this.itemErrors.set({});
    this.itemProblem.set(null);
    this.rehomeFocus();
  }

  /** Adds or saves an item, with the SECTION's row version, and reloads. */
  protected saveItem(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const editing = this.editing();
    if (editing === null) {
      return;
    }

    const body = this.itemRequest();

    this.begin(editing === 'new' ? 'Adding the item…' : 'Saving the item…');
    this.itemErrors.set({});
    this.itemProblem.set(null);

    const request =
      editing === 'new'
        ? this.api.createItem(this.section().id, body)
        : this.api.updateItem(editing, body);

    request.subscribe({
      next: () => {
        this.editing.set(null);
        this.succeed(editing === 'new' ? 'The item is added.' : 'The item is saved.');
      },
      error: (error: unknown) => {
        const failure = toContentFailure(error, 'this block');
        this.isBusy.set(false);
        this.itemErrors.set(failure.fields);
        this.itemProblem.set(Object.keys(failure.fields).length > 0 ? null : failure);
        this.said.set(
          Object.keys(failure.fields).length > 0 ? Object.values(failure.fields).join(' ') : failure.message,
        );
      },
    });
  }

  /** Removes an item on the second press, sending the section's row version in `If-Match`. */
  protected removeItem(item: AdminHomeSectionItem): void {
    if (this.isBusy()) {
      return;
    }

    if (this.removingId() !== item.id) {
      this.removingId.set(item.id);
      this.said.set(`Press Remove again to take ${this.itemSummary(item)} off this block.`);
      return;
    }

    this.begin('Removing the item…');

    this.api.deleteItem(item.id, this.section().rowVersion).subscribe({
      next: () => {
        this.removingId.set(null);
        if (this.editing() === item.id) {
          this.editing.set(null);
        }
        this.succeed('The item is removed.');
      },
      error: (error: unknown) => {
        this.removingId.set(null);
        const failure = toContentFailure(error, 'this block');
        this.isBusy.set(false);
        this.problem.set(failure);
        this.said.set(failure.message);
      },
    });
  }

  private itemRequest(): HomeItemRequest {
    const draft = this.item();
    const used = (field: HomeItemField) => this.usesItem(field);

    return {
      title: used('title') ? blankToNull(draft.title) : null,
      subtitle: used('subtitle') ? blankToNull(draft.subtitle) : null,
      body: used('body') ? blankToNull(draft.body) : null,
      buttonLabel: used('buttonLabel') ? blankToNull(draft.buttonLabel) : null,
      linkUrl: used('linkUrl') ? blankToNull(draft.linkUrl) : null,
      imagePublicId: used('image') ? (draft.image?.publicId ?? null) : null,
      mobileImagePublicId: used('mobileImage') ? (draft.mobileImage?.publicId ?? null) : null,
      videoPublicId: used('video') ? (draft.video?.publicId ?? null) : null,
      departmentId: used('department') ? toIntegerOrNull(draft.departmentId) : null,
      productSlug: used('product') ? blankToNull(draft.productSlug) : null,
      iconKey: used('iconKey') ? blankToNull(draft.iconKey) : null,
      sortOrder: toIntegerOrNull(draft.sortOrder),
      isEnabled: draft.isEnabled,
      startsAt: blankToNull(draft.startsAt),
      endsAt: blankToNull(draft.endsAt),
      rowVersion: this.section().rowVersion,
    };
  }

  private sectionControlShown(key: string): boolean {
    if (!(SECTION_CONTROL_KEYS as readonly string[]).includes(key)) {
      return false;
    }

    return ['isEnabled', 'sortOrder', 'startsAt', 'endsAt'].includes(key) || this.uses(key as HomeSectionField);
  }

  private itemControlShown(key: string): boolean {
    if (!(ITEM_CONTROL_KEYS as readonly string[]).includes(key)) {
      return false;
    }

    // `isEnabled` has no error line of its own; anything about it is shown in the list.
    if (key === 'isEnabled') {
      return false;
    }

    const field: Record<string, HomeItemField | null> = {
      imagePublicId: 'image',
      mobileImagePublicId: 'mobileImage',
      videoPublicId: 'video',
      departmentId: 'department',
      productSlug: 'product',
      sortOrder: null,
      startsAt: null,
      endsAt: null,
    };

    if (key in field) {
      const mapped = field[key];
      return mapped === null || this.usesItem(mapped);
    }

    return this.usesItem(key as HomeItemField);
  }

  private begin(message: string): void {
    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(message);
  }

  private succeed(message: string): void {
    this.isBusy.set(false);
    this.problem.set(null);
    this.said.set(message);
    this.saved.emit(message);
    this.rehomeFocus();
  }

  /** A form or row this update removes may have held focus; put it on the block's status line. */
  private rehomeFocus(): void {
    const type = this.section().type;

    afterNextRender(
      () => {
        const active = this.document.activeElement;
        if (active !== null && active !== this.document.body && active.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>(`[data-testid="${type}-said"]`)
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }
}

function emptySectionDraft(): SectionDraft {
  return {
    title: '',
    subtitle: '',
    mode: 'Auto',
    count: '',
    listingUrl: '',
    linkLabel: '',
    overlay: '',
    isEnabled: true,
    sortOrder: '0',
    startsAt: '',
    endsAt: '',
  };
}

function sectionDraftFrom(section: AdminHomeSection): SectionDraft {
  return {
    title: section.title ?? '',
    subtitle: section.subtitle ?? '',
    mode: section.mode ?? 'Auto',
    count: section.count === null ? '' : String(section.count),
    listingUrl: section.listingUrl ?? '',
    linkLabel: section.linkLabel ?? '',
    overlay: section.overlay ?? '',
    isEnabled: section.isEnabled,
    sortOrder: String(section.sortOrder),
    startsAt: toLocalInput(section.startsAt),
    endsAt: toLocalInput(section.endsAt),
  };
}

function emptyItemDraft(position: number): ItemDraft {
  return {
    title: '',
    subtitle: '',
    body: '',
    buttonLabel: '',
    linkUrl: '',
    image: null,
    mobileImage: null,
    video: null,
    departmentId: '',
    productSlug: '',
    iconKey: '',
    sortOrder: String(position),
    isEnabled: true,
    startsAt: '',
    endsAt: '',
  };
}

function itemDraftFrom(item: AdminHomeSectionItem): ItemDraft {
  return {
    title: item.title ?? '',
    subtitle: item.subtitle ?? '',
    body: item.body ?? '',
    buttonLabel: item.buttonLabel ?? '',
    linkUrl: item.linkUrl ?? '',
    image: pickedImage(item.image),
    mobileImage: pickedImage(item.mobileImage),
    video: pickedVideo(item.video),
    departmentId: item.departmentId === null ? '' : String(item.departmentId),
    productSlug: item.productSlug ?? '',
    iconKey: item.iconKey ?? '',
    sortOrder: String(item.sortOrder),
    isEnabled: item.isEnabled,
    startsAt: toLocalInput(item.startsAt),
    endsAt: toLocalInput(item.endsAt),
  };
}

/** `''` for a blank box, which is how a staff member says "not set". */
export function blankToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * A whole number, or null for a blank or unreadable box — never a zero nobody typed. The server
 * answers a null where it needs a number with a message against the control.
 */
export function toIntegerOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number(trimmed);
  return Number.isInteger(parsed) ? parsed : null;
}

/** The shop's wall-clock time as `datetime-local` wants it, without seconds; `''` for none. */
export function toLocalInput(value: string | null): string {
  if (value === null) {
    return '';
  }

  return value.length >= 16 ? value.slice(0, 16) : value;
}
