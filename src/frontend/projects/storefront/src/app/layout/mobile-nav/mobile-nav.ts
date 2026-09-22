import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Drawer, Icon } from '@spencer-denim/shared';

import { NavigationService } from '../../core/navigation/navigation.service';
import { NavItem, hasMenu } from '../../core/navigation/navigation.model';
import { SITE_CONFIG, whatsAppUrl } from '../../core/config/site-config';
import { StorefrontCounts } from '../../core/storefront-counts.service';

/**
 * The phone and tablet navigation (GLB-05): a slide-out drawer whose departments expand in place.
 *
 * The drawer itself (focus trap, Escape, focus return) is the shared `sd-drawer`. This component
 * only supplies what is inside it. One department is open at a time, which keeps the list short
 * enough to scan at 320 px.
 */
@Component({
  selector: 'sd-mobile-nav',
  imports: [Drawer, Icon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer
      [open]="open()"
      panelId="storefront-menu"
      heading="Menu"
      closeLabel="Close menu"
      (closed)="closed.emit()"
    >
      <nav aria-label="Main menu">
        <ul class="m-nav">
          @for (item of items(); track item.id) {
            <li>
              @if (menuOf(item)) {
                <button
                  class="m-nav__toggle"
                  type="button"
                  [attr.aria-expanded]="expandedId() === item.id"
                  [attr.aria-controls]="panelId(item)"
                  (click)="toggle(item.id)"
                >
                  <span>{{ item.label }}</span>
                  <sd-icon
                    class="m-nav__chevron"
                    [class.is-open]="expandedId() === item.id"
                    name="chevron-down"
                    size="sm"
                  />
                </button>
                <ul class="m-nav__panel" [id]="panelId(item)" [hidden]="expandedId() !== item.id">
                  @for (link of linksOf(item); track link.path) {
                    <li>
                      <a
                        class="m-nav__sub"
                        [class.m-nav__sub--child]="link.depth > 0"
                        [routerLink]="link.path"
                        (click)="closed.emit()"
                        >{{ link.label }}</a
                      >
                    </li>
                  }
                </ul>
              } @else {
                <a
                  class="m-nav__link"
                  [class.text-sale]="item.tone === 'sale'"
                  [routerLink]="item.path"
                  (click)="closed.emit()"
                  >{{ item.label }}</a
                >
              }
            </li>
          }
        </ul>
      </nav>

      <ul class="m-nav m-nav--secondary">
        <li>
          <a class="m-nav__link" routerLink="/account" (click)="closed.emit()">
            <sd-icon name="user" size="sm" /> <span>Sign in / Register</span>
          </a>
        </li>
        <li>
          <a class="m-nav__link" routerLink="/wishlist" (click)="closed.emit()">
            <sd-icon name="heart" size="sm" /> <span>Wishlist ({{ wishlistCount() }})</span>
          </a>
        </li>
        <li>
          <a class="m-nav__link" [href]="whatsApp" rel="noopener" target="_blank">
            <sd-icon name="whatsapp" size="sm" /> <span>Chat on WhatsApp</span>
          </a>
        </li>
      </ul>
    </sd-drawer>
  `,
  styles: `
    .m-nav {
      list-style: none;
    }
    .m-nav > li {
      border-bottom: 1px solid var(--color-border);
    }
    .m-nav__toggle,
    .m-nav__link {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1);
      width: 100%;
      min-height: 56px;
      padding: 0;
      border: 0;
      background: none;
      font-size: var(--text-button);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-align: left;
      text-decoration: none;
      cursor: pointer;
    }
    .m-nav__chevron {
      transition: transform var(--duration) var(--ease);
    }
    .m-nav__chevron.is-open {
      transform: rotate(180deg);
    }
    .m-nav__panel {
      list-style: none;
      padding-bottom: var(--space-1);
    }
    .m-nav__sub {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      padding-inline-start: var(--space-2);
      font-size: var(--text-small);
      text-decoration: none;
    }
    .m-nav__sub--child {
      padding-inline-start: var(--space-4);
      color: var(--color-text-muted);
    }
    .m-nav__sub:hover {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    .m-nav--secondary {
      margin-top: var(--space-3);
    }
    .m-nav--secondary > li {
      border-bottom: 0;
    }
    .m-nav--secondary .m-nav__link {
      justify-content: flex-start;
      min-height: var(--tap);
      text-transform: none;
      letter-spacing: 0;
      font-size: var(--text-small);
      font-weight: var(--weight-regular);
    }
  `,
})
export class MobileNav {
  readonly open = input.required<boolean>();
  readonly closed = output<void>();

  private readonly navigation = inject(NavigationService);
  private readonly counts = inject(StorefrontCounts);

  readonly items = this.navigation.items;
  protected readonly wishlistCount = this.counts.wishlist;
  protected readonly whatsApp = whatsAppUrl(inject(SITE_CONFIG));

  private readonly expanded = signal<string | null>(null);

  /** The department section that is expanded, or `null`. */
  readonly expandedId = this.expanded.asReadonly();

  /** Every department's menu flattened to a single indented list, "Shop all" first. */
  private readonly flattened = computed(() =>
    new Map(
      this.items().map((item) => [
        item.id,
        [
          ...(item.shopAll
            ? [{ label: item.shopAll.label, path: item.shopAll.path, depth: 0 }]
            : []),
          ...item.columns.flatMap((column) =>
            column.groups.flatMap((group) => [
              { label: group.link.label, path: group.link.path, depth: 0 },
              ...group.children.map((child) => ({
                label: child.label,
                path: child.path,
                depth: 1,
              })),
            ]),
          ),
        ],
      ]),
    ),
  );

  protected menuOf(item: NavItem): boolean {
    return hasMenu(item);
  }

  protected panelId(item: NavItem): string {
    return `m-nav-${item.id}`;
  }

  protected linksOf(item: NavItem): readonly { label: string; path: string; depth: number }[] {
    return this.flattened().get(item.id) ?? [];
  }

  toggle(id: string): void {
    this.expanded.update((current) => (current === id ? null : id));
  }
}
