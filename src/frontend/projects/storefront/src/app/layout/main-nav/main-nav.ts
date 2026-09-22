import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { NavigationService } from '../../core/navigation/navigation.service';
import { NavItem, hasMenu } from '../../core/navigation/navigation.model';

/**
 * The desktop main navigation and its mega menus (GLB-05).
 *
 * A department's menu opens on hover *and* on keyboard focus, so a mouse and a keyboard reach the
 * same categories. The department link itself still navigates, and it carries `aria-expanded` so
 * assistive technology knows the menu is there. Escape closes the open menu and leaves focus on
 * the department link.
 *
 * Below 1024 px this whole component is hidden and the drawer takes over (docs/04 §4).
 */
@Component({
  selector: 'sd-main-nav',
  imports: [RouterLink, RouterLinkActive, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Escape is caught on the component, not on a list item: a list item is not focusable, and
  // focus may be anywhere inside the open mega menu when Escape is pressed.
  host: { '(keydown.escape)': 'onEscape()' },
  template: `
    <nav class="site-nav" aria-label="Main">
      <ul class="site-nav__list">
        @for (item of items(); track item.id) {
          <li
            class="site-nav__item"
            (mouseenter)="openFromPointer(item)"
            (mouseleave)="onPointerLeave(item.id)"
            (focusin)="openFromFocus(item)"
            (focusout)="onFocusOut($event, item.id)"
          >
            <a
              class="site-nav__link"
              [class.site-nav__link--sale]="item.tone === 'sale'"
              [routerLink]="item.path"
              routerLinkActive="is-active"
              [attr.aria-expanded]="menuOf(item) ? openId() === item.id : null"
              [attr.aria-controls]="menuOf(item) ? panelId(item) : null"
            >
              {{ item.label }}
            </a>

            @if (menuOf(item)) {
              <div class="mega" [id]="panelId(item)" [hidden]="openId() !== item.id">
                <div class="container mega__grid">
                  @for (column of item.columns; track column.key) {
                    <div class="mega__col">
                      <ul>
                        @for (group of column.groups; track group.link.path) {
                          <li>
                            <a
                              class="mega__link"
                              [class.mega__link--group]="group.children.length > 0"
                              [routerLink]="group.link.path"
                              (click)="close(item.id)"
                            >
                              {{ group.link.label }}
                            </a>
                            @if (group.children.length > 0) {
                              <ul>
                                @for (child of group.children; track child.path) {
                                  <li>
                                    <a
                                      class="mega__link mega__link--child"
                                      [routerLink]="child.path"
                                      (click)="close(item.id)"
                                    >
                                      {{ child.label }}
                                    </a>
                                  </li>
                                }
                              </ul>
                            }
                          </li>
                        }
                      </ul>
                    </div>
                  }

                  @if (item.promo; as promo) {
                    <a class="mega__promo" [routerLink]="promo.cta.path" (click)="close(item.id)">
                      <span class="mega__promo-media">
                        @if (promo.imageUrl) {
                          <img [src]="promo.imageUrl" alt="" />
                        } @else {
                          <span class="mega__promo-tag">Promo image</span>
                        }
                      </span>
                      <span class="mega__promo-title">{{ promo.title }}</span>
                      @if (promo.caption) {
                        <span class="mega__promo-caption">{{ promo.caption }}</span>
                      }
                      <span class="mega__promo-cta">
                        {{ promo.cta.label }}
                        <sd-icon name="chevron-right" size="sm" />
                      </span>
                    </a>
                  }
                </div>
              </div>
            }
          </li>
        }
      </ul>
    </nav>
  `,
  styles: `
    :host {
      display: none;
    }
    @media (min-width: 1024px) {
      :host {
        display: block;
        flex: 1 1 auto;
      }
    }
    .site-nav__list {
      display: flex;
      flex-wrap: nowrap;
      justify-content: center;
      gap: 0 var(--space-2);
      list-style: none;
    }
    @media (min-width: 1440px) {
      .site-nav__list {
        gap: 0 var(--space-4);
      }
    }
    .site-nav__item {
      position: static;
    }
    .site-nav__link {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: 56px;
      min-width: var(--tap);
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-decoration: none;
      white-space: nowrap;
      border-bottom: 1px solid transparent;
    }
    .site-nav__link:hover,
    .site-nav__link.is-active {
      border-bottom-color: currentColor;
    }
    .site-nav__link--sale {
      color: var(--color-sale);
    }

    .mega {
      position: absolute;
      inset-inline: 0;
      top: 100%;
      z-index: 90;
      padding-block: var(--space-3) var(--space-4);
      background: var(--color-surface);
      border-bottom: 1px solid var(--color-border);
      box-shadow: none;
    }
    .mega__grid {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr)) minmax(200px, 1.1fr);
      gap: var(--space-4);
      text-align: left;
    }
    .mega__col ul {
      list-style: none;
    }
    .mega__link {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      font-size: var(--text-small);
      text-decoration: none;
    }
    .mega__link:hover {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    .mega__link--group {
      font-weight: var(--weight-semibold);
    }
    .mega__link--child {
      padding-inline-start: var(--space-1-5);
      color: var(--color-text-muted);
    }
    .mega__promo {
      display: grid;
      gap: var(--space-0-5);
      align-content: start;
      text-decoration: none;
    }
    .mega__promo-media {
      display: grid;
      place-items: center;
      aspect-ratio: 4 / 5;
      margin-bottom: var(--space-1);
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
      color: var(--color-text-muted);
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      overflow: hidden;
    }
    .mega__promo-media img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .mega__promo-title {
      font-weight: var(--weight-medium);
    }
    .mega__promo-caption {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .mega__promo-cta {
      display: inline-flex;
      align-items: center;
      gap: var(--space-0-5);
      min-height: var(--tap);
      font-size: var(--text-small);
      text-decoration: underline;
      text-underline-offset: 4px;
    }
  `,
})
export class MainNav {
  private readonly navigation = inject(NavigationService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly items = this.navigation.items;

  private readonly expanded = signal<string | null>(null);

  /** The id of the department whose mega menu is open, or `null`. */
  readonly openId = this.expanded.asReadonly();

  /** The department Escape closed, so focus alone does not reopen it. */
  private dismissed: string | null = null;

  protected menuOf(item: NavItem): boolean {
    return hasMenu(item);
  }

  protected panelId(item: NavItem): string {
    return `mega-${item.id}`;
  }

  /** Hovering always opens: a pointer arriving is a fresh request, even after Escape. */
  openFromPointer(item: NavItem): void {
    this.dismissed = null;
    this.show(item);
  }

  /**
   * Focus opens the menu too, unless this department was just dismissed with Escape. Without that
   * guard, returning focus to the department link would immediately reopen what Escape closed.
   */
  openFromFocus(item: NavItem): void {
    if (this.dismissed === item.id) {
      return;
    }

    this.show(item);
  }

  close(id: string): void {
    if (this.expanded() === id) {
      this.expanded.set(null);
    }
  }

  protected onPointerLeave(id: string): void {
    this.dismissed = null;
    this.close(id);
  }

  private show(item: NavItem): void {
    this.expanded.set(hasMenu(item) ? item.id : null);
  }

  /** Closes only when focus has really left the department, not when it moves inside its menu. */
  protected onFocusOut(event: FocusEvent, id: string): void {
    const nextFocus = event.relatedTarget;
    const item = event.currentTarget;
    if (item instanceof Node && nextFocus instanceof Node && item.contains(nextFocus)) {
      return;
    }

    this.dismissed = null;
    this.close(id);
  }

  /** Closes the open mega menu and leaves focus on its department link. */
  onEscape(): void {
    const id = this.expanded();
    if (id === null) {
      return;
    }

    this.dismissed = id;
    this.close(id);
    this.host.nativeElement
      .querySelector<HTMLElement>(`[aria-controls="mega-${id}"]`)
      ?.focus();
  }
}
