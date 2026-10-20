import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { NavigationService } from '../../core/navigation/navigation.service';
import { MegaMenuGroup, NavItem, hasMenu } from '../../core/navigation/navigation.model';

/**
 * The promo card's column in `.mega__grid` below — `repeat(4, 1fr) minmax(200px, 1.1fr)` with a
 * 32 px column gap — inside a container that caps at 1440 px. The mega menu only exists from
 * 1024 px, so there is nothing narrower to describe; without this the browser would size the
 * card against the whole viewport and fetch the largest rendition for a 260 px picture.
 */
const PROMO_SIZES =
  '(min-width: 1440px) 263px,' +
  ' (min-width: 1280px) calc(1.1 * (100vw - 224px) / 5.1),' +
  ' calc(1.1 * (100vw - 192px) / 5.1)';

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
                  @if (item.shopAll; as shopAll) {
                    <a class="mega__all" [routerLink]="shopAll.path" (click)="close(item.id)">
                      {{ shopAll.label }}
                      <sd-icon name="chevron-right" size="sm" />
                    </a>
                  }

                  @for (column of item.columns; track column.key) {
                    <div class="mega__col">
                      <ul>
                        @for (group of column.groups; track group.link.path) {
                          <li>
                            @if (group.children.length > 0) {
                              <!-- A category with children titles its group; a leaf category is
                                   just a link, so nothing is drawn for it.

                                   A paragraph rather than an h3, which is #76 and #20's SH11 - one
                                   defect seen from two distances. The mega menu is in the DOM ahead
                                   of the page's own h1, so these titles made every storefront page
                                   read "H3 Girls, H3 Boys, H3 Baby, H2 Menu, H1 the-page" to
                                   someone navigating by headings: three department headings and a
                                   "Menu" before the page says what it is. axe reports nothing,
                                   because nested heading order is a best-practice rule rather than
                                   a WCAG failure, which is exactly why it survived this long. The
                                   group is still labelled - the list below is aria-labelledby this
                                   paragraph - so the relationship the h3 carried is kept and only
                                   its place in the heading outline is given up. -->
                              <p class="mega__title" [id]="groupId(item, group)">
                                <a
                                  class="mega__link mega__link--group"
                                  [routerLink]="group.link.path"
                                  (click)="close(item.id)"
                                >
                                  {{ group.link.label }}
                                </a>
                              </p>
                              <ul [attr.aria-labelledby]="groupId(item, group)">
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
                            } @else {
                              <a
                                class="mega__link"
                                [routerLink]="group.link.path"
                                (click)="close(item.id)"
                              >
                                {{ group.link.label }}
                              </a>
                            }
                          </li>
                        }
                      </ul>
                    </div>
                  }

                  @if (item.promo; as promo) {
                    <!-- #215: an admin card may point off the site; that is an ordinary anchor in a new tab.
                         The card's insides are written out twice on purpose: a shared ng-template needed
                         NgTemplateOutlet, which cost the initial bundle 2 kB more than the copy does. Every
                         string is interpolated, so it is text. -->
                    @if (promo.cta.target.kind === 'internal') {
                      <a
                        class="mega__promo"
                        data-testid="mega-promo"
                        [routerLink]="promo.cta.target.path"
                        [queryParams]="promo.cta.target.queryParams"
                        (click)="close(item.id)"
                      >
                        <span class="mega__promo-media">
                          @if (promo.imageUrl) {
                            <img
                              [src]="promo.imageUrl"
                              [attr.srcset]="promo.imageSrcSet"
                              [attr.sizes]="promo.imageSrcSet ? promoSizes : null"
                              [alt]="promo.imageAlt"
                            />
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
                    } @else {
                      <a
                        class="mega__promo"
                        data-testid="mega-promo"
                        [href]="promo.cta.target.href"
                        target="_blank"
                        rel="noopener noreferrer"
                        (click)="close(item.id)"
                      >
                        <span class="mega__promo-media">
                          @if (promo.imageUrl) {
                            <img
                              [src]="promo.imageUrl"
                              [attr.srcset]="promo.imageSrcSet"
                              [attr.sizes]="promo.imageSrcSet ? promoSizes : null"
                              [alt]="promo.imageAlt"
                            />
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
      gap: var(--space-2) var(--space-4);
      text-align: left;
    }
    /* "Shop all Women" is the menu's own first link, on a row above the categories, so it never
       reads as one more category in the first column. */
    .mega__all {
      grid-column: 1 / -1;
      display: inline-flex;
      align-items: center;
      gap: var(--space-0-5);
      justify-self: start;
      min-height: var(--tap);
      padding-inline-end: var(--space-1);
      font-size: var(--text-label);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    .mega__col ul {
      list-style: none;
    }
    .mega__title {
      font-size: var(--text-small);
      font-weight: var(--weight-semibold);
      /* The reset gives h1-h4 a tighter line-height than body text; this keeps the look these
         titles had as h3s now that they are paragraphs. */
      line-height: 1.2;
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
      /* #215: the words are an admin's now, and a long unbroken one (a 100-character heading, a pasted
         address) pushed the card past its column and the page sideways at 1024 px. It wraps instead. */
      min-width: 0;
      overflow-wrap: anywhere;
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

  protected readonly promoSizes = PROMO_SIZES;

  private readonly expanded = signal<string | null>(null);

  /** The id of the department whose mega menu is open, or `null`. */
  readonly openId = this.expanded.asReadonly();

  /**
   * The departments Escape has closed, so focus alone does not reopen them.
   *
   * **A set and not one id, which was F3.** With a single slot a second Escape overwrote the first, so
   * four keystrokes brought #20's SH8 straight back: focus Women, Escape, Tab to Men, Escape,
   * Shift+Tab to Women - and Women opened again, because the slot now held `men`. A shopper working
   * along the nav with the keyboard dismisses more than one menu, so the guard has to remember more
   * than one. Cleared as a whole when focus leaves the nav, which is the scope that was already right.
   */
  private readonly dismissed = new Set<string>();

  protected menuOf(item: NavItem): boolean {
    return hasMenu(item);
  }

  protected panelId(item: NavItem): string {
    return `mega-${item.id}`;
  }

  /**
   * The id of a group's title, so the list of child categories under it can be `aria-labelledby` it.
   *
   * Built from the category's own path, which is unique within a menu, and reduced to the characters an
   * id is comfortable in — a path carries slashes, and an id with a slash in it is valid HTML but a
   * nuisance in every selector that ever looks for it.
   */
  protected groupId(item: NavItem, group: MegaMenuGroup): string {
    return `mega-${item.id}-${group.link.path.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '')}`;
  }

  /** Hovering always opens: a pointer arriving is a fresh request, even after Escape. */
  openFromPointer(item: NavItem): void {
    this.dismissed.clear();
    this.show(item);
  }

  /**
   * Focus opens the menu too, unless this department was just dismissed with Escape. Without that
   * guard, returning focus to the department link would immediately reopen what Escape closed.
   */
  openFromFocus(item: NavItem): void {
    if (this.dismissed.has(item.id)) {
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
    this.dismissed.clear();
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

    // The dismissed guard belongs to the **navigation**, not to one list item, and clearing it here
    // per item was #20's SH8 (reproduced 6/6). Focus Women, press Escape — the menu closes and
    // `dismissed` is 'women'. Tab to Men: Women's list item loses focus, this ran, and the guard was
    // wiped. Shift+Tab back to Women and focus reopened the menu Escape had just closed, which is the
    // one thing Escape is supposed to be able to do. So it is cleared only once focus has left the
    // nav altogether, which is a fresh arrival; moving between departments inside it is not.
    if (!(nextFocus instanceof Node && this.host.nativeElement.contains(nextFocus))) {
      this.dismissed.clear();
    }

    this.close(id);
  }

  /** Closes the open mega menu and leaves focus on its department link. */
  onEscape(): void {
    const id = this.expanded();
    if (id === null) {
      return;
    }

    this.dismissed.add(id);
    this.close(id);
    this.host.nativeElement
      .querySelector<HTMLElement>(`[aria-controls="mega-${id}"]`)
      ?.focus();
  }
}
