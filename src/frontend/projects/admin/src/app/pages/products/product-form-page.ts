import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterEveryRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import {
  AdminProductApi,
  IMAGE_ACCEPT,
  MAX_IMAGE_BYTES,
  MAX_VIDEO_BYTES,
  VIDEO_ACCEPT,
} from '../../core/products/admin-product-api';
import {
  AdminProductDetail,
  AdminRelatedProduct,
  BADGES,
  ColourValues,
  MEDIA_ROLE,
  MediaAssetType,
  MediaValues,
  ProductBadge,
  ProductFormOptions,
  ProductFormValues,
  ProductMediaRole,
} from '../../core/products/admin-product.model';
import {
  MatrixRow,
  buildMatrix,
  isUndeletable,
  moveInArray,
  scaleIn,
  sizeIdsIn,
  suggestSlug,
  toColourValues,
  toMatrixRows,
  withSortOrder,
} from '../../core/products/variant-matrix';

/** The scalar half of the form. One object, so a partial patch cannot leave two fields disagreeing. */
interface Scalars {
  productCode: string;
  name: string;
  slug: string;
  departmentId: number | null;
  primaryCategoryId: number | null;
  extraCategoryIds: number[];
  ageGroupId: number | null;
  shortDescription: string;
  description: string;
  careInstructions: string;
  materialId: number | null;
  fitId: number | null;
  occasionId: number | null;
  basePrice: number | null;
  compareAtPrice: number | null;
  badgeOverride: ProductBadge | null;
  recommendedRank: number | null;
  sizeGuideId: number | null;
  seoTitle: string;
  seoDescription: string;
}

/** A gallery row on screen: the form values plus what it takes to draw it. */
interface MediaRow extends MediaValues {
  readonly url: string | null;
  readonly posterUrl: string | null;
  readonly mediaType: MediaAssetType;
  readonly assetAltText: string | null;
}

const EMPTY: Scalars = {
  productCode: '',
  name: '',
  slug: '',
  departmentId: null,
  primaryCategoryId: null,
  extraCategoryIds: [],
  ageGroupId: null,
  shortDescription: '',
  description: '',
  careInstructions: '',
  materialId: null,
  fitId: null,
  occasionId: null,
  basePrice: null,
  compareAtPrice: null,
  badgeOverride: null,
  recommendedRank: null,
  sizeGuideId: null,
  seoTitle: '',
  seoDescription: '',
};

/**
 * ADM-10: the add / edit product form (doc 11 P1 steps 6–15).
 *
 * **Three decisions worth finding here rather than in a commit message.**
 *
 * 1. **Move up / Move down, never drag and drop**, for the colourways, the variant matrix and the gallery.
 *    That is the owner's decision of 2026-10-02 for ADM-11 (`docs/11` P1 step 4) and the argument carries
 *    over unchanged: dragging needs a pointer, needs a visible drop target at 320 px, and needs a keyboard
 *    route built separately anyway. The buttons are 44 px and work by tap, click, Tab + Enter, or
 *    **Alt + ↑/↓** from either button of a pair.
 * 2. **`aria-disabled`, not `disabled`, on every control that a save makes unavailable.** Disabling the
 *    button that currently has focus makes the browser move focus to `<body>`, so a keyboard user loses
 *    their place as a result of their own keystroke — the ADM-22 defect. The guard that actually stops a
 *    second request is in the handler.
 * 3. **The form is never unmounted by a save.** A re-read after a write goes through `refresh()`, which
 *    leaves the form on screen, so focus is never moved and never has to be restored — the other half of
 *    the ADM-22 defect.
 *
 * The API is the security boundary (doc 03 §3a): the route only asks for a session, and a user without
 * `Products.Create` or `Products.Edit` is shown the refusal rather than bounced from a page that then
 * cannot explain itself.
 */
