import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import {
  AdminHiddenSearchTerm,
  AdminPopularSearch,
  AdminPopularSearchesApi,
} from '../../core/content/admin-popular-searches-api';
import { ContentFailure, toContentFailure } from '../../core/content/admin-site-content-api';

/**
 * Popular searches (SRCH-01, issue #78): the terms other shoppers are offered in the search box, and the
 * one control staff have over them — hiding a term.
 *
 * The list is real: it is what shoppers searched for, with results, in the last 30 days, once a term has
 * been searched often enough. Nothing can be added by hand — a "popular" term nobody searched for would be
 * a made-up statistic — so the screen's only job is the safety valve: take a term off the list (an
 * offensive one, or one that should not be promoted), and put it back.
 */
@Component({
  selector: 'sda-popular-searches-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Popular searches</h1>
      <p class="adm-panel__hint">
        What shoppers see under “Popular searches” when they open the search box: the terms searched most,
        with results, in the last 30 days. A term appears once it has been searched at least
        {{ minimum() }} times. Hide a term to take it off the list; nothing else about it changes.
      </p>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="ps-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind === 'forbidden') {
          <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
        } @else {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="ps-reload" (click)="reload()">Try again</button>
          </div>
        }
      </div>
    }

    <p class="adm-muted" role="status" tabindex="-1" data-testid="ps-said">{{ said() }}</p>

    @if (!forbidden()) {
      <section class="adm-panel" aria-labelledby="ps-shown-title">
        <h2 class="adm-panel__title" id="ps-shown-title">Shown to shoppers</h2>
        @if (isLoading()) {
          <p class="adm-muted">Loading…</p>
        } @else if (shown().length === 0) {
          <p class="adm-muted" data-testid="ps-empty">
            None yet. Shoppers see ways into the shop instead until enough searches have been made.
          </p>
        } @else {
          <ol class="ps-list" data-testid="ps-shown">
            @for (row of shown(); track row.term) {
              <li class="ps-row">
                <span class="ps-row__term">{{ row.term }}</span>
                <span class="ps-row__count">{{ row.searches }} {{ row.searches === 1 ? 'search' : 'searches' }}</span>
                <button
                  class="adm-btn"
                  type="button"
                  [attr.aria-disabled]="isBusy() ? 'true' : null"
                  [attr.aria-label]="'Hide ' + row.term"
                  [attr.data-testid]="'ps-hide-' + $index"
                  (click)="hide(row)"
                >
                  Hide
                </button>
              </li>
            }
          </ol>
        }
      </section>

      <section class="adm-panel" aria-labelledby="ps-hidden-title">
        <h2 class="adm-panel__title" id="ps-hidden-title">Hidden</h2>
        <form class="ps-hide-form" (submit)="hideTyped($event)">
          <label class="adm-field">
            <span class="adm-label">Hide a term before it appears</span>
            <input
              class="adm-input"
              type="text"
              maxlength="100"
              autocomplete="off"
              [value]="typed()"
              [attr.aria-invalid]="fieldError() ? 'true' : null"
              data-testid="ps-term"
              (input)="onTyped($event)"
            />
            @if (fieldError(); as text) {
              <span class="adm-field-error" role="alert" data-testid="ps-error">{{ text }}</span>
            }
          </label>
          <div class="adm-actions">
            <button class="adm-btn" type="submit" [attr.aria-disabled]="isBusy() ? 'true' : null" data-testid="ps-hide-typed">
              Hide this term
            </button>
          </div>
        </form>
        @if (hidden().length === 0) {
          <p class="adm-muted" data-testid="ps-hidden-empty">No terms are hidden.</p>
        } @else {
          <ul class="ps-list" data-testid="ps-hidden">
            @for (row of hidden(); track row.term) {
              <li class="ps-row">
                <span class="ps-row__term">{{ row.term }}</span>
                <button
                  class="adm-btn"
                  type="button"
                  [attr.aria-disabled]="isBusy() ? 'true' : null"
                  [attr.aria-label]="'Show ' + row.term + ' again'"
                  [attr.data-testid]="'ps-show-' + $index"
                  (click)="show(row)"
                >
                  Show again
                </button>
              </li>
            }
          </ul>
        }
      </section>
    }
  `,
  styles: `
    :host {
      padding: var(--gutter);
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-3);
      min-width: 0;
    }
    .ps-list {
      display: grid;
      gap: var(--space-1);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .ps-row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1) var(--space-2);
      padding: var(--space-1) var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .ps-row__term {
      flex: 1 1 12rem;
      min-width: 0;
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .ps-row__count {
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .ps-hide-form {
      display: grid;
      gap: var(--space-1);
      margin-block-end: var(--space-2);
    }
  `,
})
export class PopularSearchesPage {
  private readonly api = inject(AdminPopularSearchesApi);

  protected readonly shown = signal<readonly AdminPopularSearch[]>([]);
  protected readonly hidden = signal<readonly AdminHiddenSearchTerm[]>([]);
  protected readonly minimum = signal(5);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly forbidden = signal(false);
  protected readonly problem = signal<ContentFailure | null>(null);
  protected readonly said = signal('');
  protected readonly typed = signal('');
  protected readonly fieldError = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected onTyped(event: Event): void {
    this.typed.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected reload(): void {
    this.problem.set(null);
    this.load();
  }

  protected hide(row: AdminPopularSearch): void {
    this.change('hide', row.term, `“${row.term}” is hidden from shoppers.`);
  }

  protected show(row: AdminHiddenSearchTerm): void {
    this.change('show', row.term, `“${row.term}” can appear again.`);
  }

  protected hideTyped(event: Event): void {
    event.preventDefault();
    const term = this.typed().trim();
    if (term.length === 0) {
      this.fieldError.set('Type the term to hide.');
      return;
    }

    this.change('hide', term, `“${term}” is hidden from shoppers.`, () => this.typed.set(''));
  }

  private change(action: 'hide' | 'show', term: string, done: string, then?: () => void): void {
    if (this.isBusy()) {
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldError.set(null);
    this.said.set(action === 'hide' ? 'Hiding…' : 'Showing…');

    (action === 'hide' ? this.api.hide(term) : this.api.show(term)).subscribe({
      next: (result) => {
        then?.();
        this.load(() =>
          this.said.set(result.changed ? done : action === 'hide' ? 'That term was already hidden.' : 'That term was not hidden.'),
        );
      },
      error: (error: unknown) => {
        this.isBusy.set(false);
        const failure = toContentFailure(error, 'this term');
        if (failure.fields['term']) {
          this.fieldError.set(failure.fields['term']);
          this.said.set(failure.fields['term']);
          return;
        }

        this.problem.set(failure);
        this.said.set(failure.message);
      },
    });
  }

  private load(then?: () => void): void {
    this.api.list().subscribe({
      next: (list) => {
        this.shown.set(list.shown);
        this.hidden.set(list.hidden);
        this.minimum.set(list.minimumSearches);
        this.isLoading.set(false);
        this.isBusy.set(false);
        this.forbidden.set(false);
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.isBusy.set(false);
        const failure = toContentFailure(error, 'this term');
        if (failure.kind === 'forbidden') {
          this.forbidden.set(true);
          this.problem.set({
            ...failure,
            message: 'Your role does not include popular searches. Ask an administrator for the Content permission.',
          });
        } else {
          this.problem.set(failure);
        }

        this.said.set(failure.message);
      },
    });
  }
}
