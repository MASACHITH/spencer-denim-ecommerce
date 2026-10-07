import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { AdminContentApi } from '../../core/content/admin-content-api';
import { AdminContentPage, ContentPageValues } from '../../core/content/admin-content.model';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/** The editor's fields. The keys are the API's field names, so an error lands on its own control. */
interface PageForm {
  title: string;
  slug: string;
  bodyHtml: string;
  seoTitle: string;
  seoDescription: string;
}

const EMPTY_FORM: PageForm = { title: '', slug: '', bodyHtml: '', seoTitle: '', seoDescription: '' };

/**
 * The content-page editor (ADM-17, GLB-10, doc 11 P13 step 12): title, address, body, SEO fields.
 *
 * **The body is typed as HTML rather than through a rich-text widget, and that is a deliberate scope
 * decision rather than an unfinished one.** Doc 11 P13 calls the field "rich text"; a WYSIWYG editor is
 * a third-party dependency whose output has to be cleaned by the same allow-list anyway, and it would
 * hide from the client exactly the thing this screen has to be honest about — that the shop accepts a
 * short list of tags and removes everything else. The hint below names the list, and the save reports
 * what it removed. A widget is a clean addition later: it writes into the same field and the same
 * sanitiser still has the last word.
 *
 * **What the sanitiser removed is reported on a successful save**, not swallowed. A save that silently
 * dropped half a paste is the defect shape this project has shipped three times, and the allow-list is
 * strict enough that a paste from Word loses something most times.
 */
