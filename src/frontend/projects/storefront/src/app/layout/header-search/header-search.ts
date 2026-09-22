import { ChangeDetectionStrategy, Component, ElementRef, inject, input, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

let nextId = 0;

/**
 * The search entry in the header (GLB-04).
 *
 * TODO(SRCH-01): this is the entry point only. The suggestions overlay (products, categories,
 * popular and recent searches, keyboard navigation) is its own task; submitting here already
 * goes to the search results route that SRCH-02 will render.
 */
@Component({
  selector: 'sd-header-search',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="header-search" role="search" (submit)="submit($event)">
      <label class="sr-only" [attr.for]="inputId">Search products</label>
      <sd-icon name="search" size="sm" />
      <input
        class="header-search__input"
        [attr.id]="inputId"
        type="search"
        name="q"
        autocomplete="off"
        [attr.placeholder]="placeholder()"
        #field
      />
      <button class="header-search__submit" type="submit">
        <span class="sr-only">Search</span>
        <sd-icon name="chevron-right" size="sm" />
      </button>
    </form>
  `,
  styles: `
    :host {
      display: block;
    }
    .header-search {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      padding-inline: var(--space-1);
      border-bottom: 1px solid var(--color-border-strong);
      color: var(--color-text-muted);
    }
    .header-search__input {
      flex: 1 1 auto;
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      border: 0;
      background: transparent;
      color: var(--color-text);
      font-size: var(--text-small);
    }
    .header-search__input::placeholder {
      color: var(--color-text-muted);
      opacity: 1;
    }
    .header-search__submit {
      display: inline-grid;
      place-items: center;
      width: var(--tap);
      height: var(--tap);
      flex: none;
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--color-text);
      cursor: pointer;
    }
  `,
})
export class HeaderSearch {
  readonly placeholder = input('Search');

  protected readonly inputId = `sd-search-${nextId++}`;

  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');
  private readonly router = inject(Router);

  /** Moves the caret into the field: used when the collapsed search panel opens. */
  focusField(): void {
    this.field().nativeElement.focus();
  }

  protected submit(event: Event): void {
    event.preventDefault();

    const term = this.field().nativeElement.value.trim();
    if (term.length === 0) {
      this.field().nativeElement.focus();
      return;
    }

    void this.router.navigate(['/search'], { queryParams: { q: term } });
  }
}
