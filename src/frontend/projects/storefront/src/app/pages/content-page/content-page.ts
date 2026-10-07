import { ChangeDetectionStrategy, Component, DestroyRef, RESPONSE_INIT, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap } from 'rxjs';

import { ContactPanel } from './contact-panel';
import { CONTENT_PAGE_KEYS, ContentPageDto } from '../../core/content/content-page.model';
import { ContentPageService } from '../../core/content/content-page.service';
import { PageMeta } from '../../core/platform/page-metadata';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

/** What a page that is not there should answer, so a crawler does not index it as a real page. */
const NOT_FOUND_STATUS = 404;

/** What the component is doing, which is the difference between three quite different renders. */
type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'page'; readonly page: ContentPageDto }
  | { readonly kind: 'missing' };

/**
 * A published content page (GLB-10): About, Contact, Returns, Shipping, Privacy, Terms, or any extra
 * page the client has created.
 *
 * **The body is rendered with `[innerHTML]` and never with `bypassSecurityTrustHtml`.** The API has
 * already cleaned it against the allow-list on save (doc 08 §3.7), which is the authoritative control;
 * binding it this way means Angular's own DOM sanitiser runs over it a second time, here and during the
 * server render. That costs nothing and it is the layer that still holds for a row written by a support
 * script, a data fix or a restore — none of which goes through the save path that cleans it. Marking the
 * value trusted would throw that away in exchange for nothing.
 *
 * **It can answer 404, and that is the whole reason the route reaches the component at all.** The guard
 * matches against the published-page index, so an address that reaches here was published when the index
 * was read; a page unpublished in the seconds since answers 404 from the API, and this renders the
 * missing state with a real 404 status. A draft, a removed page and a slug that never existed are
 * deliberately indistinguishable — "there is a page here you may not see" is itself something a draft
 * policy should not disclose.
 *
 * **The Contact page is the one of the six with more than a body**, and it is identified by its
 * `systemKey` rather than by its slug, because the slug is editable (doc 11 P13 step 12) and a client
 * who renames it to `contact-us` must not lose the form.
 */
