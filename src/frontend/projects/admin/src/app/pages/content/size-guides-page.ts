import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
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
import { AdminSizeGuideListItem } from '../../core/content/admin-site-content.model';

/**
 * Size guides (PDP-04, doc 11 P13 step 11; #192): the list, with how many categories and products use
 * each one. A guide in use cannot be removed — the server refuses with `size_guide_in_use` and the screen
 * says what still points at it.
 */
@Component({
  selector: 'sda-size-guides-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Size guides</h1>
      <p class="adm-panel__hint">
        A size guide is the table a shopper opens from a product page. Make one here, then choose it on a
        category or a product.
      </p>
      @if (!forbidden()) {
        <div class="adm-actions">
          <a class="adm-btn--primary" routerLink="/content/size-guides/new" data-testid="sg-add">Add a size guide</a>
        </div>
      }
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="sg-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind === 'forbidden') {
          <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
        } @else {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="sg-reload" (click)="load()">
              {{ failure.stale ? 'Reload' : 'Try again' }}
            </button>
          </div>
        }
      </div>
    }

    <p class="adm-muted" role="status" tabindex="-1" data-testid="sg-said">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading size guides…</p>
    } @else if (!forbidden()) {
      @if (guides().length === 0) {
        <p class="adm-muted" data-testid="sg-empty">No size guides yet. Add the first one above.</p>
      } @else {
        <div class="adm-table-scroll" tabindex="0" role="region" aria-label="Size guides">
          <table class="adm-table" data-testid="sg-table">
            <caption class="sr-only">Size guides and where they are used</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Categories</th>
                <th scope="col">Products</th>
                <th scope="col">Image</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (guide of guides(); track guide.id) {
                <tr [attr.data-testid]="'sg-' + guide.id">
                  <th scope="row">
                    <a class="sg-link" [routerLink]="['/content/size-guides', guide.id]" [attr.data-testid]="'sg-open-' + guide.id">{{ guide.name }}</a>
                  </th>
                  <td [attr.data-testid]="'sg-categories-' + guide.id">{{ guide.categoryCount }}</td>
                  <td [attr.data-testid]="'sg-products-' + guide.id">{{ guide.productCount }}</td>
                  <td>{{ guide.hasImage ? 'Yes' : 'No' }}</td>
                  <td>
                    <div class="adm-actions">
                      <button
                        class="adm-btn--danger"
                        type="button"
                        [attr.aria-disabled]="isBusy() ? 'true' : null"
                        [attr.data-testid]="'sg-remove-' + guide.id"
                        (click)="remove(guide)"
                      >
                        {{ removingId() === guide.id ? 'Yes, remove it' : 'Remove' }}<span class="sr-only"> {{ guide.name }}</span>
                      </button>
                      @if (removingId() === guide.id) {
                        <button class="adm-btn" type="button" [attr.data-testid]="'sg-keep-' + guide.id" (click)="removingId.set(null)">
                          Keep it
                        </button>
                      }
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      /* One track that may shrink below its content, so a wide table scrolls in its own region
         rather than widening the page. */
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-3);
      min-width: 0;
    }
    /* The screen-reader-only labels inside the table are absolutely positioned; without a positioned
       scroller they escape it and widen the page at 320 px. */
    .adm-table-scroll {
      position: relative;
    }
    .adm-table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-small);
    }
    .adm-table th,
    .adm-table td {
      border-bottom: 1px solid var(--color-border);
      padding: var(--space-1);
      text-align: left;
      vertical-align: top;
    }
    .adm-table thead th {
      background: var(--color-bg-alt);
    }
    .adm-table tbody th {
      min-width: 9rem;
    }
    .sg-link {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      min-width: var(--tap);
      text-decoration: underline;
      overflow-wrap: anywhere;
    }
  `,
})
export class SizeGuidesPage {
  private readonly api = inject(AdminSiteContentApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly guides = signal<readonly AdminSizeGuideListItem[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly forbidden = signal(false);
  protected readonly problem = signal<ContentFailure | null>(null);
  protected readonly said = signal('');
  protected readonly removingId = signal<number | null>(null);

  constructor() {
    this.load();
  }

  protected remove(guide: AdminSizeGuideListItem): void {
    if (this.isBusy()) {
      return;
    }

    if (this.removingId() !== guide.id) {
      this.removingId.set(guide.id);
      this.said.set(`Press Remove again to delete ${guide.name}.`);
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(`Removing ${guide.name}…`);

    this.api.deleteSizeGuide(guide.id, guide.rowVersion).subscribe({
      next: () => {
        this.removingId.set(null);
        this.load(() => {
          this.said.set(`${guide.name} is removed.`);
          this.rehomeFocus();
        });
      },
      error: (error: unknown) => {
        this.removingId.set(null);
        this.isBusy.set(false);
        const failure = toContentFailure(error, 'this size guide');
        const message = failure.code === 'size_guide_in_use' ? inUseMessage(guide, failure) : failure.message;
        this.problem.set({ ...failure, message });
        this.said.set(message);
      },
    });
  }

  protected load(then?: () => void): void {
    this.problem.set(null);

    this.api.sizeGuides().subscribe({
      next: (guides) => {
        this.guides.set(guides);
        this.isLoading.set(false);
        this.isBusy.set(false);
        this.forbidden.set(false);
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.isBusy.set(false);
        const failure = toContentFailure(error, 'the size guides');

        if (failure.kind === 'forbidden') {
          this.forbidden.set(true);
          this.problem.set({
            ...failure,
            message: 'Your role does not include size guides. Ask an administrator for the Content permission.',
          });
          return;
        }

        this.problem.set(failure);
      },
    });
  }

  private rehomeFocus(): void {
    afterNextRender(
      () => {
        const active = this.document.activeElement;
        if (active !== null && active !== this.document.body && active.isConnected) {
          return;
        }
        this.document.querySelector<HTMLElement>('[data-testid="sg-said"]')?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }
}

/**
 * "Still used by 2 categories and 5 products" — the counts from the 409 body, whether they arrive at its
 * top level or under `details`, falling back to the counts the list already showed.
 */
function inUseMessage(guide: AdminSizeGuideListItem, failure: ContentFailure): string {
  const body = (failure.details ?? {}) as Record<string, unknown>;
  const nested = (typeof body['details'] === 'object' && body['details'] !== null
    ? body['details']
    : {}) as Record<string, unknown>;
  const read = (key: 'categoryCount' | 'productCount'): number => {
    const value = body[key] ?? nested[key];
    return typeof value === 'number' ? value : guide[key];
  };

  const categories = read('categoryCount');
  const products = read('productCount');
  const parts: string[] = [];

  if (categories > 0) {
    parts.push(`${categories} ${categories === 1 ? 'category' : 'categories'}`);
  }
  if (products > 0) {
    parts.push(`${products} ${products === 1 ? 'product' : 'products'}`);
  }

  const users = parts.length > 0 ? parts.join(' and ') : 'a category or product';

  return `${guide.name} cannot be removed: it is still used by ${users}. Choose another size guide for them first.`;
}
