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

import {
  AdminCatalogApi,
  CatalogFailure,
  toCatalogFailure,
} from '../../core/catalog/admin-catalog-api';
import {
  AdminCategoryTree,
  AdminDepartment,
  DepartmentFormValues,
  departmentFormFrom,
} from '../../core/catalog/admin-catalog.model';
import { moveInArray } from '../../core/catalog/reorder';
import { CategoryTree } from './category-tree';

/**
 * ADM-11 (W1-12): the departments and, for the department in hand, its category tree.
 *
 * **Departments are edited, never created or removed.** Doc 08 §3.2: "the nav is fixed (Q5): there is no
 * admin create or delete; admins edit only." The only way a department leaves the shop is the `IsActive`
 * switch of doc 08 §7.1, which is its own button here rather than a checkbox on the edit form — emptying a
 * third of the shop should not be something that rides along with a change of SEO copy.
 *
 * **Reorder is the same interaction as the category tree's**, for the same reasons: Move up / Move down
 * buttons that work by tap, click, keyboard and <kbd>Alt</kbd> + arrow, staged and then saved as the whole
 * list the API asks for. See `CategoryTree` for the argument in full.
 *
 * The API is the security boundary (doc 03 §3a). A user without `Categories.View` reaches this page — the
 * route only requires a session, like the rest of the admin shell — and is shown the refusal rather than an
 * empty screen.
 */
