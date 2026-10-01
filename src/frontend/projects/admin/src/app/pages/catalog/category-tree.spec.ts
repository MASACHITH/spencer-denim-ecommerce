import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminCatalogApi } from '../../core/catalog/admin-catalog-api';
import { AdminCategoryNode, AdminCategoryTree } from '../../core/catalog/admin-catalog.model';
import { CategoryTree } from './category-tree';

/** Hosts the tree so its required input and its `reloadRequested` output can both be driven. */
@Component({
  imports: [CategoryTree],
  template: '<sda-category-tree [tree]="tree()" (reloadRequested)="reloads = reloads + 1" />',
})
class Host {
  readonly tree = signal<AdminCategoryTree>(emptyTree());
  reloads = 0;
}

function node(
  id: number,
  name: string,
  overrides: Partial<AdminCategoryNode> = {},
): AdminCategoryNode {
  return {
    id,
    departmentId: 1,
    parentCategoryId: null,
    path: `/${id}/`,
    depth: 1,
    name,
    slug: name.toLowerCase().replace(/[^a-z0-9]+/g, '-'),
    description: null,

    // Not null, and that is the point: with every image column null a save that silently drops the field
    // round-trips null to null and no assertion can see it (W1-12 fix round 1 blocker).
    imageAssetId: 41,
    bannerAssetId: 42,
    bannerLinkUrl: '/women/jeans',
    ageGroupId: 3,
    sizeGuideId: 2,
    sortOrder: 0,
    showInMegaMenu: true,
    megaMenuColumn: null,
    isActive: true,
    seoTitle: null,
    seoDescription: null,
    rowVersion: `rv-${id}`,
    productCount: 0,
    primaryProductCount: 0,
    children: [],
    ...overrides,
  };
}

function emptyTree(categories: readonly AdminCategoryNode[] = []): AdminCategoryTree {
  return {
    departmentId: 1,
    departmentName: 'Women',
    departmentSlug: 'women',
    departmentIsActive: true,
    categories,
  };
}

/**
 * Dresses (with Midi and Maxi under it), Jeans and Skirts — two sibling lists, one deactivated category and
 * one with products in it.
 *
 * Three at the top level rather than two on purpose: with two, a move always lands at an end of the list and
 * the "focus stays on the button that moved" assertion could not tell that behaviour from the fallback.
 */
function seededTree(): AdminCategoryTree {
  return emptyTree([
    node(10, 'Dresses', {
      productCount: 3,
      primaryProductCount: 3,
      children: [
        node(11, 'Midi', { parentCategoryId: 10, path: '/10/11/', depth: 2 }),
        node(12, 'Maxi', { parentCategoryId: 10, path: '/10/12/', depth: 2, sortOrder: 1 }),
      ],
    }),
    node(20, 'Jeans', { sortOrder: 1, isActive: false, productCount: 2, primaryProductCount: 2 }),
    node(30, 'Skirts', { sortOrder: 2, productCount: 1, primaryProductCount: 1 }),
  ]);
}

