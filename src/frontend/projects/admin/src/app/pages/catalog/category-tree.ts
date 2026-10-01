import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterEveryRender,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';

import {
  AdminCatalogApi,
  CatalogFailure,
  toCatalogFailure,
} from '../../core/catalog/admin-catalog-api';
import {
  AdminCategoryNode,
  AdminCategoryTree,
  CategoryDeactivationImpact,
  CategoryFormValues,
  categoryFormFrom,
  emptyCategoryForm,
  findNode,
  flatten,
} from '../../core/catalog/admin-catalog.model';
import {
  canMove,
  changedSiblingOrders,
  moveWithinSiblings,
} from '../../core/catalog/reorder';

/** A row of the flattened tree, with everything the template needs precomputed. */
interface TreeRow {
  readonly node: AdminCategoryNode;
  readonly canMoveUp: boolean;
  readonly canMoveDown: boolean;
  /**
   * The grey second line, assembled here rather than in the template.
   *
   * Angular's template whitespace handling turns `level {{ depth }}@if (…) { , under {{ name }} }` into
   * "level 2 , under Dresses" — a stray space before the comma, invisible in review and caught by the
   * browser test that read the rendered text. A sentence with conditional clauses in it is a string, not
   * a layout.
   */
  readonly meta: string;
}

/**
 * The category tree of one department (ADM-11): create, edit, move, reorder, hide and remove.
 *
 * **Reorder is buttons, not drag-and-drop, and that is the design decision in this screen.** Dragging is
 * the obvious answer and the wrong one here: it needs a pointer, it needs a drop target an admin can see on
 * a 320 px screen, and the keyboard story has to be built separately anyway — which means building the
 * button version regardless and then maintaining two. So there is one mechanism: a **Move up** and a **Move
 * down** button on every row, each a full 44 px target, usable by tap, click, Tab + Enter, or
 * <kbd>Alt</kbd> + <kbd>↑</kbd> / <kbd>↓</kbd> with either move button focused — so one Tab gets you to a
 * row and the arrows then take it either way without Tabbing again. Focus follows the row it moved, which
 * is what lets two presses move a category two places.
 *
 * **Moves are staged and saved in one go**, because the API takes a whole sibling list rather than a row's
 * new position (see `ReorderList` on the server, and `reorder.ts` here). One request per keypress would be
 * the increment-per-row design the server deliberately refuses, and it would also make every intermediate
 * arrangement an audit row. Each changed sibling list is then its own request and its own audit row.
 *
 * **Moving to another parent is the edit form's parent field, not a drag.** It re-paths the whole subtree
 * (doc 11 P1 step 4), which is a different and much larger act than nudging a sibling, and it should say so
 * in words rather than happen because something was dropped a few pixels to the right.
 */
