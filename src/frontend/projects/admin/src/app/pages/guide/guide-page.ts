import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute } from '@angular/router';

import { GUIDE_GROUPS, GUIDE_GROUP_HUE } from './guide.model';
import { GUIDE_CONTENT } from './guide-content.token';
import { GuideScroller } from './guide-scroller';
import { GuideSectionView } from './guide-section';
import { matchSections, searchTerms } from './guide-search';

/**
 * The admin user guide (OPS-04, #199): what every screen is for, how to do each job step by step, and
 * how to prepare photos and video. Open to every signed-in staff member, so there is no permission to
 * ask for; a screen a role cannot use is still described, and the guide says where a role decides.
 *
 * The words live in `./content`, one file per topic; this page only searches, lists and prints them.
 */
@Component({
  selector: 'sda-guide-page',
  imports: [GuideSectionView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="guide">
      <header class="guide__head">
        <h1 class="guide__title">User guide</h1>
        <p class="guide__lead">
          Everything you can do in this admin panel and how to do it, step by step: orders, products, photos and
          video, promotions, the shop's pages, staff and settings. Use the search box or the contents list to find
          a job.
        </p>
        <div class="guide__tools">
          <div class="adm-field guide__search">
            <label class="adm-label" for="guide-search">Search the guide</label>
            <input
              id="guide-search"
              class="adm-input"
              type="search"
              autocomplete="off"
              placeholder="For example: bank transfer, banner size, refund"
              [value]="query()"
              (input)="query.set(read($event))"
            />
          </div>
          <button class="adm-btn guide__print" type="button" (click)="print()">Print or save as PDF</button>
        </div>
        <p class="guide__count" role="status" data-testid="guide-count">{{ announcement() }}</p>
      </header>

      <div class="guide__body">
        <nav class="guide__toc" aria-label="Guide contents">
          <button
            class="adm-btn guide__toc-toggle"
            type="button"
            aria-controls="guide-toc-list"
            [attr.aria-expanded]="tocOpen()"
            (click)="tocOpen.set(!tocOpen())"
          >
            {{ tocOpen() ? 'Hide contents' : 'Show contents' }}
          </button>
          <div class="guide__toc-list" id="guide-toc-list" [class.is-open]="tocOpen()">
            @for (group of groups(); track group.label) {
              <div class="guide__toc-set" [style.--g]="group.hue">
                <p class="guide__toc-group">{{ group.label }}</p>
                <ul class="guide__toc-items">
                  @for (section of group.sections; track section.id) {
                    <li>
                      <a
                        class="guide__toc-link"
                        [class.is-current]="current() === section.id"
                        [attr.aria-current]="current() === section.id ? 'location' : null"
                        [href]="'#' + section.id"
                        (click)="jump($event, section.id)"
                        >{{ section.title }}</a
                      >
                    </li>
                  }
                </ul>
              </div>
            }
          </div>
        </nav>

        <div class="guide__sections" data-testid="guide-sections">
          @for (section of shown(); track section.id) {
            <sda-guide-section [section]="section" />
          } @empty {
            <div class="adm-state" data-testid="guide-empty">
              <p>Nothing in the guide matches "{{ query().trim() }}".</p>
              <p class="adm-muted">Try fewer or different words, such as "order", "banner" or "password".</p>
            </div>
          }
        </div>
      </div>
    </div>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .guide { display: grid; gap: var(--space-3); max-width: var(--container-max); min-width: 0; }
    /* The guide's own colours (UAT feedback, 2026-10-24): a blue head, and a colour per group of sections. */
    .guide__head { display: grid; gap: var(--space-1-5); padding: var(--space-2) var(--space-3); background: color-mix(in srgb, #1d4ed8 6%, var(--color-surface)); border: 1px solid color-mix(in srgb, #1d4ed8 25%, var(--color-surface)); border-inline-start: 6px solid #1d4ed8; }
    .guide__head > * { margin: 0; }
    .guide__title { font-size: var(--text-h1); font-weight: var(--weight-regular); }
    .guide__lead { max-width: 70ch; color: var(--color-text); }
    .guide__print, .guide__toc-toggle { color: #1d4ed8; font-weight: var(--weight-semibold); border-color: #1d4ed8; background: var(--color-surface); }
    .guide__print:hover, .guide__toc-toggle:hover { background: color-mix(in srgb, #1d4ed8 10%, var(--color-surface)); }
    .guide__tools { display: flex; flex-wrap: wrap; align-items: end; gap: var(--space-2); }
    .guide__search { flex: 1 1 18rem; max-width: 36rem; }
    .guide__count { min-height: 1.4em; font-size: var(--text-small); color: var(--color-text-muted); }

    .guide__body { display: grid; gap: var(--space-2); grid-template-columns: minmax(0, 1fr); align-items: start; }
    .guide__sections { display: grid; gap: var(--space-3); min-width: 0; }

    .guide__toc { display: grid; gap: var(--space-1); padding: var(--space-1-5); background: var(--color-surface); border: 1px solid var(--color-border); border-top: 4px solid #1d4ed8; min-width: 0; }
    .guide__toc-toggle { justify-self: start; }
    .guide__toc-list { display: none; }
    .guide__toc-list.is-open { display: block; }
    /* One block per group, in its colour: a bar down the left, a heading with a colour square, and links in the
       colour mixed toward the text colour so they stay readable (4.5:1 or more). */
    .guide__toc-set { margin-top: var(--space-2); }
    .guide__toc-set:first-child { margin-top: 0; }
    .guide__toc-group { display: flex; align-items: center; gap: var(--space-1); margin: 0 0 var(--space-0-5); font-size: var(--text-label); font-weight: var(--weight-semibold); letter-spacing: var(--tracking-label); text-transform: uppercase; color: color-mix(in srgb, var(--g) 62%, var(--color-text)); }
    .guide__toc-group::before { content: ''; flex: none; width: 0.7rem; height: 0.7rem; background: var(--g); }
    .guide__toc-items { margin: 0; padding: 0; list-style: none; border-inline-start: 3px solid color-mix(in srgb, var(--g) 35%, var(--color-surface)); }
    .guide__toc-items li + li { border-top: 1px solid color-mix(in srgb, var(--g) 14%, var(--color-surface)); }
    .guide__toc-link { display: flex; align-items: center; min-height: var(--tap); padding: var(--space-0-5) var(--space-1-5); line-height: 1.35; margin-inline-start: -3px; border-inline-start: 3px solid transparent; color: color-mix(in srgb, var(--g) 62%, var(--color-text)); font-size: var(--text-small); text-decoration: none; overflow-wrap: anywhere; }
    .guide__toc-link:hover { background: color-mix(in srgb, var(--g) 10%, var(--color-surface)); border-inline-start-color: var(--g); text-decoration: underline; text-underline-offset: 3px; }
    .guide__toc-link.is-current { font-weight: var(--weight-semibold); background: color-mix(in srgb, var(--g) 14%, var(--color-surface)); border-inline-start-color: var(--g); }

    @media (min-width: 1100px) {
      .guide__body { grid-template-columns: 17rem minmax(0, 1fr); gap: var(--space-3); }
      .guide__toc { position: sticky; top: var(--space-2); max-height: calc(100vh - var(--space-4)); overflow-y: auto; }
      .guide__toc-toggle { display: none; }
      .guide__toc-list { display: block; }
    }

    /* On paper: the sections only. The application's own frame is hidden by the rule in styles.css. */
    @media print {
      :host { padding: 0; }
      .guide__tools, .guide__toc, .guide__count { display: none; }
      .guide__body { display: block; }
    }
  `,
})
export class GuidePage {
  private readonly scroller = inject(GuideScroller);
  private readonly injector = inject(Injector);
  private readonly content = inject(GUIDE_CONTENT);

  protected readonly query = signal('');
  protected readonly tocOpen = signal(false);

  /** The section the reader last jumped to, marked in the contents list. */
  protected readonly current = signal<string | null>(inject(ActivatedRoute).snapshot.fragment ?? null);

  private readonly terms = computed(() => searchTerms(this.query()));

  protected readonly shown = computed(() => matchSections(this.content.sections, this.content.index, this.terms()));

  /** The contents list follows the search: only the sections still shown, under their group. */
  protected readonly groups = computed(() =>
    GUIDE_GROUPS.map((label) => ({
      label,
      hue: GUIDE_GROUP_HUE[label],
      sections: this.shown().filter((section) => section.group === label),
    })).filter((group) => group.sections.length > 0),
  );

  /** Read out when a search changes the list; empty until then, so opening the page says nothing extra. */
  protected readonly announcement = computed(() => {
    if (this.terms().length === 0) {
      return '';
    }

    const found = this.shown().length;
    return found === 0
      ? 'No sections match your search.'
      : `${found} of ${this.content.sections.length} sections match your search.`;
  });

  constructor() {
    // A link such as /guide#photos-and-video arrives with its anchor; the page has to draw first.
    const fragment = inject(ActivatedRoute).snapshot.fragment;

    if (fragment) {
      afterNextRender(() => this.scroller.to(fragment, false), { injector: this.injector });
    }
  }

  protected read(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected jump(event: Event, id: string): void {
    event.preventDefault();
    this.current.set(id);
    this.tocOpen.set(false);
    // After the phone-width contents list has closed, so the section is scrolled to where it ends up.
    afterNextRender(() => this.scroller.to(id), { injector: this.injector });
  }

  protected print(): void {
    globalThis.print();
  }
}
