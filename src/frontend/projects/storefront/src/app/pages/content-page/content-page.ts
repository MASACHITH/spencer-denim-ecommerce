import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  RESPONSE_INIT,
  computed,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap } from 'rxjs';

import { ContactPanel } from './contact-panel';
import { ShippingRates } from './shipping-rates';
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
  imports: [RouterLink, ContactPanel, ShippingRates],
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

          @if (shippingParts(); as parts) {
            <!-- #33: the Shipping page's live fee list, drawn where the copy marks it (under "Delivery within
                 Sri Lanka"), or after the copy when the owner has removed the marker line. -->
            <div class="page__body rich rich--page" [innerHTML]="parts.before"></div>
            <sd-shipping-rates [inline]="parts.after !== null" />
            @if (parts.after) {
              <div class="page__body rich rich--page" [innerHTML]="parts.after"></div>
            }
          } @else if (page()!.bodyHtml) {
            <!-- Already-sanitised HTML from the API, bound so Angular's sanitiser runs over it too.
                 See the component's own documentation for why both layers are kept. -->
            <div class="page__body rich rich--page" [innerHTML]="page()!.bodyHtml"></div>
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

    /* **The body's own styles are in projects/storefront/src/styles.css, not here, and that is not a
       preference.** Content injected with [innerHTML] carries no _ngcontent attribute, so a component's
       scoped styles can never reach it: every rule written here for '.page__body table', '.page__body h2'
       and the rest matched nothing at all. Measured by content-pages.e2e.ts, which read 'overflow-x'
       off a table inside the body and got the empty string. The global sheet's '.rich'/'.rich--page'
       block is where they live, which is the convention PDP-05's product description already set and the
       note in that file already anticipated for this task. */
  `,
})
export class ContentPage {
  private readonly route = inject(ActivatedRoute);
  private readonly content = inject(ContentPageService);
  private readonly pageMeta = inject(PageMeta);
  private readonly robots = inject(RobotsMeta);
  private readonly response = inject(RESPONSE_INIT, { optional: true });

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

  protected readonly isShipping = computed(
    () => this.page()?.systemKey === CONTENT_PAGE_KEYS.shipping,
  );

  /**
   * The Shipping page's body split at the paragraph that holds only `{{delivery fees}}` (issue #33), or null for any
   * other page. With no marker the whole body comes first and the list follows it (`after` is null).
   */
  protected readonly shippingParts = computed(() => {
    const page = this.page();
    return page && this.isShipping() ? splitAtFeeMarker(page.bodyHtml) : null;
  });

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
    // **An `effect`, and the first version was wrong about this.** It read the state signal once in the
    // constructor, reasoning that a server render would have resolved it by then — and it had not: the
    // HTTP call resolves later, so the title, the description and the canonical address were never set
    // at all. The body still rendered, because the template reads the signal reactively, which is
    // exactly what made it invisible until `content-pages.e2e.ts` read the document title off the wire.
    //
    // An effect is the primitive the product page uses for the same job and for the same reason: the
    // server render waits for stability before serialising, so an effect that runs during change
    // detection has set the tags before the HTML a crawler reads is produced. `untracked` around the
    // writes, as there, so setting a tag cannot re-trigger the effect.
    effect(() => {
      const state = this.state();

      untracked(() => {
        if (state.kind === 'page') {
          this.describePage(state.page);
          return;
        }

        if (state.kind === 'missing') {
          this.describeMissing();
        }
      });
    });

    // The next page owns its own canonical address and card, or has none.
    inject(DestroyRef).onDestroy(() => {
      this.pageMeta.clear();
      this.robots.clear();
    });
  }

  private describePage(page: ContentPageDto): void {
    // A page that is fine must not inherit the noindex a 404 left behind on a client-side navigation.
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

/** The Shipping page's fee-list marker: a paragraph holding only `{{delivery fees}}` (PolicyPageContent). */
const FEE_MARKER = /<p>\s*\{\{\s*delivery fees\s*\}\}\s*<\/p>/i;

/** Splits a body at the first fee-list marker; `after` is null when there is none. */
export function splitAtFeeMarker(bodyHtml: string): { before: string; after: string | null } {
  const match = FEE_MARKER.exec(bodyHtml);
  if (!match) {
    return { before: bodyHtml, after: null };
  }

  return {
    before: bodyHtml.slice(0, match.index),
    after: bodyHtml.slice(match.index + match[0].length).replace(FEE_MARKER, ''),
  };
}
