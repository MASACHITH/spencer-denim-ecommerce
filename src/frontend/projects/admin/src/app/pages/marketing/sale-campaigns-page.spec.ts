import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { SaleCampaignsPage } from './sale-campaigns-page';
import { AdminCampaignApi } from '../../core/marketing/admin-campaign-api';
import {
  AdminSaleCampaign,
  AdminSaleCampaignSaved,
  AdminSaleCampaignTarget,
} from '../../core/marketing/admin-campaign.model';
import { AdminCatalogApi } from '../../core/catalog/admin-catalog-api';
import { AdminCategoryNode, AdminDepartment } from '../../core/catalog/admin-catalog.model';

function department(id: number, name: string): AdminDepartment {
  return {
    id,
    name,
    slug: name.toLowerCase(),
    description: null,
    heroImageAssetId: null,
    sortOrder: id,
    isActive: true,
    seoTitle: null,
    seoDescription: null,
    rowVersion: 'AAAAAAAAB9E=',
    categoryCount: 1,
    storefrontProductCount: 4,
  };
}

function category(
  id: number,
  departmentId: number,
  name: string,
  children: readonly AdminCategoryNode[] = [],
): AdminCategoryNode {
  return {
    id,
    departmentId,
    parentCategoryId: null,
    path: `/${departmentId}/${id}/`,
    depth: 1,
    name,
    slug: name.toLowerCase(),
    description: null,
    imageAssetId: null,
    bannerAssetId: null,
    bannerLinkUrl: null,
    ageGroupId: null,
    sizeGuideId: null,
    sortOrder: id,
    showInMegaMenu: true,
    megaMenuColumn: null,
    isActive: true,
    seoTitle: null,
    seoDescription: null,
    rowVersion: 'AAAAAAAAB9I=',
    productCount: 6,
    primaryProductCount: 6,
    children,
  };
}

function target(overrides: Partial<AdminSaleCampaignTarget> = {}): AdminSaleCampaignTarget {
  return {
    targetType: 'Category',
    departmentId: null,
    categoryId: 31,
    productId: null,
    includeSubcategories: true,
    name: 'Women › Shirts & Blouses',
    ...overrides,
  };
}

function campaign(overrides: Partial<AdminSaleCampaign> = {}): AdminSaleCampaign {
  return {
    id: 4,
    name: 'A fixture, not a sale',
    description: null,
    discountType: 'Percent',
    discountValue: 20,
    startsAt: '2026-10-19T00:00:00',
    endsAt: '2026-10-26T00:00:00',
    isActive: true,
    bannerAssetId: null,
    targets: [target()],
    status: 'Scheduled',
    rowVersion: 'AAAAAAAACl8=',
    ...overrides,
  };
}

function saved(overrides: Partial<AdminSaleCampaignSaved> = {}): AdminSaleCampaignSaved {
  return { campaign: campaign(), warnings: [], productsRepriced: 6, ...overrides };
}

/**
 * Sale campaigns (ADM-14, doc 11 P3 section A).
 *
 * **The acceptance test is doc 11 line 450's worked example** — "Linen Week, 20% off Women › Shirts &
 * Blouses" — and the first test here is exactly that, asserted on the request body rather than on the
 * screen, because the body is what the shop acts on.
 *
 * Nothing here can observe the API: that is `AdminSaleCampaignEndpointTests`' job against a real SQL
 * Server (#126). What these prove is the form — the body it sends, the feedback it shows and the two
 * things it refuses to do silently.
 */
