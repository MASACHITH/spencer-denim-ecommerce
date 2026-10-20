import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import {
  AdminSiteContentApi,
  ContentFailure,
  toContentFailure,
} from '../../core/content/admin-site-content-api';
import { AdminHomeContent } from '../../core/content/admin-site-content.model';
import { HomeSectionEditor } from './home-section-editor';

/**
 * The Homepage screen (ADM-17, HOME-01…10, doc 11 P13 section B; #192).
 *
 * The ten blocks in the proposal's order, each with its own settings form and its own items. Each block
 * saves on its own — there is no page-wide Save — because each block is its own row with its own row
 * version, and one Save across ten blocks would turn one stale block into ten refused changes.
 *
 * Like every admin route, the guard asks only for a session: the API is the security boundary, and a role
 * without `Content.View` is shown the refusal rather than an empty page.
 */
@Component({
  selector: 'sda-home-content-page',
  imports: [HomeSectionEditor, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Homepage</h1>
      <p class="adm-panel__hint">
        The ten blocks of the homepage, top to bottom. Each block saves on its own. A block or item with
        dates shows only between them; dates and times are this shop's own time. The homepage picks up a
        save straight away.
      </p>
    </section>

    <p class="sr-only" role="status" data-testid="home-said">{{ said() }}</p>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="home-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind === 'forbidden') {
          <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
        } @else {
          <div class="adm-actions">
            <button class="adm-btn" type="button" (click)="load()">Try again</button>
          </div>
        }
      </div>
    }

    @if (isLoading()) {
      <p class="adm-muted">Loading the homepage…</p>
    } @else if (content(); as loaded) {
      @for (section of loaded.sections; track section.id) {
        <sda-home-section-editor
          [section]="section"
          [departments]="loaded.departments"
          [benefitIcons]="loaded.benefitIcons"
          (saved)="afterSave($event)"
          (reloadRequested)="load()"
        />
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
  `,
})
export class HomeContentPage {
  private readonly api = inject(AdminSiteContentApi);

  protected readonly content = signal<AdminHomeContent | null>(null);
  protected readonly isLoading = signal(true);
  protected readonly problem = signal<ContentFailure | null>(null);
  protected readonly said = signal('');

  constructor() {
    this.load();
  }

  protected afterSave(message: string): void {
    this.said.set(message);
    this.load();
  }

  protected load(): void {
    this.problem.set(null);

    this.api.home().subscribe({
      next: (content) => {
        this.content.set(content);
        this.isLoading.set(false);
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        const failure = toContentFailure(error);
        this.problem.set(
          failure.kind === 'forbidden'
            ? {
                ...failure,
                message:
                  'Your role does not include the homepage. Ask an administrator for the Content permission.',
              }
            : failure,
        );
        // A refusal on a reload leaves nothing stale on screen to edit.
        if (failure.kind === 'forbidden') {
          this.content.set(null);
        }
      },
    });
  }
}