@Component({
  selector: 'sd-content-page',
  imports: [RouterLink, ContactPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (state().kind) {
      @case ('page') {
        <article class="container page">
          <header class="page__header">
            <h1 class="page__title">{{ page()!.title }}</h1>
            @if (updatedOn(); as updated) {
              <p class="page__updated t-small muted">Last updated {{ updated }}</p>
            }
          </header>

          @if (page()!.bodyHtml) {
            <!-- Already-sanitised HTML from the API, bound so Angular's sanitiser runs over it too.
                 See the component's own documentation for why both layers are kept. -->
            <div class="page__body" [innerHTML]="page()!.bodyHtml"></div>
          } @else if (!isContact()) {
            <!-- A page the client has published with nothing in it. It is a real page and it says so,
                 rather than rendering a heading over empty space. The Contact page is excluded because
                 its content is the panel below, not a body. -->
            <p class="page__empty">
              We are still writing this page. In the meantime, our team can answer anything you need.
            </p>
            @if (contactPath(); as path) {
              <p><a class="link page__link" [routerLink]="path">Get in touch</a></p>
            }
          }

          @if (isContact()) {
            <sd-contact-panel />
          }
        </article>
      }
      @case ('missing') {
        <div class="container page page--missing">
          <h1 class="page__title">We could not find that page</h1>
          <p class="page__empty">
            The address may have changed, or the page may not be published yet. Everything we sell is in
            one of these:
          </p>
          <ul class="page__links">
            <li><a class="btn btn--secondary" routerLink="/">Home</a></li>
            <li><a class="btn btn--secondary" routerLink="/new-arrivals">New Arrivals</a></li>
            <li><a class="btn btn--secondary" routerLink="/sale">Sale</a></li>
          </ul>
        </div>
      }
      @default {
        <!-- The server render never shows this: the page is awaited before the HTML is produced. It is
             what a client-side navigation shows for the moment the body is in flight, and it keeps the
             heading's space so the layout does not jump. -->
        <div class="container page" aria-busy="true">
          <p class="page__empty">Loading…</p>
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .page {
      max-width: 72ch;
      padding-block: var(--space-6) var(--space-12);
    }
    .page--missing {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
    }
    .page__header {
      display: grid;
      gap: var(--space-1);
      padding-bottom: var(--space-4);
    }
    .page__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .page__empty {
      color: var(--color-text-muted);
      max-width: 62ch;
    }
    /* The 44 px floor applies to a plain link as much as to a button — the lesson '.missing__home'
       records on the 404 page, where a 175 x 20 link passed a selector scoped to the buttons beside
       it. 'width: fit-content' so the row is not a wide target with most of it empty. */
    .page__link {
      display: inline-flex;
      align-items: center;
      width: fit-content;
      min-height: var(--tap);
    }
    .page__links {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      list-style: none;
      padding-top: var(--space-1);
    }

    /* The body is admin-written markup, so it is styled by element rather than by class: there are no
       classes on it to hook, because the sanitiser's allow-list does not permit the 'class' attribute.
       That is deliberate — a client cannot accidentally reach into the theme — and it means every
       element the allow-list does permit needs a rule here, or it renders unstyled. */
    .page__body {
      line-height: 1.7;
    }
    .page__body > * + * {
      margin-top: var(--space-2);
    }
    .page__body h2 {
      font-size: var(--text-h3);
      margin-top: var(--space-5);
    }
    .page__body h3 {
      font-size: var(--text-h4);
      margin-top: var(--space-4);
    }
    .page__body h4 {
      font-size: var(--text-body);
      font-weight: 600;
      margin-top: var(--space-3);
    }
    .page__body ul,
    .page__body ol {
      padding-left: var(--space-3);
    }
    .page__body li + li {
      margin-top: var(--space-0-5);
    }
    .page__body a {
      color: var(--color-primary);
      text-decoration: underline;
      /* An inline link inside a paragraph cannot be given a 44 px box without breaking the line box it
         sits in, and WCAG 2.2's 2.5.8 exempts a target "in a sentence or block of text" for exactly
         that reason. The underline and the 1.7 line height are what make it hittable; the project's
         44 px floor applies to the standalone links above, which have it. */
    }
    .page__body img {
      max-width: 100%;
      height: auto;
      display: block;
    }
    .page__body figure {
      margin: 0;
    }
    .page__body figcaption {
      color: var(--color-text-muted);
      font-size: var(--text-small);
      padding-top: var(--space-0-5);
    }
    .page__body blockquote {
      border-left: 2px solid var(--color-border);
      padding-left: var(--space-2);
      color: var(--color-text-muted);
    }
    .page__body hr {
      border: 0;
      border-top: 1px solid var(--color-border);
    }
    /* A shipping page is mostly a table of zones and fees, and a wide one must not make the whole
       document scroll sideways (GLB-09: no horizontal scroll at any of the eight widths). The wrapper
       the body would need for that cannot be added — the allow-list permits no markup around the table
       — so the table itself scrolls, which 'display: block' plus 'overflow-x: auto' gives it. */
    .page__body table {
      display: block;
      overflow-x: auto;
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-small);
    }
    .page__body th,
    .page__body td {
      border: 1px solid var(--color-border);
      padding: var(--space-1) var(--space-1-5);
      text-align: left;
      vertical-align: top;
    }
    .page__body th {
      background: var(--color-bg-alt);
      font-weight: 600;
    }
    .page__body caption {
      caption-side: bottom;
      color: var(--color-text-muted);
      font-size: var(--text-small);
      padding-top: var(--space-1);
      text-align: left;
    }
  `,
})
export class ContentPage {
  private readonly route = inject(ActivatedRoute);
  private readonly content = inject(ContentPageService);
  private readonly pageMeta = inject(PageMeta);
  private readonly robots = inject(RobotsMeta);
  private readonly response = inject(RESPONSE_INIT, { optional: true });

  /** Set once per resolved address, so a client-side navigation away clears what this page claimed. */
  private readonly announcedMissing = signal(false);

  protected readonly state = toSignal(
    this.route.paramMap.pipe(
      switchMap((params) => {
        const slug = params.get('slug') ?? '';

        return this.content.page(slug).pipe(
          map((page): State => ({ kind: 'page', page })),

          // Every failure is the missing state, deliberately. A 404 is the expected one; a 500 or a
          // timeout on *this* call is not routed to the 503 page, because the route guard has already
          // confirmed from a live index that this address is a published page — so the honest thing is
          // the one render that does not claim the shop has no such page while still answering a status
          // a crawler will not index. See `describeMissing`.
          catchError(() => of<State>({ kind: 'missing' })),
        );
      }),
    ),
    { initialValue: { kind: 'loading' } satisfies State },
  );

  protected readonly page = computed(() =>
    this.state().kind === 'page' ? (this.state() as { page: ContentPageDto }).page : null,
  );

  protected readonly isContact = computed(
    () => this.page()?.systemKey === CONTENT_PAGE_KEYS.contact,
  );

  /** The Contact page's address, for the "get in touch" link on an empty page. Null when unpublished. */
  protected readonly contactPath = computed(() => {
    const contact = this.content.bySystemKey(CONTENT_PAGE_KEYS.contact);

    return contact && contact.slug !== this.page()?.slug ? `/${contact.slug}` : null;
  });

  /**
   * When the page last changed, in a form a reader wants on a policy page.
   *
   * `en-GB` and a long month, because "7/10/2026" is ambiguous to half the world and this is the one
   * date on a legal page people actually check. A value the API could not produce renders nothing
   * rather than "Invalid Date".
   */
  protected readonly updatedOn = computed(() => {
    const page = this.page();
    const stamp = page?.updatedAtUtc ?? page?.publishedAtUtc;
    if (!stamp) {
      return null;
    }

    const parsed = new Date(stamp);

    return Number.isNaN(parsed.getTime())
      ? null
      : parsed.toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
  });

  constructor() {
    // The metadata is set as an effect of the state rather than in the subscription, so a client-side
    // navigation between two content pages updates it — and so the server render has it before the HTML
    // is produced.
    inject(DestroyRef).onDestroy(() => {
      this.pageMeta.clear();
      this.robots.clear();
    });

    this.describe();
  }

  private describe(): void {
    // A computed that runs for its side effects would be wrong (it would not run unless read), so this
    // is an explicit effect-like subscription on the same signal the template reads.
    const apply = (): void => {
      const state = this.state();

      if (state.kind === 'page') {
        this.describePage(state.page);
        return;
      }

      if (state.kind === 'missing') {
        this.describeMissing();
      }
    };

    // Angular's `effect` is the right primitive and it is deliberately not used during SSR for
    // metadata: effects are flushed after the first render pass, which is after the HTML the crawler
    // reads has been produced. `toObservable` on the state signal would have the same problem. So the
    // state is read once synchronously here — `toSignal` has already resolved it during a server render,
    // because the render waits for the HTTP call — and again whenever the route's params change.
    apply();

    this.route.paramMap.subscribe(() => apply());
  }

  private describePage(page: ContentPageDto): void {
    this.announcedMissing.set(false);
    this.robots.clear();

    if (this.response) {
      // A page that resolved is a 200, and a previous render on this response object cannot have set
      // anything else — but saying so costs nothing and makes the pairing with `describeMissing` legible.
      this.response.status = 200;
    }

    const title = page.seoTitle?.trim();
    const description = page.seoDescription?.trim();

    this.pageMeta.set({
      title: title && title.length > 0 ? title : brandPageTitle(page.title),

      // The client's own description when they wrote one. Otherwise the page's heading in a sentence,
      // which is a poor description and still better than the shop's generic one on six different
      // pages — a crawler treats identical descriptions as a signal that the pages are duplicates.
      description:
        description && description.length > 0
          ? description
          : `${page.title} at ${BRAND.name}.`,
      path: `/${page.slug}`,
    });
  }

  /**
   * The missing render: a real 404 with `noindex`.
   *
   * The status is set here rather than in `app.routes.server.ts` because a server route's status is
   * matched by path, and no pattern can express "a slug the content feed no longer publishes":
   * `/returns` and `/retunrs` are the same pattern and only the API can tell them apart. Angular hands
   * the response's `ResponseInit` to the render and builds the `Response` from that same object
   * afterwards, so setting the status during the render is what reaches the wire — the mechanism
   * `NotFoundPage` documents in full.
   */
  private describeMissing(): void {
    if (this.announcedMissing()) {
      return;
    }

    this.announcedMissing.set(true);

    this.pageMeta.set({
      title: brandPageTitle('Page not found'),
      description: `The page you asked for is not part of the ${BRAND.name} store.`,
      path: '/',
    });

    // A 404 must never be indexed, whatever a crawler makes of the status code. Cleared on the way out
    // by the DestroyRef above, like the canonical address: the next page is not missing, and on a
    // client-side navigation nothing else would remove it.
    this.robots.noindex();

    if (this.response) {
      this.response.status = NOT_FOUND_STATUS;
    }
  }
}
