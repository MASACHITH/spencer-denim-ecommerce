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
import { RouterLink } from '@angular/router';

import {
  AdminSiteContentApi,
  ContentFailure,
  toContentFailure,
} from '../../core/content/admin-site-content-api';
import {
  AdminMegaMenuCard,
  AdminMegaMenuDepartment,
  MEGA_MENU_CARD_LIMITS,
  cardStatus,
} from '../../core/content/admin-mega-menu.model';
import { blankToNull, toIntegerOrNull, toLocalInput } from './home-section-editor';
import { MediaPicker, PickedMedia, pickedImage } from './media-picker';

/** The fields the form shows next to their own control; anything else is listed under the form. */
const SHOWN_FIELDS = [
  'departmentId',
  'title',
  'subtitle',
  'buttonLabel',
  'linkUrl',
  'imagePublicId',
  'sortOrder',
  'startsAt',
  'endsAt',
];

/**
 * Mega-menu cards (GLB-05, doc 11 P13 step 10; #215): the picture card at the end of each department's menu on a
 * desktop — an image, a heading, an optional line, a button and where it goes, with an order and a schedule.
 *
 * The same look and rules as the announcement bar (#192): one form above, the cards below, every key sent on
 * save, the row version on every edit and remove, the server's checks the ones that count. A department may line
 * up several cards; the menu shows the first one that is switched on, inside its dates and has its picture, and
 * with none it keeps the card it has always drawn from the department.
 */
