import { ChangeDetectionStrategy, Component, DestroyRef, RESPONSE_INIT, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';

import { NavigationService } from '../../core/navigation/navigation.service';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

/** What a page that is not there should answer, so a crawler does not index it as a real page. */
const NOT_FOUND_STATUS = 404;

/**
 * The page for an address the catalogue does not have — a category that belongs to another
 * department, or one that has been retired.
 *
 * It is a real 404, not a 200 that says "not found". The status is set here rather than in
 * `app.routes.server.ts` because a server route's `status` is matched by path, and no path
 * pattern can express "a category this department does not have": `/women/jeans` and
 * `/women/shirts` are the same pattern and only the catalogue can tell them apart. Angular hands
 * the response's `ResponseInit` to the render through `RESPONSE_INIT` and builds the `Response`
 * from that same object afterwards, so setting the status during the render is what reaches the
 * wire. The token is absent in the browser, where a client-side navigation has no status at all.
 */
@Component({
  selector: 'sd-not-found-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container missing">
      <h1 class="missing__title">We could not find that page</h1>
      <p class="missing__lead">
        The address may have changed, or the page may no longer exist. Everything we sell is in one
        of these:
      </p>
      <ul class="missing__links">
        @for (department of departments(); track department.slug) {
          <li>
            <a class="btn btn--secondary" [routerLink]="'/' + department.slug">
              {{ department.name }}
            </a>
          </li>
        }
        <li><a class="btn btn--secondary" routerLink="/new-arrivals">New Arrivals</a></li>
        <li><a class="btn btn--secondary" routerLink="/sale">Sale</a></li>
      </ul>
      <p><a class="link missing__home" routerLink="/">Back to the home page</a></p>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .missing {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      max-width: 62ch;
      padding-block: var(--space-8) var(--space-12);
    }
    .missing__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .missing__lead {
      color: var(--color-text-muted);
    }
    .missing__links {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      list-style: none;
      padding-top: var(--space-1);
    }
    /* The 44 px floor applies to a plain link as much as to a button, and this one was 175 x 20 at
       every one of the eight breakpoints — WCAG 2.2's 2.5.8 is 24 px, and this project's own floor
       is 44. It went unnoticed because nothing measured the links on this page: the five department
       links beside it are .btn btn--secondary and pass, so a selector scoped to the buttons could
       not fail. Fixed the same way the sibling 503 page fixes its own (unavailable__links a), and
       width: fit-content so the row the link sits on is not a 175 px-wide 44 px-tall target with
       most of it empty. */
    .missing__home {
      display: inline-flex;
      align-items: center;
      width: fit-content;
      min-height: var(--tap);
    }
  `,
})
export class NotFoundPage {
  protected readonly departments = inject(NavigationService).departments;

  constructor() {
    inject(Title).setTitle(brandPageTitle('Page not found'));

    const meta = inject(Meta);
    meta.updateTag({
      name: 'description',
      content: `The page you asked for is not part of the ${BRAND.name} store.`,
    });
    // A 404 must never be indexed, whatever a crawler makes of the status code. Cleared on the way
    // out, like the canonical address: the next page is not missing, and on a client-side
    // navigation nothing else would remove it — "Back to the home page" from here left the home
    // page carrying `noindex`.
    const robots = inject(RobotsMeta);
    robots.noindex();
    inject(DestroyRef).onDestroy(() => robots.clear());

    const response = inject(RESPONSE_INIT, { optional: true });
    if (response) {
      response.status = NOT_FOUND_STATUS;
    }
  }
}