describe('SaleCampaignsPage (ADM-14)', () => {
  let fixture: ComponentFixture<SaleCampaignsPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const treeUrl = (departmentId: number) =>
    `${AdminCatalogApi.departmentsUrl}/${departmentId}/categories`;

  /**
   * Creates the page and answers its three kinds of opening request: the campaign list, the departments,
   * and one category tree per department.
   */
  async function open(
    campaigns: readonly AdminSaleCampaign[] = [campaign()],
    departments: readonly AdminDepartment[] = [department(3, 'Women')],
  ): Promise<void> {
    fixture = TestBed.createComponent(SaleCampaignsPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(AdminCampaignApi.campaignsUrl).flush(campaigns);
    http.expectOne(AdminCatalogApi.departmentsUrl).flush(departments);

    await settle();

    for (const own of departments) {
      http.expectOne(treeUrl(own.id)).flush({
        departmentId: own.id,
        departmentName: own.name,
        departmentSlug: own.slug,
        departmentIsActive: true,
        categories: [category(31, own.id, 'Shirts & Blouses', [category(44, own.id, 'Linen')])],
      });
    }

    await settle();
  }

  function click(testId: string): void {
    element.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!.click();
  }

  function type(testId: string, value: string): void {
    const input = element.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function choose(testId: string, value: string): void {
    const select = element.querySelector<HTMLSelectElement>(`[data-testid="${testId}"]`)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
  }

  function said(): string {
    return element.querySelector('[data-testid="campaign-said"]')?.textContent?.trim() ?? '';
  }

  /** The antiforgery token every mutating call fetches first. */
  function answerAntiforgery(): void {
    http
      .match((request) => request.url.includes('antiforgery'))
      .forEach((request) => request.flush({ token: 'token' }));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  /**
   * **Doc 11 line 450's worked example, step for step.** This is the acceptance test for the screen: a
   * staff member names the campaign, chooses 20%, sets a window, picks Women › Shirts & Blouses, adds it
   * and saves. Every field of the body is asserted, including `bannerAssetId` and `targets`, because the
   * endpoint refuses a body that leaves a key out and the field that would be dropped is the one that
   * decides which products go on sale.
   */
  it('creates doc 11 line 450 Linen Week, 20% off Women Shirts & Blouses', async () => {
    await open([]);

    type('campaign-name', 'Linen Week');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');

    choose('target-type', 'Category');
    await settle();

    choose('target-category', '31');
    click('add-target');
    await settle();

    expect(element.querySelector('[data-testid="campaign-targets"]')?.textContent).toContain(
      'Women › Shirts & Blouses',
    );

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );

    expect(request.request.body).toEqual({
      name: 'Linen Week',
      description: null,
      discountType: 'Percent',
      discountValue: 20,
      startsAt: '2026-10-19T00:00',
      endsAt: '2026-10-26T00:00',
      isActive: true,
      bannerAssetId: null,
      targets: [
        {
          targetType: 'Category',
          departmentId: null,
          categoryId: 31,
          productId: null,
          includeSubcategories: true,
        },
      ],
    });

    // The name comes from the saved row the API answers with, not from the box — so a server that
    // trimmed or changed it is what the staff member is told about.
    request.flush(saved({ campaign: campaign({ name: 'Linen Week' }) }));
    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign({ name: 'Linen Week' })]);
    await settle();

    expect(said()).toContain('Linen Week is saved');
    expect(said()).toContain('6 products were repriced');
  });

  /**
   * **The dates go out with no offset and no `Z`.** The API's contract is the shop's own wall-clock time
   * and it refuses anything carrying an offset, because the server would otherwise guess whether the
   * caller meant Colombo — and guessing wrong moves a sale's window by five and a half hours.
   */
  it('sends both dates as the shop own time, with no offset', async () => {
    await open([]);

    type('campaign-name', 'T4WINDOW');
    type('campaign-value', '10');
    type('campaign-starts', '2026-10-19T23:59');
    type('campaign-ends', '2026-10-26T23:59');
    choose('target-category', '31');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );
    const body = request.request.body as { startsAt: string; endsAt: string };

    expect(body.startsAt).toBe('2026-10-19T23:59');
    expect(body.endsAt).toBe('2026-10-26T23:59');
    expect(`${body.startsAt}${body.endsAt}`).not.toContain('Z');
    expect(`${body.startsAt}${body.endsAt}`).not.toContain('+');

    request.flush(saved());
    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();
  });

  /** A department target carries its id in `departmentId` and nothing in the other two. */
  it('sends a department target with only its own id', async () => {
    await open([]);

    type('campaign-name', 'T4DEPT');
    type('campaign-value', '500');
    choose('campaign-type', 'FixedAmount');
    await settle();

    type('campaign-value', '500');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');

    choose('target-type', 'Department');
    await settle();

    choose('target-department', '3');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );

    expect(request.request.body).toMatchObject({
      discountType: 'FixedAmount',
      discountValue: 500,
      targets: [
        {
          targetType: 'Department',
          departmentId: 3,
          categoryId: null,
          productId: null,

          // Forced true for a department: it already covers every category under it, so the API stores
          // a value nothing could read if the form sent false.
          includeSubcategories: true,
        },
      ],
    });

    request.flush(saved());
    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();
  });

  /** The subtree checkbox is the one choice a category target has, and it travels. */
  it('sends includeSubcategories false when the staff member narrows the category', async () => {
    await open([]);

    type('campaign-name', 'T4NARROW');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');

    click('target-subcategories');
    choose('target-category', '31');
    click('add-target');
    await settle();

    // The chip says so, because "this category only" is the difference between two sales.
    expect(element.querySelector('[data-testid="campaign-targets"]')?.textContent).toContain(
      'this category only',
    );

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );

    expect(
      (request.request.body as { targets: { includeSubcategories: boolean }[] }).targets[0]
        .includeSubcategories,
    ).toBe(false);

    request.flush(saved());
    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();
  });

  /** A nested category is offered under its whole path, which is how a staff member tells two apart. */
  it('lists every category under its full path', async () => {
    await open([]);

    const options = Array.from(
      element.querySelectorAll<HTMLOptionElement>('[data-testid="target-category"] option'),
    ).map((option) => option.textContent?.trim());

    expect(options).toContain('Women › Shirts & Blouses');
    expect(options).toContain('Women › Shirts & Blouses › Linen');
  });

  /**
   * **The warning that stops a staff member creating a campaign that does nothing.**
   *
   * A campaign whose targets reach no product looks identical on the screen to one that is working, and
   * the shop would simply never go on sale. The API answers that on the success path, and it has to be
   * readable rather than only announced — so it is asserted in the rendered warnings as well as in the
   * live region.
   */
  it('shows the API warning that a campaign reaches no product', async () => {
    await open([]);

    type('campaign-name', 'T4EMPTY');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');
    choose('target-category', '31');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );

    request.flush(
      saved({
        productsRepriced: 0,
        warnings: [
          "This campaign's targets do not reach any product, so no price has changed. Check the "
            + 'departments, categories and products it applies to.',
        ],
      }),
    );

    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();

    expect(element.querySelector('[data-testid="campaign-warnings"]')?.textContent).toContain(
      'do not reach any product',
    );

    // And in the live region, which is the node that survives the re-render the save caused.
    expect(said()).toContain('do not reach any product');
    expect(said()).toContain('0 products were repriced');
  });

  /** What the recompute did, which is the only answer to "did that change any price". */
  it('says how many products a save repriced', async () => {
    await open([]);

    type('campaign-name', 'T4PRICED');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');
    choose('target-category', '31');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    http
      .expectOne(
        (candidate) =>
          candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
      )
      .flush(saved({ productsRepriced: 1 }));

    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();

    // Singular, because "1 products" is the kind of detail a client reads as carelessness.
    expect(said()).toContain('One product was repriced');
  });

  /**
   * Saving with nothing chosen is refused here rather than at the server.
   *
   * The chips only exist on the client until Save, and a staff member who has filled in the dates and
   * not pressed "Add to the campaign" has made the one mistake this form invites.
   */
  it('refuses to save a campaign with nothing chosen, and says where to look', async () => {
    await open([]);

    type('campaign-name', 'T4NOTHING');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');

    click('campaign-save');
    await settle();

    expect(element.querySelector('[data-testid="error-targets"]')?.textContent).toContain(
      'at least one department or category',
    );
    expect(said()).toContain('at least one department or category');

    // And nothing was sent: `http.verify()` in afterEach would fail on an unexpected request, so this
    // asserts the absence directly rather than relying on that.
    http.expectNone(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );
  });

  /** The same target twice is a mistake, not something to tidy away silently. */
  it('refuses the same target twice and names it', async () => {
    await open([]);

    choose('target-category', '31');
    click('add-target');
    await settle();

    choose('target-category', '31');
    click('add-target');
    await settle();

    expect(element.querySelector('[data-testid="error-targets"]')?.textContent).toContain(
      'already on this campaign',
    );
    expect(element.querySelectorAll('[data-testid="campaign-targets"] li').length).toBe(1);
  });

  /** A chip can be taken off again before the campaign is saved. */
  it('removes a chosen target', async () => {
    await open([]);

    choose('target-category', '31');
    click('add-target');
    await settle();

    click('drop-target-Category-31');
    await settle();

    expect(element.querySelector('[data-testid="campaign-no-targets"]')).not.toBeNull();
  });

  /**
   * **The derived status is shown and never sent.** Doc 11 line 474: Scheduled → Live → Ended / Inactive
   * is computed from the dates and the active flag, so the screen renames it and there is no status
   * control on the form.
   */
  it('shows the derived status and has no status control', async () => {
    await open([campaign({ status: 'Scheduled' })]);
    expect(element.querySelector('[data-testid="status-4"]')?.textContent).toContain('Starts later');

    await open([campaign({ status: 'Live' })]);
    expect(element.querySelector('[data-testid="status-4"]')?.textContent).toContain('Live');

    await open([campaign({ status: 'Inactive' })]);
    expect(element.querySelector('[data-testid="status-4"]')?.textContent).toContain('Switched off');

    await open([campaign({ status: 'Ended' })]);
    expect(element.querySelector('[data-testid="status-4"]')?.textContent).toContain('Ended');

    expect(element.querySelector('[name="status"]')).toBeNull();
  });

  /**
   * Editing loads the row, and **keeps a product target this screen cannot show**.
   *
   * The save is a full replacement, so a campaign created through the API with an individual product on
   * it would have that product silently taken off the sale by a screen that only knows about departments
   * and categories. The product target travels back untouched and the staff member is told it is there.
   */
  it('keeps a product target through an edit it cannot show', async () => {
    await open([
      campaign({
        targets: [
          target(),
          target({
            targetType: 'Product',
            categoryId: null,
            productId: 88,
            name: 'Relaxed Linen Blend Shirt',
          }),
        ],
      }),
    ]);

    click('edit-4');
    await settle();

    expect(said()).toContain('1 individual product');

    // The category chip is editable; the product is not shown as one.
    expect(element.querySelectorAll('[data-testid="campaign-targets"] li').length).toBe(1);

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url.endsWith('/4') && candidate.method === 'PUT',
    );

    expect((request.request.body as { targets: unknown[] }).targets).toEqual([
      {
        targetType: 'Category',
        departmentId: null,
        categoryId: 31,
        productId: null,
        includeSubcategories: true,
      },
      {
        targetType: 'Product',
        departmentId: null,
        categoryId: null,
        productId: 88,
        includeSubcategories: true,
      },
    ]);

    // The row version from the list travels as an ETag, so an edit from a stale list is refused.
    expect(request.request.headers.get('If-Match')).toBe('"AAAAAAAACl8="');

    request.flush(saved());
    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();
  });

  /**
   * **A product target kept for an edit must not leak into the next campaign.**
   *
   * `edit` holds the product targets this screen cannot show, and the form posts every field — so a
   * `startNew` that did not clear them would put the last-edited campaign's products on the next campaign
   * a staff member created. That is a product on a sale nobody chose, by a screen that never showed it.
   * This was a real defect in this file, found by re-reading it rather than by a failing test.
   */
  it('does not carry a kept product target into the next campaign', async () => {
    await open([
      campaign({
        targets: [
          target(),
          target({
            targetType: 'Product',
            categoryId: null,
            productId: 88,
            name: 'Relaxed Linen Blend Shirt',
          }),
        ],
      }),
    ]);

    click('edit-4');
    await settle();

    // Cancel, which is `startNew`, and then build a fresh campaign.
    click('campaign-cancel');
    await settle();

    type('campaign-name', 'T4FRESH');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');
    choose('target-category', '31');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
    );

    expect((request.request.body as { targets: unknown[] }).targets).toEqual([
      {
        targetType: 'Category',
        departmentId: null,
        categoryId: 31,
        productId: null,
        includeSubcategories: true,
      },
    ]);

    request.flush(saved());
    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    await settle();
  });

  /** The server's field message lands against its own control, which is the only thing saying where. */
  it('shows the server message against the field it named', async () => {
    await open([]);

    type('campaign-name', 'T4BAD');
    type('campaign-value', '95');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');
    choose('target-category', '31');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    http
      .expectOne(
        (candidate) =>
          candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
      )
      .flush(
        { errors: { discountValue: ['Enter a percentage above 0 and up to 90.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

    await settle();

    expect(element.querySelector('[data-testid="error-value"]')?.textContent).toContain('up to 90');
    expect(
      element.querySelector('[data-testid="campaign-value"]')?.getAttribute('aria-invalid'),
    ).toBe('true');
  });

  /**
   * A target's message is named `targets[0]`, and that index is a position in a body this screen built —
   * it means nothing to the person reading it, so the sentences are gathered on to the chip list's own
   * error line.
   */
  it('gathers indexed target messages on to the chip list', async () => {
    await open([]);

    type('campaign-name', 'T4TARGET');
    type('campaign-value', '20');
    type('campaign-starts', '2026-10-19T00:00');
    type('campaign-ends', '2026-10-26T00:00');
    choose('target-category', '31');
    click('add-target');
    await settle();

    click('campaign-save');
    answerAntiforgery();
    await settle();

    http
      .expectOne(
        (candidate) =>
          candidate.url === AdminCampaignApi.campaignsUrl && candidate.method === 'POST',
      )
      .flush(
        { errors: { 'targets[0]': ['That category no longer exists.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

    await settle();

    expect(element.querySelector('[data-testid="error-targets"]')?.textContent).toContain(
      'no longer exists',
    );
  });

  /**
   * **A staff member who can manage campaigns but not read the catalogue is told which permission is
   * missing.** The campaign list and the catalogue are two different permissions (doc 11 P3's table gives
   * this screen `Promotions.*`; the department list is behind `Categories.View`), and the page stays
   * usable for doc 11 P3 step 5 — switching a sale off — rather than failing whole.
   */
  it('names the missing permission when the catalogue cannot be read', async () => {
    fixture = TestBed.createComponent(SaleCampaignsPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(AdminCampaignApi.campaignsUrl).flush([campaign()]);
    http
      .expectOne(AdminCatalogApi.departmentsUrl)
      .flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });

    await settle();

    expect(element.querySelector('[data-testid="campaign-catalog-problem"]')?.textContent).toContain(
      'Departments and categories',
    );

    // The campaign list is still there, which is the point of not failing the page.
    expect(element.querySelector('[data-testid="campaign-4"]')).not.toBeNull();
  });

  /** One department's categories failing is said, because a short select is how a sale goes on the wrong thing. */
  it('says when some categories are missing from the list', async () => {
    fixture = TestBed.createComponent(SaleCampaignsPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(AdminCampaignApi.campaignsUrl).flush([]);
    http
      .expectOne(AdminCatalogApi.departmentsUrl)
      .flush([department(3, 'Women'), department(5, 'Men')]);

    await settle();

    http.expectOne(treeUrl(3)).flush({
      departmentId: 3,
      departmentName: 'Women',
      departmentSlug: 'women',
      departmentIsActive: true,
      categories: [category(31, 3, 'Shirts & Blouses')],
    });
    http.expectOne(treeUrl(5)).flush(null, { status: 500, statusText: 'Server Error' });

    await settle();

    expect(element.querySelector('[data-testid="campaign-catalog-problem"]')?.textContent).toContain(
      '1 of 2',
    );
  });

  /** Two presses to remove, and the confirmation replaces the label on the same button (#109). */
  it('asks once before removing', async () => {
    await open();

    const remove = element.querySelector<HTMLElement>('[data-testid="remove-4"]')!;

    remove.click();
    await settle();

    expect(remove.textContent).toContain('Yes, remove it');

    remove.click();
    answerAntiforgery();
    await settle();

    http
      .expectOne((candidate) => candidate.url.endsWith('/4') && candidate.method === 'DELETE')
      .flush(null, { status: 204, statusText: 'No Content' });

    http.expectOne(AdminCampaignApi.campaignsUrl).flush([]);
    await settle();

    expect(said()).toContain('put back');
  });

  /**
   * **An armed Remove is disarmed by leaving the form, not carried through a reload.**
   *
   * `remove` arms a row on its first press. `startNew` runs on Cancel and after every save, and a build
   * that did not clear `removingId` there left the row's button reading "Yes, remove it" across an
   * unrelated save — so the next single press deleted a campaign the staff member had moved on from,
   * with no second confirmation. Found in the W3-05c tester pass.
   */
  it('disarms an armed Remove when the form is left', async () => {
    await open();

    const remove = element.querySelector<HTMLElement>('[data-testid="remove-4"]')!;

    click('edit-4');
    await settle();

    remove.click();
    await settle();

    expect(remove.textContent).toContain('Yes, remove it');

    click('campaign-cancel');
    await settle();

    expect(
      element.querySelector('[data-testid="remove-4"]')?.textContent?.trim(),
    ).toBe('Remove');
    expect(element.querySelector('[data-testid="remove-cancel-4"]')).toBeNull();
  });

  /** `aria-disabled`, never `disabled` — a control that changes under the user throws focus to body. */
  it('never gives a button the disabled attribute', async () => {
    await open();

    for (const button of Array.from(element.querySelectorAll('button'))) {
      expect(button.hasAttribute('disabled')).toBe(false);
    }
  });

  /**
   * **Nothing on the screen names an example campaign, a percentage or a date.** A plausible-looking
   * sale anywhere here — in a default, in a placeholder that renders — would be a discount the client
   * never agreed to, which is what W3-03 shipped with a real bank's name.
   */
  it('invents no campaign, no discount and no window', async () => {
    await open([]);

    expect(element.querySelector<HTMLInputElement>('[data-testid="campaign-name"]')?.value).toBe('');
    expect(element.querySelector<HTMLInputElement>('[data-testid="campaign-value"]')?.value).toBe(
      '',
    );
    expect(element.querySelector<HTMLInputElement>('[data-testid="campaign-starts"]')?.value).toBe(
      '',
    );
    expect(element.querySelector<HTMLInputElement>('[data-testid="campaign-ends"]')?.value).toBe('');

    expect(element.querySelectorAll('input[placeholder]').length).toBe(0);
  });

  /** The screen says what it does not do, so a staff member is not left hunting for the option. */
  it('says that product targets and banners are not on this screen', async () => {
    await open([]);

    const note = element.querySelector('[data-testid="campaign-scope-note"]')?.textContent ?? '';

    expect(note).toContain('individual products');
    expect(note).toContain('banner');
  });

  /** The one heading structure check axe does not make: an h1 before any h2. */
  it('starts with the h1', async () => {
    await open();

    const headings = Array.from(element.querySelectorAll('h1, h2, h3')).map((node) =>
      node.tagName.toLowerCase(),
    );

    expect(headings[0]).toBe('h1');
    expect(headings.filter((tag) => tag === 'h1').length).toBe(1);
  });
});