@Component({
  selector: 'sda-mega-menu-page',
  imports: [RouterLink, MediaPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Mega menu cards</h1>
      <p class="adm-panel__hint">
        The picture card at the end of each department's menu on a computer screen. A department can line up
        several cards; the menu shows the first one, by order, that is switched on and inside its dates. With
        none, the menu shows "New in" the department with its own picture and description. Dates and times are
        this shop's own time.
      </p>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="mm-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind === 'forbidden') {
          <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
        } @else {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="mm-reload" (click)="reload()">
              {{ failure.stale ? 'Reload' : 'Try again' }}
            </button>
          </div>
        }
      </div>
    }

    <p class="adm-muted" role="status" tabindex="-1" data-testid="mm-said">{{ said() }}</p>

    @if (!forbidden()) {
      <section class="adm-panel" aria-labelledby="mm-form-title">
        <h2 class="adm-panel__title" id="mm-form-title">
          {{ editingId() === null ? 'Add a card' : 'Edit this card' }}
        </h2>
        <form (submit)="save($event)">
          <div class="adm-fields">
            <label class="adm-field">
              <span class="adm-label">Department</span>
              <select
                class="adm-input"
                [value]="departmentId()"
                [attr.aria-invalid]="errorFor('departmentId') ? 'true' : null"
                data-testid="mm-department"
                (change)="type($event, departmentId)"
              >
                <option value="">Choose a department</option>
                @for (department of departments(); track department.id) {
                  <option [value]="department.id" [selected]="String(department.id) === departmentId()">
                    {{ department.name }}{{ department.isActive ? '' : ' (hidden on the shop)' }}
                  </option>
                }
              </select>
              @if (errorFor('departmentId'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-departmentId">{{ text }}</span>
              }
            </label>

            <label class="adm-field adm-field--wide">
              <span class="adm-label">Heading</span>
              <input
                class="adm-input"
                type="text"
                autocomplete="off"
                [attr.maxlength]="limits.title"
                [value]="title()"
                [attr.aria-invalid]="errorFor('title') ? 'true' : null"
                data-testid="mm-title"
                (input)="type($event, title)"
              />
              @if (errorFor('title'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-title">{{ text }}</span>
              }
            </label>

            <label class="adm-field adm-field--wide">
              <span class="adm-label">Line under the heading (optional)</span>
              <input
                class="adm-input"
                type="text"
                autocomplete="off"
                [attr.maxlength]="limits.subtitle"
                [value]="subtitle()"
                [attr.aria-invalid]="errorFor('subtitle') ? 'true' : null"
                data-testid="mm-subtitle"
                (input)="type($event, subtitle)"
              />
              @if (errorFor('subtitle'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-subtitle">{{ text }}</span>
              }
            </label>

            <label class="adm-field">
              <span class="adm-label">Button words</span>
              <input
                class="adm-input"
                type="text"
                autocomplete="off"
                [attr.maxlength]="limits.buttonLabel"
                [value]="buttonLabel()"
                [attr.aria-invalid]="errorFor('buttonLabel') ? 'true' : null"
                data-testid="mm-button"
                (input)="type($event, buttonLabel)"
              />
              @if (errorFor('buttonLabel'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-buttonLabel">{{ text }}</span>
              }
            </label>

            <label class="adm-field adm-field--wide">
              <span class="adm-label">Link</span>
              <input
                class="adm-input"
                type="text"
                autocomplete="off"
                [attr.maxlength]="limits.linkUrl"
                aria-describedby="mm-link-hint"
                [value]="linkUrl()"
                [attr.aria-invalid]="errorFor('linkUrl') ? 'true' : null"
                data-testid="mm-link"
                (input)="type($event, linkUrl)"
              />
              <span class="adm-panel__hint" id="mm-link-hint">
                A page on this shop starting with /, such as /new-arrivals or /women/dresses?material=linen, or a full
                https:// address.
              </span>
              @if (errorFor('linkUrl'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-linkUrl">{{ text }}</span>
              }
            </label>

            <div class="adm-field adm-field--wide">
              <sda-media-picker
                label="Picture"
                testId="mm-image"
                [media]="image()"
                [required]="isActive()"
                [error]="errorFor('imagePublicId')"
                (mediaChange)="image.set($event)"
              />
              <span class="adm-panel__hint">
                Needed before the card can be shown. It fills a tall frame (4 by 5) about 260 pixels wide.
              </span>
            </div>

            <label class="adm-field">
              <span class="adm-label">Order (0 to 999)</span>
              <input
                class="adm-input"
                type="number"
                min="0"
                max="999"
                step="1"
                [value]="sortOrder()"
                [attr.aria-invalid]="errorFor('sortOrder') ? 'true' : null"
                data-testid="mm-order"
                (input)="type($event, sortOrder)"
              />
              @if (errorFor('sortOrder'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-sortOrder">{{ text }}</span>
              }
            </label>

            <label class="adm-field">
              <span class="adm-label">Show from (optional)</span>
              <input
                class="adm-input"
                type="datetime-local"
                [value]="startsAt()"
                [attr.aria-invalid]="errorFor('startsAt') ? 'true' : null"
                data-testid="mm-starts"
                (input)="type($event, startsAt)"
              />
              @if (errorFor('startsAt'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-startsAt">{{ text }}</span>
              }
            </label>

            <label class="adm-field">
              <span class="adm-label">Show until (optional)</span>
              <input
                class="adm-input"
                type="datetime-local"
                [value]="endsAt()"
                [attr.aria-invalid]="errorFor('endsAt') ? 'true' : null"
                data-testid="mm-ends"
                (input)="type($event, endsAt)"
              />
              @if (errorFor('endsAt'); as text) {
                <span class="adm-field-error" role="alert" data-testid="mm-error-endsAt">{{ text }}</span>
              }
            </label>

            <label class="adm-check adm-field--wide" data-testid="mm-active-field">
              <input type="checkbox" [checked]="isActive()" data-testid="mm-active" (change)="toggleActive($event)" />
              <span>Show this card</span>
            </label>
          </div>

          @if (otherErrors().length > 0) {
            <ul class="adm-field-error" role="alert" data-testid="mm-errors">
              @for (text of otherErrors(); track text) {
                <li>{{ text }}</li>
              }
            </ul>
          }

          <div class="adm-actions">
            <button
              class="adm-btn--primary"
              type="submit"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="mm-save"
            >
              {{ editingId() === null ? 'Add the card' : 'Save the card' }}
            </button>
            @if (editingId() !== null) {
              <button class="adm-btn" type="button" data-testid="mm-cancel" (click)="startNew()">Cancel</button>
            }
          </div>
        </form>
      </section>

      @if (isLoading()) {
        <p class="adm-muted">Loading cards…</p>
      } @else {
        @for (department of departments(); track department.id) {
          <section class="adm-panel mm-department" [attr.aria-labelledby]="'mm-dept-' + department.id" [attr.data-testid]="'mm-dept-' + department.slug">
            <h2 class="adm-panel__title" [id]="'mm-dept-' + department.id">
              {{ department.name }}
              @if (!department.isActive) {
                <span class="adm-muted">(hidden on the shop)</span>
              }
            </h2>

            @if (!hasShownCard(department)) {
              <p class="adm-panel__hint" [attr.data-testid]="'mm-fallback-' + department.slug">
                No card is on the menu now, so it shows “New in {{ department.name }}”{{
                  department.description ? ', ' + department.description : ''
                }}{{ department.heroImage ? ' with the department picture' : ' with an empty picture frame' }}
                and “Shop now” to New Arrivals.
              </p>
            }

            @if (department.cards.length === 0) {
              <p class="adm-muted" [attr.data-testid]="'mm-empty-' + department.slug">No cards yet.</p>
            } @else {
              <ul class="mm-list">
                @for (card of department.cards; track card.id) {
                  <li class="mm-card" [attr.data-testid]="'mm-card-' + card.id">
                    @if (card.image?.url) {
                      <img class="mm-card__thumb" [src]="card.image?.url" [alt]="card.image?.altText ?? ''" />
                    } @else {
                      <span class="mm-card__thumb mm-card__thumb--empty" aria-hidden="true">No picture</span>
                    }
                    <div class="mm-card__body">
                      <p class="mm-card__title">{{ card.title }}</p>
                      @if (card.subtitle) {
                        <p class="mm-card__subtitle">{{ card.subtitle }}</p>
                      }
                      <dl class="mm-card__facts">
                        <dt>Status</dt>
                        <dd>
                          <span class="adm-pill" [class.adm-pill--hidden]="!card.isShownNow" [attr.data-testid]="'mm-status-' + card.id">{{
                            status(card)
                          }}</span>
                        </dd>
                        <dt>Button</dt>
                        <dd>{{ card.buttonLabel }}</dd>
                        <dt>Link</dt>
                        <dd>{{ card.linkUrl }}</dd>
                        <dt>Order</dt>
                        <dd>{{ card.sortOrder }}</dd>
                        @if (card.startsAt || card.endsAt) {
                          <dt>Dates</dt>
                          <dd>{{ card.startsAt ?? 'Now' }} – {{ card.endsAt ?? 'no end' }}</dd>
                        }
                      </dl>
                      <div class="adm-actions">
                        <button class="adm-btn" type="button" [attr.data-testid]="'mm-edit-' + card.id" (click)="edit(card)">
                          Edit
                        </button>
                        <button
                          class="adm-btn--danger"
                          type="button"
                          [attr.aria-disabled]="isBusy() ? 'true' : null"
                          [attr.data-testid]="'mm-remove-' + card.id"
                          (click)="remove(card)"
                        >
                          {{ removingId() === card.id ? 'Yes, remove it' : 'Remove' }}
                        </button>
                        @if (removingId() === card.id) {
                          <button class="adm-btn" type="button" [attr.data-testid]="'mm-keep-' + card.id" (click)="removingId.set(null)">
                            Keep it
                          </button>
                        }
                      </div>
                    </div>
                  </li>
                }
              </ul>
            }

            @if (department.cards.length < maxCards()) {
              <div class="adm-actions">
                <button class="adm-btn" type="button" [attr.data-testid]="'mm-add-' + department.slug" (click)="startNew(department)">
                  Add a card for {{ department.name }}
                </button>
              </div>
            } @else {
              <p class="adm-panel__hint">{{ department.name }} holds {{ maxCards() }} cards, the most it can. Remove one to add another.</p>
            }
          </section>
        }
      }
    }
  `,
  styles: `
    :host {
      /* The side gutter every admin page has (#192 tester UX). */
      padding: var(--gutter);
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-3);
      min-width: 0;
    }
    .mm-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .mm-card {
      display: grid;
      grid-template-columns: minmax(0, 6rem) minmax(0, 1fr);
      gap: var(--space-2);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .mm-card__thumb {
      display: grid;
      place-items: center;
      width: 100%;
      aspect-ratio: 4 / 5;
      object-fit: cover;
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
      font-size: var(--text-small);
      color: var(--color-text-muted);
      text-align: center;
    }
    .mm-card__body {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
    }
    .mm-card__title {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
      margin: 0;
    }
    .mm-card__subtitle {
      overflow-wrap: anywhere;
      margin: 0;
    }
    .mm-card__facts {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .mm-card__facts dt {
      color: var(--color-text-muted);
    }
    .mm-card__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    @media (max-width: 374px) {
      .mm-card {
        grid-template-columns: minmax(0, 1fr);
      }
      .mm-card__thumb {
        max-width: 8rem;
      }
    }
  `,
})
export class MegaMenuPage {
  private readonly api = inject(AdminSiteContentApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly limits = MEGA_MENU_CARD_LIMITS;
  protected readonly String = String;

  protected readonly departments = signal<readonly AdminMegaMenuDepartment[]>([]);
  protected readonly maxCards = signal(6);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<ContentFailure | null>(null);
  protected readonly forbidden = signal(false);
  protected readonly said = signal('');

  protected readonly editingId = signal<number | null>(null);
  protected readonly removingId = signal<number | null>(null);

  protected readonly departmentId = signal('');
  protected readonly title = signal('');
  protected readonly subtitle = signal('');
  protected readonly buttonLabel = signal('Shop now');
  protected readonly linkUrl = signal('/new-arrivals');
  protected readonly image = signal<PickedMedia | null>(null);
  protected readonly sortOrder = signal('0');
  protected readonly isActive = signal(true);
  protected readonly startsAt = signal('');
  protected readonly endsAt = signal('');

  private readonly fieldErrors = signal<Readonly<Record<string, string>>>({});
  private rowVersion: string | null = null;

  protected readonly otherErrors = computed(() =>
    Object.entries(this.fieldErrors())
      .filter(([key]) => !SHOWN_FIELDS.includes(key))
      .map(([, text]) => text),
  );

  constructor() {
    this.load();
  }

  protected errorFor(field: string): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  protected status(card: AdminMegaMenuCard): string {
    return cardStatus(card);
  }

  protected hasShownCard(department: AdminMegaMenuDepartment): boolean {
    return department.cards.some((card) => card.isShownNow);
  }

  protected type(event: Event, target: { set(value: string): void }): void {
    target.set((event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '');
  }

  protected toggleActive(event: Event): void {
    this.isActive.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  /** Clears the form for a new card, for the department given (or the one chosen already). */
  protected startNew(department?: AdminMegaMenuDepartment): void {
    this.editingId.set(null);
    this.rowVersion = null;
    this.removingId.set(null);
    this.departmentId.set(department ? String(department.id) : '');
    this.title.set(department ? `New in ${department.name}` : '');
    this.subtitle.set('');
    this.buttonLabel.set('Shop now');
    this.linkUrl.set('/new-arrivals');
    this.image.set(null);
    this.sortOrder.set(String(department?.cards.length ?? 0));
    this.isActive.set(true);
    this.startsAt.set('');
    this.endsAt.set('');
    this.fieldErrors.set({});

    if (department) {
      this.said.set(`Adding a card for ${department.name}.`);
      this.focusForm();
    }
  }

  protected edit(card: AdminMegaMenuCard): void {
    this.editingId.set(card.id);
    this.rowVersion = card.rowVersion;
    this.removingId.set(null);
    this.departmentId.set(String(card.departmentId));
    this.title.set(card.title);
    this.subtitle.set(card.subtitle ?? '');
    this.buttonLabel.set(card.buttonLabel);
    this.linkUrl.set(card.linkUrl);
    this.image.set(pickedImage(card.image));
    this.sortOrder.set(String(card.sortOrder));
    this.isActive.set(card.isActive);
    this.startsAt.set(toLocalInput(card.startsAt));
    this.endsAt.set(toLocalInput(card.endsAt));
    this.fieldErrors.set({});
    this.said.set('Editing the card.');
    this.focusForm();
  }

  /** Saves the form. Every key is sent; blanks are null. The server's checks are the ones that count. */
  protected save(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const id = this.editingId();
    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.said.set(id === null ? 'Adding the card…' : 'Saving the card…');

    this.api
      .saveMegaMenuCard(
        id,
        {
          departmentId: toIntegerOrNull(this.departmentId()),
          title: blankToNull(this.title()),
          subtitle: blankToNull(this.subtitle()),
          buttonLabel: blankToNull(this.buttonLabel()),
          linkUrl: blankToNull(this.linkUrl()),
          imagePublicId: this.image()?.publicId ?? null,
          sortOrder: toIntegerOrNull(this.sortOrder()),
          isActive: this.isActive(),
          startsAt: blankToNull(this.startsAt()),
          endsAt: blankToNull(this.endsAt()),
        },
        this.rowVersion,
      )
      .subscribe({
        next: () => {
          const text = id === null ? 'The card is added.' : 'The card is saved.';
          this.load(() => {
            this.startNew();
            this.said.set(text);
            this.rehomeFocus();
          });
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  protected remove(card: AdminMegaMenuCard): void {
    if (this.isBusy()) {
      return;
    }

    if (this.removingId() !== card.id) {
      this.removingId.set(card.id);
      this.said.set('Press Remove again to delete this card.');
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set('Removing the card…');

    this.api.deleteMegaMenuCard(card.id, card.rowVersion).subscribe({
      next: () => {
        this.removingId.set(null);
        if (this.editingId() === card.id) {
          this.startNew();
        }
        this.load(() => {
          this.said.set('The card is removed.');
          this.rehomeFocus();
        });
      },
      error: (error: unknown) => {
        this.removingId.set(null);
        this.fail(error);
      },
    });
  }

  protected reload(): void {
    this.problem.set(null);
    this.load();
  }

  private load(then?: () => void): void {
    this.api.megaMenu().subscribe({
      next: (content) => {
        this.departments.set(content.departments);
        this.maxCards.set(content.maxCardsPerDepartment);
        this.isLoading.set(false);
        this.isBusy.set(false);
        this.forbidden.set(false);
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        const failure = toContentFailure(error, 'this card');

        if (failure.kind === 'forbidden') {
          // A role without Content.View: nothing to edit, so the form goes and the refusal stays.
          this.isBusy.set(false);
          this.forbidden.set(true);
          this.departments.set([]);
          this.problem.set({
            ...failure,
            message: 'Your role does not include the mega menu cards. Ask an administrator for the Content permission.',
          });
          this.said.set(failure.message);
          return;
        }

        this.fail(error);
      },
    });
  }

  private fail(error: unknown): void {
    this.isBusy.set(false);
    const failure = toContentFailure(error, 'this card');

    if (Object.keys(failure.fields).length > 0) {
      this.fieldErrors.set(failure.fields);
      this.said.set(Object.values(failure.fields).join(' '));
      return;
    }

    this.problem.set(failure);
    this.said.set(failure.message);
  }

  /** Moves focus to the form's first box, so a keyboard user lands where the editing happens. */
  private focusForm(): void {
    afterNextRender(
      () => this.document.querySelector<HTMLElement>('[data-testid="mm-department"]')?.focus(),
      { injector: this.injector },
    );
  }

  private rehomeFocus(): void {
    afterNextRender(
      () => {
        const active = this.document.activeElement;
        if (active !== null && active !== this.document.body && active.isConnected) {
          return;
        }
        this.document.querySelector<HTMLElement>('[data-testid="mm-said"]')?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }
}
