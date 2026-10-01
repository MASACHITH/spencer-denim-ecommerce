import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, untracked } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';

import { BenefitsRow } from './sections/benefits-row';
import { DepartmentTiles } from './sections/department-tiles';
import { FeatureBanner } from './sections/feature-banner';
import { HomeContentStore } from '../../core/content/home-content.store';
import { HomeHero } from './sections/home-hero';
import { NavigationService } from '../../core/navigation/navigation.service';
import { NewsletterSignup } from './sections/newsletter-signup';
import { ProductRow } from './sections/product-row';
import { SocialGallery } from './sections/social-gallery';
import { CanonicalLink } from '../../core/platform/canonical-link';
import { BRAND } from '@spencer-denim/shared';

/**
 * The storefront homepage (HOME-01..10).
 *
 * <b>The page has no opinion about what is on it.</b> Which blocks appear, in what order, with
 * what headings and for how long is `content.HomeSections`, which the client edits in ADM-17. This
 * component reads the list the API sends and renders each entry with the component for its type —
 * nothing here is a constant that would have to be deployed to change.
 *
 * <b>A section with nothing in it renders nothing at all.</b> The API has already dropped every
 * section that is switched off or outside its schedule; `renderableSections` drops the ones that
 * arrived with no content. Neither produces an empty frame, a heading over a gap or a "coming
 * soon" — four of the ten sections are seeded switched off because their artwork does not exist
 * yet (W1-04), and the page is correct with them absent.
 *
 * <b>There is exactly one `h1`, and it is not a section's.</b> Every section is the client's to
 * remove, so a heading that depends on one being present would come and go with it. The store's
 * name is the page's subject, the sections are its parts, and each of those carries its own `h2`.
 */
@Component({
  selector: 'sd-home-page',
  imports: [
    BenefitsRow,
    DepartmentTiles,
    FeatureBanner,
    HomeHero,
    NewsletterSignup,
    ProductRow,
    RouterLink,
    SocialGallery,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 class="sr-only">{{ brand }}</h1>

    @for (section of sections(); track section.id) {
      @switch (section.type) {
        @case ('HeroVideo') {
          <sd-home-hero [section]="section" />
        }
        @case ('ShopByDepartment') {
          <sd-department-tiles [section]="section" />
        }
        @case ('NewArrivals') {
          <sd-product-row [section]="section" />
        }
        @case ('TrendingItems') {
          <sd-product-row [section]="section" />
        }
        @case ('SaleSection') {
          <sd-product-row [section]="section" />
        }
        @case ('CampaignBanner') {
          <sd-feature-banner [section]="section" />
        }
        @case ('ActivewearFeature') {
          <sd-feature-banner [section]="section" />
        }
        @case ('SocialGallery') {
          <sd-social-gallery [section]="section" />
        }
        @case ('Benefits') {
          <sd-benefits-row [section]="section" />
        }
        @case ('Newsletter') {
          <sd-newsletter-signup [section]="section" />
        }
      }
    }

    <!-- The homepage's content service is not the catalogue, and the catalogue is what a shopper
         came for. If the content cannot be fetched — or the client has switched every section off —
         the page still renders the shell and the way in, rather than a blank screen or a status
         code. The department list comes from the navigation service, which has a fallback of its
         own, so this block has something to offer even when both feeds are down. -->
    @if (store.isEmpty()) {
      <section class="fallback container" aria-labelledby="home-fallback-title">
        <h2 class="fallback__title" id="home-fallback-title">Shop {{ brand }}</h2>

        @if (store.hasError()) {
          <p class="fallback__note">
            We could not load the rest of the home page just now. Everything in the shop is still
            here.
          </p>
        }

        <ul class="fallback__links">
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

        @if (store.hasError()) {
          <p>
            <button class="btn btn--primary" type="button" (click)="retry()">Try again</button>
          </p>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    /* The gap between one block and the next belongs to the page, not to any one section: a
       section that is switched off must take its spacing with it, and it does if the spacing is
       here. Full-bleed blocks (the hero, the banners, the benefits band) carry their own. */
    :host > sd-product-row,
    :host > sd-department-tiles,
    :host > sd-social-gallery {
      /* The bottom space is not decoration. The full-bleed blocks (the hero, the banners) are
         positioned, so they paint above the static content before them: with these sections
         flush against the next one, the focus ring on the last row was painted over by the
         banner below it. Measured on a trending tile at 1280 px, bottom edge, in
         home-page.e2e.ts. */
      padding-block: var(--space-6) var(--space-2);
    }
    :host > :last-child {
      padding-bottom: var(--space-8);
    }
    .fallback {
      display: grid;
      gap: var(--space-3);
      justify-items: start;
      padding-block: var(--space-8);
    }
    .fallback__title {
      font-size: var(--text-h2);
    }
    .fallback__note {
      max-width: 52ch;
      color: var(--color-text-muted);
    }
    .fallback__links {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
      list-style: none;
    }
  `,
})
export class HomePage {
  /** The shop's name, from the one place it is written down (@spencer-denim/shared). */
  protected readonly brand = BRAND.name;

  protected readonly store = inject(HomeContentStore);
  private readonly navigation = inject(NavigationService);
  private readonly canonical = inject(CanonicalLink);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);

  protected readonly sections = this.store.sections;

  protected readonly departments = computed(() => this.navigation.departments());

  constructor() {
    // Before the first change detection, so the request is already in flight while the server
    // renders and the HTML a crawler receives has the real sections in it.
    this.store.load();

    effect(() => {
      untracked(() => {
        this.title.setTitle(`${BRAND.name} | Clothing for women, men and kids`);
        this.meta.updateTag({
          name: 'description',
          content:
            `Shop denim and everyday clothing for women, men and kids at ${BRAND.name}, with delivery across Sri Lanka.`,
        });
        this.canonical.set('/');
      });
    });

    // The next page owns its own canonical address, or has none.
    inject(DestroyRef).onDestroy(() => this.canonical.clear());
  }

  protected retry(): void {
    this.store.retry();
  }
}