@Component({
  selector: 'sda-product-form-page',
  imports: [FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="form" #formRoot>
      <div class="form__head">
        <h1 class="form__title">{{ productId() === null ? 'Add a product' : 'Edit product' }}</h1>
        <a class="adm-btn" routerLink="/products" data-testid="back-to-list">All products</a>
      </div>

      <p class="sr-only" role="status" data-testid="announcement">{{ announcement() }}</p>

      @if (failure(); as problem) {
        <div class="adm-state adm-state--error" role="alert" data-testid="page-error">
          <p>{{ problem.message }}</p>
          @if (slugConfirmation(); as pending) {
            <p>
              Confirm to move the page from <code>/p/{{ pending.currentSlug }}</code> to
              <code>/p/{{ pending.requestedSlug }}</code>.
            </p>
            <div class="adm-actions">
              <button class="adm-btn--danger" type="button" (click)="save(true)" data-testid="confirm-slug">
                Yes, change the address
              </button>
              <button class="adm-btn" type="button" (click)="cancelSlugChange()" data-testid="cancel-slug">
                Keep the old address
              </button>
            </div>
          }
        </div>
      }

      @if (loading()) {
        <p class="adm-state" data-testid="loading">Loading…</p>
      } @else {
        <form class="form__body" (ngSubmit)="save(false)" novalidate>
          <!-- 1. The product itself (doc 11 P1 step 6) ------------------------------------------ -->
          <fieldset class="adm-panel">
            <legend class="adm-panel__title">The product</legend>

            <div class="adm-fields">
              <label class="adm-field">
                <span class="adm-label">Product code</span>
                <input
                  class="adm-input"
                  name="productCode"
                  [ngModel]="form().productCode"
                  (ngModelChange)="patch({ productCode: $event })"
                  [attr.aria-invalid]="fieldError('productCode') ? 'true' : null"
                  [attr.aria-describedby]="fieldError('productCode') ? 'err-productCode' : null"
                  data-testid="field-product-code"
                />
                @if (fieldError('productCode'); as message) {
                  <span class="adm-field-error" id="err-productCode" data-testid="error-product-code">
                    {{ message }}
                  </span>
                }
              </label>

              <label class="adm-field">
                <span class="adm-label">Name</span>
                <input
                  class="adm-input"
                  name="name"
                  [ngModel]="form().name"
                  (ngModelChange)="nameChanged($event)"
                  [attr.aria-invalid]="fieldError('name') ? 'true' : null"
                  [attr.aria-describedby]="fieldError('name') ? 'err-name' : null"
                  data-testid="field-name"
                />
                @if (fieldError('name'); as message) {
                  <span class="adm-field-error" id="err-name">{{ message }}</span>
                }
              </label>

              <label class="adm-field adm-field--wide">
                <span class="adm-label">Web address</span>
                <span class="form__slug">
                  <span class="adm-muted" aria-hidden="true">/p/</span>
                  <input
                    class="adm-input"
                    name="slug"
                    [ngModel]="form().slug"
                    (ngModelChange)="patch({ slug: $event })"
                    [attr.aria-invalid]="fieldError('slug') ? 'true' : null"
                    [attr.aria-describedby]="'slug-hint' + (fieldError('slug') ? ' err-slug' : '')"
                    data-testid="field-slug"
                  />
                </span>
                <span class="adm-panel__hint" id="slug-hint">
                  Lower-case letters, digits and single hyphens. It is suggested from the name until you
                  change it.
                  @if (hasBeenPublished()) {
                    <strong>
                      This product has been published, so changing the address will break any link to the
                      old one — there are no redirects yet.
                    </strong>
                  }
                </span>
                @if (fieldError('slug'); as message) {
                  <span class="adm-field-error" id="err-slug" data-testid="error-slug">{{ message }}</span>
                }
              </label>

              <label class="adm-field">
                <span class="adm-label">Department</span>
                <select
                  class="adm-input"
                  name="departmentId"
                  [ngModel]="form().departmentId"
                  (ngModelChange)="departmentChanged($event === null || $event === '' ? null : +$event)"
                  data-testid="field-department"
                >
                  <option [ngValue]="null">Choose…</option>
                  @for (department of options().departments; track department.id) {
                    <option [ngValue]="department.id">{{ department.name }}</option>
                  }
                </select>
                @if (fieldError('departmentId'); as message) {
                  <span class="adm-field-error">{{ message }}</span>
                }
              </label>

              <label class="adm-field">
                <span class="adm-label">Main category</span>
                <select
                  class="adm-input"
                  name="primaryCategoryId"
                  [ngModel]="form().primaryCategoryId"
                  (ngModelChange)="patch({ primaryCategoryId: $event === null || $event === '' ? null : +$event })"
                  [attr.aria-describedby]="'category-hint'"
                  data-testid="field-primary-category"
                >
                  <option [ngValue]="null">Choose…</option>
                  @for (category of categoriesForDepartment(); track category.id) {
                    <option [ngValue]="category.id">
                      {{ category.displayName }}{{ category.isActive ? '' : ' (switched off)' }}
                    </option>
                  }
                </select>
                <span class="adm-panel__hint" id="category-hint">
                  Where the product lives. It has to be one of this department's own categories.
                </span>
                @if (fieldError('primaryCategoryId'); as message) {
                  <span class="adm-field-error" data-testid="error-primary-category">{{ message }}</span>
                }
              </label>

              @if (needsAgeGroup()) {
                <label class="adm-field">
                  <span class="adm-label">Age group</span>
                  <select
                    class="adm-input"
                    name="ageGroupId"
                    [ngModel]="form().ageGroupId"
                    (ngModelChange)="patch({ ageGroupId: $event === null || $event === '' ? null : +$event })"
                    data-testid="field-age-group"
                  >
                    <option [ngValue]="null">Choose…</option>
                    @for (group of options().ageGroups; track group.id) {
                      <option [ngValue]="group.id">{{ group.name }}</option>
                    }
                  </select>
                  <span class="adm-panel__hint">
                    Kids products need one: it is what the Baby / Toddler / Kids / Juniors filters are built
                    from.
                  </span>
                  @if (fieldError('ageGroupId'); as message) {
                    <span class="adm-field-error" data-testid="error-age-group">{{ message }}</span>
                  }
                </label>
              }
            </div>

            <fieldset class="form__subgroup">
              <legend class="adm-label">Also list it in</legend>
              <p class="adm-panel__hint">
                Extra categories the product should appear in as well. The main category is always included.
              </p>
              <div class="form__checks">
                @for (category of extraCategoryChoices(); track category.id) {
                  <label class="adm-check">
                    <input
                      type="checkbox"
                      [checked]="form().extraCategoryIds.includes(category.id)"
                      (change)="toggleExtraCategory(category.id)"
                      [attr.data-testid]="'extra-category-' + category.id"
                    />
                    <span>{{ category.displayName }}</span>
                  </label>
                }
              </div>
            </fieldset>
          </fieldset>

          <!-- 2. Price (doc 11 P1 step 7) ------------------------------------------------------- -->
          <fieldset class="adm-panel">
            <legend class="adm-panel__title">Price</legend>
            <div class="adm-fields">
              <label class="adm-field">
                <span class="adm-label">Price (Rs.)</span>
                <input
                  class="adm-input"
                  name="basePrice"
                  type="number"
                  min="0.01"
                  step="0.01"
                  inputmode="decimal"
                  [ngModel]="form().basePrice"
                  (ngModelChange)="patch({ basePrice: toNumber($event) })"
                  [attr.aria-invalid]="fieldError('basePrice') ? 'true' : null"
                  data-testid="field-base-price"
                />
                @if (fieldError('basePrice'); as message) {
                  <span class="adm-field-error" data-testid="error-base-price">{{ message }}</span>
                }
              </label>

              <label class="adm-field">
                <span class="adm-label">Original price (Rs., optional)</span>
                <input
                  class="adm-input"
                  name="compareAtPrice"
                  type="number"
                  min="0.01"
                  step="0.01"
                  inputmode="decimal"
                  [ngModel]="form().compareAtPrice"
                  (ngModelChange)="patch({ compareAtPrice: toNumber($event) })"
                  [attr.aria-invalid]="fieldError('compareAtPrice') ? 'true' : null"
                  data-testid="field-compare-at-price"
                />
                <span class="adm-panel__hint">
                  Higher than the price. The shop shows it crossed out with the discount worked out from it.
                  @if (discountPercent(); as percent) {
                    <strong data-testid="discount-preview">That is {{ percent }}% off.</strong>
                  }
                </span>
                @if (fieldError('compareAtPrice'); as message) {
                  <span class="adm-field-error" data-testid="error-compare-at-price">{{ message }}</span>
                }
              </label>
            </div>
          </fieldset>

          <!-- 3. Colours (doc 11 P1 step 8) ----------------------------------------------------- -->
          <fieldset class="adm-panel" #coloursRoot>
            <legend class="adm-panel__title">Colours</legend>
            <p class="adm-panel__hint">
              One row per colourway. The name is what a shopper sees; the family is what the colour filter
              on the listing page groups it under.
            </p>

            @if (colours().length === 0) {
              <p class="adm-state" data-testid="no-colours">
                No colours yet. Add one, then tick the sizes it comes in.
              </p>
            }

            <ul class="form__rows" data-testid="colour-list">
              @for (colour of colours(); track $index; let index = $index) {
                <li class="form__row" [attr.data-testid]="'colour-' + index">
                  <div class="adm-fields">
                    <label class="adm-field">
                      <span class="adm-label">Name</span>
                      <input
                        class="adm-input"
                        [name]="'colour-name-' + index"
                        [ngModel]="colour.name"
                        (ngModelChange)="patchColour(index, { name: $event })"
                        [attr.data-testid]="'colour-name-' + index"
                      />
                    </label>

                    <label class="adm-field">
                      <span class="adm-label">Colour family</span>
                      <select
                        class="adm-input"
                        [name]="'colour-family-' + index"
                        [ngModel]="colour.colourFamilyId"
                        (ngModelChange)="patchColour(index, { colourFamilyId: $event === null || $event === '' ? null : +$event })"
                        [attr.data-testid]="'colour-family-' + index"
                      >
                        <option [ngValue]="null">Choose…</option>
                        @for (family of options().colourFamilies; track family.id) {
                          <option [ngValue]="family.id">{{ family.name }}</option>
                        }
                      </select>
                    </label>

                    <label class="adm-field">
                      <span class="adm-label">Swatch colour</span>
                      <input
                        class="adm-input"
                        [name]="'colour-hex-' + index"
                        placeholder="#1a2b3c"
                        [ngModel]="colour.swatchHex"
                        (ngModelChange)="patchColour(index, { swatchHex: $event })"
                        [attr.data-testid]="'colour-hex-' + index"
                      />
                    </label>
                  </div>

                  <div class="adm-actions">
                    <button
                      class="adm-btn"
                      type="button"
                      [attr.aria-disabled]="index === 0 ? 'true' : null"
                      (click)="moveColour(index, -1, $event)"
                      (keydown.alt.arrowup)="moveColour(index, -1, $event)"
                      (keydown.alt.arrowdown)="moveColour(index, 1, $event)"
                      [attr.data-testid]="'colour-up-' + index"
                    >
                      Move up<span class="sr-only"> — {{ colour.name || 'this colour' }}</span>
                    </button>
                    <button
                      class="adm-btn"
                      type="button"
                      [attr.aria-disabled]="index === colours().length - 1 ? 'true' : null"
                      (click)="moveColour(index, 1, $event)"
                      (keydown.alt.arrowup)="moveColour(index, -1, $event)"
                      (keydown.alt.arrowdown)="moveColour(index, 1, $event)"
                      [attr.data-testid]="'colour-down-' + index"
                    >
                      Move down<span class="sr-only"> — {{ colour.name || 'this colour' }}</span>
                    </button>
                    <button
                      class="adm-btn--danger"
                      type="button"
                      (click)="removeColour(index)"
                      [attr.data-testid]="'colour-remove-' + index"
                    >
                      Remove<span class="sr-only"> {{ colour.name || 'this colour' }}</span>
                    </button>
                  </div>
                </li>
              }
            </ul>

            <div class="adm-actions">
              <button class="adm-btn" type="button" (click)="addColour()" data-testid="add-colour">
                Add a colour
              </button>
            </div>
          </fieldset>

          <!-- 4. Sizes and the variant matrix (doc 11 P1 step 9) -------------------------------- -->
          <fieldset class="adm-panel" #matrixRoot>
            <legend class="adm-panel__title">Sizes and SKUs</legend>

            <div class="adm-fields">
              <label class="adm-field">
                <span class="adm-label">Size scale</span>
                <select
                  class="adm-input"
                  name="sizeScale"
                  [ngModel]="sizeScale()"
                  (ngModelChange)="scaleChanged($event)"
                  data-testid="field-size-scale"
                >
                  <option [ngValue]="null">Choose…</option>
                  @for (scale of scales(); track scale) {
                    <option [ngValue]="scale">{{ scale }}</option>
                  }
                </select>
              </label>
            </div>

            @if (sizeScale()) {
              <fieldset class="form__subgroup">
                <legend class="adm-label">Sizes offered</legend>
                <div class="form__checks">
                  @for (size of sizesInScale(); track size.id) {
                    <label class="adm-check">
                      <input
                        type="checkbox"
                        [checked]="chosenSizeIds().includes(size.id)"
                        (change)="toggleSize(size.id)"
                        [attr.data-testid]="'size-' + size.id"
                      />
                      <span>{{ size.label }}</span>
                    </label>
                  }
                </div>
              </fieldset>
            }

            @if (matrix().length === 0) {
              <p class="adm-state" data-testid="no-variants">
                Nothing to sell yet. Add a colour and tick a size: one row appears per colour and size.
              </p>
            } @else {
              <ul class="form__rows" data-testid="variant-list">
                @for (row of matrix(); track row.colourName + '/' + row.sizeId; let index = $index) {
                  <li class="form__row" [attr.data-testid]="'variant-' + index">
                    <p class="form__row-title">
                      {{ row.colourName }} · {{ row.sizeLabel }}
                      @if (row.hasOrders) {
                        <span class="adm-pill" [attr.data-testid]="'variant-ordered-' + index">Ordered</span>
                      } @else if (row.onHand > 0) {
                        <span class="adm-pill" [attr.data-testid]="'variant-stocked-' + index">In stock</span>
                      }
                    </p>

                    <div class="adm-fields">
                      <label class="adm-field">
                        <span class="adm-label">SKU</span>
                        <input
                          class="adm-input"
                          [name]="'sku-' + index"
                          [ngModel]="row.sku"
                          (ngModelChange)="patchVariant(index, { sku: $event })"
                          [attr.aria-invalid]="fieldError('variants[' + index + '].sku') ? 'true' : null"
                          [attr.data-testid]="'variant-sku-' + index"
                        />
                        @if (fieldError('variants[' + index + '].sku'); as message) {
                          <span class="adm-field-error" [attr.data-testid]="'variant-sku-error-' + index">
                            {{ message }}
                          </span>
                        }
                      </label>

                      <label class="adm-field">
                        <span class="adm-label">Barcode (optional)</span>
                        <input
                          class="adm-input"
                          [name]="'barcode-' + index"
                          [ngModel]="row.barcode"
                          (ngModelChange)="patchVariant(index, { barcode: $event })"
                          [attr.data-testid]="'variant-barcode-' + index"
                        />
                      </label>

                      <label class="adm-field">
                        <span class="adm-label">Price for this size (optional)</span>
                        <input
                          class="adm-input"
                          [name]="'price-' + index"
                          type="number"
                          min="0.01"
                          step="0.01"
                          inputmode="decimal"
                          [ngModel]="row.priceOverride"
                          (ngModelChange)="patchVariant(index, { priceOverride: toNumber($event) })"
                          [attr.data-testid]="'variant-price-' + index"
                        />
                      </label>

                      <label class="adm-field">
                        <span class="adm-label">Original price for this size (optional)</span>
                        <input
                          class="adm-input"
                          [name]="'was-' + index"
                          type="number"
                          min="0.01"
                          step="0.01"
                          inputmode="decimal"
                          [ngModel]="row.compareAtPriceOverride"
                          (ngModelChange)="patchVariant(index, { compareAtPriceOverride: toNumber($event) })"
                          [attr.data-testid]="'variant-was-' + index"
                        />
                      </label>
                    </div>

                    <p class="adm-muted">
                      In stock: {{ row.onHand }} — change it on the Inventory screen.
                      @if (undeletable(row)) {
                        This size cannot be deleted; untick it and it stays here, switched off.
                      }
                    </p>

                    <div class="adm-actions">
                      <label class="adm-check">
                        <input
                          type="checkbox"
                          [checked]="row.isActive"
                          (change)="patchVariant(index, { isActive: !row.isActive })"
                          [attr.data-testid]="'variant-active-' + index"
                        />
                        <!--
                          Not "On sale". Sale is one of the four badges a product can carry, and a
                          size being offered has nothing to do with whether it is discounted — two
                          meanings for one phrase on the same screen.
                        -->
                        <span>Available to buy</span>
                      </label>

                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="index === 0 ? 'true' : null"
                        (click)="moveVariant(index, -1, $event)"
                        (keydown.alt.arrowup)="moveVariant(index, -1, $event)"
                        (keydown.alt.arrowdown)="moveVariant(index, 1, $event)"
                        [attr.data-testid]="'variant-up-' + index"
                      >
                        Move up<span class="sr-only"> — {{ row.colourName }} {{ row.sizeLabel }}</span>
                      </button>
                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="index === matrix().length - 1 ? 'true' : null"
                        (click)="moveVariant(index, 1, $event)"
                        (keydown.alt.arrowup)="moveVariant(index, -1, $event)"
                        (keydown.alt.arrowdown)="moveVariant(index, 1, $event)"
                        [attr.data-testid]="'variant-down-' + index"
                      >
                        Move down<span class="sr-only"> — {{ row.colourName }} {{ row.sizeLabel }}</span>
                      </button>
                    </div>
                  </li>
                }
              </ul>
            }
          </fieldset>

          <!-- 5. Photographs and the video (doc 11 P1 step 11) --------------------------------- -->
          <fieldset class="adm-panel" #mediaRoot>
            <legend class="adm-panel__title">Photographs and video</legend>
            <p class="adm-panel__hint">
              The first gallery photograph becomes the picture on the listing card. Every photograph needs
              alt text before the product can go on the shop. A video is optional: one MP4, at most 50 MB,
              with a poster image.
            </p>

            <div class="adm-fields">
              <label class="adm-field">
                <span class="adm-label">Add a photograph</span>
                <input
                  class="adm-input"
                  type="file"
                  [accept]="imageAccept"
                  (change)="uploadImage($event)"
                  #imageInput
                  data-testid="upload-image"
                />
              </label>

              <label class="adm-field">
                <span class="adm-label">Add the video (MP4)</span>
                <input
                  class="adm-input"
                  type="file"
                  [accept]="videoAccept"
                  (change)="chooseVideo($event)"
                  #videoInput
                  data-testid="upload-video"
                />
              </label>

              @if (pendingVideoName(); as videoName) {
                <label class="adm-field adm-field--wide">
                  <span class="adm-label">Poster image for {{ videoName }}</span>
                  <input
                    class="adm-input"
                    type="file"
                    [accept]="imageAccept"
                    (change)="uploadVideo($event)"
                    #posterInput
                    data-testid="upload-poster"
                  />
                  <span class="adm-panel__hint">
                    A video cannot be stored without one, and nothing on the server extracts a frame.
                  </span>
                </label>
              }
            </div>

            @if (mediaError(); as message) {
              <p class="adm-state adm-state--error" role="alert" data-testid="media-error">{{ message }}</p>
            }

            @if (media().length === 0) {
              <p class="adm-state" data-testid="no-media">No photographs yet.</p>
            } @else {
              <ul class="form__rows" data-testid="media-list">
                @for (row of media(); track row.assetPublicId + '/' + (row.colourName ?? ''); let index = $index) {
                  <li class="form__row" [attr.data-testid]="'media-' + index">
                    <div class="form__media-head">
                      @if (row.mediaRole === 'Video') {
                        <span class="adm-pill">Video</span>
                        @if (row.posterUrl) {
                          <img class="form__thumb" [src]="row.posterUrl" alt="" width="72" height="72" />
                        }
                      } @else if (row.url) {
                        <img class="form__thumb" [src]="row.url" alt="" width="72" height="72" />
                      }
                    </div>

                    <div class="adm-fields">
                      @if (row.mediaRole !== 'Video') {
                        <label class="adm-field">
                          <span class="adm-label">Role</span>
                          <select
                            class="adm-input"
                            [name]="'media-role-' + index"
                            [ngModel]="row.mediaRole"
                            (ngModelChange)="patchMedia(index, { mediaRole: toMediaRole($event) })"
                            [attr.data-testid]="'media-role-' + index"
                          >
                            <option [ngValue]="'Gallery'">Gallery photograph</option>
                            <option [ngValue]="'Hover'">Hover photograph</option>
                          </select>
                        </label>

                        <label class="adm-field">
                          <span class="adm-label">Only for colour</span>
                          <select
                            class="adm-input"
                            [name]="'media-colour-' + index"
                            [ngModel]="row.colourName"
                            (ngModelChange)="patchMedia(index, { colourName: $event || null })"
                            [attr.data-testid]="'media-colour-' + index"
                          >
                            <option [ngValue]="null">Every colour</option>
                            @for (colour of colours(); track $index) {
                              <option [ngValue]="colour.name">{{ colour.name }}</option>
                            }
                          </select>
                        </label>

                        <label class="adm-field adm-field--wide">
                          <span class="adm-label">Alt text</span>
                          <input
                            class="adm-input"
                            [name]="'media-alt-' + index"
                            [ngModel]="row.altTextOverride"
                            (ngModelChange)="patchMedia(index, { altTextOverride: $event })"
                            [attr.placeholder]="row.assetAltText ?? 'Describe the photograph'"
                            [attr.data-testid]="'media-alt-' + index"
                          />
                          <span class="adm-panel__hint">
                            What a customer using a screen reader hears instead of the picture.
                            @if (!row.altTextOverride && row.assetAltText) {
                              The library's own text ("{{ row.assetAltText }}") is used if you leave this
                              empty.
                            }
                          </span>
                        </label>
                      }
                    </div>

                    <div class="adm-actions">
                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="index === 0 ? 'true' : null"
                        (click)="moveMedia(index, -1, $event)"
                        (keydown.alt.arrowup)="moveMedia(index, -1, $event)"
                        (keydown.alt.arrowdown)="moveMedia(index, 1, $event)"
                        [attr.data-testid]="'media-up-' + index"
                      >
                        Move up
                      </button>
                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="index === media().length - 1 ? 'true' : null"
                        (click)="moveMedia(index, 1, $event)"
                        (keydown.alt.arrowup)="moveMedia(index, -1, $event)"
                        (keydown.alt.arrowdown)="moveMedia(index, 1, $event)"
                        [attr.data-testid]="'media-down-' + index"
                      >
                        Move down
                      </button>
                      <button
                        class="adm-btn--danger"
                        type="button"
                        (click)="removeMedia(index)"
                        [attr.data-testid]="'media-remove-' + index"
                      >
                        Remove
                      </button>
                    </div>
                  </li>
                }
              </ul>
            }
          </fieldset>

          <!-- 6. Details and merchandising (doc 11 P1 steps 6 and 12) -------------------------- -->
          <fieldset class="adm-panel">
            <legend class="adm-panel__title">Details</legend>
            <div class="adm-fields">
              <label class="adm-field adm-field--wide">
                <span class="adm-label">Short description</span>
                <textarea
                  class="adm-input"
                  name="shortDescription"
                  rows="2"
                  [ngModel]="form().shortDescription"
                  (ngModelChange)="patch({ shortDescription: $event })"
                  data-testid="field-short-description"
                ></textarea>
              </label>

              <label class="adm-field adm-field--wide">
                <span class="adm-label">Description</span>
                <textarea
                  class="adm-input"
                  name="description"
                  rows="5"
                  [ngModel]="form().description"
                  (ngModelChange)="patch({ description: $event })"
                  data-testid="field-description"
                ></textarea>
              </label>

              <label class="adm-field adm-field--wide">
                <span class="adm-label">Care instructions</span>
                <textarea
                  class="adm-input"
                  name="careInstructions"
                  rows="2"
                  [ngModel]="form().careInstructions"
                  (ngModelChange)="patch({ careInstructions: $event })"
                  data-testid="field-care"
                ></textarea>
              </label>

              <label class="adm-field">
                <span class="adm-label">Material</span>
                <select
                  class="adm-input"
                  name="materialId"
                  [ngModel]="form().materialId"
                  (ngModelChange)="patch({ materialId: $event === null || $event === '' ? null : +$event })"
                  data-testid="field-material"
                >
                  <option [ngValue]="null">Not set</option>
                  @for (option of options().materials; track option.id) {
                    <option [ngValue]="option.id">{{ option.name }}</option>
                  }
                </select>
              </label>

              <label class="adm-field">
                <span class="adm-label">Fit</span>
                <select
                  class="adm-input"
                  name="fitId"
                  [ngModel]="form().fitId"
                  (ngModelChange)="patch({ fitId: $event === null || $event === '' ? null : +$event })"
                  data-testid="field-fit"
                >
                  <option [ngValue]="null">Not set</option>
                  @for (option of options().fits; track option.id) {
                    <option [ngValue]="option.id">{{ option.name }}</option>
                  }
                </select>
              </label>

              <label class="adm-field">
                <span class="adm-label">Occasion</span>
                <select
                  class="adm-input"
                  name="occasionId"
                  [ngModel]="form().occasionId"
                  (ngModelChange)="patch({ occasionId: $event === null || $event === '' ? null : +$event })"
                  data-testid="field-occasion"
                >
                  <option [ngValue]="null">Not set</option>
                  @for (option of options().occasions; track option.id) {
                    <option [ngValue]="option.id">{{ option.name }}</option>
                  }
                </select>
              </label>

              <label class="adm-field">
                <span class="adm-label">Badge</span>
                <select
                  class="adm-input"
                  name="badgeOverride"
                  [ngModel]="form().badgeOverride"
                  (ngModelChange)="patch({ badgeOverride: toBadge($event) })"
                  data-testid="field-badge"
                >
                  <option [ngValue]="null">Work it out automatically</option>
                  @for (badge of badges; track badge.value) {
                    <option [ngValue]="badge.value">{{ badge.label }}</option>
                  }
                </select>
                <span class="adm-panel__hint">
                  One badge is shown on a card. Leave this alone and the shop picks Sale, Low Stock, Best
                  Seller or New for you.
                </span>
              </label>

              <label class="adm-field">
                <span class="adm-label">Size guide</span>
                <select
                  class="adm-input"
                  name="sizeGuideId"
                  [ngModel]="form().sizeGuideId"
                  (ngModelChange)="patch({ sizeGuideId: $event === null || $event === '' ? null : +$event })"
                  data-testid="field-size-guide"
                >
                  <option [ngValue]="null">Not set</option>
                  @for (guide of options().sizeGuides; track guide.id) {
                    <option [ngValue]="guide.id">{{ guide.name }}</option>
                  }
                </select>
              </label>

              <label class="adm-field">
                <span class="adm-label">Recommended rank (optional)</span>
                <input
                  class="adm-input"
                  name="recommendedRank"
                  type="number"
                  min="0"
                  step="1"
                  inputmode="numeric"
                  [ngModel]="form().recommendedRank"
                  (ngModelChange)="patch({ recommendedRank: toNumber($event) })"
                  data-testid="field-recommended-rank"
                />
                <span class="adm-panel__hint">
                  Pins the product high in the Recommended order. Lowest number first; empty means
                  not pinned.
                </span>
              </label>
            </div>
          </fieldset>

          <!-- 7. Related products (PDP-06) ----------------------------------------------------- -->
          <fieldset class="adm-panel">
            <legend class="adm-panel__title">Related products</legend>
            <p class="adm-panel__hint">
              Your picks appear first on the product page; it tops the list up from the same category.
            </p>

            <div class="adm-fields">
              <label class="adm-field adm-field--wide">
                <span class="adm-label">Find a product</span>
                <input
                  class="adm-input"
                  name="relatedSearch"
                  type="search"
                  autocomplete="off"
                  [ngModel]="relatedTerm()"
                  (ngModelChange)="searchRelated($event)"
                  data-testid="related-search"
                />
              </label>
            </div>

            @if (relatedMatches().length > 0) {
              <ul class="form__rows" data-testid="related-matches">
                @for (match of relatedMatches(); track match.relatedProductId) {
                  <li class="form__row form__row--compact">
                    <span>{{ match.name }} <span class="adm-muted">({{ match.productCode }})</span></span>
                    <button
                      class="adm-btn"
                      type="button"
                      (click)="addRelated(match)"
                      [attr.data-testid]="'related-add-' + match.relatedProductId"
                    >
                      Add<span class="sr-only"> {{ match.name }}</span>
                    </button>
                  </li>
                }
              </ul>
            }

            @if (related().length > 0) {
              <ul class="form__rows" data-testid="related-list">
                @for (pick of related(); track pick.relatedProductId; let index = $index) {
                  <li class="form__row form__row--compact" [attr.data-testid]="'related-' + index">
                    <span>
                      {{ pick.name }}
                      @if (!pick.isPublished) {
                        <span class="adm-pill adm-pill--hidden">Not published</span>
                      }
                    </span>
                    <div class="adm-actions">
                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="index === 0 ? 'true' : null"
                        (click)="moveRelated(index, -1, $event)"
                        (keydown.alt.arrowup)="moveRelated(index, -1, $event)"
                        (keydown.alt.arrowdown)="moveRelated(index, 1, $event)"
                        [attr.data-testid]="'related-up-' + index"
                      >
                        Move up
                      </button>
                      <button
                        class="adm-btn"
                        type="button"
                        [attr.aria-disabled]="index === related().length - 1 ? 'true' : null"
                        (click)="moveRelated(index, 1, $event)"
                        (keydown.alt.arrowup)="moveRelated(index, -1, $event)"
                        (keydown.alt.arrowdown)="moveRelated(index, 1, $event)"
                        [attr.data-testid]="'related-down-' + index"
                      >
                        Move down
                      </button>
                      <button
                        class="adm-btn--danger"
                        type="button"
                        (click)="removeRelated(index)"
                        [attr.data-testid]="'related-remove-' + index"
                      >
                        Remove
                      </button>
                    </div>
                  </li>
                }
              </ul>
            }
          </fieldset>

          <!-- 8. SEO (ADM-10) ------------------------------------------------------------------ -->
          <fieldset class="adm-panel">
            <legend class="adm-panel__title">Search engines</legend>
            <div class="adm-fields">
              <label class="adm-field adm-field--wide">
                <span class="adm-label">Page title</span>
                <input
                  class="adm-input"
                  name="seoTitle"
                  maxlength="160"
                  [ngModel]="form().seoTitle"
                  (ngModelChange)="patch({ seoTitle: $event })"
                  data-testid="field-seo-title"
                />
              </label>

              <label class="adm-field adm-field--wide">
                <span class="adm-label">Page description</span>
                <textarea
                  class="adm-input"
                  name="seoDescription"
                  rows="2"
                  maxlength="320"
                  [ngModel]="form().seoDescription"
                  (ngModelChange)="patch({ seoDescription: $event })"
                  data-testid="field-seo-description"
                ></textarea>
              </label>
            </div>
          </fieldset>

          <!-- 9. Save, publish, remove --------------------------------------------------------- -->
          <div class="form__actions adm-panel">
            @if (publishBlockers().length > 0) {
              <div class="adm-state adm-state--warning" data-testid="publish-blockers">
                <p>Before this product can go on the shop:</p>
                <ul>
                  @for (blocker of publishBlockers(); track blocker) {
                    <li>{{ blocker }}</li>
                  }
                </ul>
              </div>
            }

            <div class="adm-actions">
              <button
                class="adm-btn--primary"
                type="submit"
                [attr.aria-disabled]="busy() ? 'true' : null"
                data-testid="save"
              >
                {{ busy() ? 'Saving…' : productId() === null ? 'Create product' : 'Save changes' }}
              </button>

              @if (saved(); as product) {
                <button
                  class="adm-btn"
                  type="button"
                  [attr.aria-disabled]="busy() || (!product.isPublished && !product.canBePublished) ? 'true' : null"
                  (click)="togglePublished()"
                  data-testid="toggle-published"
                >
                  {{ product.isPublished ? 'Take off the shop' : 'Put on the shop' }}
                </button>

                <button
                  class="adm-btn--danger"
                  type="button"
                  [attr.aria-disabled]="busy() ? 'true' : null"
                  (click)="confirmRemove()"
                  data-testid="remove"
                >
                  Remove this product
                </button>
              }
            </div>

            @if (removing()) {
              <div class="adm-state adm-state--warning" role="alert" data-testid="confirm-remove">
                <p>
                  Remove {{ form().name }}? It leaves the shop at once. Its sizes, photographs and order
                  history are kept, but nothing brings it back from this screen.
                </p>
                <div class="adm-actions">
                  <button class="adm-btn--danger" type="button" (click)="remove()" data-testid="confirm-remove-yes">
                    Yes, remove it
                  </button>
                  <button class="adm-btn" type="button" (click)="cancelRemove()" data-testid="confirm-remove-no">
                    Keep it
                  </button>
                </div>
              </div>
            }
          </div>
        </form>
      }
    </section>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-2) var(--gutter) var(--space-4);
    }

    .form__head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: center;
      justify-content: space-between;
    }

    .form__title {
      font-size: var(--text-h2);
      font-weight: var(--weight-regular);
    }

    .form__body {
      display: grid;
      gap: var(--space-2);
    }

    .form__subgroup {
      display: grid;
      gap: var(--space-1);
      padding-top: var(--space-1);
      border: 0;
    }

    .form__checks {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-2);
    }

    /* A tick is 44 px in both directions, not only in height.
       .adm-check in the shared stylesheet sets a min-height of one tap target and nothing about width, which is
       right for a long label ("Show in the mega menu") and wrong for these: the size ticks are labelled
       "S", "M", "L" and measured 36–41 px wide at 320 px. The browser suite caught it; jsdom could not,
       because it has no layout. WCAG 2.2 AA asks for 44 px, which means the smaller of the two edges. */
    .form__checks .adm-check {
      min-width: var(--tap);
      justify-content: center;
      padding-inline: var(--space-0-5);
    }

    .form__slug {
      display: flex;
      align-items: center;
      gap: var(--space-0-5);
    }

    .form__slug .adm-input {
      flex: 1 1 auto;
      min-width: 0;
    }

    .form__rows {
      display: grid;
      gap: var(--space-1);
      list-style: none;
    }

    /* One row per colour / variant / photograph, stacked at 320 px. A variant matrix drawn as a table
       would scroll sideways on a phone; this is the same data as cards, which is why the brief's hard
       case is met by not having a table at all. */
    .form__row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-1-5);
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }

    .form__row--compact {
      grid-template-columns: minmax(0, 1fr);
      align-items: center;
    }

    .form__row-title {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }

    .form__media-head {
      display: flex;
      gap: var(--space-1);
      align-items: center;
    }

    .form__thumb {
      width: 72px;
      height: 72px;
      object-fit: cover;
      border: 1px solid var(--color-border);
    }

    /* Not sticky. It was, and an opaque panel stuck to the bottom of the viewport covered whatever
       was behind it: at 320 px, scrolling the form put the save bar over field-care, every media
       row's alt text box and the related-product buttons, 44 px of each — their whole height, so
       they could not be clicked at all until the admin nudged the page. axe reported the same
       thing as a serious target-size violation against whichever control sat at the viewport edge,
       which is why the finding moved as the content changed and why the 320 px scan passed while
       the 1280 px one did not. No other admin screen has a sticky bar; this one no longer does. */
    .form__actions {
      display: grid;
      gap: var(--space-1);
    }

    /* The visual unavailable state, which aria-disabled has to draw because the disabled attribute is
       deliberately not used — see the component's docblock. */
    .form [aria-disabled='true'] {
      opacity: 0.55;
      cursor: not-allowed;
    }

    @media (min-width: 768px) {
      .form__row--compact {
        grid-template-columns: minmax(0, 1fr) auto;
      }
    }
  `,
})
export class ProductFormPage {
  private readonly api = inject(AdminProductApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly imageAccept = IMAGE_ACCEPT;
  protected readonly videoAccept = VIDEO_ACCEPT;
  protected readonly badges = BADGES;

  private readonly formRoot = viewChild<ElementRef<HTMLElement>>('formRoot');
  private readonly imageInput = viewChild<ElementRef<HTMLInputElement>>('imageInput');
  private readonly videoInput = viewChild<ElementRef<HTMLInputElement>>('videoInput');
  private readonly posterInput = viewChild<ElementRef<HTMLInputElement>>('posterInput');

  private readonly loadedOptions = signal<ProductFormOptions | null>(null);
  private readonly loadedProduct = signal<AdminProductDetail | null>(null);
  private readonly scalars = signal<Scalars>(EMPTY);
  private readonly colourRows = signal<readonly ColourValues[]>([]);
  private readonly matrixRows = signal<readonly MatrixRow[]>([]);
  private readonly mediaRows = signal<readonly MediaRow[]>([]);
  private readonly relatedRows = signal<readonly AdminRelatedProduct[]>([]);
  private readonly scale = signal<string | null>(null);
  private readonly sizeIds = signal<readonly number[]>([]);
  private readonly isLoading = signal(true);
  private readonly isBusy = signal(false);
  private readonly problem = signal<CatalogFailure | null>(null);
  private readonly mediaProblem = signal<string | null>(null);
  private readonly said = signal('');
  private readonly isRemoving = signal(false);
  private readonly videoFile = signal<File | null>(null);
  private readonly relatedSearchTerm = signal('');
  private readonly relatedResults = signal<readonly AdminRelatedProduct[]>([]);
  private readonly focusAfterRender = signal<string | null>(null);

  /**
   * Whether the gallery has been touched since the product was read.
   *
   * **This is the W1-12 defect's fix on the client side.** The API leaves a product's photographs alone
   * when the body carries no `media` field and clears them when it carries an empty one. So the form only
   * sends the field once the admin has actually changed something about the media — which means a save of
   * the name, the price or the SEO fields cannot clear a gallery even if this component had a bug that
   * emptied `mediaRows`.
   */
  private mediaEdited = false;

  protected readonly form = this.scalars.asReadonly();
  protected readonly colours = this.colourRows.asReadonly();
  protected readonly matrix = this.matrixRows.asReadonly();
  protected readonly media = this.mediaRows.asReadonly();
  protected readonly related = this.relatedRows.asReadonly();
  protected readonly sizeScale = this.scale.asReadonly();
  protected readonly chosenSizeIds = this.sizeIds.asReadonly();
  protected readonly loading = this.isLoading.asReadonly();
  protected readonly busy = this.isBusy.asReadonly();
  protected readonly failure = this.problem.asReadonly();
  protected readonly mediaError = this.mediaProblem.asReadonly();
  protected readonly announcement = this.said.asReadonly();
  protected readonly removing = this.isRemoving.asReadonly();
  protected readonly saved = this.loadedProduct.asReadonly();
  protected readonly relatedTerm = this.relatedSearchTerm.asReadonly();
  protected readonly relatedMatches = this.relatedResults.asReadonly();
  protected readonly pendingVideoName = computed(() => this.videoFile()?.name ?? null);

  protected readonly productId = computed(() => {
    const raw = this.route.snapshot.paramMap.get('id');
    const parsed = raw === null ? Number.NaN : Number(raw);

    return Number.isInteger(parsed) ? parsed : null;
  });

  protected readonly options = computed<ProductFormOptions>(
    () =>
      this.loadedOptions() ?? {
        departments: [],
        categories: [],
        ageGroups: [],
        materials: [],
        fits: [],
        occasions: [],
        colourFamilies: [],
        sizes: [],
        sizeGuides: [],
      },
  );

  protected readonly hasBeenPublished = computed(() => this.loadedProduct()?.firstPublishedAtUtc != null);

  protected readonly publishBlockers = computed(() => this.loadedProduct()?.publishBlockers ?? []);

  /** The categories of the chosen department. A product's main category has to be one of its own. */
  protected readonly categoriesForDepartment = computed(() => {
    const departmentId = this.scalars().departmentId;

    return departmentId === null
      ? []
      : this.options().categories.filter((category) => category.departmentId === departmentId);
  });

  protected readonly extraCategoryChoices = computed(() =>
    this.categoriesForDepartment().filter((category) => category.id !== this.scalars().primaryCategoryId),
  );

  /**
   * Whether the department is Kids, by slug rather than by id — the same rule, and the same reasoning,
   * as `CatalogDepartments.KidsSlug` in the domain. Resolved from the department list rather than
   * hard-coded here, so the API stays the one place that enforces it.
   */
  protected readonly needsAgeGroup = computed(() => {
    const departmentId = this.scalars().departmentId;
    const name = this.options().departments.find((department) => department.id === departmentId)?.name;

    return name?.trim().toLowerCase() === 'kids';
  });

  protected readonly scales = computed(() => [
    ...new Set(this.options().sizes.map((size) => size.scaleName)),
  ]);

  protected readonly sizesInScale = computed(() => {
    const scale = this.scale();

    return scale === null ? [] : this.options().sizes.filter((size) => size.scaleName === scale);
  });

  /** The discount the shop would show, by doc 08 §6 step 4's own arithmetic. */
  protected readonly discountPercent = computed(() => {
    const { basePrice, compareAtPrice } = this.scalars();

    if (basePrice === null || compareAtPrice === null || compareAtPrice <= basePrice) {
      return null;
    }

    return Math.floor(((compareAtPrice - basePrice) / compareAtPrice) * 100);
  });

  /** The 409 the slug rule produced, when that is what the current failure is. */
  protected readonly slugConfirmation = computed(() => {
    const failure = this.problem();

    if (failure?.code !== 'slug_change_needs_confirmation') {
      return null;
    }

    return {
      currentSlug: String(failure.details?.['currentSlug'] ?? ''),
      requestedSlug: String(failure.details?.['requestedSlug'] ?? ''),
    };
  });

  private readonly sizesById = computed(
    () => new Map(this.options().sizes.map((size) => [size.id, size])),
  );

  constructor() {
    afterEveryRender(() => {
      const target = this.focusAfterRender();
      const host = this.formRoot()?.nativeElement;
      if (!target || !host) {
        return;
      }

      this.focusAfterRender.set(null);

      // A move relocates the row's DOM node rather than rebuilding it, and re-inserting an element
      // elsewhere in the document blurs it in Chromium — so the focus has to be restored *after* the
      // render, not on a microtask. (The same lesson `category-tree` records.)
      host.querySelector<HTMLButtonElement>(`[data-testid="${target}"]`)?.focus();
    });

    this.api.formOptions().subscribe({
      next: (options) => {
        this.loadedOptions.set(options);
        this.afterOptions();
      },
      error: (error: unknown) => {
        this.problem.set(toCatalogFailure(error));
        this.isLoading.set(false);
      },
    });

    const productId = this.productId();
    if (productId === null) {
      this.isLoading.set(false);
    } else {
      this.read(productId, { takeScreenDown: true });
    }
  }

  // ---- scalar fields --------------------------------------------------------------------------

  protected patch(change: Partial<Scalars>): void {
    this.scalars.update((current) => ({ ...current, ...change }));
  }

  /**
   * Suggests a slug while the product has never been published and its slug still matches its old name.
   *
   * It stops the moment the product has been published: an address a customer may have bookmarked is not
   * something a typing correction should move, and the API would refuse the change without a confirmation
   * anyway.
   */
  protected nameChanged(name: string): void {
    const current = this.scalars();
    const shouldSuggest = !this.hasBeenPublished() && current.slug === suggestSlug(current.name);

    this.patch(shouldSuggest ? { name, slug: suggestSlug(name) } : { name });
  }

  /** Changing the department invalidates the category choices, so they are cleared rather than left wrong. */
  protected departmentChanged(departmentId: number | null): void {
    this.patch({ departmentId, primaryCategoryId: null, extraCategoryIds: [], ageGroupId: null });
  }

  protected toggleExtraCategory(categoryId: number): void {
    this.scalars.update((current) => ({
      ...current,
      extraCategoryIds: current.extraCategoryIds.includes(categoryId)
        ? current.extraCategoryIds.filter((id) => id !== categoryId)
        : [...current.extraCategoryIds, categoryId],
    }));
  }

  /** The `<select>` gives a number; the model wants the narrowed union the API accepts. */
  protected toMediaRole(value: unknown): ProductMediaRole {
    // Only the two a photograph can switch between. The video's row has no role select at all, so
    // Video can never arrive here, and narrowing anything unexpected to Gallery keeps a stale option
    // from sending a role the API would refuse.
    return value === MEDIA_ROLE.hover ? MEDIA_ROLE.hover : MEDIA_ROLE.gallery;
  }

  /** <inheritdoc cref="toMediaRole" /> — the four assignable badges; LowStock is computed, never stored. */
  protected toBadge(value: unknown): ProductBadge | null {
    return BADGES.find((badge) => badge.value === value)?.value ?? null;
  }

  /**
   * Whether the API would refuse to delete this row. Read from the one function
   * {@link buildMatrix} applies, so the explanation on screen and the rows the matrix keeps cannot
   * disagree.
   */
  protected undeletable(row: MatrixRow): boolean {
    return isUndeletable(row);
  }

  protected toNumber(value: unknown): number | null {
    if (value === null || value === undefined || value === '') {
      return null;
    }

    const parsed = Number(value);

    return Number.isFinite(parsed) ? parsed : null;
  }

  // ---- colours --------------------------------------------------------------------------------

  protected addColour(): void {
    this.colourRows.update((current) =>
      withSortOrder([
        ...current,
        { id: null, name: '', colourFamilyId: null, swatchHex: null, swatchAssetPublicId: null, sortOrder: current.length },
      ]),
    );

    this.said.set('Colour added. Give it a name and a colour family.');
  }

  protected patchColour(index: number, change: Partial<ColourValues>): void {
    const before = this.colourRows()[index];
    if (!before) {
      return;
    }

    this.colourRows.update((current) =>
      current.map((colour, position) => (position === index ? { ...colour, ...change } : colour)),
    );

    // A rename has to follow through to the variants and the gallery rows that name this colourway, or
    // the save would be refused with "that is not one of this product's colours" for a colour that is
    // plainly on screen.
    if (change.name !== undefined && change.name !== before.name) {
      this.matrixRows.update((current) =>
        current.map((row) => (row.colourName === before.name ? { ...row, colourName: change.name! } : row)),
      );

      this.mediaRows.update((current) =>
        current.map((row) => (row.colourName === before.name ? { ...row, colourName: change.name! } : row)),
      );
    }

    this.rebuildMatrix();
  }

  protected removeColour(index: number): void {
    const colour = this.colourRows()[index];
    if (!colour) {
      return;
    }

    this.colourRows.update((current) => withSortOrder(current.filter((_, position) => position !== index)));
    this.matrixRows.update((current) => withSortOrder(current.filter((row) => row.colourName !== colour.name)));

    const galleryForColour = this.mediaRows().some((row) => row.colourName === colour.name);
    if (galleryForColour) {
      this.mediaRows.update((current) =>
        withSortOrder(current.filter((row) => row.colourName !== colour.name)),
      );
      this.mediaEdited = true;
    }

    this.said.set(`${colour.name || 'The colour'} removed, with its sizes and its own photographs.`);
    this.rebuildMatrix();
  }

  protected moveColour(index: number, delta: number, event?: Event): void {
    const moved = moveInArray(this.colourRows(), index, delta);
    if (!moved) {
      return;
    }

    event?.preventDefault();
    this.colourRows.set(withSortOrder(moved));
    this.announceMove('Colour', index, delta, moved.length);
    this.focusAfterRender.set(this.movedTestId('colour', index, delta, event));
  }

  // ---- sizes and variants ---------------------------------------------------------------------

  protected scaleChanged(scale: string | null): void {
    this.scale.set(scale === '' ? null : scale);

    // The ticks belong to the scale, so switching scale clears them — but the matrix is rebuilt rather
    // than emptied, so an ordered variant on the old scale stays visible and switched off.
    this.sizeIds.set([]);
    this.rebuildMatrix();
  }

  protected toggleSize(sizeId: number): void {
    this.sizeIds.update((current) =>
      current.includes(sizeId) ? current.filter((id) => id !== sizeId) : [...current, sizeId],
    );

    this.rebuildMatrix();
  }

  protected patchVariant(index: number, change: Partial<MatrixRow>): void {
    this.matrixRows.update((current) =>
      current.map((row, position) => (position === index ? { ...row, ...change } : row)),
    );
  }

  protected moveVariant(index: number, delta: number, event?: Event): void {
    const moved = moveInArray(this.matrixRows(), index, delta);
    if (!moved) {
      return;
    }

    event?.preventDefault();
    this.matrixRows.set(withSortOrder(moved));
    this.announceMove('Size', index, delta, moved.length);
    this.focusAfterRender.set(this.movedTestId('variant', index, delta, event));
  }

  // ---- media ----------------------------------------------------------------------------------

  protected uploadImage(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    if (file.size > MAX_IMAGE_BYTES) {
      this.mediaProblem.set('That photograph is larger than 8 MB. Save it smaller and try again.');
      this.clearFileInput(this.imageInput());
      return;
    }

    this.mediaProblem.set(null);
    this.isBusy.set(true);
    this.said.set('Uploading the photograph…');

    // Uploaded with no alt text on purpose. Pre-filling it with the product's name defeated
    // doc 11 §7 G-01's third rule outright: the checklist accepts the library asset's own alt
    // text, so every photograph arrived already "described" and the rule could never block a
    // publish. A shopper using a screen reader would have heard the product's name read out
    // once for each of its six pictures.
    this.api.uploadImage(file, null).subscribe({
      next: (asset) => {
        this.isBusy.set(false);
        this.said.set('Photograph added. Describe it in the alt text box.');
        this.clearFileInput(this.imageInput());
        this.mediaEdited = true;

        this.mediaRows.update((current) =>
          withSortOrder([
            ...current,
            {
              assetPublicId: asset.publicId,
              colourName: null,
              mediaRole: MEDIA_ROLE.gallery,
              sortOrder: current.length,
              altTextOverride: null,
              url: asset.url,
              posterUrl: null,
              mediaType: asset.mediaType,
              assetAltText: asset.altText,
            },
          ]),
        );
      },
      error: (error: unknown) => {
        this.isBusy.set(false);
        this.said.set('');
        this.mediaProblem.set(toCatalogFailure(error).message);
        this.clearFileInput(this.imageInput());
      },
    });
  }

  /**
   * Holds the chosen video until its poster arrives.
   *
   * The library cannot store a video without a poster (doc 08 §3.3's check constraint) and nothing on the
   * server extracts a frame, so the form asks for both and sends them in one request.
   */
  protected chooseVideo(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;

    if (file && file.size > MAX_VIDEO_BYTES) {
      this.mediaProblem.set(
        'That video is larger than 50 MB, which is the most the library stores. Export it smaller.',
      );
      this.clearFileInput(this.videoInput());
      this.videoFile.set(null);
      return;
    }

    this.mediaProblem.set(null);
    this.videoFile.set(file);

    if (file) {
      this.said.set('Now choose a poster image for the video.');
    }
  }

  protected uploadVideo(event: Event): void {
    const video = this.videoFile();
    const poster = (event.target as HTMLInputElement).files?.[0];

    if (!video || !poster) {
      return;
    }

    this.mediaProblem.set(null);
    this.isBusy.set(true);
    this.said.set('Uploading the video…');

    // As with a photograph, and for the same reason. The checklist excludes the video itself (it
    // is not an image), but its poster is an image, and a poster "described" as the product's
    // own name is the same empty gesture.
    this.api.uploadVideo(video, poster, null).subscribe({
      next: (asset) => {
        this.isBusy.set(false);
        this.said.set('Video added.');
        this.videoFile.set(null);
        this.clearFileInput(this.videoInput());
        this.clearFileInput(this.posterInput());
        this.mediaEdited = true;

        this.mediaRows.update((current) =>
          withSortOrder([
            // One video per product: a new one replaces the old row rather than being refused on save.
            ...current.filter((row) => row.mediaRole !== MEDIA_ROLE.video),
            {
              assetPublicId: asset.publicId,
              colourName: null,
              mediaRole: MEDIA_ROLE.video,
              sortOrder: current.length,
              altTextOverride: null,
              url: asset.url,
              posterUrl: asset.posterUrl,
              mediaType: asset.mediaType,
              assetAltText: asset.altText,
            },
          ]),
        );
      },
      error: (error: unknown) => {
        this.isBusy.set(false);
        this.said.set('');
        this.mediaProblem.set(toCatalogFailure(error).message);
        this.clearFileInput(this.posterInput());
      },
    });
  }

  protected patchMedia(index: number, change: Partial<MediaRow>): void {
    this.mediaEdited = true;
    this.mediaRows.update((current) =>
      current.map((row, position) => (position === index ? { ...row, ...change } : row)),
    );
  }

  protected removeMedia(index: number): void {
    this.mediaEdited = true;
    this.mediaRows.update((current) => withSortOrder(current.filter((_, position) => position !== index)));
    this.said.set('Photograph removed from this product. It stays in the media library.');
  }

  protected moveMedia(index: number, delta: number, event?: Event): void {
    const moved = moveInArray(this.mediaRows(), index, delta);
    if (!moved) {
      return;
    }

    event?.preventDefault();
    this.mediaEdited = true;
    this.mediaRows.set(withSortOrder(moved));
    this.announceMove('Photograph', index, delta, moved.length);
    this.focusAfterRender.set(this.movedTestId('media', index, delta, event));
  }

  // ---- related --------------------------------------------------------------------------------

  protected searchRelated(term: string): void {
    this.relatedSearchTerm.set(term);

    if (term.trim().length < 2) {
      this.relatedResults.set([]);
      return;
    }

    this.api.searchForRelated(term.trim(), this.productId()).subscribe({
      next: (matches) => {
        const already = new Set(this.relatedRows().map((pick) => pick.relatedProductId));
        this.relatedResults.set(matches.filter((match) => !already.has(match.relatedProductId)));
      },
      error: () => this.relatedResults.set([]),
    });
  }

  protected addRelated(match: AdminRelatedProduct): void {
    this.relatedRows.update((current) => withSortOrder([...current, { ...match, sortOrder: current.length }]));
    this.relatedResults.update((current) =>
      current.filter((candidate) => candidate.relatedProductId !== match.relatedProductId),
    );

    this.said.set(`${match.name} added to the related products.`);
  }

  protected removeRelated(index: number): void {
    this.relatedRows.update((current) => withSortOrder(current.filter((_, position) => position !== index)));
  }

  protected moveRelated(index: number, delta: number, event?: Event): void {
    const moved = moveInArray(this.relatedRows(), index, delta);
    if (!moved) {
      return;
    }

    event?.preventDefault();
    this.relatedRows.set(withSortOrder(moved));
    this.announceMove('Related product', index, delta, moved.length);
    this.focusAfterRender.set(this.movedTestId('related', index, delta, event));
  }

  // ---- save, publish, remove ------------------------------------------------------------------

  /**
   * Saves the form.
   *
   * The guard is here rather than on the button's `disabled` attribute: disabling the control that has
   * focus moves focus to `<body>`, which is the ADM-22 defect. The live region says "Saving…" on the way
   * in and the outcome on the way out, so a second identical save still announces twice — a `role="status"`
   * region announces a *change*, and setting it to the text it already holds is not one.
   */
  protected save(confirmSlugChange: boolean): void {
    if (this.isBusy()) {
      return;
    }

    const values = this.toValues(confirmSlugChange);

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set('Saving…');

    const productId = this.productId();
    const existing = this.loadedProduct();

    if (productId === null || existing === null) {
      this.api.create(values).subscribe({
        next: (created) => {
          this.isBusy.set(false);
          this.said.set('Product created. It is not on the shop yet.');
          this.mediaEdited = false;

          // Navigating is right here and only here: the product did not have a URL of its own until now,
          // and the admin has to be on it to publish it. Nothing is being saved over.
          void this.router.navigate(['/products', created.id]);
        },
        error: (error: unknown) => this.afterFailure(error),
      });

      return;
    }

    this.api.update(productId, values, existing.rowVersion).subscribe({
      next: () => {
        this.isBusy.set(false);
        this.said.set('Changes saved.');
        this.mediaEdited = false;

        // refresh(), not read(…, takeScreenDown: true): taking the screen down would destroy the control
        // the admin pressed and drop their focus to <body>.
        this.read(productId, { takeScreenDown: false });
      },
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  protected cancelSlugChange(): void {
    const slug = this.slugConfirmation()?.currentSlug;
    if (slug) {
      this.patch({ slug });
    }

    this.problem.set(null);
    this.said.set('The web address is unchanged.');
  }

  protected togglePublished(): void {
    const product = this.loadedProduct();
    if (!product || this.isBusy()) {
      return;
    }

    if (!product.isPublished && !product.canBePublished) {
      return;
    }

    const next = !product.isPublished;

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(next ? 'Putting the product on the shop…' : 'Taking the product off the shop…');

    this.api.setPublished(product.id, next, product.rowVersion).subscribe({
      next: () => {
        this.isBusy.set(false);
        this.said.set(next ? 'The product is on the shop.' : 'The product is no longer on the shop.');
        this.read(product.id, { takeScreenDown: false });
      },
      error: (error: unknown) => this.afterFailure(error),
    });
  }

  protected confirmRemove(): void {
    this.isRemoving.set(true);
  }

  protected cancelRemove(): void {
    this.isRemoving.set(false);
  }

  protected remove(): void {
    const product = this.loadedProduct();
    if (!product || this.isBusy()) {
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set('Removing the product…');

    this.api.remove(product.id, product.rowVersion).subscribe({
      next: () => {
        this.isBusy.set(false);
        this.isRemoving.set(false);
        void this.router.navigate(['/products']);
      },
      error: (error: unknown) => {
        this.isRemoving.set(false);
        this.afterFailure(error);
      },
    });
  }

  protected fieldError(field: string): string | null {
    return this.problem()?.fieldErrors?.[field]?.[0] ?? null;
  }

  // ---- reading --------------------------------------------------------------------------------

  private read(productId: number, { takeScreenDown }: { takeScreenDown: boolean }): void {
    if (takeScreenDown) {
      this.isLoading.set(true);
    }

    this.api.get(productId).subscribe({
      next: (product) => {
        this.loadedProduct.set(product);
        this.apply(product);
        this.isLoading.set(false);
      },
      error: (error: unknown) => {
        this.problem.set(toCatalogFailure(error));
        this.isLoading.set(false);
      },
    });
  }

  private apply(product: AdminProductDetail): void {
    this.scalars.set({
      productCode: product.productCode,
      name: product.name,
      slug: product.slug,
      departmentId: product.departmentId,
      primaryCategoryId: product.primaryCategoryId,
      extraCategoryIds: [...product.extraCategoryIds],
      ageGroupId: product.ageGroupId,
      shortDescription: product.shortDescription ?? '',
      description: product.description ?? '',
      careInstructions: product.careInstructions ?? '',
      materialId: product.materialId,
      fitId: product.fitId,
      occasionId: product.occasionId,
      basePrice: product.basePrice,
      compareAtPrice: product.compareAtPrice,
      badgeOverride: product.badgeOverride,
      recommendedRank: product.recommendedRank,
      sizeGuideId: product.sizeGuideId,
      seoTitle: product.seoTitle ?? '',
      seoDescription: product.seoDescription ?? '',
    });

    this.colourRows.set(toColourValues(product.colours));

    const rows = toMatrixRows(product.variants, this.sizesById());
    this.matrixRows.set(rows);
    this.scale.set(scaleIn(rows));
    this.sizeIds.set(sizeIdsIn(rows));

    this.mediaRows.set(
      product.media.map((row, index) => ({
        assetPublicId: row.assetPublicId,
        colourName: row.colourName,
        mediaRole: row.mediaRole,
        sortOrder: index,
        altTextOverride: row.altTextOverride,
        url: row.url,
        posterUrl: row.posterUrl,
        mediaType: row.mediaType,
        assetAltText: row.assetAltText,
      })),
    );

    this.relatedRows.set([...product.relatedProducts]);
    this.mediaEdited = false;
  }

  /** Once the option lists arrive, the saved variants can be labelled with their real sizes. */
  private afterOptions(): void {
    const rows = this.matrixRows();
    if (rows.length === 0) {
      return;
    }

    const sizes = this.sizesById();
    this.matrixRows.set(
      rows.map((row) => ({
        ...row,
        sizeLabel: sizes.get(row.sizeId)?.label ?? row.sizeLabel,
        sizeScaleName: sizes.get(row.sizeId)?.scaleName ?? row.sizeScaleName,
      })),
    );

    this.scale.set(scaleIn(this.matrixRows()));
  }

  private rebuildMatrix(): void {
    this.matrixRows.set(
      buildMatrix(
        this.scalars().productCode,
        this.colourRows(),
        this.sizeIds(),
        this.sizesById(),
        this.matrixRows(),
      ),
    );
  }

  /**
   * The body to send.
   *
   * **`media` is only included when the gallery has been touched.** See `mediaEdited`: the API leaves a
   * product's photographs alone when the field is absent, and `JSON.stringify` drops an `undefined`
   * property — so a save of the name or the price cannot clear the gallery. That is the W1-12 defect made
   * impossible from this end as well as from the server's.
   */
  private toValues(confirmSlugChange: boolean): ProductFormValues {
    const scalars = this.scalars();

    return {
      productCode: scalars.productCode,
      name: scalars.name,
      slug: scalars.slug,
      departmentId: scalars.departmentId,
      primaryCategoryId: scalars.primaryCategoryId,
      extraCategoryIds: scalars.extraCategoryIds,
      ageGroupId: scalars.ageGroupId,
      shortDescription: blankToNull(scalars.shortDescription),
      description: blankToNull(scalars.description),
      careInstructions: blankToNull(scalars.careInstructions),
      materialId: scalars.materialId,
      fitId: scalars.fitId,
      occasionId: scalars.occasionId,
      basePrice: scalars.basePrice,
      compareAtPrice: scalars.compareAtPrice,
      badgeOverride: scalars.badgeOverride,
      recommendedRank: scalars.recommendedRank,
      sizeGuideId: scalars.sizeGuideId,
      seoTitle: blankToNull(scalars.seoTitle),
      seoDescription: blankToNull(scalars.seoDescription),
      colours: this.colourRows().map((colour, index) => ({
        id: colour.id,
        name: colour.name,
        colourFamilyId: colour.colourFamilyId,
        swatchHex: blankToNull(colour.swatchHex ?? ''),
        swatchAssetPublicId: colour.swatchAssetPublicId,
        sortOrder: index,
      })),
      variants: this.matrixRows().map((row, index) => ({
        id: row.id,
        colourName: row.colourName,
        sizeId: row.sizeId,
        sku: row.sku,
        barcode: blankToNull(row.barcode ?? ''),
        priceOverride: row.priceOverride,
        compareAtPriceOverride: row.compareAtPriceOverride,
        isActive: row.isActive,
        sortOrder: index,
      })),
      media: this.mediaEdited
        ? this.mediaRows().map((row, index) => ({
            assetPublicId: row.assetPublicId,
            colourName: row.colourName,
            mediaRole: row.mediaRole,
            sortOrder: index,
            altTextOverride: blankToNull(row.altTextOverride ?? ''),
          }))
        : undefined,
      relatedProducts: this.relatedRows().map((pick, index) => ({
        relatedProductId: pick.relatedProductId,
        sortOrder: index,
      })),
      confirmSlugChange,
    };
  }

  private afterFailure(error: unknown): void {
    this.isBusy.set(false);
    this.said.set('');
    this.problem.set(toCatalogFailure(error));
  }

  private announceMove(noun: string, index: number, delta: number, total: number): void {
    const position = index + delta + 1;
    this.said.set(`${noun} moved ${delta < 0 ? 'up' : 'down'} to position ${position} of ${total}.`);
  }

  /**
   * Which control to focus once the moved row has been drawn: the one the admin actually pressed, by
   * preference, because Alt + an arrow works from either button of a pair and jumping focus across the
   * pair mid-keystroke is disorientating.
   */
  private movedTestId(prefix: string, index: number, delta: number, event?: Event): string {
    const pressed = (event?.currentTarget as HTMLElement | null)?.getAttribute('data-testid');
    const direction = delta < 0 ? 'up' : 'down';
    const newIndex = index + delta;

    if (pressed?.startsWith(`${prefix}-up-`)) {
      return `${prefix}-up-${newIndex}`;
    }

    if (pressed?.startsWith(`${prefix}-down-`)) {
      return `${prefix}-down-${newIndex}`;
    }

    return `${prefix}-${direction}-${newIndex}`;
  }

  /**
   * Empties a file box after an upload, successful or not.
   *
   * Without it the browser keeps the chosen filename on screen, so after a refused upload the screen shows
   * a file that is not attached beside an error about it — and choosing the *same* file again fires no
   * `change` event at all, so the admin's obvious next move would do nothing.
   */
  private clearFileInput(input: ElementRef<HTMLInputElement> | undefined): void {
    if (input) {
      input.nativeElement.value = '';
    }
  }
}

function blankToNull(value: string | null): string | null {
  return value === null || value.trim() === '' ? null : value.trim();
}