@Component({
  selector: 'sda-catalog-page',
  imports: [FormsModule, CategoryTree],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="catalog">
      <h1 class="catalog__title">Departments and categories</h1>
      <p class="sr-only" role="status">{{ announcement() }}</p>

      @if (failure(); as problem) {
        <div class="adm-state adm-state--error" role="alert" data-testid="page-error">
          <p>{{ problem.message }}</p>
          @if (problem.kind !== 'forbidden') {
            <button class="adm-btn" type="button" (click)="load()">Try again</button>
          }
        </div>
      }

      @if (loading()) {
        <p class="adm-state">Loading the catalogue structure…</p>
      } @else if (departments().length > 0) {
        <section class="adm-panel" aria-labelledby="dept-title">
          <div class="catalog__head">
            <h2 class="adm-panel__title" id="dept-title">Departments</h2>
            <p class="adm-panel__hint">
              The three departments are fixed. You can rename them, reorder the navigation and take one off
              the shop.
            </p>
          </div>

          @if (orderChanged()) {
            <div class="adm-state adm-state--warning" data-testid="dept-order-pending">
              <p>The navigation order is not saved yet.</p>
              <div class="adm-actions">
                <button
                  class="adm-btn--primary"
                  type="button"
                  [disabled]="busy()"
                  (click)="saveDepartmentOrder()"
                  data-testid="save-dept-order"
                >
                  {{ busy() ? 'Saving…' : 'Save order' }}
                </button>
                <button
                  class="adm-btn"
                  type="button"
                  [disabled]="busy()"
                  (click)="discardOrder()"
                  data-testid="discard-dept-order"
                >
                  Discard
                </button>
              </div>
            </div>
          }

          <ol class="depts" #deptList>
            @for (row of departmentRows(); track row.department.id) {
              <li
                class="depts__item"
                [attr.data-testid]="'department-' + row.department.id"
              >
                <div class="depts__row">
                  <div class="depts__identity">
                    <span class="depts__name">{{ row.department.name }}</span>
                    <span class="adm-muted depts__meta">
                      /{{ row.department.slug }} · {{ row.department.categoryCount }}
                      {{ row.department.categoryCount === 1 ? 'category' : 'categories' }} ·
                      {{ row.department.storefrontProductCount }} published
                      {{ row.department.storefrontProductCount === 1 ? 'product' : 'products' }}
                    </span>
                  </div>

                  @if (row.department.isActive) {
                    <span class="adm-pill">Live</span>
                  } @else {
                    <span class="adm-pill adm-pill--hidden">Hidden</span>
                  }

                  <div class="adm-actions">
                    <button
                      class="adm-btn"
                      type="button"
                      [disabled]="!row.canMoveUp || busy()"
                      [attr.aria-label]="'Move ' + row.department.name + ' earlier in the navigation'"
                      [attr.data-testid]="'dept-up-' + row.department.id"
                      (click)="moveDepartment(row.department, -1, $event)"
                      (keydown.alt.arrowup)="moveDepartment(row.department, -1, $event)"
                      (keydown.alt.arrowdown)="moveDepartment(row.department, 1, $event)"
                    >
                      ↑
                    </button>
                    <button
                      class="adm-btn"
                      type="button"
                      [disabled]="!row.canMoveDown || busy()"
                      [attr.aria-label]="'Move ' + row.department.name + ' later in the navigation'"
                      [attr.data-testid]="'dept-down-' + row.department.id"
                      (click)="moveDepartment(row.department, 1, $event)"
                      (keydown.alt.arrowup)="moveDepartment(row.department, -1, $event)"
                      (keydown.alt.arrowdown)="moveDepartment(row.department, 1, $event)"
                    >
                      ↓
                    </button>
                    <button
                      class="adm-btn"
                      type="button"
                      [disabled]="busy()"
                      [attr.aria-pressed]="selectedId() === row.department.id"
                      [attr.data-testid]="'select-' + row.department.id"
                      (click)="select(row.department.id)"
                    >
                      {{ selectedId() === row.department.id ? 'Showing' : 'Categories' }}
                    </button>
                    <button
                      class="adm-btn"
                      type="button"
                      [disabled]="busy()"
                      [attr.aria-label]="'Edit ' + row.department.name"
                      [attr.data-testid]="'dept-edit-' + row.department.id"
                      (click)="startEdit(row.department)"
                    >
                      Edit
                    </button>
                    <button
                      class="adm-btn"
                      type="button"
                      [disabled]="busy()"
                      [attr.aria-label]="
                        (row.department.isActive ? 'Hide ' : 'Show ') +
                        row.department.name +
                        ' on the shop'
                      "
                      [attr.data-testid]="'dept-toggle-' + row.department.id"
                      (click)="toggleDepartment(row.department)"
                    >
                      {{ row.department.isActive ? 'Hide' : 'Show' }}
                    </button>
                  </div>
                </div>

                @if (editing()?.id === row.department.id) {
                  <form class="depts__form adm-fields" (ngSubmit)="saveDepartment()" novalidate>
                    <label class="adm-field">
                      <span class="adm-label">Name</span>
                      <input
                        class="adm-input"
                        type="text"
                        name="name"
                        required
                        [attr.aria-invalid]="fieldError('name') ? 'true' : null"
                        [ngModel]="form().name"
                        (ngModelChange)="patch('name', $event)"
                        data-testid="dept-field-name"
                      />
                      @if (fieldError('name'); as message) {
                        <span class="adm-field-error">{{ message }}</span>
                      }
                    </label>

                    <label class="adm-field">
                      <span class="adm-label">URL slug</span>
                      <input
                        class="adm-input"
                        type="text"
                        name="slug"
                        required
                        [attr.aria-invalid]="fieldError('slug') ? 'true' : null"
                        [ngModel]="form().slug"
                        (ngModelChange)="patch('slug', $event)"
                        data-testid="dept-field-slug"
                      />
                      @if (fieldError('slug'); as message) {
                        <span class="adm-field-error" data-testid="dept-slug-error">{{ message }}</span>
                      } @else {
                        <span class="adm-panel__hint">
                          Changing this changes every listing URL in the department, and old links stop
                          working.
                        </span>
                      }
                    </label>

                    <label class="adm-field adm-field--wide">
                      <span class="adm-label">Description</span>
                      <textarea
                        class="adm-input"
                        name="description"
                        rows="2"
                        [ngModel]="form().description"
                        (ngModelChange)="patch('description', $event)"
                      ></textarea>
                    </label>

                    <label class="adm-field">
                      <span class="adm-label">SEO title</span>
                      <input
                        class="adm-input"
                        type="text"
                        name="seoTitle"
                        [ngModel]="form().seoTitle"
                        (ngModelChange)="patch('seoTitle', $event)"
                      />
                    </label>

                    <label class="adm-field">
                      <span class="adm-label">SEO description</span>
                      <input
                        class="adm-input"
                        type="text"
                        name="seoDescription"
                        [ngModel]="form().seoDescription"
                        (ngModelChange)="patch('seoDescription', $event)"
                      />
                    </label>

                    <div class="adm-actions adm-field--wide">
                      <button
                        class="adm-btn--primary"
                        type="submit"
                        [disabled]="busy()"
                        data-testid="dept-save"
                      >
                        {{ busy() ? 'Saving…' : 'Save changes' }}
                      </button>
                      <button class="adm-btn" type="button" [disabled]="busy()" (click)="cancelEdit()">
                        Cancel
                      </button>
                    </div>
                  </form>
                }
              </li>
            }
          </ol>
        </section>

        @if (tree(); as loaded) {
          <sda-category-tree [tree]="loaded" (reloadRequested)="reloadTree()" />
        } @else if (selectedId() !== null) {
          <p class="adm-state">Loading categories…</p>
        }
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .catalog { display: grid; gap: var(--space-3); max-width: var(--container-max); }
    .catalog__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .catalog__head { display: grid; gap: var(--space-0-5); }
    .depts { display: grid; gap: var(--space-1); margin: 0; padding: 0; list-style: none; }
    .depts__row {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: center;
      padding: var(--space-1);
      background: var(--color-bg);
      border: 1px solid var(--color-border);
    }
    .depts__identity { display: grid; gap: 2px; flex: 1 1 12rem; min-width: 0; }
    .depts__name { font-weight: var(--weight-semibold); overflow-wrap: anywhere; }
    .depts__meta { font-size: var(--text-small); overflow-wrap: anywhere; }
    .depts__form {
      margin-top: var(--space-1);
      padding: var(--space-2);
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }
  `,
})
export class CatalogPage {
  private readonly api = inject(AdminCatalogApi);
  private readonly deptList = viewChild<ElementRef<HTMLElement>>('deptList');

  /** The departments as the server sent them. */
  private readonly loaded = signal<readonly AdminDepartment[]>([]);

  /** The order the admin has staged, before Save order. */
  private readonly staged = signal<readonly AdminDepartment[] | null>(null);

  protected readonly tree = signal<AdminCategoryTree | null>(null);
  protected readonly selectedId = signal<number | null>(null);
  protected readonly editing = signal<AdminDepartment | null>(null);
  protected readonly form = signal<DepartmentFormValues>({
    name: '',
    slug: '',
    description: '',
    seoTitle: '',
    seoDescription: '',
  });

  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly failure = signal<CatalogFailure | null>(null);
  protected readonly announcement = signal('');

  /** The controls to focus once the re-ordered list has been drawn, best first; see `restoreFocus`. */
  private readonly focusAfterRender = signal<readonly string[] | null>(null);

  protected readonly departments = computed(() => this.staged() ?? this.loaded());

  protected readonly departmentRows = computed(() =>
    this.departments().map((department, index, all) => ({
      department,
      canMoveUp: index > 0,
      canMoveDown: index < all.length - 1,
    })),
  );

  protected readonly orderChanged = computed(() => {
    const staged = this.staged();
    if (!staged) {
      return false;
    }

    const original = this.loaded();
    return staged.some((department, index) => department.id !== original[index]?.id);
  });

  constructor() {
    afterEveryRender(() => {
      const candidates = this.focusAfterRender();
      const host = this.deptList()?.nativeElement;
      if (!candidates || !host) {
        return;
      }

      this.focusAfterRender.set(null);

      for (const candidate of candidates) {
        const control = host.querySelector<HTMLButtonElement>(`[data-testid="${candidate}"]`);
        if (control && !control.disabled) {
          control.focus();
          return;
        }
      }
    });

    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.failure.set(null);

    this.api.departments().subscribe({
      next: (departments) => {
        this.loading.set(false);
        this.loaded.set(departments);
        this.staged.set(null);

        // Keep the department in hand across a reload, and open the first one on the first load, so the
        // page is never a list with nothing under it.
        const keep = this.selectedId() ?? departments[0]?.id ?? null;
        if (keep !== null) {
          this.select(keep);
        }
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.loaded.set([]);
        this.failure.set(toCatalogFailure(error));
      },
    });
  }

  protected select(departmentId: number): void {
    this.selectedId.set(departmentId);
    this.loadTree();
  }

  /**
   * After a write in the tree. The department list is refreshed too, because a category that was created,
   * removed or hidden changes the counts beside the department's name — and a count that silently stops
   * agreeing with the tree under it is worse than no count.
   */
  protected reloadTree(): void {
    this.loadTree();
    this.refreshDepartmentCounts();
  }

  private loadTree(): void {
    const departmentId = this.selectedId();
    if (departmentId === null) {
      return;
    }

    this.api.categoryTree(departmentId).subscribe({
      next: (tree) => this.tree.set(tree),
      error: (error: unknown) => {
        this.tree.set(null);
        this.failure.set(toCatalogFailure(error));
      },
    });
  }

  private refreshDepartmentCounts(): void {
    // Not while an order is staged: overwriting the list would throw away moves the admin has made and not
    // yet saved.
    if (this.staged() !== null) {
      return;
    }

    this.api.departments().subscribe({
      next: (departments) => this.loaded.set(departments),
      error: () => {
        // The tree's own error is the one worth showing; a stale count is not worth a second alert.
      },
    });
  }

  protected fieldError(field: string): string | null {
    return this.failure()?.fieldErrors?.[field]?.[0] ?? null;
  }

  protected patch<K extends keyof DepartmentFormValues>(
    field: K,
    value: DepartmentFormValues[K],
  ): void {
    this.form.update((current) => ({ ...current, [field]: value }));
  }

  protected startEdit(department: AdminDepartment): void {
    this.failure.set(null);
    this.editing.set(department);
    this.form.set(departmentFormFrom(department));
  }

  protected cancelEdit(): void {
    this.editing.set(null);
    this.failure.set(null);
  }

  protected saveDepartment(): void {
    const department = this.editing();
    if (!department || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    this.api.updateDepartment(department.id, this.form(), department.rowVersion).subscribe({
      next: () => {
        this.busy.set(false);
        this.editing.set(null);
        this.announcement.set(`${this.form().name} was saved.`);
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.failure.set(toCatalogFailure(error));
      },
    });
  }

  protected toggleDepartment(department: AdminDepartment): void {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    this.api
      .setDepartmentActive(department.id, !department.isActive, department.rowVersion)
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.announcement.set(
            department.isActive
              ? `${department.name} is hidden from the shop.`
              : `${department.name} is back on the shop.`,
          );
          this.load();
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.failure.set(toCatalogFailure(error));
        },
      });
  }

  protected moveDepartment(department: AdminDepartment, delta: number, event?: Event): void {
    event?.preventDefault();

    const current = this.departments();
    const index = current.findIndex((candidate) => candidate.id === department.id);
    const moved = moveInArray(current, index, delta);
    if (!moved) {
      return;
    }

    this.staged.set(moved);
    this.failure.set(null);
    this.announcement.set(
      `${department.name} moved to position ${index + 1 + delta} of ${moved.length}. ` +
        'Press Save order to apply it.',
    );

    this.restoreFocus(
      department.id,
      delta,
      (event?.currentTarget as HTMLElement | null)?.getAttribute('data-testid') ?? null,
    );
  }

  protected saveDepartmentOrder(): void {
    const staged = this.staged();
    if (!staged || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    this.api.reorderDepartments(staged.map((department) => department.id)).subscribe({
      next: () => {
        this.busy.set(false);
        this.announcement.set('The navigation order is saved.');
        this.staged.set(null);
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.failure.set(toCatalogFailure(error));
      },
    });
  }

  protected discardOrder(): void {
    this.staged.set(null);
    this.announcement.set('The staged order was discarded.');
  }

  /**
   * See `CategoryTree.restoreFocus`: a move that loses focus is a move a keyboard user cannot repeat, and
   * the focus has to be set after the render that re-orders the rows rather than on a microtask before it —
   * re-inserting a focused element elsewhere in the document blurs it in Chromium.
   */
  private restoreFocus(departmentId: number, delta: number, pressed: string | null): void {
    const direction = delta < 0 ? `dept-up-${departmentId}` : `dept-down-${departmentId}`;
    const other = delta < 0 ? `dept-down-${departmentId}` : `dept-up-${departmentId}`;

    this.focusAfterRender.set([...new Set([pressed, direction, other].filter((id) => id !== null))]);
  }
}
