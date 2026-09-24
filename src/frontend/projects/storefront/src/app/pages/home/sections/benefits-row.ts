import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Icon, IconName } from '@spencer-denim/shared';

import { HomeSectionDto, HomeSectionItemDto } from '../../../core/content/content.model';

/** One benefit, resolved once. */
interface Benefit {
  readonly id: number;
  readonly icon: IconName;
  readonly title: string | null;
  readonly body: string | null;
}

/**
 * <c>content.HomeSectionItems.IconKey</c> to an icon in the storefront's own set.
 *
 * The client picks a key from a list in ADM-17, never a file, so this map is the whole contract.
 * A key that is not in it draws the neutral mark rather than nothing: an icon missing from a row
 * of three leaves a hole where the other two have a picture.
 */
const BENEFIT_ICONS: Readonly<Record<string, IconName>> = {
  delivery: 'truck',
  shipping: 'truck',
  returns: 'refresh',
  exchanges: 'refresh',
  quality: 'shield',
  secure: 'shield',
  payment: 'shield',
  support: 'phone',
  contact: 'mail',
};

/** What an unknown key draws. */
const FALLBACK_ICON: IconName = 'star';

/**
 * HOME-09: customer benefits — delivery, returns, quality.
 *
 * The only block that needs no media at all, which is why it is on the homepage from day one while
 * the four artwork blocks wait for W1-04. The copy is the client's, from the database, and the
 * seeded lines are placeholders they replace before go-live (docs/04 §4 decision 8, D8).
 *
 * The icons are decorative: each sits beside a heading that says the same thing in words.
 */
@Component({
  selector: 'sd-benefits-row',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="benefits" [attr.aria-labelledby]="section().title ? headingId() : null"
      [attr.aria-label]="section().title ? null : 'Why shop with us'">
      <div class="container">
        @if (section().title; as heading) {
          <div class="home-section__head">
            <h2 class="home-section__title" [id]="headingId()">{{ heading }}</h2>
            @if (section().subtitle; as standfirst) {
              <p class="home-section__subtitle">{{ standfirst }}</p>
            }
          </div>
        }

        <ul class="benefits__list">
          @for (benefit of benefits(); track benefit.id) {
            <li class="benefit" data-testid="benefit">
              <sd-icon class="benefit__icon" [name]="benefit.icon" />
              <div class="benefit__text">
                @if (benefit.title; as title) {
                  <h3 class="benefit__title">{{ title }}</h3>
                }
                @if (benefit.body; as body) {
                  <p class="benefit__body">{{ body }}</p>
                }
              </div>
            </li>
          }
        </ul>
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .benefits {
      background: var(--color-bg-alt);
      padding-block: var(--space-6);
    }
    .benefits__list {
      display: grid;
      grid-template-columns: 1fr;
      gap: var(--space-3);
      list-style: none;
    }
    @media (min-width: 768px) {
      .benefits__list {
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: var(--space-4);
      }
    }
    .benefit {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      gap: var(--space-2);
      align-items: start;
    }
    @media (min-width: 768px) {
      .benefit {
        grid-template-columns: 1fr;
        gap: var(--space-1-5);
        text-align: center;
        justify-items: center;
      }
    }
    .benefit__icon {
      color: var(--color-text);
    }
    .benefit__text {
      display: grid;
      gap: var(--space-0-5);
    }
    .benefit__title {
      font-size: var(--text-h4);
      font-weight: var(--weight-medium);
    }
    .benefit__body {
      font-size: var(--text-small);
      line-height: 1.5;
      color: var(--color-text-muted);
      max-width: 40ch;
    }
  `,
})
export class BenefitsRow {
  readonly section = input.required<HomeSectionDto>();

  protected readonly headingId = computed(() => `home-section-${this.section().id}`);

  protected readonly benefits = computed<readonly Benefit[]>(() =>
    this.section()
      .items.map((item) => this.toBenefit(item))
      .filter((benefit): benefit is Benefit => benefit !== null),
  );

  private toBenefit(item: HomeSectionItemDto): Benefit | null {
    const title = item.title?.trim() || null;
    const body = item.body?.trim() || null;

    // A benefit with no words is not a benefit; an icon on its own says nothing.
    if (!title && !body) {
      return null;
    }

    const key = item.iconKey?.trim().toLowerCase() ?? '';

    return {
      id: item.id,
      icon: BENEFIT_ICONS[key] ?? FALLBACK_ICON,
      title,
      body,
    };
  }
}
