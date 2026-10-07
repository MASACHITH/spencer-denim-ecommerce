import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { RouterLink } from '@angular/router';

import { AdminContentApi } from '../../core/content/admin-content-api';
import { AdminContentPageListItem } from '../../core/content/admin-content.model';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/**
 * The content pages screen (ADM-17, GLB-10, doc 11 P13 step 12).
 *
 * **It opens on six unwritten pages, and the screen says so rather than looking broken.** All six
 * system pages are seeded unpublished with empty bodies, because doc 11 P13 is explicit that the copy
 * is the client's and that Privacy and Terms need a PDPA review. So the list's first job is to tell the
 * client which pages are waiting for their words, and the second is to let them publish one when it is
 * ready. Nothing in this repository writes a returns policy.
 *
 * **The Contact page is the one of the six that can be published immediately**, and the screen says so,
 * because its content is the form and the Settings details rather than a body the client has to write.
 * That is worth pointing out on the screen: without it, a client reasonably assumes all six are blocked
 * on them and the shop opens with no way to send an enquiry except WhatsApp.
 *
 * **Publishing is a separate act from saving** (its own route, its own `StatusChange` audit row), so it
 * is a button here rather than a checkbox on the editor — the rule W1-03 established for products.
 */
@Component({
  selector: 'sda-content-pages-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Content pages</h1>
      <p class="adm-panel__hint">
        The six pages the shop is built around, plus any extra pages you add. A page is only visible to
        shoppers once you put it on the shop, and the footer links it only then — so a page you have not
        written yet is not a broken link.
      </p>
      <p class="adm-panel__hint">
        <strong>Contact</strong> needs no writing from you: its content is the enquiry form and the
        details on the Settings screen, so you can put it on the shop as soon as those are filled in.
      </p>

      <div class="adm-actions">
        <a class="adm-btn--primary" routerLink="/content/pages/new">Add a page</a>
      </div>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
      </div>
    }

    <!-- Always mounted, so a screen reader is watching before there is anything to say. Every action
         sets a working message on the way in and the outcome on the way out, so a second identical
         action still speaks — a live region announces a *change* (ADM-22). -->
    <p class="adm-muted" role="status" data-testid="content-status">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading…</p>
    } @else {
      <table class="adm-table" data-testid="content-pages">
        <!-- tabindex="-1" so 'focusTable' can land here after a row is removed: a <caption> is not
             focusable otherwise, and focus would fall to <body> instead, which is the harm the
             re-homing exists to prevent. -1 keeps it out of the tab order. -->
        <caption class="adm-muted" tabindex="-1">
          {{ pages().length }} pages. {{ unwritten() }} still waiting for your copy.
        </caption>
        <thead>
          <tr>
            <th scope="col">Page</th>
            <th scope="col">Address</th>
            <th scope="col">On the shop</th>
            <th scope="col">Actions</th>
          </tr>
        </thead>
        <tbody>
          @for (page of pages(); track page.id) {
            <tr [attr.data-testid]="'page-' + page.id">
              <th scope="row">
                <a [routerLink]="['/content/pages', page.id]">{{ page.title }}</a>
                @if (page.systemKey) {
                  <span class="adm-pill">built in</span>
                }
                @if (!page.hasBody) {
                  <span class="adm-pill adm-pill--hidden" [attr.data-testid]="'empty-' + page.id">
                    no copy yet
                  </span>
                }
                @if (page.imagesMissingAltText > 0) {
                  <span class="adm-pill adm-pill--hidden">
                    {{ page.imagesMissingAltText }} image(s) need alt text
                  </span>
                }
              </th>
              <td><code>/{{ page.slug }}</code></td>
              <td>
                @if (page.isPublished) {
                  <span class="adm-pill">Yes</span>
                } @else {
                  <span class="adm-pill adm-pill--hidden">No</span>
                }
              </td>
              <td>
                <div class="adm-actions">
                  <!-- aria-disabled and a guard in the handler, never [disabled] (#109): disabling the
                       control that has focus moves focus to <body>. -->
                  <button
                    class="adm-btn"
                    type="button"
                    [attr.aria-disabled]="isBusy() ? 'true' : null"
                    [attr.data-testid]="'publish-' + page.id"
                    (click)="togglePublished(page)"
                  >
                    {{ page.isPublished ? 'Take off the shop' : 'Put on the shop' }}
                  </button>
                  @if (page.canBeDeleted) {
                    <button
                      class="adm-btn--danger"
                      type="button"
                      [attr.aria-disabled]="isBusy() ? 'true' : null"
                      [attr.data-testid]="'remove-' + page.id"
                      (click)="remove(page)"
                    >
                      Remove
                    </button>
                  }
                </div>
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .adm-table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-small);
    }
    .adm-table caption {
      text-align: left;
      padding-bottom: var(--space-1);
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
    /* A standalone link in a cell, so the 44 px floor applies as it does to the buttons beside it —
       the lesson the storefront's 404 page records about a 175 x 20 link that passed a
       button-scoped selector. */
    .adm-table a {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      text-decoration: underline;
    }
  `,
})
export class ContentPagesPage {
  private readonly api = inject(AdminContentApi);
  private readonly document = inject(DOCUMENT);

  protected readonly pages = signal<readonly AdminContentPageListItem[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly said = signal('');

  protected readonly unwritten = computed(
    () => this.pages().filter((page) => !page.hasBody && page.systemKey !== 'contact').length,
  );

  constructor() {
    this.load();
  }

  /**
   * Publishes or unpublishes a page.
   *
   * The refusal worth naming is the alt-text one: doc 08 §3.3 and GLB-12 require a public image to have
   * alt text before the content using it is published, so a 409 `alt_text_required` is not a bug and the
   * message says how many images to fix.
   */
  protected togglePublished(page: AdminContentPageListItem): void {
    if (this.isBusy()) {
      return;
    }

    const wanted = !page.isPublished;

    this.begin(wanted ? 'Putting the page on the shop…' : 'Taking the page off the shop…');

    this.api.setPublished(page.id, wanted, page.rowVersion).subscribe({
      next: () => {
        this.said.set(
          wanted
            ? `“${page.title}” is on the shop. The footer links it now.`
            : `“${page.title}” is off the shop. Shoppers get a "page not found" for it.`,
        );
        this.reload();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  /**
   * Removes a custom page.
   *
   * **Focus is re-homed, because the row this destroys holds the button being pressed.** Destroying the
   * focused node drops focus to `<body>`, which is the same harm as `[disabled]` (#109) from the other
   * direction, and the project's rule is that re-homing is necessary and not sufficient. The table's own
   * caption is the sensible destination: it is where the next thing to read is.
   */
  protected remove(page: AdminContentPageListItem): void {
    if (this.isBusy()) {
      return;
    }

    this.begin('Removing the page…');

    this.api.deletePage(page.id, page.rowVersion).subscribe({
      next: () => {
        this.said.set(`“${page.title}” has been removed. Its address is free to reuse.`);
        this.reload(() => this.focusTable());
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  private load(then?: () => void): void {
    this.api.pages().subscribe({
      next: (pages) => {
        this.pages.set(pages);
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

  /** Re-reads the list, because every write moves a row version and the buttons carry one. */
  private reload(then?: () => void): void {
    this.load(then);
  }

  private begin(message: string): void {
    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set(message);
  }

  private fail(error: unknown): void {
    this.isBusy.set(false);
    const failure = toCatalogFailure(error);
    this.problem.set(failure);

    // The outcome in the live region too, not only in the banner: a screen-reader user who pressed a
    // button needs to hear that it did not work, and the banner is above the table they are in.
    this.said.set(failure.message);
  }

  private focusTable(): void {
    queueMicrotask(() => {
      this.document.querySelector<HTMLElement>('[data-testid="content-pages"] caption')?.focus();
    });
  }
}