@Component({
  selector: 'adm-content-page-form-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">{{ heading() }}</h1>
      <p class="adm-panel__hint">
        <a routerLink="/content/pages">Back to content pages</a>
      </p>

      @if (loaded(); as page) {
        @if (page.systemKey) {
          <p class="adm-panel__hint">
            This is one of the shop's built-in pages, so it cannot be removed — take it off the shop
            instead. You can still change its heading, its address and its words.
          </p>
        }
        @if (page.systemKey === 'contact') {
          <p class="adm-panel__hint">
            The enquiry form, the address, the opening hours, the map and the WhatsApp link are not part
            of this body — they come from
            <a routerLink="/settings">Settings</a>. Anything you write here appears above them.
          </p>
        }
        @if (page.systemKey === 'privacy' || page.systemKey === 'terms') {
          <div class="adm-state adm-state--warning">
            <p>
              This page carries legal weight. It should be your own wording, reviewed against Sri
              Lanka's Personal Data Protection Act before you put it on the shop.
            </p>
          </div>
        }
      }
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
      </div>
    }

    @if (removed().length > 0) {
      <!-- Not an error: the save worked. It is the one thing the editor could not otherwise know. -->
      <div class="adm-state adm-state--warning" data-testid="removed-from-body">
        <p>Saved. The shop does not allow some of what you pasted, so this was removed:</p>
        <ul>
          @for (item of removed(); track item) {
            <li><code>{{ item }}</code></li>
          }
        </ul>
      </div>
    }

    <!-- Always mounted, and set to a working message on the way in so the outcome is always a change. -->
    <p class="adm-muted" role="status" data-testid="page-status">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading…</p>
    } @else {
      <form class="adm-panel" (submit)="save($event)" novalidate>
        <div class="adm-fields">
          <label class="adm-field">
            <span class="adm-label">Heading</span>
            <input
              class="adm-input"
              name="title"
              type="text"
              maxlength="150"
              [value]="form().title"
              [attr.aria-invalid]="fieldError('title') ? 'true' : null"
              data-testid="field-title"
              (input)="set('title', $event)"
            />
            @if (fieldError('title'); as message) {
              <span class="adm-field-error" data-testid="error-title">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Address</span>
            <input
              class="adm-input"
              name="slug"
              type="text"
              maxlength="100"
              [value]="form().slug"
              [attr.aria-invalid]="fieldError('slug') ? 'true' : null"
              data-testid="field-slug"
              (input)="set('slug', $event)"
            />
            <span class="adm-panel__hint">
              The last part of the web address, so the page is at
              <code>/{{ form().slug || 'your-page' }}</code>. Lower-case letters, numbers and single
              hyphens. Changing it breaks any link anybody has already saved.
            </span>
            @if (fieldError('slug'); as message) {
              <span class="adm-field-error" data-testid="error-slug">{{ message }}</span>
            }
          </label>

          <label class="adm-field adm-field--wide">
            <span class="adm-label">The page itself</span>
            <textarea
              class="adm-input adm-body"
              name="bodyHtml"
              rows="18"
              [value]="form().bodyHtml"
              [attr.aria-invalid]="fieldError('bodyHtml') ? 'true' : null"
              [attr.aria-describedby]="'body-hint'"
              data-testid="field-bodyHtml"
              (input)="set('bodyHtml', $event)"
            ></textarea>
            <span class="adm-panel__hint" id="body-hint">
              Headings, paragraphs, <strong>bold</strong>, lists, links, tables and pictures. Written as
              HTML: <code>&lt;h2&gt;</code>, <code>&lt;p&gt;</code>, <code>&lt;strong&gt;</code>,
              <code>&lt;ul&gt;</code>, <code>&lt;li&gt;</code>, <code>&lt;a href="…"&gt;</code>,
              <code>&lt;table&gt;</code>, <code>&lt;img src="…" alt="…"&gt;</code>. Anything else is
              removed when you save, and we will tell you what went. A picture needs
              <code>alt</code> text describing it before the page can go on the shop.
            </span>
            @if (fieldError('bodyHtml'); as message) {
              <span class="adm-field-error" data-testid="error-bodyHtml">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Search-engine title</span>
            <input
              class="adm-input"
              name="seoTitle"
              type="text"
              maxlength="160"
              [value]="form().seoTitle"
              [attr.aria-invalid]="fieldError('seoTitle') ? 'true' : null"
              data-testid="field-seoTitle"
              (input)="set('seoTitle', $event)"
            />
            <span class="adm-panel__hint">
              Leave it empty and the heading above is used, with the shop's name after it.
            </span>
            @if (fieldError('seoTitle'); as message) {
              <span class="adm-field-error" data-testid="error-seoTitle">{{ message }}</span>
            }
          </label>

          <label class="adm-field">
            <span class="adm-label">Search-engine description</span>
            <input
              class="adm-input"
              name="seoDescription"
              type="text"
              maxlength="320"
              [value]="form().seoDescription"
              [attr.aria-invalid]="fieldError('seoDescription') ? 'true' : null"
              data-testid="field-seoDescription"
              (input)="set('seoDescription', $event)"
            />
            @if (fieldError('seoDescription'); as message) {
              <span class="adm-field-error" data-testid="error-seoDescription">{{ message }}</span>
            }
          </label>
        </div>

        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="submit"
            data-testid="save-page"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
          >
            {{ isBusy() ? 'Saving…' : 'Save' }}
          </button>
        </div>
      </form>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .adm-body {
      min-height: 22rem;
      resize: vertical;
      font-family: var(--font-mono, ui-monospace, monospace);
      font-size: var(--text-small);
      line-height: 1.6;
    }
    .adm-field--wide {
      grid-column: 1 / -1;
    }
  `,
})
export class ContentPageFormPage {
  private readonly api = inject(AdminContentApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly form = signal<PageForm>({ ...EMPTY_FORM });
  protected readonly loaded = signal<AdminContentPage | null>(null);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly removed = signal<readonly string[]>([]);
  protected readonly said = signal('');

  private readonly pageId = signal<number | null>(null);

  protected readonly heading = computed(() =>
    this.loaded() ? `Edit “${this.loaded()!.title}”` : 'Add a page',
  );

  constructor() {
    const id = Number(this.route.snapshot.paramMap.get('id'));

    if (Number.isInteger(id) && id > 0) {
      this.pageId.set(id);
      this.load(id);
    } else {
      this.isLoading.set(false);
    }
  }

  protected fieldError(field: keyof PageForm): string | null {
    return this.problem()?.fieldErrors?.[field]?.[0] ?? null;
  }

  protected set(field: keyof PageForm, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement | null)?.value ?? '';

    this.form.update((current) => ({ ...current, [field]: value }));
  }

  /**
   * Saves the form.
   *
   * The guard is here rather than on the button's `disabled` attribute: disabling the control that has
   * focus moves focus to `<body>`, which is the ADM-22 defect and six recorded instances (#109). The
   * live region says "Saving…" on the way in and the outcome on the way out, so a second identical save
   * still announces twice.
   *
   * **The editor sends every field, and that is correct here even though the API treats an omitted
   * field as "leave it alone".** It has read the whole page, so it is the one caller entitled to state
   * the whole of it; a caller that has not read the page should be sending a subset. The API's two
   * meanings exist for *that* caller — see `ContentPageValues`.
   */
  protected save(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const values: ContentPageValues = {
      title: this.form().title,
      slug: this.form().slug,
      bodyHtml: this.form().bodyHtml,

      // Empty means "clear it", which is what an empty box means to a person. The distinction the API
      // draws is between this and *not sending the field at all*, which this screen never does.
      seoTitle: this.form().seoTitle.trim() === '' ? null : this.form().seoTitle,
      seoDescription: this.form().seoDescription.trim() === '' ? null : this.form().seoDescription,
    };

    this.isBusy.set(true);
    this.problem.set(null);
    this.removed.set([]);
    this.said.set('Saving…');

    const id = this.pageId();
    const existing = this.loaded();

    if (id === null || existing === null) {
      this.api.createPage(values).subscribe({
        next: (saved) => {
          this.removed.set(saved.removedFromBody);
          this.said.set('Page created. It is not on the shop yet.');
          this.isBusy.set(false);
          void this.router.navigate(['/content/pages', saved.id]);
        },
        error: (error: unknown) => this.fail(error),
      });

      return;
    }

    this.api.updatePage(id, values, existing.rowVersion).subscribe({
      next: (saved) => {
        this.removed.set(saved.removedFromBody);
        this.said.set(
          saved.removedFromBody.length > 0
            ? 'Saved, with some of what you pasted removed. See the note above.'
            : 'Saved.',
        );
        // Re-read, because the save moved the row version and the next one needs the new token.
        this.load(id);
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  private load(id: number): void {
    this.api.page(id).subscribe({
      next: (page) => {
        this.loaded.set(page);
        this.form.set({
          title: page.title,
          slug: page.slug,
          bodyHtml: page.bodyHtml,
          seoTitle: page.seoTitle ?? '',
          seoDescription: page.seoDescription ?? '',
        });
        this.isLoading.set(false);
        this.isBusy.set(false);
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.fail(error);
      },
    });
  }

  private fail(error: unknown): void {
    this.isBusy.set(false);
    const failure = toCatalogFailure(error);
    this.problem.set(failure);
    this.said.set(failure.message);
  }
}
