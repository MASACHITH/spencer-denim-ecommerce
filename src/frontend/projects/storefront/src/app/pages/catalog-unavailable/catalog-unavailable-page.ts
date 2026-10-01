import { ChangeDetectionStrategy, Component, RESPONSE_INIT, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';

import { NavigationService } from '../../core/navigation/navigation.service';
import { PageReload } from '../../core/platform/page-reload';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { serviceUnavailable } from '../../core/platform/service-unavailable';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

/**
 * The page for an address the storefront **could not look up** — as opposed to one the catalogue
 * does not have, which is `NotFoundPage` and a real 404.
 *
 * The route guards match a listing against the department tree, and when `GET
 * /api/catalog/departments` fails, times out or answers with something unusable, that tree is
 * `core/navigation/navigation.data.ts`: a frozen copy of the seed that knows nothing an admin has
 * added since (ADM-11). Answering 404 with `noindex` there deindexed a live, perfectly healthy
 * listing for as long as the API was unhappy, and a 2-second timeout is easy to trip on a loaded
 * VPS (#30). So this page answers **503 with `Retry-After`** instead, which a crawler retries and
 * which keeps the URL.
 *
 * It sets **no** `robots` meta, and removes one a previous page left behind: a 503 asking to be
 * deindexed is the exact outcome the status code is here to avoid.
 *
 * The status reaches the wire only during server-side rendering. On a client-side navigation there
 * is no response to set a status on, so what carries the weight there is this page itself: it says
 * what happened, keeps the header and footer, offers the seeded departments, and offers "Try again",
 * which re-requests the address so the server can have another go at it.
 */
@Component({
  selector: 'sd-catalog-unavailable-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container unavailable">
      <h1 class="unavailable__title">We could not load this page just now</h1>
      <p class="unavailable__lead">
        Our catalogue is not answering, so we cannot tell you what is on this page. It is almost
        certainly us rather than you, and it is usually over in a moment.
      </p>
      <div class="unavailable__actions">
        <button class="btn btn--primary" type="button" (click)="retry()">Try again</button>
        <a class="btn btn--secondary" routerLink="/">Back to the home page</a>
      </div>
      <p class="unavailable__lead">In the meantime, these are still here:</p>
      <ul class="unavailable__links">
        @for (department of departments(); track department.slug) {
          <li>
            <a class="link" [routerLink]="'/' + department.slug">{{ department.name }}</a>
          </li>
        }
        <li><a class="link" routerLink="/new-arrivals">New Arrivals</a></li>
        <li><a class="link" routerLink="/sale">Sale</a></li>
      </ul>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .unavailable {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      max-width: 62ch;
      padding-block: var(--space-8) var(--space-12);
    }
    .unavailable__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .unavailable__lead {
      color: var(--color-text-muted);
    }
    .unavailable__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
    }
    .unavailable__links {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5) var(--space-3);
      list-style: none;
    }
    /* The 44 px floor applies to a plain link as much as to a button, and these were 20 px tall
       until the tester measured them at 320 px. The site footer solves the same problem the same
       way (site-footer__col a:not(.icon-btn)): the link becomes a flex box with a minimum of
       --tap on both axes, so the target is the row rather than the few characters of the label
       ("Sale" measured 32 x 20). The sibling not-found page uses .btn btn--secondary for the
       identical five links; here they sit below two real buttons, so a seventh and eighth button
       would read as eight equal choices rather than two actions and a list of departments. */
    .unavailable__links a {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      min-width: var(--tap);
      max-width: 100%;
      overflow-wrap: anywhere;
    }
  `,
})
export class CatalogUnavailablePage {
  private readonly pageReload = inject(PageReload);

  /** The seeded departments: not live, but every one of them is a real page. */
  protected readonly departments = inject(NavigationService).departments;

  constructor() {
    inject(Title).setTitle(brandPageTitle('Page unavailable'));

    const meta = inject(Meta);
    meta.updateTag({
      name: 'description',
      content: `This page of the ${BRAND.name} store could not be loaded. Please try again shortly.`,
    });
    // No `noindex` here, and none inherited from a 404 the shopper came through on a client-side
    // navigation: this page exists to stop a URL being dropped from the index, not to help it.
    inject(RobotsMeta).clear();

    serviceUnavailable(inject(RESPONSE_INIT, { optional: true }));
  }

  /**
   * Asks the server for this address again.
   *
   * A full request rather than a client-side retry: only the server can replace the 503 it sent,
   * and the catalogue may now answer. See {@link PageReload}.
   */
  protected retry(): void {
    this.pageReload.reload();
  }
}