@Component({
  selector: 'sda-category-tree',
  imports: [FormsModule, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel" aria-labelledby="cat-tree-title">
      <div class="tree__head">
        <h2 class="adm-panel__title" id="cat-tree-title">
          Categories in {{ tree().departmentName }}
        </h2>
        <div class="adm-actions">
          <button
            class="adm-btn"
            type="button"
            [disabled]="busy()"
            (click)="startCreate(null)"
            data-testid="add-top-level"
          >
            Add a top-level category
          </button>
        </div>
      </div>

      @if (pendingOrders().length > 0) {
        <div class="adm-state adm-state--warning" data-testid="order-pending">
          <p>
            {{ pendingOrders().length === 1 ? '1 list has' : pendingOrders().length + ' lists have' }}
            a new order that is not saved yet.
          </p>
          <div class="adm-actions">
            <button
              class="adm-btn--primary"
              type="button"
              [disabled]="busy()"
              (click)="saveOrder()"
              data-testid="save-order"
            >
              {{ busy() ? 'Saving…' : 'Save order' }}
            </button>
            <button class="adm-btn" type="button" [disabled]="busy()" (click)="discardOrder()">
              Discard
            </button>
          </div>
        </div>
      }

      @if (failure(); as problem) {
        <div class="adm-state adm-state--error" role="alert" data-testid="tree-error">
          <p>{{ problem.message }}</p>
          @if (problem.kind === 'conflict' || problem.kind === 'notFound') {
            <button class="adm-btn" type="button" (click)="reloadRequested.emit()">
              Reload the tree
            </button>
          }
        </div>
      }

      <p class="sr-only" role="status">{{ announcement() }}</p>

      @if (rows().length === 0) {
        <p class="adm-panel__hint">
          This department has no categories yet. Add one to put it in the mega menu.
        </p>
      } @else {
        <ol class="tree" #treeList>
          @for (row of rows(); track row.node.id) {
            <li
              class="tree__item"
              [attr.aria-level]="row.node.depth"
              [style.--depth]="row.node.depth - 1"
              [attr.data-testid]="'category-' + row.node.id"
            >
              <div class="tree__row">
                <div class="tree__identity">
                  <span class="tree__name">{{ row.node.name }}</span>
                  <span class="adm-muted tree__meta">{{ row.meta }}</span>
                </div>

                @if (row.node.isActive) {
                  <span class="adm-pill">Live</span>
                } @else {
                  <span class="adm-pill adm-pill--hidden">Hidden</span>
                }

                <div class="adm-actions tree__buttons">
                  <button
                    class="adm-btn"
                    type="button"
                    [disabled]="!row.canMoveUp || busy()"
                    [attr.aria-label]="'Move ' + row.node.name + ' up, above its previous sibling'"
                    [attr.data-testid]="'move-up-' + row.node.id"
                    (click)="move(row.node, -1, $event)"
                    (keydown.alt.arrowup)="move(row.node, -1, $event)"
                    (keydown.alt.arrowdown)="move(row.node, 1, $event)"
                  >
                    ↑
                  </button>
                  <button
                    class="adm-btn"
                    type="button"
                    [disabled]="!row.canMoveDown || busy()"
                    [attr.aria-label]="'Move ' + row.node.name + ' down, below its next sibling'"
                    [attr.data-testid]="'move-down-' + row.node.id"
                    (click)="move(row.node, 1, $event)"
                    (keydown.alt.arrowup)="move(row.node, -1, $event)"
                    (keydown.alt.arrowdown)="move(row.node, 1, $event)"
                  >
                    ↓
                  </button>
                  <button
                    class="adm-btn"
                    type="button"
                    [disabled]="busy()"
                    [attr.aria-label]="'Edit ' + row.node.name"
                    (click)="startEdit(row.node)"
                    [attr.data-testid]="'edit-' + row.node.id"
                  >
                    Edit
                  </button>
                  @if (row.node.depth < maxDepth) {
                    <button
                      class="adm-btn"
                      type="button"
                      [disabled]="busy()"
                      [attr.aria-label]="'Add a category under ' + row.node.name"
                      (click)="startCreate(row.node.id)"
                    >
                      Add child
                    </button>
                  }
                  <button
                    class="adm-btn"
                    type="button"
                    [disabled]="busy()"
                    [attr.aria-label]="
                      (row.node.isActive ? 'Hide ' : 'Show ') + row.node.name + ' on the shop'
                    "
                    [attr.data-testid]="'toggle-' + row.node.id"
                    (click)="startToggle(row.node)"
                  >
                    {{ row.node.isActive ? 'Hide' : 'Show' }}
                  </button>
                  <button
                    class="adm-btn--danger"
                    type="button"
                    [disabled]="busy()"
                    [attr.aria-label]="'Remove ' + row.node.name"
                    [attr.data-testid]="'remove-' + row.node.id"
                    (click)="startRemove(row.node)"
                  >
                    Remove
                  </button>
                </div>
              </div>

              @if (confirming()?.id === row.node.id) {
                <div class="adm-state adm-state--warning tree__confirm" data-testid="confirm-panel">
                  @if (mode() === 'toggle' && row.node.isActive) {
                    <p><strong>Hide “{{ row.node.name }}” from the shop?</strong></p>
                    @if (impact(); as effect) {
                      <ul class="tree__impact">
                        <li>
                          <strong>{{ effect.productsLeavingTheStorefront }}</strong>
                          {{ effect.productsLeavingTheStorefront === 1 ? 'product' : 'products' }}
                          will disappear from listings, search and their own page.
                        </li>
                        @if (effect.descendantCategories > 0) {
                          <li>
                            <strong>{{ effect.descendantCategories }}</strong>
                            {{ effect.descendantCategories === 1 ? 'category' : 'categories' }}
                            underneath it will drop out of the mega menu.
                          </li>
                        }
                        @if (effect.descendantProductsLosingTheirMenuRoute > 0) {
                          <li>
                            <strong>{{ effect.descendantProductsLosingTheirMenuRoute }}</strong>
                            {{
                              effect.descendantProductsLosingTheirMenuRoute === 1
                                ? 'product stays'
                                : 'products stay'
                            }}
                            on sale underneath it, with no menu route left to them.
                          </li>
                        }
                        @if (effect.examples.length > 0) {
                          <li class="adm-muted">For example: {{ effect.examples.join(', ') }}.</li>
                        }
                      </ul>
                    } @else {
                      <p class="adm-muted">Working out what this will hide…</p>
                    }
                  } @else if (mode() === 'toggle') {
                    <p><strong>Show “{{ row.node.name }}” on the shop again?</strong></p>
                    <p class="adm-muted">
                      Its {{ row.node.primaryProductCount }} published
                      {{ row.node.primaryProductCount === 1 ? 'product' : 'products' }} return to the
                      listings.
                    </p>
                  } @else {
                    <p><strong>Remove “{{ row.node.name }}”?</strong></p>
                    <p class="adm-muted">
                      It stops appearing anywhere and cannot be brought back from this screen. Nothing
                      is removed with it: a category that still has products or child categories is
                      refused.
                    </p>
                  }

                  <div class="adm-actions">
                    <button
                      class="adm-btn--primary"
                      type="button"
                      [disabled]="busy()"
                      (click)="confirm(row.node)"
                      data-testid="confirm-yes"
                    >
                      {{ mode() === 'remove' ? 'Remove' : row.node.isActive ? 'Hide it' : 'Show it' }}
                    </button>
                    <button class="adm-btn" type="button" [disabled]="busy()" (click)="cancel()">
                      Cancel
                    </button>
                  </div>
                </div>
              }

              @if (editing()?.id === row.node.id && mode() === 'edit') {
                <div class="tree__form">
                  <h3 class="adm-label">Edit {{ row.node.name }}</h3>
                  <ng-container [ngTemplateOutlet]="formTemplate" />
                </div>
              }
            </li>
          }
        </ol>
      }

      @if (mode() === 'create') {
        <div class="tree__form" data-testid="create-form">
          <h3 class="adm-label">
            New category
            @if (form().parentCategoryId !== null) {
              under {{ parentNameOf(form().parentCategoryId) }}
            }
          </h3>
          <ng-container [ngTemplateOutlet]="formTemplate" />
        </div>
      }

      <ng-template #formTemplate>
        <form class="adm-fields" (ngSubmit)="save()" novalidate>
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
              data-testid="field-name"
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
              data-testid="field-slug"
            />
            @if (fieldError('slug'); as message) {
              <span class="adm-field-error" data-testid="slug-error">{{ message }}</span>
            } @else {
              <span class="adm-panel__hint">
                Lower-case letters, digits and single hyphens, e.g. denim-jackets.
              </span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Parent category</span>
            <select
              class="adm-input"
              name="parentCategoryId"
              [attr.aria-invalid]="fieldError('parentCategoryId') ? 'true' : null"
              [ngModel]="form().parentCategoryId"
              (ngModelChange)="patch('parentCategoryId', $event)"
              data-testid="field-parent"
            >
              <option [ngValue]="null">Top level</option>
              @for (option of parentOptions(); track option.node.id) {
                <option [ngValue]="option.node.id">{{ option.label }}</option>
              }
            </select>
            @if (fieldError('parentCategoryId'); as message) {
              <span class="adm-field-error" data-testid="parent-error">{{ message }}</span>
            } @else if (mode() === 'edit') {
              <span class="adm-panel__hint">
                Changing this moves the category and everything under it.
              </span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Mega-menu column (1–4)</span>
            <input
              class="adm-input"
              type="number"
              name="megaMenuColumn"
              min="1"
              max="4"
              [attr.aria-invalid]="fieldError('megaMenuColumn') ? 'true' : null"
              [ngModel]="form().megaMenuColumn"
              (ngModelChange)="patch('megaMenuColumn', $event === null || $event === '' ? null : +$event)"
            />
            @if (fieldError('megaMenuColumn'); as message) {
              <span class="adm-field-error">{{ message }}</span>
            }
          </label>

          <label class="adm-field adm-field--wide">
            <span class="adm-label">Description</span>
            <textarea
              class="adm-input"
              name="description"
              rows="2"
              [attr.aria-invalid]="fieldError('description') ? 'true' : null"
              [ngModel]="form().description"
              (ngModelChange)="patch('description', $event)"
            ></textarea>
            @if (fieldError('description'); as message) {
              <span class="adm-field-error">{{ message }}</span>
            }
          </label>

          <label class="adm-field adm-field--wide">
            <span class="adm-label">Banner link</span>
            <input
              class="adm-input"
              type="text"
              name="bannerLinkUrl"
              placeholder="/women/jeans"
              [attr.aria-invalid]="fieldError('bannerLinkUrl') ? 'true' : null"
              [ngModel]="form().bannerLinkUrl"
              (ngModelChange)="patch('bannerLinkUrl', $event)"
            />
            @if (fieldError('bannerLinkUrl'); as message) {
              <span class="adm-field-error" data-testid="banner-error">{{ message }}</span>
            }
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

          <div class="adm-field adm-field--wide">
            <label class="adm-check">
              <input
                type="checkbox"
                name="showInMegaMenu"
                [ngModel]="form().showInMegaMenu"
                (ngModelChange)="patch('showInMegaMenu', $event)"
              />
              Show in the mega menu
            </label>
          </div>

          <div class="adm-actions adm-field--wide">
            <button class="adm-btn--primary" type="submit" [disabled]="busy()" data-testid="save-category">
              {{ busy() ? 'Saving…' : mode() === 'create' ? 'Create category' : 'Save changes' }}
            </button>
            <button class="adm-btn" type="button" [disabled]="busy()" (click)="cancel()">Cancel</button>
          </div>
        </form>
      </ng-template>
    </section>
  `,
  styles: `
    :host { display: block; }
    .tree__head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: baseline;
      justify-content: space-between;
    }
    .tree { display: grid; gap: var(--space-1); margin: 0; padding: 0; list-style: none; }
    .tree__item {
      padding-left: calc(var(--depth, 0) * var(--space-3));
      min-width: 0;
    }
    .tree__row {
      display: flex;
      min-width: 0;
      flex-wrap: wrap;
      gap: var(--space-1);
      align-items: center;
      padding: var(--space-1);
      background: var(--color-bg);
      border: 1px solid var(--color-border);
    }
    .tree__identity { display: grid; gap: 2px; flex: 1 1 12rem; min-width: 0; }
    .tree__name { font-weight: var(--weight-semibold); overflow-wrap: anywhere; }
    .tree__meta { font-size: var(--text-small); overflow-wrap: anywhere; }
    /* It has to be allowed to shrink. With "flex: 0 0 auto" the row is sized to max-content, so its own
       "flex-wrap: wrap" never applies: six buttons ran 489 px wide inside a 320 px page, measured at
       320, 375 and 390 px. */
    .tree__buttons { flex: 1 1 auto; min-width: 0; }
    .tree__confirm, .tree__form { margin-top: var(--space-1); }
    .tree__form { display: grid; gap: var(--space-1); padding: var(--space-2); background: var(--color-bg-alt); border: 1px solid var(--color-border); }
    .tree__impact { margin: 0; padding-left: var(--space-3); display: grid; gap: var(--space-0-5); }
  `,
})
export class CategoryTree {
  /** The server's tree, as loaded. The staged order is kept separately so it can be discarded. */
  readonly tree = input.required<AdminCategoryTree>();

  /** Asks the page to reload the tree, after a save or a conflict. */
  readonly reloadRequested = output<void>();

  /** Doc 08 §3.2's `CK_Categories_Depth`. */
  protected readonly maxDepth = 3;

  private readonly api = inject(AdminCatalogApi);
  private readonly treeList = viewChild<ElementRef<HTMLElement>>('treeList');

  /**
   * The categories as the admin has arranged them, before Save order.
   *
   * It resets whenever a new tree arrives from the server, and that is deliberate rather than incidental:
   * a staged arrangement is a list of the nodes that were on screen, and once the tree has been reloaded —
   * after a create, a removal or somebody else's change — some of those nodes may not exist any more.
   * Keeping the staged copy would draw a tree the server does not have and then send a list it would
   * rightly refuse.
   */
  private readonly staged = linkedSignal<AdminCategoryTree, readonly AdminCategoryNode[] | null>({
    source: this.tree,
    computation: () => null,
  });

  protected readonly mode = signal<'none' | 'create' | 'edit' | 'toggle' | 'remove'>('none');
  protected readonly editing = signal<AdminCategoryNode | null>(null);
  protected readonly confirming = signal<AdminCategoryNode | null>(null);
  protected readonly impact = signal<CategoryDeactivationImpact | null>(null);
  protected readonly form = signal<CategoryFormValues>(emptyCategoryForm());
  protected readonly busy = signal(false);
  protected readonly failure = signal<CatalogFailure | null>(null);
  protected readonly announcement = signal('');

  /**
   * The controls to focus once the re-ordered list has actually been drawn, best first; see
   * `restoreFocus`.
   */
  private readonly focusAfterRender = signal<readonly string[] | null>(null);

  constructor() {
    afterEveryRender(() => {
      const candidates = this.focusAfterRender();
      const host = this.treeList()?.nativeElement;
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
  }

  /** What is on screen: the staged arrangement when there is one, otherwise the server's. */
  private readonly nodes = computed(() => this.staged() ?? this.tree().categories);

  protected readonly rows = computed<TreeRow[]>(() => {
    const nodes = this.nodes();
    const names = new Map(flatten(nodes).map((node) => [node.id, node.name]));

    return flatten(nodes).map((node) => {
      const parentName =
        node.parentCategoryId === null ? null : (names.get(node.parentCategoryId) ?? null);

      const parts = [
        `/${node.slug}`,
        parentName ? `level ${node.depth}, under ${parentName}` : `level ${node.depth}`,
        `${node.productCount} ${node.productCount === 1 ? 'product' : 'products'}`,
      ];

      if (!node.showInMegaMenu) {
        parts.push('not in the mega menu');
      }

      return {
        node,
        canMoveUp: canMove(nodes, node.id, -1),
        canMoveDown: canMove(nodes, node.id, 1),
        meta: parts.join(' · '),
      };
    });
  });

  /** The sibling lists whose order is staged and unsaved. */
  protected readonly pendingOrders = computed(() => {
    const staged = this.staged();
    return staged ? changedSiblingOrders(this.tree().categories, staged) : [];
  });

  /**
   * The parents a category may be given. A category cannot become its own descendant, and the three-level
   * bound means a parent has to have room for this category's own subtree underneath it.
   */
  protected readonly parentOptions = computed(() => {
    const editingNode = this.editing();
    const all = flatten(this.nodes());

    const subtreeDepth = editingNode
      ? Math.max(...flatten([editingNode]).map((node) => node.depth)) - editingNode.depth
      : 0;

    return all
      .filter((node) => {
        if (editingNode && node.path.startsWith(editingNode.path)) {
          return false;
        }

        return node.depth + 1 + subtreeDepth <= this.maxDepth;
      })
      .map((node) => ({
        node,
        label: `${'— '.repeat(node.depth - 1)}${node.name}`,
      }));
  });

  protected parentNameOf(parentCategoryId: number | null): string {
    return parentCategoryId === null
      ? 'the top level'
      : (findNode(this.nodes(), parentCategoryId)?.name ?? 'this department');
  }

  protected fieldError(field: string): string | null {
    return this.failure()?.fieldErrors?.[field]?.[0] ?? null;
  }

  protected patch<K extends keyof CategoryFormValues>(
    field: K,
    value: CategoryFormValues[K],
  ): void {
    this.form.update((current) => ({ ...current, [field]: value }));
  }

  /**
   * Stages one move among siblings and keeps focus on the button that did it, so a second press moves the
   * same category again. Without the focus restore, a keyboard user loses their place on every move — the
   * control they pressed is still the same DOM node, but a disabled button at the end of a list drops focus
   * to the document.
   */
  protected move(node: AdminCategoryNode, delta: number, event?: Event): void {
    event?.preventDefault();

    const moved = moveWithinSiblings(this.nodes(), node.id, delta);
    if (!moved) {
      return;
    }

    this.staged.set(moved);
    this.failure.set(null);

    const position = this.siblingPosition(moved, node.id);
    this.announcement.set(
      `${node.name} moved to position ${position.index} of ${position.total}. ` +
        'Press Save order to apply it.',
    );

    this.restoreFocus(
      node.id,
      delta,
      (event?.currentTarget as HTMLElement | null)?.getAttribute('data-testid') ?? null,
    );
  }

  protected saveOrder(): void {
    const pending = this.pendingOrders();
    if (pending.length === 0 || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    // One request per changed sibling list, in sequence: each list is its own decision on the server and
    // gets its own audit row, and a later one must not be sent if an earlier one was refused.
    const send = (index: number): void => {
      if (index >= pending.length) {
        this.busy.set(false);
        this.staged.set(null);
        this.announcement.set('The new order is saved.');
        this.reloadRequested.emit();
        return;
      }

      const list = pending[index];
      this.api
        .reorderCategories(this.tree().departmentId, list.parentCategoryId, list.orderedCategoryIds)
        .subscribe({
          next: () => send(index + 1),
          error: (error: unknown) => {
            this.busy.set(false);
            this.failure.set(toCatalogFailure(error));
            this.announcement.set('The new order could not be saved.');
          },
        });
    };

    send(0);
  }

  protected discardOrder(): void {
    this.staged.set(null);
    this.announcement.set('The staged order was discarded.');
  }

  protected startCreate(parentCategoryId: number | null): void {
    this.failure.set(null);
    this.editing.set(null);
    this.confirming.set(null);
    this.form.set(emptyCategoryForm(parentCategoryId));
    this.mode.set('create');
  }

  protected startEdit(node: AdminCategoryNode): void {
    this.failure.set(null);
    this.confirming.set(null);
    this.editing.set(node);
    this.form.set(categoryFormFrom(node));
    this.mode.set('edit');
  }

  protected startToggle(node: AdminCategoryNode): void {
    this.failure.set(null);
    this.editing.set(null);
    this.impact.set(null);
    this.confirming.set(node);
    this.mode.set('toggle');

    // Doc 08 §7.1: this is the one switch that takes a section off the shop, so the numbers are fetched
    // before the button is offered rather than after it is pressed.
    if (node.isActive) {
      this.api.deactivationImpact(node.id).subscribe({
        next: (impact) => {
          this.impact.set(impact);
          this.announcement.set(
            `Hiding ${node.name} would take ${impact.productsLeavingTheStorefront} products off the shop.`,
          );
        },
        error: (error: unknown) => this.failure.set(toCatalogFailure(error)),
      });
    }
  }

  protected startRemove(node: AdminCategoryNode): void {
    this.failure.set(null);
    this.editing.set(null);
    this.confirming.set(node);
    this.mode.set('remove');
  }

  protected cancel(): void {
    this.mode.set('none');
    this.editing.set(null);
    this.confirming.set(null);
    this.impact.set(null);
    this.failure.set(null);
  }

  protected confirm(node: AdminCategoryNode): void {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    const request =
      this.mode() === 'remove'
        ? this.api.removeCategory(node.id, node.rowVersion)
        : this.api.setCategoryActive(node.id, !node.isActive, node.rowVersion);

    const done = this.mode() === 'remove' ? `${node.name} was removed.` : `${node.name} was updated.`;

    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.cancel();
        this.announcement.set(done);
        this.reloadRequested.emit();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.failure.set(toCatalogFailure(error));
      },
    });
  }

  protected save(): void {
    if (this.busy()) {
      return;
    }

    const editingNode = this.editing();
    this.busy.set(true);
    this.failure.set(null);

    // Typed as "something happened" because the two calls answer differently — a create replies with the
    // new id, an edit with nothing — and the screen reloads the tree either way.
    const request: Observable<unknown> = editingNode
      ? this.api.updateCategory(editingNode.id, this.form(), editingNode.rowVersion)
      : this.api.createCategory(this.tree().departmentId, this.form());

    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.announcement.set(editingNode ? 'The category was saved.' : 'The category was created.');
        this.cancel();
        this.reloadRequested.emit();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.failure.set(toCatalogFailure(error));
      },
    });
  }

  private siblingPosition(
    nodes: readonly AdminCategoryNode[],
    categoryId: number,
  ): { index: number; total: number } {
    const node = findNode(nodes, categoryId);
    const siblings =
      node?.parentCategoryId == null
        ? nodes
        : (findNode(nodes, node.parentCategoryId)?.children ?? []);

    return {
      index: siblings.findIndex((candidate) => candidate.id === categoryId) + 1,
      total: siblings.length,
    };
  }

  /**
   * Asks for focus to go back to the move button for this category once the list has been re-rendered.
   *
   * The control the admin pressed is preferred over the one for the direction, because Alt + an arrow
   * works from either move button and jumping focus across the pair mid-keystroke is disorientating.
   *
   * **It has to be after the render, not on a microtask.** `@for` tracks by id, so a move <i>relocates</i>
   * the row's DOM node rather than rebuilding it — and re-inserting an element elsewhere in the document
   * blurs it in Chromium. Focusing on a microtask therefore focused the node and then watched Angular move
   * it and drop the focus: the unit test passed, because it calls `detectChanges()` synchronously before
   * the microtask runs, and the browser test failed. The browser was right.
   *
   * When the button that was pressed is now disabled — the category reached the end of its list — focus
   * goes to the other direction's button on the same row, which is the nearest control that still does
   * something.
   */
  private restoreFocus(categoryId: number, delta: number, pressed: string | null): void {
    const direction = delta < 0 ? `move-up-${categoryId}` : `move-down-${categoryId}`;
    const other = delta < 0 ? `move-down-${categoryId}` : `move-up-${categoryId}`;

    // The control the admin actually activated comes first. Alt + an arrow works from either move button,
    // so focusing "the button for this direction" would jump focus across the pair mid-keystroke.
    this.focusAfterRender.set([...new Set([pressed, direction, other].filter((id) => id !== null))]);
  }
}
