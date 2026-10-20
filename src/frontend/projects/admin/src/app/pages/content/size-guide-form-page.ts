import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import {
  AdminSiteContentApi,
  ContentFailure,
  toContentFailure,
} from '../../core/content/admin-site-content-api';
import {
  AdminSizeGuide,
  DEFAULT_SIZE_GUIDE_LIMITS,
  SizeGuideLimits,
  SizeGuideSaved,
} from '../../core/content/admin-site-content.model';
import { blankToNull } from './home-section-editor';
import { MediaPicker, PickedMedia, pickedImage } from './media-picker';

/** What the sanitiser removed on the save that created a guide, carried across the navigation to it. */
interface SavedState {
  readonly removedFromIntro: readonly string[];
  readonly removedFromTips: readonly string[];
}

/**
 * The size-guide editor (PDP-04, doc 11 P13 step 11; #192): name, introduction, the size table,
 * measuring tips and an image.
 *
 * **The table is a grid of plain text boxes**, sent as the `tableJson` string the API stores. The
 * limits (columns, rows, cells, characters per cell) are the server's, read from the guide when there
 * is one; the grid refuses to grow past them so the staff member never builds a table the save would
 * refuse.
 *
 * The two HTML fields are typed as HTML, as on the content-page editor, and the server cleans them on
 * save. What it removed is shown after a successful save rather than swallowed.
 */