describe('CategoryTree', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function create(tree: AdminCategoryTree = seededTree()): ComponentFixture<Host> {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.tree.set(tree);
    fixture.detectChanges();

    // The pickers' lists are loaded once with the component (doc 11 P1 step 2).
    controller.expectOne(AdminCatalogApi.formOptionsUrl).flush({
      ageGroups: [
        { id: 1, name: 'Baby' },
        { id: 3, name: 'Kids' },
      ],
      sizeGuides: [
        { id: 2, name: "Women's clothing" },
        { id: 5, name: "Kids' clothing" },
      ],
    });
    fixture.detectChanges();

    return fixture;
  }

  function text(fixture: ComponentFixture<Host>): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  function button(fixture: ComponentFixture<Host>, testId: string): HTMLButtonElement {
    const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      `[data-testid="${testId}"]`,
    );

    if (!element) {
      throw new Error(`no [data-testid="${testId}"] on the page`);
    }

    return element;
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  // ---- rendering -------------------------------------------------------------------------------

  it('lists every category depth-first, with its level as an accessible level', () => {
    const fixture = create();

    const items = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.tree__item'),
    ];

    expect(items.map((item) => item.getAttribute('data-testid'))).toEqual([
      'category-10',
      'category-11',
      'category-12',
      'category-20',
      'category-30',
    ]);
    expect(items.map((item) => item.getAttribute('aria-level'))).toEqual(['1', '2', '2', '1', '1']);
  });

  /**
   * The storefront's reader drops a deactivated category, which is what the switch means. This screen must
   * show it, or it could never be switched back on.
   */
  it('shows a deactivated category as hidden, with a button that would show it again', () => {
    const fixture = create();

    expect(text(fixture)).toContain('Hidden');
    expect(button(fixture, 'toggle-20').textContent).toContain('Show');
    expect(button(fixture, 'toggle-10').textContent).toContain('Hide');
  });

  it('says how many products are in each category, which is what the remove guard counts', () => {
    const fixture = create();

    expect(text(fixture)).toContain('3 products');
  });

  it('offers no Add child on a category that is already at the third level', () => {
    const deep = emptyTree([
      node(10, 'Dresses', {
        children: [
          node(11, 'Midi', {
            parentCategoryId: 10,
            path: '/10/11/',
            depth: 2,
            children: [node(12, 'Pleated', { parentCategoryId: 11, path: '/10/11/12/', depth: 3 })],
          }),
        ],
      }),
    ]);

    const fixture = create(deep);
    const row = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="category-12"]');

    expect(row?.textContent).not.toContain('Add child');
    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('[data-testid="category-11"]')
        ?.textContent,
    ).toContain('Add child');
  });

  it('says there are no categories yet rather than drawing an empty list', () => {
    const fixture = create(emptyTree());

    expect(text(fixture)).toContain('no categories yet');
  });

  // ---- reorder ---------------------------------------------------------------------------------

  /**
   * Moves are staged. The API takes a whole sibling list, so a request per keypress would make every
   * intermediate arrangement its own write and its own audit row.
   */
  it('stages a move and sends nothing until Save order', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('1 list has a new order');

    const items = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.tree__item'),
    ];
    expect(items.map((item) => item.getAttribute('data-testid'))).toEqual([
      'category-20',
      'category-10',
      'category-11',
      'category-12',
      'category-30',
    ]);
  });

  it('sends the whole list for the one parent whose order changed', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    button(fixture, 'save-order').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/order`);
    expect(request.request.body).toEqual({
      departmentId: 1,
      parentCategoryId: null,
      orderedCategoryIds: [20, 10, 30],
    });
    request.flush(null);
    fixture.detectChanges();

    expect(fixture.componentInstance.reloads).toBe(1);
  });

  /**
   * Two lists moved, so two decisions and two requests. One tree-shaped request would have no route on the
   * server, and one audit row for both would not say what either list became.
   */
  it('sends one request per changed sibling list', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    button(fixture, 'move-down-11').click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('2 lists have a new order');

    button(fixture, 'save-order').click();
    fixture.detectChanges();

    grantToken();
    const first = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/order`);
    expect(first.request.body.parentCategoryId).toBeNull();
    first.flush(null);
    fixture.detectChanges();

    grantToken();
    const second = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/order`);
    expect(second.request.body).toEqual({
      departmentId: 1,
      parentCategoryId: 10,
      orderedCategoryIds: [12, 11],
    });
    second.flush(null);
    fixture.detectChanges();
  });

  /** A refused list must stop the run, not leave later lists half-sent. */
  it('stops at the first refused list and offers a reload', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    button(fixture, 'save-order').click();
    fixture.detectChanges();
    grantToken();

    controller.expectOne(`${AdminCatalogApi.categoriesUrl}/order`).flush(
      {
        detail: 'This order was made from an older version of the list…',
        code: 'stale_order',
        missingIds: [30],
        unknownIds: [],
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    const error = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="tree-error"]');
    expect(error?.textContent).toContain('older version of the list');
    expect(error?.querySelector('button')?.textContent).toContain('Reload');
  });

  it('cannot move a category past either end of its own sibling list', () => {
    const fixture = create();

    expect(button(fixture, 'move-up-10').disabled).toBe(true);
    expect(button(fixture, 'move-down-30').disabled).toBe(true);

    // Midi and Maxi are a list of two: "up" from the first must not promote it a level.
    expect(button(fixture, 'move-up-11').disabled).toBe(true);
    expect(button(fixture, 'move-down-12').disabled).toBe(true);
  });

  it('keeps focus on the button that moved, so a keyboard user can press it again', async () => {
    const fixture = create();

    const down = button(fixture, 'move-down-10');
    down.focus();
    down.click();
    fixture.detectChanges();
    await new Promise((resolve) => queueMicrotask(() => resolve(null)));

    expect(document.activeElement).toBe(button(fixture, 'move-down-10'));
  });

  it('moves focus to the other direction when the pressed button runs out of list', async () => {
    const fixture = create();

    button(fixture, 'move-up-20').focus();
    button(fixture, 'move-up-20').click();
    fixture.detectChanges();
    await new Promise((resolve) => queueMicrotask(() => resolve(null)));

    expect(button(fixture, 'move-up-20').disabled).toBe(true);
    expect(document.activeElement).toBe(button(fixture, 'move-down-20'));
  });

  it('moves on Alt and an arrow key as well as on a click', () => {
    const fixture = create();

    button(fixture, 'move-up-10').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true }),
    );
    fixture.detectChanges();

    const items = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.tree__item'),
    ];
    expect(items[0].getAttribute('data-testid')).toBe('category-20');
  });

  it('announces a move with the position in its own sibling list', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();

    const status = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    expect(status?.textContent).toContain('Dresses moved to position 2 of 3');
  });

  /**
   * The round-2 Major. Discarding is the right behaviour — a category reload can genuinely add or remove
   * nodes, so reapplying a stale arrangement would send a list the server refuses with `stale_order`, which
   * is why the department list's id-based reapply is not copied here. Doing it **silently** was the defect:
   * the banner and the Save order button vanished with no word, and the live region said only that some
   * other category had been updated.
   */
  it('says so, on screen and out loud, when a reload discards a staged order', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    expect(text(fixture)).toContain('new order');

    // What the parent does after any write: hand the child the tree the server now holds.
    fixture.componentInstance.tree.set(seededTree());
    fixture.detectChanges();

    const notice = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="order-discarded"]',
    );

    expect(notice).not.toBeNull();
    expect(notice?.getAttribute('role')).toBe('status');
    expect(notice?.textContent).toContain('order you had arranged was discarded');

    const announcement = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="tree-announcement"]',
    );
    expect(announcement?.textContent).toContain('order you had arranged was discarded');
  });

  it('lets the discard notice be dismissed, and does not show it again', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    fixture.componentInstance.tree.set(seededTree());
    fixture.detectChanges();

    button(fixture, 'dismiss-discarded').click();
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="order-discarded"]'),
    ).toBeNull();
  });

  /**
   * Found by writing the test, not by reading the code: the staged arrangement is a list of the nodes that
   * were on screen, and after a reload some of them may not exist. Keeping it would draw a tree the server
   * does not have and then send a list it would rightly refuse.
   */
  it('drops a staged order when a fresh tree arrives from the server', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    expect(text(fixture)).toContain('new order');

    // What the parent does after any write: hand the child the tree the server now holds.
    fixture.componentInstance.tree.set(seededTree());
    fixture.detectChanges();

    expect(text(fixture)).not.toContain('new order');

    const items = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.tree__item'),
    ];
    expect(items[0].getAttribute('data-testid')).toBe('category-10');
  });

  it('discards a staged order without sending anything', () => {
    const fixture = create();

    button(fixture, 'move-down-10').click();
    fixture.detectChanges();
    expect(text(fixture)).toContain('new order');

    const discard = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>(
        '[data-testid="order-pending"] button',
      ),
    ].find((candidate) => candidate.textContent?.includes('Discard'));

    discard!.click();
    fixture.detectChanges();

    expect(text(fixture)).not.toContain('new order');
  });

  // ---- create, edit and move to another parent -------------------------------------------------

  /**
   * m10: these two were deferred with the image pickers and should not have been. The images need ADM-18's
   * media library; an age group is four seeded rows. Without them a Kids category made here could never be
   * given an age group, and a new category would silently get no size guide — breaking PDP-04 for every
   * product in it with no override of its own.
   */
  it('offers the age groups and size guides the API listed', async () => {
    const fixture = create();

    button(fixture, 'edit-10').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    const ageGroups = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLOptionElement>(
        '[data-testid="field-age-group"] option',
      ),
    ].map((option) => option.textContent?.trim());

    expect(ageGroups).toEqual(['None', 'Baby', 'Kids']);

    const sizeGuides = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLOptionElement>(
        '[data-testid="field-size-guide"] option',
      ),
    ].map((option) => option.textContent?.trim());

    expect(sizeGuides).toEqual(['None', "Women's clothing", "Kids' clothing"]);
  });

  it('sends the age group and size guide the admin picked', async () => {
    const fixture = create();

    button(fixture, 'edit-10').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="field-age-group"]',
    )!;
    select.selectedIndex = 2;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    button(fixture, 'save-category').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/10`);
    expect(request.request.body.ageGroupId).toBe(3);
    request.flush(null);
    fixture.detectChanges();
  });

  /**
   * A select that silently loses the row's value is worse than a short one: it misreports the row, and the
   * next save writes what it shows. The fallback keeps the value visible and says why the list is short.
   */
  it('keeps the row’s own age group and size guide visible when the lists cannot be loaded', async () => {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.tree.set(seededTree());
    fixture.detectChanges();

    controller
      .expectOne(AdminCatalogApi.formOptionsUrl)
      .flush({ detail: 'nope' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    // The form still opens at all.
    button(fixture, 'edit-10').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    const ageGroup = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="field-age-group"]',
    )!;
    const options = [...ageGroup.options].map((option) => option.textContent?.trim());

    expect(options).toEqual(['None', 'Current age group (#3) — the list could not be loaded']);

    // And it is the one the row actually holds that is selected, not "None".
    expect(ageGroup.selectedIndex).toBe(1);

    const sizeGuide = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="field-size-guide"]',
    )!;
    expect([...sizeGuide.options].map((option) => option.textContent?.trim())).toEqual([
      'None',
      'Current size guide (#2) — the list could not be loaded',
    ]);
  });

  it('offers only the real choices for a category that has neither', async () => {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.tree.set(
      emptyTree([node(90, 'Blank', { ageGroupId: null, sizeGuideId: null })]),
    );
    fixture.detectChanges();

    controller
      .expectOne(AdminCatalogApi.formOptionsUrl)
      .flush({ detail: 'nope' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    button(fixture, 'edit-90').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    const ageGroup = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="field-age-group"]',
    )!;

    expect([...ageGroup.options].map((option) => option.textContent?.trim())).toEqual(['None']);
  });

  it('creates a category under the parent the button named', () => {
    const fixture = create();

    const addChild = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
    ].find((candidate) => candidate.getAttribute('aria-label') === 'Add a category under Dresses');

    addChild!.click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('New category');

    button(fixture, 'save-category').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(AdminCatalogApi.categoriesUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.departmentId).toBe(1);
    expect(request.request.body.parentCategoryId).toBe(10);
    request.flush({ id: 99 });
    fixture.detectChanges();

    expect(fixture.componentInstance.reloads).toBe(1);
  });

  it('puts a rejected slug against the slug box', () => {
    const fixture = create();

    button(fixture, 'add-top-level').click();
    fixture.detectChanges();
    button(fixture, 'save-category').click();
    fixture.detectChanges();
    grantToken();

    controller.expectOne(AdminCatalogApi.categoriesUrl).flush(
      {
        title: 'One or more fields are invalid.',
        errors: { slug: ['The URL slug may contain only lower-case letters, digits and single hyphens.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="slug-error"]')?.textContent,
    ).toContain('lower-case');
    expect(fixture.componentInstance.reloads).toBe(0);
  });

  /**
   * The parent list is where a re-parenting happens, so it must never offer a parent that would make a loop
   * or push the subtree past three levels — the two refusals the server makes, made unreachable here.
   */
  it('never offers a category its own descendant or a parent with no room as a new parent', async () => {
    const deep = emptyTree([
      node(10, 'Dresses', {
        children: [
          node(11, 'Midi', {
            parentCategoryId: 10,
            path: '/10/11/',
            depth: 2,
            children: [node(12, 'Pleated', { parentCategoryId: 11, path: '/10/11/12/', depth: 3 })],
          }),
        ],
      }),
      node(20, 'Jeans', { sortOrder: 1 }),
    ]);

    const fixture = create(deep);

    button(fixture, 'edit-11').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    const options = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLOptionElement>(
        '[data-testid="field-parent"] option',
      ),
    ].map((option) => option.textContent?.trim());

    // Midi carries Pleated, so a new parent must be at level 1. Its own subtree is excluded.
    expect(options).toContain('Top level');
    expect(options).toContain('Dresses');
    expect(options).toContain('Jeans');
    expect(options).not.toContain('— Midi');
    expect(options).not.toContain('— — Pleated');
  });

  /**
   * The same hazard as the department form, on three columns at once. The tile image and the banner have no
   * picker yet (ADM-18's library listing does not exist), so the form carries what it loaded; a `PUT` body
   * that omits them tells the API to clear them.
   */
  it('a category save keeps every field it cannot yet show a control for', async () => {
    const fixture = create();

    button(fixture, 'edit-10').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    button(fixture, 'save-category').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/10`);
    expect(request.request.body.imageAssetId).toBe(41);
    expect(request.request.body.bannerAssetId).toBe(42);
    expect(request.request.body.ageGroupId).toBe(3);
    expect(request.request.body.sizeGuideId).toBe(2);
    expect(request.request.body.bannerLinkUrl).toBe('/women/jeans');
    request.flush(null);
    fixture.detectChanges();
  });

  it('sends a parent change as part of the edit, with the row version', async () => {
    const fixture = create();

    button(fixture, 'edit-11').click();
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '[data-testid="field-parent"]',
    )!;
    select.selectedIndex = 0;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    button(fixture, 'save-category').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/11`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.parentCategoryId).toBeNull();
    expect(request.request.body.rowVersion).toBe('rv-11');
    request.flush(null);
    fixture.detectChanges();
  });

  // ---- hide and remove -------------------------------------------------------------------------

  /**
   * Doc 08 §7.1: this is the one switch that takes a section off the shop. The numbers are fetched and shown
   * before the confirm button is offered — finding out afterwards is the thing this exists to prevent.
   */
  it('shows what hiding a category will do before it is confirmed', () => {
    const fixture = create();

    button(fixture, 'toggle-10').click();
    fixture.detectChanges();

    controller.expectOne(`${AdminCatalogApi.categoriesUrl}/10/deactivation-impact`).flush({
      categoryId: 10,
      name: 'Dresses',
      isActive: true,
      productsLeavingTheStorefront: 3,
      descendantCategories: 1,
      descendantProductsLosingTheirMenuRoute: 4,
      examples: ['Linen shift dress', 'Midi wrap dress'],
    });
    fixture.detectChanges();

    const panel = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="confirm-panel"]');

    expect(panel?.textContent).toContain('3');
    expect(panel?.textContent).toContain('will disappear from listings');
    expect(panel?.textContent).toContain('1');
    expect(panel?.textContent).toContain('drop out of the mega menu');
    expect(panel?.textContent).toContain('4');
    expect(panel?.textContent).toContain('no menu route left to them');
    expect(panel?.textContent).toContain('Linen shift dress');
  });

  it('does not ask for an impact when it is switching a category back on', () => {
    const fixture = create();

    button(fixture, 'toggle-20').click();
    fixture.detectChanges();

    // No impact request at all: verify() in afterEach fails if one was made.
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="confirm-panel"]')?.textContent,
    ).toContain('Show “Jeans” on the shop again?');
  });

  it('switches the category off only after the confirmation, through its own route', () => {
    const fixture = create();

    button(fixture, 'toggle-10').click();
    fixture.detectChanges();
    controller.expectOne(`${AdminCatalogApi.categoriesUrl}/10/deactivation-impact`).flush({
      categoryId: 10,
      name: 'Dresses',
      isActive: true,
      productsLeavingTheStorefront: 3,
      descendantCategories: 1,
      descendantProductsLosingTheirMenuRoute: 0,
      examples: [],
    });
    fixture.detectChanges();

    button(fixture, 'confirm-yes').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/10/active`);
    expect(request.request.body).toEqual({ isActive: false, rowVersion: 'rv-10' });
    request.flush(null);
    fixture.detectChanges();

    expect(fixture.componentInstance.reloads).toBe(1);
  });

  it('removes a category only after the confirmation, with the row version in If-Match', () => {
    const fixture = create();

    button(fixture, 'remove-11').click();
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="confirm-panel"]')?.textContent,
    ).toContain('Remove “Midi”?');

    button(fixture, 'confirm-yes').click();
    fixture.detectChanges();
    grantToken();

    const request = controller.expectOne(`${AdminCatalogApi.categoriesUrl}/11`);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"rv-11"');
    request.flush(null);
    fixture.detectChanges();

    expect(fixture.componentInstance.reloads).toBe(1);
  });

  /** The refusal has to name what is in the way, or an admin presses the same button again. */
  it('shows the counts behind a refused removal', () => {
    const fixture = create();

    button(fixture, 'remove-10').click();
    fixture.detectChanges();
    button(fixture, 'confirm-yes').click();
    fixture.detectChanges();
    grantToken();

    controller.expectOne(`${AdminCatalogApi.categoriesUrl}/10`).flush(
      {
        detail: "'Dresses' still has 1 category underneath it and 3 products in it.",
        code: 'category_in_use',
        childCategories: 1,
        linkedProducts: 3,
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    const error = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="tree-error"]');
    expect(error?.textContent).toContain('1 category underneath it');
    expect(error?.textContent).toContain('3 products in it');
    expect(fixture.componentInstance.reloads).toBe(0);
  });
});
