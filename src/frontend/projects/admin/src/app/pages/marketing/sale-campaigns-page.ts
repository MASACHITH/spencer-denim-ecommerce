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
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { AdminCampaignApi } from '../../core/marketing/admin-campaign-api';
import {
  AdminSaleCampaign,
  AdminSaleCampaignBanner,
  SaleCampaignTargetType,
  SaleDiscountType,
  campaignDiscountLabel,
  campaignStatusLabel,
  campaignTargetLabel,
} from '../../core/marketing/admin-campaign.model';
import { AdminCatalogApi, CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { AdminCategoryNode, AdminDepartment } from '../../core/catalog/admin-catalog.model';
import { AdminProductListItem } from '../../core/products/admin-product.model';
import { CampaignBannerPicker } from './campaign-banner-picker';
import { CampaignProductPicker } from './campaign-product-picker';

/** One department or category a campaign can be pointed at, as the two selects list them. */
interface TargetOption {
  readonly id: number;
  /** `Women`, or `Women › Shirts & Blouses` — the label doc 11 line 450's worked example uses. */
  readonly label: string;
}

/** A target on the form, before it is saved. Carries its own label so the chip can name it. */
interface DraftTarget {
  readonly targetType: SaleCampaignTargetType;
  readonly id: number;
  readonly includeSubcategories: boolean;
  readonly label: string;
}

/**
 * Sale campaigns (ADM-14, doc 11 P3 section A).
 *
 * **Departments, categories and individual products (#191).** A department and a category are a few
 * dozen rows that this screen loads whole and offers as a plain `<select>`; a product is one of thousands,
 * so it is chosen through {@link CampaignProductPicker}, a search-as-you-type combobox over the product
 * list's own paged endpoint. Every kind of target lands in the same chip list, and every chip can be
 * removed. A campaign may name a product that a department or category on it already covers: that changes
 * nothing, because a campaign discounts a product once, and two campaigns on one product never add up —
 * the lower price wins (doc 08 §6 step 3).
 *
 * **The banner** is chosen through {@link CampaignBannerPicker}, which uploads to the media library the
 * way the product form and the logo setting do, and is shown on the Sale page while the campaign is live.
 *
 * **Nothing is seeded and no example campaign is shown.** No default name, no default percentage, no
 * placeholder that renders — a plausible-looking campaign anywhere here would be a discount the client
 * never agreed to, and the dates are blank rather than "now" because a window this screen invented is a
 * window nobody chose.
 *
 * **The dates are the shop's own time.** Both inputs are `datetime-local`, which has no offset, and the
 * API's contract is Colombo wall-clock for exactly that reason. The server converts; nothing here does
 * arithmetic on a date.
 *
 * **The status is derived, not stored** (doc 11 line 474). It arrives on the row and the screen only
 * renames it; there is no status field on the form, and the two ways to end a sale early are the ones doc
 * 11 P3 step 5 gives — move the end date, or switch it off.
 *
 * **The warnings are rendered, not only announced.** `productsRepriced` and the API's `warnings[]` are
 * the only feedback that distinguishes a campaign that is discounting something from one that silently
 * reaches no product at all, which is the mistake this form would otherwise make in silence.
 *
 * **Nothing is ever `disabled`** (#109): a control whose state changes under the user throws focus to
 * `<body>`, so the save button carries `aria-disabled` and the handler refuses instead. Focus is re-homed
 * on `afterNextRender` rather than `queueMicrotask`, which passes Vitest and fails a browser.
 */
@Component({
  selector: 'sda-sale-campaigns-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CampaignBannerPicker, CampaignProductPicker],
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Sale campaigns</h1>
      <p class="adm-panel__hint">
        A campaign takes a percentage or an amount off everything in the departments, categories and
        products you choose, between a start and an end. Nothing is set up in advance — which sales this
        shop runs is yours to decide.
      </p>
      <p class="adm-panel__hint">
        Dates and times are this shop's own time. The bag and the checkout always charge the campaign
        price from the moment it starts; listing pages and the Sale page can take up to five minutes to
        catch up at the start and at the end.
      </p>
      <p class="adm-panel__hint" data-testid="campaign-scope-note">
        A product on two campaigns, or reached twice by one, gets the lower price once — campaigns never
        add up. A campaign's banner shows at the top of the Sale page while the campaign is live.
      </p>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        <div class="adm-actions">
          <button class="adm-btn" type="button" (click)="reload()">Try again</button>
        </div>
      </div>
    }

    <!-- tabindex="-1" so focus can land here when a save replaces the row that was being edited. This
         node is always rendered: a message written into a node the same update removes is a message
         nobody hears, which this project has shipped twice. -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="campaign-said">{{ said() }}</p>

    @if (warnings().length > 0) {
      <div class="adm-state adm-state--warning" data-testid="campaign-warnings">
        <ul class="adm-list">
          @for (warning of warnings(); track warning) {
            <li>{{ warning }}</li>
          }
        </ul>
      </div>
    }

    <section class="adm-panel">
      <h2 class="adm-panel__title">
        {{ editingId() === null ? 'Add a sale campaign' : 'Edit this sale campaign' }}
      </h2>
      <form (submit)="save($event)">
        <div class="adm-fields">
          <label class="adm-field adm-field--wide">
            <span class="adm-label">Name</span>
            <input
              class="adm-input"
              type="text"
              name="name"
              maxlength="100"
              autocomplete="off"
              [value]="name()"
              [attr.aria-describedby]="'campaign-name-hint'"
              [attr.aria-invalid]="errorFor('name') ? 'true' : null"
              data-testid="campaign-name"
              (input)="type($event, name)"
            />
            <span class="adm-panel__hint" id="campaign-name-hint">
              For your own records. Shoppers see the price, not the campaign's name.
            </span>
            @if (errorFor('name'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-name">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Type</span>
            <select
              class="adm-input"
              name="discountType"
              [value]="discountType()"
              data-testid="campaign-type"
              (change)="chooseType($event)"
            >
              <option value="Percent">Percentage off each price</option>
              <option value="FixedAmount">Fixed amount off each price</option>
            </select>
            @if (errorFor('discountType'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-type">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">
              {{ discountType() === 'Percent' ? 'Percentage (up to 90)' : 'Amount off (Rs.)' }}
            </span>
            <input
              class="adm-input"
              type="number"
              name="discountValue"
              step="0.01"
              min="0"
              [value]="discountValue()"
              [attr.aria-describedby]="'campaign-value-hint'"
              [attr.aria-invalid]="errorFor('discountValue') ? 'true' : null"
              data-testid="campaign-value"
              (input)="type($event, discountValue)"
            />
            <span class="adm-panel__hint" id="campaign-value-hint">
              No price ever falls below Rs. 1.00, and a product already on a lower campaign keeps the
              lower price — campaigns never add up.
            </span>
            @if (errorFor('discountValue'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-value">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Starts</span>
            <input
              class="adm-input"
              type="datetime-local"
              name="startsAt"
              [value]="startsAt()"
              [attr.aria-invalid]="errorFor('startsAt') ? 'true' : null"
              data-testid="campaign-starts"
              (input)="type($event, startsAt)"
            />
            @if (errorFor('startsAt'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-starts">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Ends</span>
            <input
              class="adm-input"
              type="datetime-local"
              name="endsAt"
              [value]="endsAt()"
              [attr.aria-describedby]="'campaign-ends-hint'"
              [attr.aria-invalid]="errorFor('endsAt') ? 'true' : null"
              data-testid="campaign-ends"
              (input)="type($event, endsAt)"
            />
            <span class="adm-panel__hint" id="campaign-ends-hint">
              Required, and it is the moment the sale stops: a campaign ending at midnight runs all
              through the evening before.
            </span>
            @if (errorFor('endsAt'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-ends">{{ message }}</span>
            }
          </label>

          <label class="adm-field adm-field--wide">
            <span class="adm-label">Note to yourselves <span class="adm-muted">(optional)</span></span>
            <input
              class="adm-input"
              type="text"
              name="description"
              maxlength="500"
              autocomplete="off"
              [value]="description()"
              [attr.aria-invalid]="errorFor('description') ? 'true' : null"
              data-testid="campaign-description"
              (input)="type($event, description)"
            />
            @if (errorFor('description'); as message) {
              <span class="adm-field-error" role="alert" data-testid="error-description">
                {{ message }}
              </span>
            }
          </label>

          <!-- A 44 px row around a 24 px box, and the label is part of the control: the shape
               CouriersPage settled on, because the shared 20 px checkbox fails GLB-09's floor and a
               44 px native one renders as an outsized glyph. -->
          <span class="campaign-check">
            <input
              type="checkbox"
              id="campaign-active"
              [checked]="isActive()"
              data-testid="campaign-active"
              (change)="toggleActive($event)"
            />
            <label for="campaign-active" data-testid="campaign-active-field">
              Run this campaign
            </label>
          </span>
        </div>

        <h3 class="adm-panel__subtitle">What it applies to</h3>

        @if (catalogProblem(); as message) {
          <p class="adm-field-error" role="alert" data-testid="campaign-catalog-problem">
            {{ message }}
          </p>
        }

        @if (targets().length === 0) {
          <p class="adm-muted" data-testid="campaign-no-targets">
            Nothing chosen yet. A campaign with nothing chosen discounts nothing.
          </p>
        } @else {
          <ul class="campaign-chips" data-testid="campaign-targets">
            @for (target of targets(); track target.targetType + ':' + target.id) {
              <li class="campaign-chip">
                <span>{{ target.label }}</span>
                <button
                  class="adm-btn"
                  type="button"
                  [attr.data-testid]="'drop-target-' + target.targetType + '-' + target.id"
                  (click)="dropTarget(target)"
                >
                  Remove {{ target.label }}
                </button>
              </li>
            }
          </ul>
        }

        @if (errorFor('targets'); as message) {
          <p class="adm-field-error" role="alert" data-testid="error-targets">{{ message }}</p>
        }

        <div class="adm-fields">
          <label class="adm-field">
            <span class="adm-label">Add</span>
            <select
              class="adm-input"
              name="targetType"
              [value]="targetType()"
              data-testid="target-type"
              (change)="chooseTargetType($event)"
            >
              <option value="Department">A whole department</option>
              <option value="Category">One category</option>
              <option value="Product">Individual products</option>
            </select>
          </label>

          @if (targetType() === 'Product') {
            <sda-campaign-product-picker
              [chosen]="chosenProductIds()"
              (picked)="addProduct($event)"
              (refused)="refuseTarget($event)"
            />
          } @else if (targetType() === 'Department') {
            <label class="adm-field">
              <span class="adm-label">Department</span>
              <select
                class="adm-input"
                name="targetDepartment"
                [value]="targetDepartmentId()"
                data-testid="target-department"
                (change)="type($event, targetDepartmentId)"
              >
                <option value="">Choose a department…</option>
                @for (option of departmentOptions(); track option.id) {
                  <option [value]="option.id">{{ option.label }}</option>
                }
              </select>
            </label>
          } @else {
            <label class="adm-field">
              <span class="adm-label">Category</span>
              <select
                class="adm-input"
                name="targetCategory"
                [value]="targetCategoryId()"
                data-testid="target-category"
                (change)="type($event, targetCategoryId)"
              >
                <option value="">Choose a category…</option>
                @for (option of categoryOptions(); track option.id) {
                  <option [value]="option.id">{{ option.label }}</option>
                }
              </select>
            </label>

            <span class="campaign-check campaign-check--wide">
              <input
                type="checkbox"
                id="target-subcategories"
                aria-describedby="target-subcategories-hint"
                [checked]="includeSubcategories()"
                data-testid="target-subcategories"
                (change)="toggleSubcategories($event)"
              />
              <label for="target-subcategories" data-testid="target-subcategories-field">
                Include everything under this category
              </label>
            </span>
            <p class="adm-panel__hint campaign-check__hint" id="target-subcategories-hint">
              On, the campaign also covers the categories inside the one you choose. Off, it covers only
              products in that category itself.
            </p>
          }
        </div>

        <!-- A product is added the moment it is chosen from the list, so this button is for a department
             or a category only. -->
        @if (targetType() !== 'Product') {
          <div class="adm-actions">
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="add-target"
              (click)="addTarget()"
            >
              Add to the campaign
            </button>
          </div>
        }

        <sda-campaign-banner-picker
          [banner]="banner()"
          [unavailable]="bannerGone()"
          (bannerChange)="chooseBanner($event)"
          (said)="announce($event)"
        />
        @if (errorFor('bannerPublicId'); as message) {
          <p class="adm-field-error" role="alert" data-testid="error-banner">{{ message }}</p>
        }

        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
            data-testid="campaign-save"
          >
            {{ editingId() === null ? 'Add the campaign' : 'Save the campaign' }}
          </button>
          @if (editingId() !== null) {
            <button class="adm-btn" type="button" data-testid="campaign-cancel" (click)="startNew()">
              Cancel
            </button>
          }
        </div>
      </form>
    </section>

    @if (isLoading()) {
      <p class="adm-muted">Loading sale campaigns…</p>
    } @else if (campaigns().length === 0) {
      <p class="adm-muted" data-testid="campaign-empty">
        No sale campaign has been added yet, so nothing in the shop is discounted by one. Add the first
        one above.
      </p>
    } @else {
      <ul class="adm-list" data-testid="campaigns">
        @for (campaign of campaigns(); track campaign.id) {
          <li class="campaign-row" [attr.data-testid]="'campaign-' + campaign.id">
            <p class="campaign-row__name">
              {{ campaign.name }}
              <!-- The derived status, shown whatever it is: unlike a promo code, a campaign that is
                   merely scheduled is the normal state of a sale a staff member has just set up, and
                   "Live" is the word that tells them it is already changing prices. -->
              <span
                class="adm-pill"
                [class.adm-pill--hidden]="campaign.status !== 'Live'"
                [attr.data-testid]="'status-' + campaign.id"
                >{{ statusLabel(campaign.status) }}</span
              >
            </p>
            <dl class="campaign-row__facts">
              <dt>Discount</dt>
              <dd>{{ discountLabel(campaign) }}</dd>
              <dt>Applies to</dt>
              <dd [attr.data-testid]="'targets-' + campaign.id">
                @if (campaign.targets.length === 0) {
                  Nothing — this campaign discounts no product
                } @else {
                  {{ targetSummary(campaign) }}
                }
              </dd>
              <dt>Window</dt>
              <dd>{{ campaign.startsAt }} &ndash; {{ campaign.endsAt }}</dd>
              <dt>Banner</dt>
              <dd [attr.data-testid]="'banner-' + campaign.id">
                @if (campaign.banner; as chosen) {
                  {{ chosen.altText ?? 'A picture with no description' }}
                } @else if (campaign.bannerUnavailable) {
                  No longer available in the media library
                } @else {
                  None
                }
              </dd>
              @if (campaign.description) {
                <dt>Note</dt>
                <dd>{{ campaign.description }}</dd>
              }
            </dl>
            <div class="adm-actions">
              <button
                class="adm-btn"
                type="button"
                [attr.data-testid]="'edit-' + campaign.id"
                (click)="edit(campaign)"
              >
                Edit
              </button>
              <!-- Two presses, because this cannot be undone from the screen. The confirmation replaces
                   the label on the same button, so focus stays where the staff member put it. -->
              <button
                class="adm-btn"
                type="button"
                [attr.data-testid]="'remove-' + campaign.id"
                (click)="remove(campaign)"
              >
                {{ removingId() === campaign.id ? 'Yes, remove it' : 'Remove' }}
              </button>
              @if (removingId() === campaign.id) {
                <button
                  class="adm-btn"
                  type="button"
                  [attr.data-testid]="'remove-cancel-' + campaign.id"
                  (click)="cancelRemove()"
                >
                  Keep it
                </button>
              }
            </div>
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
    .adm-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .adm-panel__subtitle {
      font-size: var(--text-data);
      font-weight: var(--weight-semibold);
      margin-block: var(--space-2) var(--space-1);
    }
    /* A product name is staff text with no guaranteed break in it; the status line must wrap it rather
       than widen the page (#191 tester). */
    [data-testid='campaign-said'] {
      overflow-wrap: anywhere;
    }
    .campaign-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .campaign-row__name {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .campaign-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .campaign-row__facts dt {
      color: var(--color-text-muted);
    }
    .campaign-row__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .campaign-chips {
      display: grid;
      gap: var(--space-1);
      list-style: none;
      padding: 0;
      margin-block: 0 var(--space-2);
    }
    /* A row rather than an inline pill: the Remove button has to clear 44 px and name what it removes,
       and a label like "Women > Shirts & Blouses" does not fit beside one at 320 px. */
    .campaign-chip {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      padding: var(--space-1) var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      font-size: var(--text-small);
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .campaign-chip > span {
      flex: 1 1 auto;
      min-width: 0;
    }
    .campaign-check {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      min-width: 0;
    }
    .campaign-check input[type='checkbox'] {
      width: 24px;
      height: 24px;
      flex: 0 0 auto;
      margin: 0;
      cursor: pointer;
    }
    .campaign-check label {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      cursor: pointer;
    }
    /* The long label gets the whole row rather than a grid column, so it does not wrap to one word per
       line at 320 px. The same escape .adm-field--wide takes. */
    .campaign-check--wide {
      grid-column: 1 / -1;
    }
    .campaign-check__hint {
      grid-column: 1 / -1;
      margin: 0;
    }
  `,
})
export class SaleCampaignsPage {
  private readonly api = inject(AdminCampaignApi);
  private readonly catalog = inject(AdminCatalogApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly campaigns = signal<readonly AdminSaleCampaign[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  /** The API's `warnings[]`, kept until the next save so they can be read rather than only heard. */
  protected readonly warnings = signal<readonly string[]>([]);

  protected readonly editingId = signal<number | null>(null);

  /** Which row's Remove has been pressed once. Null when nothing is awaiting confirmation. */
  protected readonly removingId = signal<number | null>(null);

  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly discountType = signal<SaleDiscountType>('Percent');
  protected readonly discountValue = signal('');
  protected readonly startsAt = signal('');
  protected readonly endsAt = signal('');
  protected readonly isActive = signal(true);

  protected readonly targets = signal<readonly DraftTarget[]>([]);

  /** The ids of the products on the form, so the picker can mark them and refuse a repeat. */
  protected readonly chosenProductIds = computed(() =>
    this.targets()
      .filter((target) => target.targetType === 'Product')
      .map((target) => target.id),
  );

  /** The banner the form will save, or null for none. */
  protected readonly banner = signal<AdminSaleCampaignBanner | null>(null);

  /**
   * The campaign being edited names a banner the library can no longer show. Said beside the picker, so
   * that clearing it on save is something the staff member was told about rather than a silent change.
   */
  protected readonly bannerGone = signal(false);

  protected readonly targetType = signal<SaleCampaignTargetType>('Category');
  protected readonly targetDepartmentId = signal('');
  protected readonly targetCategoryId = signal('');
  protected readonly includeSubcategories = signal(true);

  protected readonly departmentOptions = signal<readonly TargetOption[]>([]);
  protected readonly categoryOptions = signal<readonly TargetOption[]>([]);

  /**
   * Why the two selects are empty, when they are.
   *
   * **Named separately from {@link problem} on purpose.** The campaign list and the catalogue lists are
   * two different permissions: doc 11 P3's table gives this screen `Promotions.View/Create/Edit`, and the
   * department and category lists are behind `Categories.View`. Every seeded role that holds
   * `Promotions.*` also holds `Categories.View` (Store Manager, and Administrator through the wildcard),
   * so this is reachable only through a custom role — and a staff member in one is owed the name of the
   * permission they are missing rather than "something went wrong".
   */
  protected readonly catalogProblem = signal<string | null>(null);

  private readonly fieldErrors = signal<Readonly<Record<string, string>>>({});

  /** The row version of the campaign being edited, round-tripped as an ETag. */
  private rowVersion: string | null = null;

  /** @see PromoCodesPage.errorFor — a method, not a computed returning a closure, for the same reason. */
  protected errorFor(field: string): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  protected readonly discountLabel = campaignDiscountLabel;

  protected readonly statusLabel = campaignStatusLabel;

  /** Every target of a saved campaign, in one sentence for the list. */
  protected targetSummary(campaign: AdminSaleCampaign): string {
    return campaign.targets.map(campaignTargetLabel).join(', ');
  }

  constructor() {
    this.startNew();
    this.load();
    this.loadCatalog();
  }

  protected type(event: Event, target: { set(value: string): void }): void {
    target.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected toggleActive(event: Event): void {
    this.isActive.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  protected toggleSubcategories(event: Event): void {
    this.includeSubcategories.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  /**
   * Changes the discount type.
   *
   * **The figure is cleared**, as the promo-code form clears its two: 20 means 20% under one type and
   * Rs. 20.00 under the other, and a figure left in a box whose meaning has changed is a figure a staff
   * member will not notice has changed meaning. Here the two readings differ by a factor of hundreds.
   */
  protected chooseType(event: Event): void {
    this.discountType.set(
      ((event.target as HTMLSelectElement | null)?.value ?? 'Percent') as SaleDiscountType,
    );
    this.discountValue.set('');
    this.fieldErrors.set({});
  }

  protected chooseTargetType(event: Event): void {
    this.targetType.set(
      ((event.target as HTMLSelectElement | null)?.value ?? 'Category') as SaleCampaignTargetType,
    );
  }

  /**
   * Adds a product the picker offered.
   *
   * The label names the product code as well as the name, because two products in a fashion catalogue
   * often share a name across colours or seasons, and the code is what is on the label in the stockroom.
   */
  protected addProduct(product: AdminProductListItem): void {
    if (this.targets().some((target) => target.targetType === 'Product' && target.id === product.id)) {
      this.refuseTarget(`${product.name} is already on this campaign.`);
      return;
    }

    this.targets.set([
      ...this.targets(),
      {
        targetType: 'Product',
        id: product.id,
        includeSubcategories: true,
        label: `${product.name} (${product.productCode})`,
      },
    ]);

    this.fieldErrors.set({});
    this.announce(`${product.name} added. ${this.targets().length} chosen.`);
  }

  /** A target the form would not add, said beside the chip list and out loud. */
  protected refuseTarget(message: string): void {
    this.fieldErrors.set({ targets: message });
    this.announce(message);
  }

  protected chooseBanner(banner: AdminSaleCampaignBanner | null): void {
    this.banner.set(banner);
    this.bannerGone.set(false);

    this.fieldErrors.set(
      Object.fromEntries(
        Object.entries(this.fieldErrors()).filter(([field]) => field !== 'bannerPublicId'),
      ),
    );
  }

  /**
   * Adds the chosen department or category to the campaign.
   *
   * **Two checks happen here rather than at the server**, which is the one place this screen departs from
   * the promo-code form's "nothing is validated here that the server validates". The list of targets only
   * exists on the client until Save, so "you have not chosen anything" and "that one is already on the
   * campaign" have to be answerable without a round trip — a chip that could not be added would
   * otherwise appear and then vanish on save. Both sentences say the same thing the API's would.
   */
  protected addTarget(): void {
    if (this.isBusy()) {
      return;
    }

    const isDepartment = this.targetType() === 'Department';
    const options = isDepartment ? this.departmentOptions() : this.categoryOptions();
    const chosen = Number(isDepartment ? this.targetDepartmentId() : this.targetCategoryId());
    const option = options.find((candidate) => candidate.id === chosen);

    if (option === undefined) {
      const message = isDepartment ? 'Choose a department first.' : 'Choose a category first.';

      this.fieldErrors.set({ targets: message });
      this.announce(message);
      return;
    }

    const type = isDepartment ? 'Department' : 'Category';

    if (this.targets().some((target) => target.targetType === type && target.id === option.id)) {
      const message = `${option.label} is already on this campaign.`;

      this.fieldErrors.set({ targets: message });
      this.announce(message);
      return;
    }

    // A category's subtree flag is the staff member's choice; a department already covers every category
    // under it, so the flag is meaningless there and the API forces it true rather than storing a value
    // nothing can read.
    const subtree = isDepartment ? true : this.includeSubcategories();

    this.targets.set([
      ...this.targets(),
      {
        targetType: type,
        id: option.id,
        includeSubcategories: subtree,
        label:
          type === 'Category' && !subtree ? `${option.label} (this category only)` : option.label,
      },
    ]);

    this.fieldErrors.set({});
    this.targetDepartmentId.set('');
    this.targetCategoryId.set('');
    this.announce(`${option.label} added. ${this.targets().length} chosen.`);
  }

  /**
   * Removes a chosen target.
   *
   * **Focus moves to the chip that took its place**, or the one before it, or the "Add" control when the
   * list is empty. The pressed button is gone after this, and a keyboard user removing six products in a
   * row would otherwise be thrown back to the top of the page after each one (#109's shape).
   */
  protected dropTarget(target: DraftTarget): void {
    const index = this.targets().findIndex(
      (candidate) => candidate.targetType === target.targetType && candidate.id === target.id,
    );

    this.targets.set(
      this.targets().filter(
        (candidate) => candidate.targetType !== target.targetType || candidate.id !== target.id,
      ),
    );

    this.announce(`${target.label} removed. ${this.targets().length} chosen.`);

    afterNextRender(
      () => {
        const buttons = this.document.querySelectorAll<HTMLElement>(
          '[data-testid="campaign-targets"] button',
        );
        const next = buttons[Math.min(index, buttons.length - 1)];

        (next ?? this.document.querySelector<HTMLElement>('[data-testid="target-type"]'))?.focus();
      },
      { injector: this.injector },
    );
  }

  protected startNew(): void {
    this.editingId.set(null);
    this.rowVersion = null;

    // **Disarmed here too, and leaving it out was the second half of the `startNew` defect above.**
    // `remove` arms a row on its first press and `startNew` runs after every save and on Cancel — so a
    // row armed and then left while the form was saved stayed armed through the reload, with its button
    // still reading "Yes, remove it". The next single press would then delete a campaign the staff
    // member had moved on from. `edit` already clears it; this is the other way out of the form.
    this.removingId.set(null);
    this.name.set('');
    this.description.set('');
    this.discountType.set('Percent');
    this.discountValue.set('');

    // Deliberately blank rather than "now" or "next week": a window this screen invented would be a sale
    // the staff member did not choose, and the server refuses a missing date with a message against the
    // control.
    this.startsAt.set('');
    this.endsAt.set('');
    this.isActive.set(true);

    // Products included: `startNew` runs after every save and on Cancel, and a form that kept the last
    // campaign's chips would put those products on the next sale a staff member created.
    this.targets.set([]);
    this.banner.set(null);
    this.bannerGone.set(false);
    this.targetType.set('Category');
    this.targetDepartmentId.set('');
    this.targetCategoryId.set('');
    this.includeSubcategories.set(true);
    this.fieldErrors.set({});
  }

  /**
   * Loads a saved campaign into the form.
   *
   * **Every target becomes a chip, products included**, so what the save sends is exactly what the staff
   * member can see and remove. A product that has since been removed from the catalogue keeps its chip,
   * named "(removed from the catalogue)", because the API refuses a save that still names it and the chip
   * is how the staff member takes it off.
   */
  protected edit(campaign: AdminSaleCampaign): void {
    this.editingId.set(campaign.id);
    this.rowVersion = campaign.rowVersion;
    this.removingId.set(null);

    // **The last save's warnings go, and that matters more here than it looks.** They are kept after a
    // save on purpose — they are the only thing that says a campaign reaches no product — but every one
    // of them is written as "this campaign's…", so leaving them above a form that is now editing a
    // *different* campaign attaches the most important sentence on this screen to the wrong row.
    this.warnings.set([]);
    this.name.set(campaign.name);
    this.description.set(campaign.description ?? '');
    this.discountType.set(campaign.discountType);
    this.discountValue.set(String(campaign.discountValue));

    // The server sends Colombo wall-clock and `datetime-local` wants exactly that with no offset, so the
    // only transformation is trimming the seconds the input does not show.
    this.startsAt.set(toLocalInput(campaign.startsAt));
    this.endsAt.set(toLocalInput(campaign.endsAt));
    this.isActive.set(campaign.isActive);

    this.targets.set(
      campaign.targets.map((target) => ({
        targetType: target.targetType,
        id:
          (target.targetType === 'Department'
            ? target.departmentId
            : target.targetType === 'Category'
              ? target.categoryId
              : target.productId) ?? 0,
        includeSubcategories: target.includeSubcategories,
        label: campaignTargetLabel(target),
      })),
    );

    this.banner.set(campaign.banner);
    this.bannerGone.set(campaign.banner === null && campaign.bannerUnavailable);
    this.fieldErrors.set({});
    this.announce(`Editing ${campaign.name}.`);
  }

  /**
   * Saves the form.
   *
   * **Every field is sent, including `targets` and the blank ones**, because the endpoint refuses a body
   * that leaves one out rather than reading its absence as "leave it alone".
   *
   * **Nothing is validated here that the server validates**, with one exception: a campaign with no
   * target at all is caught before the request, because a staff member who has filled in the dates and
   * not pressed "Add to the campaign" has made the one mistake this form invites, and the sentence is
   * more useful beside the chips than in a field error from a round trip. The percentage ceiling, "end
   * after start", the names and the ids are the server's, and its sentences land against their controls.
   */
  protected save(event: Event): void {
    event.preventDefault();

    // The guard `disabled` would have provided, which is why the button is never disabled: `[disabled]`
    // on a control whose state changes under the user throws focus to `<body>` (#109).
    if (this.isBusy()) {
      return;
    }

    if (this.targets().length === 0) {
      const message =
        'Choose at least one department, category or product for the campaign to apply to. A department '
        + 'or a category is added with "Add to the campaign"; a product is added by choosing it from the '
        + 'list.';

      this.fieldErrors.set({ targets: message });
      this.announce(message);
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.warnings.set([]);
    this.announce(this.editingId() === null ? 'Adding the campaign…' : 'Saving the campaign…');

    const focused = this.document.activeElement as HTMLElement | null;

    this.api
      .save(
        this.editingId(),
        {
          name: this.name().trim(),
          description: blankToNull(this.description()),
          discountType: this.discountType(),
          discountValue: toNumberOrNull(this.discountValue()),
          startsAt: this.startsAt().trim(),
          endsAt: this.endsAt().trim(),
          isActive: this.isActive(),

          // The key is always present, null for no banner, because the endpoint refuses a body that
          // leaves one out.
          bannerPublicId: this.banner()?.publicId ?? null,
          targets: this.targets().map((target) => ({
            targetType: target.targetType,
            departmentId: target.targetType === 'Department' ? target.id : null,
            categoryId: target.targetType === 'Category' ? target.id : null,
            productId: target.targetType === 'Product' ? target.id : null,

            // Forced on for a department and a product, as the API itself forces it: the flag only
            // means something for a category.
            includeSubcategories: target.targetType === 'Category' ? target.includeSubcategories : true,
          })),
        },
        this.rowVersion,
      )
      .subscribe({
        next: (saved) => {
          // `productsRepriced` first, because it is the only answer to "did that do anything", and then
          // the warnings — the API's own "this campaign's targets do not reach any product" among them.
          const repriced = saved.productsRepriced;
          const priced =
            repriced === 1 ? 'One product was repriced' : `${repriced} products were repriced`;

          this.warnings.set(saved.warnings);
          this.announce(
            [`${saved.campaign.name} is saved. ${priced}.`, ...saved.warnings].join(' '),
          );

          this.startNew();
          this.load(() => this.rehomeFocusIfLost(focused));
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  /**
   * Removes a campaign, on the second press.
   *
   * Two presses because it cannot be undone from this screen, and the confirmation replaces the label on
   * the same button so focus stays where the staff member put it — a dialog or a new button would move
   * it, which is the #109 shape by another route.
   */
  protected remove(campaign: AdminSaleCampaign): void {
    if (this.isBusy()) {
      return;
    }

    if (this.removingId() !== campaign.id) {
      this.removingId.set(campaign.id);
      this.announce(`Press Remove again to delete ${campaign.name}.`);
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.warnings.set([]);
    this.announce(`Removing ${campaign.name}…`);

    const focused = this.document.activeElement as HTMLElement | null;

    this.api.remove(campaign.id, campaign.rowVersion).subscribe({
      next: () => {
        this.removingId.set(null);

        if (this.editingId() === campaign.id) {
          this.startNew();
        }

        this.announce(
          `${campaign.name} is removed. The prices it was discounting have been put back.`,
        );
        this.load(() => this.rehomeFocusIfLost(focused));
      },
      error: (error: unknown) => {
        this.removingId.set(null);
        this.fail(error);
      },
    });
  }

  protected cancelRemove(): void {
    this.removingId.set(null);
    this.announce('Nothing was removed.');
  }

  protected reload(): void {
    this.load();
    this.loadCatalog();
  }

  private load(then?: () => void): void {
    this.api.list().subscribe({
      next: (list) => {
        this.campaigns.set(list);
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

  /**
   * Loads the departments and, for each one, its category tree.
   *
   * **One request per department, in parallel, and it is the whole catalogue structure.** Doc 02 line 119
   * scopes ADM-14 to one screen and the structure is a few dozen rows, so a staff member choosing "Women
   * › Shirts & Blouses" sees every category in one select rather than picking a department first and
   * waiting. There is no endpoint that answers every department's categories at once; adding one for a
   * label would be a new API surface for a `<select>`.
   *
   * **A failure here does not fail the page**, because the campaign list is still readable and a campaign
   * that already exists can still be switched off — which is doc 11 P3 step 5's reason to come here at
   * all. The two selects say why they are empty instead.
   */
  private loadCatalog(): void {
    this.catalogProblem.set(null);

    this.catalog.departments().subscribe({
      next: (departments) => {
        this.departmentOptions.set(
          departments.map((department) => ({ id: department.id, label: department.name })),
        );

        if (departments.length === 0) {
          this.catalogProblem.set(
            'There are no departments yet, so there is nothing for a campaign to apply to. Add a '
              + 'department under Departments and categories first.',
          );
          return;
        }

        this.loadCategories(departments);
      },
      error: (error: unknown) => this.failCatalog(error),
    });
  }

  private loadCategories(departments: readonly AdminDepartment[]): void {
    forkJoin(
      departments.map((department) =>
        this.catalog.categoryTree(department.id).pipe(catchError(() => of(null))),
      ),
    ).subscribe({
      next: (trees) => {
        const options: TargetOption[] = [];
        let failed = 0;

        trees.forEach((tree, index) => {
          if (tree === null) {
            failed += 1;
            return;
          }

          collect(tree.categories, departments[index].name, options);
        });

        this.categoryOptions.set(options);

        if (failed > 0) {
          // Named rather than silent: a select that is quietly short of one department's categories is
          // how a staff member puts a sale on the wrong thing.
          this.catalogProblem.set(
            failed === departments.length
              ? 'The categories could not be loaded, so only whole departments can be chosen. Try again '
                + 'in a moment.'
              : `${failed} of ${departments.length} departments' categories could not be loaded, so `
                + 'some categories are missing from the list. Try again in a moment.',
          );
        }
      },
      error: (error: unknown) => this.failCatalog(error),
    });
  }

  private failCatalog(error: unknown): void {
    const failure = toCatalogFailure(error);

    this.catalogProblem.set(
      failure.kind === 'forbidden'
        ? 'You can manage campaigns but not read the catalogue, so the departments and categories cannot '
          + 'be listed. Ask for the "Departments and categories" permission as well.'
        : `The departments and categories could not be loaded, so nothing can be chosen. ${failure.message}`,
    );
  }

  /**
   * Turns a failure into something the staff member can act on, **keeping the server's per-field
   * messages**.
   *
   * A percentage above 90, "the end must be after the start" and "this target is already on the campaign"
   * all arrive as a 400 naming their own field, and those sentences are written for the staff member.
   * Collapsing them into one banner would throw away the field, which is the only thing that says where
   * to look. A target's message is named `targets[0]`, `targets[1]` and so on, so those are gathered on
   * to the chip list's own error line — the index is a position in a body this screen built, and means
   * nothing to the person reading it.
   */
  private fail(error: unknown): void {
    this.isBusy.set(false);

    const failure = toCatalogFailure(error);
    const fields = failure.fieldErrors;

    if (fields !== undefined && Object.keys(fields).length > 0) {
      const flattened: Record<string, string> = {};

      for (const [field, messages] of Object.entries(fields)) {
        const key = field.startsWith('targets') ? 'targets' : field;

        flattened[key] =
          key in flattened ? `${flattened[key]} ${messages.join(' ')}` : messages.join(' ');
      }

      this.fieldErrors.set(flattened);
      this.announce(Object.values(flattened).join(' '));
      return;
    }

    this.problem.set(failure);
    this.announce(failure.message);
  }

  /** @see CouriersPage.rehomeFocusIfLost — the same rule, and the same reason. */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="campaign-said"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /** @see CouriersPage.announce — a repeated identical announcement has to re-fire (ADM-22). */
  protected announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}

/**
 * Every category under these nodes, depth-first, labelled with its whole path.
 *
 * The label is built from the ancestors as they are walked rather than from `Categories.Path`, which
 * holds ids: `Women › Shirts & Blouses` is what doc 11 line 450's worked example calls the target, and it
 * is what the API's own list says about a saved one.
 */
function collect(
  nodes: readonly AdminCategoryNode[],
  prefix: string,
  into: TargetOption[],
): void {
  for (const node of nodes) {
    const label = `${prefix} › ${node.name}`;

    into.push({ id: node.id, label });
    collect(node.children, label, into);
  }
}

/** `''` for a blank box, which is how a staff member says "not set". */
function blankToNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length === 0 ? null : trimmed;
}

/**
 * A number, or null for a blank box.
 *
 * **An unparseable box is null rather than zero**, for the reason the promo-code form gives: zero is a
 * figure the staff member did not type, and here a 0% campaign is a sale the shop advertises that
 * discounts nothing — which `CK_SaleCampaigns_DiscountValue` refuses anyway, as a field error.
 */
function toNumberOrNull(value: string): number | null {
  const trimmed = value.trim();

  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number(trimmed);

  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * The shop's wall-clock time as a `datetime-local` input wants it: `yyyy-MM-ddTHH:mm`.
 *
 * The server sends `yyyy-MM-ddTHH:mm:ss`, which the input accepts but renders without the seconds — so
 * trimming them here means the value the form posts back is the value the form was given, and a save
 * that changes nothing does not move the window by a second.
 */
function toLocalInput(value: string): string {
  return value.length >= 16 ? value.slice(0, 16) : value;
}
