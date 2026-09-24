import { ChangeDetectionStrategy, Component, RESPONSE_INIT, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';

import { NavigationService } from '../../core/navigation/navigation.service';

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
      <p><a class="link" routerLink="/">Back to the home page</a></p>
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
  `,
})
export class NotFoundPage {
  protected readonly departments = inject(NavigationService).departments;

  constructor() {
    inject(Title).setTitle('Page not found | Spencer Denim');

    const meta = inject(Meta);
    meta.updateTag({
      name: 'description',
      content: 'The page you asked for is not part of the Spencer Denim store.',
    });
    // A 404 must never be indexed, whatever a crawler makes of the status code.
    meta.updateTag({ name: 'robots', content: 'noindex' });

    const response = inject(RESPONSE_INIT, { optional: true });
    if (response) {
      response.status = NOT_FOUND_STATUS;
    }
  }
}