@Component({
  selector: 'sda-size-guide-form-page',
  imports: [MediaPicker, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <p><a class="adm-link" routerLink="/content/size-guides">All size guides</a></p>
      <h1 class="adm-panel__title">{{ heading() }}</h1>
      @if (loaded(); as guide) {
        <p class="adm-panel__hint" data-testid="sg-usage">
          Used by {{ guide.categoryCount }} {{ guide.categoryCount === 1 ? 'category' : 'categories' }} and
          {{ guide.productCount }} {{ guide.productCount === 1 ? 'product' : 'products' }}.
        </p>
      }
    </section>

    <p class="adm-muted" role="status" data-testid="sg-said">{{ said() }}</p>

    @if (removedFromIntro().length > 0 || removedFromTips().length > 0) {
      <div class="adm-state adm-state--warning" data-testid="sg-removed">
        <p>Saved, but the shop removed some HTML it does not allow:</p>
        <ul>
          @if (removedFromIntro().length > 0) {
            <li data-testid="sg-removed-intro">From the introduction: {{ removedFromIntro().join(', ') }}</li>
          }
          @if (removedFromTips().length > 0) {
            <li data-testid="sg-removed-tips">From the measuring tips: {{ removedFromTips().join(', ') }}</li>
          }
        </ul>
      </div>
    }

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="sg-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind === 'forbidden') {
          <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
        } @else if (failure.stale) {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="sg-reload" (click)="reload()">Reload</button>
          </div>
        }
      </div>
    }

    @if (isLoading()) {
      <p class="adm-muted">Loading…</p>
    } @else if (!forbidden()) {
      <form class="sg-form" (submit)="save($event)">
        <div class="adm-fields">
          <label class="adm-field adm-field--wide">
            <span class="adm-label">Name</span>
            <input
              class="adm-input"
              type="text"
              maxlength="100"
              autocomplete="off"
              [value]="name()"
              [attr.aria-invalid]="errorFor('name') ? 'true' : null"
              data-testid="sg-name"
              (input)="type($event, name)"
            />
            @if (errorFor('name'); as message) {
              <span class="adm-field-error" role="alert" data-testid="sg-error-name">{{ message }}</span>
            }
          </label>

          <label class="adm-field adm-field--wide">
            <span class="adm-label">Introduction (HTML, optional)</span>
            <textarea
              class="adm-input sg-html"
              aria-describedby="sg-html-hint sg-intro-count"
              [value]="introHtml()"
              [attr.aria-invalid]="errorFor('introHtml') ? 'true' : null"
              data-testid="sg-intro"
              (input)="type($event, introHtml)"
            ></textarea>
            <span class="adm-panel__hint" id="sg-intro-count">
              {{ introHtml().length }} of {{ limits().maxHtmlLength }} characters
            </span>
            @if (errorFor('introHtml'); as message) {
              <span class="adm-field-error" role="alert" data-testid="sg-error-introHtml">{{ message }}</span>
            }
          </label>
        </div>
        <p class="adm-panel__hint" id="sg-html-hint">
          Simple formatting only: paragraphs, bold, italics, lists and links. Anything else is removed when
          you save, and the screen tells you what.
        </p>

        <fieldset class="sg-grid" aria-describedby="sg-table-hint">
          <legend class="adm-label">Size table</legend>
          <p class="adm-panel__hint" id="sg-table-hint">
            The first row is the column headings, which cannot be blank. Up to {{ limits().maxColumns }}
            columns, {{ limits().maxRows }} rows and {{ limits().maxCells }} cells; each cell up to
            {{ limits().maxCellLength }} characters.
          </p>
          @if (tableWasUnreadable()) {
            <p class="adm-state adm-state--warning" data-testid="sg-table-unreadable">
              The saved table could not be read, so the grid starts empty. Saving replaces it.
            </p>
          }
          <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Size table editor">
            <table class="sg-table" data-testid="sg-table-editor">
              <thead>
                <tr>
                  @for (heading of columns(); track $index; let c = $index) {
                    <th scope="col">
                      <input
                        class="adm-input sg-cell"
                        type="text"
                        [attr.maxlength]="limits().maxCellLength"
                        [attr.aria-label]="'Heading of column ' + (c + 1)"
                        [value]="heading"
                        [attr.data-testid]="'sg-col-' + c"
                        (input)="setColumn(c, $event)"
                      />
                      <button
                        class="adm-btn sg-mini"
                        type="button"
                        [attr.data-testid]="'sg-remove-col-' + c"
                        (click)="removeColumn(c)"
                      >
                        Remove<span class="sr-only"> column {{ c + 1 }}</span>
                      </button>
                    </th>
                  }
                  <th scope="col"><span class="sr-only">Row actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track $index; let r = $index) {
                  <tr>
                    @for (cell of row; track $index; let c = $index) {
                      <td>
                        <input
                          class="adm-input sg-cell"
                          type="text"
                          [attr.maxlength]="limits().maxCellLength"
                          [attr.aria-label]="'Row ' + (r + 1) + ', ' + (columns()[c] || 'column ' + (c + 1))"
                          [value]="cell"
                          [attr.data-testid]="'sg-cell-' + r + '-' + c"
                          (input)="setCell(r, c, $event)"
                        />
                      </td>
                    }
                    <td>
                      <button
                        class="adm-btn sg-mini"
                        type="button"
                        [attr.data-testid]="'sg-remove-row-' + r"
                        (click)="removeRow(r)"
                      >
                        Remove<span class="sr-only"> row {{ r + 1 }}</span>
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="sg-add-col" (click)="addColumn()">Add a column</button>
            <button class="adm-btn" type="button" data-testid="sg-add-row" (click)="addRow()">Add a row</button>
          </div>
          @if (gridMessage(); as message) {
            <p class="adm-field-error" role="alert" data-testid="sg-grid-message">{{ message }}</p>
          }
          @if (errorFor('tableJson'); as message) {
            <p class="adm-field-error" role="alert" data-testid="sg-error-tableJson">{{ message }}</p>
          }
        </fieldset>

        <div class="adm-fields">
          <label class="adm-field adm-field--wide">
            <span class="adm-label">Measuring tips (HTML, optional)</span>
            <textarea
              class="adm-input sg-html"
              aria-describedby="sg-html-hint sg-tips-count"
              [value]="measuringTipsHtml()"
              [attr.aria-invalid]="errorFor('measuringTipsHtml') ? 'true' : null"
              data-testid="sg-tips"
              (input)="type($event, measuringTipsHtml)"
            ></textarea>
            <span class="adm-panel__hint" id="sg-tips-count">
              {{ measuringTipsHtml().length }} of {{ limits().maxHtmlLength }} characters
            </span>
            @if (errorFor('measuringTipsHtml'); as message) {
              <span class="adm-field-error" role="alert" data-testid="sg-error-measuringTipsHtml">{{ message }}</span>
            }
          </label>
        </div>

        <sda-media-picker
          label="Image, e.g. how to measure"
          testId="sg-image"
          [media]="image()"
          [error]="errorFor('imagePublicId')"
          (mediaChange)="image.set($event)"
        />

        @if (otherErrors().length > 0) {
          <ul class="adm-field-error" role="alert" data-testid="sg-errors">
            @for (message of otherErrors(); track message) {
              <li>{{ message }}</li>
            }
          </ul>
        }

        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="submit"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
            data-testid="sg-save"
          >
            {{ isBusy() ? 'Saving…' : loaded() ? 'Save the size guide' : 'Add the size guide' }}
          </button>
        </div>
      </form>
    }
  `,
  styles: `
    :host {
      /* The side gutter every admin page has (dashboard, catalogue, inventory): #192 tester UX. */
      padding: var(--gutter);
      display: grid;
      /* One track that may shrink below its content, so a wide table scrolls in its own region
         rather than widening the page. */
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-3);
      min-width: 0;
    }
    .sg-form {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    .sg-html {
      min-height: 10rem;
      font-family: var(--font-mono, ui-monospace, monospace);
      font-size: var(--text-small);
    }
    .sg-grid {
      display: grid;
      gap: var(--space-1);
      border: 1px solid var(--color-border);
      padding: var(--space-1-5) var(--space-2);
      margin: 0;
      min-width: 0;
    }
    /* The screen-reader-only labels inside the table are absolutely positioned; without a positioned
       scroller they escape it and widen the page at 320 px. */
    .adm-table-scroll {
      position: relative;
    }
    .sg-table {
      border-collapse: collapse;
    }
    .sg-table th,
    .sg-table td {
      padding: var(--space-0-5);
      vertical-align: top;
      text-align: left;
    }
    .sg-table th {
      font-weight: var(--weight-semibold);
    }
    .sg-cell {
      width: 9rem;
    }
    .sg-mini {
      margin-top: var(--space-0-5);
    }
  `,
})
export class SizeGuideFormPage {
  private readonly api = inject(AdminSiteContentApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly loaded = signal<AdminSizeGuide | null>(null);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly forbidden = signal(false);
  protected readonly problem = signal<ContentFailure | null>(null);
  protected readonly said = signal('');
  protected readonly removedFromIntro = signal<readonly string[]>([]);
  protected readonly removedFromTips = signal<readonly string[]>([]);
  protected readonly gridMessage = signal<string | null>(null);
  protected readonly tableWasUnreadable = signal(false);

  protected readonly name = signal('');
  protected readonly introHtml = signal('');
  protected readonly measuringTipsHtml = signal('');
  protected readonly image = signal<PickedMedia | null>(null);
  protected readonly columns = signal<readonly string[]>(['']);
  protected readonly rows = signal<readonly (readonly string[])[]>([['']]);

  protected readonly limits = computed<SizeGuideLimits>(() => this.loaded()?.limits ?? DEFAULT_SIZE_GUIDE_LIMITS);
  protected readonly heading = computed(() =>
    this.loaded() ? `Edit “${this.loaded()!.name}”` : 'Add a size guide',
  );

  private readonly fieldErrors = signal<Readonly<Record<string, string>>>({});
  private readonly guideId: number | null;

  constructor() {
    const id = Number(this.route.snapshot.paramMap.get('id'));
    this.guideId = Number.isInteger(id) && id > 0 ? id : null;

    // A guide just created arrives here with what the sanitiser removed from it.
    const state = this.router.currentNavigation()?.extras.state?.['sizeGuideSaved'] as SavedState | undefined;
    if (state) {
      this.removedFromIntro.set(state.removedFromIntro);
      this.removedFromTips.set(state.removedFromTips);
      this.said.set('The size guide is added.');
    }

    if (this.guideId === null) {
      this.isLoading.set(false);
    } else {
      this.load(this.guideId);
    }
  }

  protected errorFor(field: string): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  protected otherErrors(): string[] {
    const shown = ['name', 'introHtml', 'tableJson', 'measuringTipsHtml', 'imagePublicId'];
    return Object.entries(this.fieldErrors())
      .filter(([key]) => !shown.includes(key))
      .map(([, message]) => message);
  }

  protected type(event: Event, target: { set(value: string): void }): void {
    target.set((event.target as HTMLInputElement | HTMLTextAreaElement | null)?.value ?? '');
  }

  protected setColumn(index: number, event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    this.columns.update((columns) => columns.map((heading, at) => (at === index ? value : heading)));
  }

  protected setCell(row: number, column: number, event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    this.rows.update((rows) =>
      rows.map((cells, r) => (r === row ? cells.map((cell, c) => (c === column ? value : cell)) : cells)),
    );
  }

  protected addColumn(): void {
    const limits = this.limits();
    const columns = this.columns().length;
    const rows = this.rows().length;

    if (columns >= limits.maxColumns) {
      this.gridMessage.set(`A size table can have up to ${limits.maxColumns} columns.`);
      return;
    }
    if ((columns + 1) * rows > limits.maxCells) {
      this.gridMessage.set(`A size table can have up to ${limits.maxCells} cells. Remove a row first.`);
      return;
    }

    this.gridMessage.set(null);
    this.columns.update((current) => [...current, '']);
    this.rows.update((current) => current.map((cells) => [...cells, '']));
  }

  protected addRow(): void {
    const limits = this.limits();
    const columns = this.columns().length;
    const rows = this.rows().length;

    if (rows >= limits.maxRows) {
      this.gridMessage.set(`A size table can have up to ${limits.maxRows} rows.`);
      return;
    }
    if (columns * (rows + 1) > limits.maxCells) {
      this.gridMessage.set(`A size table can have up to ${limits.maxCells} cells. Remove a column first.`);
      return;
    }

    this.gridMessage.set(null);
    this.rows.update((current) => [...current, Array.from({ length: columns }, () => '')]);
  }

  protected removeColumn(index: number): void {
    if (this.columns().length <= 1) {
      this.gridMessage.set('A size table needs at least one column.');
      return;
    }

    this.gridMessage.set(null);
    this.columns.update((current) => current.filter((_, at) => at !== index));
    this.rows.update((current) => current.map((cells) => cells.filter((_, at) => at !== index)));
  }

  protected removeRow(index: number): void {
    if (this.rows().length <= 1) {
      this.gridMessage.set('A size table needs at least one row.');
      return;
    }

    this.gridMessage.set(null);
    this.rows.update((current) => current.filter((_, at) => at !== index));
  }

  protected reload(): void {
    this.problem.set(null);
    if (this.guideId !== null) {
      this.load(this.guideId);
    }
  }

  /** Saves every field. The table travels as the JSON string the API stores. */
  protected save(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.removedFromIntro.set([]);
    this.removedFromTips.set([]);
    this.said.set('Saving…');

    const body = {
      name: this.name().trim(),
      introHtml: blankToNull(this.introHtml()),
      tableJson: JSON.stringify({
        columns: this.columns().map((heading) => heading.trim()),
        rows: this.rows().map((cells) => cells.map((cell) => cell.trim())),
      }),
      measuringTipsHtml: blankToNull(this.measuringTipsHtml()),
      imagePublicId: this.image()?.publicId ?? null,
    };

    const existing = this.loaded();

    this.api.saveSizeGuide(this.guideId, body, existing?.rowVersion ?? null).subscribe({
      next: (saved) => {
        if (this.guideId === null) {
          void this.router.navigate(['/content/size-guides', saved.id], {
            state: { sizeGuideSaved: removedOf(saved) },
          });
          return;
        }

        this.removedFromIntro.set(saved.removedFromIntro);
        this.removedFromTips.set(saved.removedFromTips);
        this.said.set(
          saved.removedFromIntro.length + saved.removedFromTips.length > 0
            ? 'Saved, with some HTML removed. See the note above.'
            : 'The size guide is saved.',
        );
        this.load(this.guideId);
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  private load(id: number): void {
    this.api.sizeGuide(id).subscribe({
      next: (guide) => {
        this.loaded.set(guide);
        this.name.set(guide.name);
        this.introHtml.set(guide.introHtml ?? '');
        this.measuringTipsHtml.set(guide.measuringTipsHtml ?? '');
        this.image.set(pickedImage(guide.image));

        const table = guide.table;
        const readable = table !== null && table.columns.length > 0;
        this.tableWasUnreadable.set(!readable);
        this.columns.set(readable ? [...table.columns] : ['']);
        this.rows.set(
          readable && table.rows.length > 0
            ? table.rows.map((cells) => table.columns.map((_, c) => cells[c] ?? ''))
            : [Array.from({ length: readable ? table.columns.length : 1 }, () => '')],
        );

        this.isLoading.set(false);
        this.isBusy.set(false);
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        const failure = toContentFailure(error, 'this size guide');
        if (failure.kind === 'forbidden') {
          this.forbidden.set(true);
          this.isBusy.set(false);
          this.problem.set({
            ...failure,
            message: 'Your role does not include size guides. Ask an administrator for the Content permission.',
          });
          return;
        }
        this.fail(error);
      },
    });
  }

  private fail(error: unknown): void {
    this.isBusy.set(false);
    const failure = toContentFailure(error, 'this size guide');

    if (Object.keys(failure.fields).length > 0) {
      this.fieldErrors.set(failure.fields);
      this.said.set(Object.values(failure.fields).join(' '));
      return;
    }

    this.problem.set(failure);
    this.said.set(failure.message);
  }
}

function removedOf(saved: SizeGuideSaved): SavedState {
  return { removedFromIntro: saved.removedFromIntro, removedFromTips: saved.removedFromTips };
}
