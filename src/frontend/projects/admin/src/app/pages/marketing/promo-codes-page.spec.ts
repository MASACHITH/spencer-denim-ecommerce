import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PromoCodesPage } from './promo-codes-page';
import { AdminPromoApi } from '../../core/marketing/admin-promo-api';
import { AdminPromoCode, AdminPromoCodeSaved } from '../../core/marketing/admin-promo.model';

function promoCode(overrides: Partial<AdminPromoCode> = {}): AdminPromoCode {
  return {
    id: 7,
    code: 'T4FIXTURE',
    description: 'A fixture, not an offer.',
    discountType: 'Percent',
    discountValue: 10,
    maxDiscountAmount: 2000,
    minSpendAmount: 5000,
    startsAt: '2026-10-01T00:00:00',
    endsAt: '2026-10-31T23:59:00',
    excludeSaleItems: false,
    totalUsageLimit: 50,
    perCustomerLimit: 1,
    usageCount: 3,
    isActive: true,
    status: 'Live',
    rowVersion: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

function saved(overrides: Partial<AdminPromoCodeSaved> = {}): AdminPromoCodeSaved {
  return { promoCode: promoCode(), warnings: [], ...overrides };
}

/**
 * Promo codes (ADM-13, doc 11 P3 part B).
 *
 * **Two of these are negative assertions and they are the ones this file exists for.** Nothing on the
 * screen names a plausible promo code — doc 02 line 167 replaces the demo's TUASL10 and FREESHIP with
 * admin-managed ones, and W3-03 shipped a real bank's name as placeholder text and was pulled up for it —
 * and no save may leave a field out of the body, because the field that would be dropped is a usage limit.
 */
describe('PromoCodesPage (ADM-13)', () => {
  let fixture: ComponentFixture<PromoCodesPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload: readonly AdminPromoCode[] = [promoCode()]): Promise<void> {
    fixture = TestBed.createComponent(PromoCodesPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(AdminPromoApi.promoCodesUrl).flush(payload);

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

  it('shows a code, what it is worth and how much of it is used', async () => {
    await open();

    const text = element.textContent ?? '';

    expect(text).toContain('T4FIXTURE');
    expect(text).toContain('10% off, up to Rs. 2,000');

    // The figure doc 11 §6.4 item 17 asks a staff member to check weekly ("check upcoming campaign
    // dates and code limits"), and the per-customer limit beside it.
    expect(text).toContain('3 of 50 used');
    expect(text).toContain('1 per customer');
  });

  it('says what a free-delivery code is worth without inventing an amount', async () => {
    await open([
      promoCode({ discountType: 'FreeDelivery', discountValue: null, maxDiscountAmount: null }),
    ]);

    expect(element.textContent).toContain('Free delivery');
  });

  /**
   * Doc 11 P3's derived statuses. A code that is not Live says so on the row, because a staff member
   * looking for "why is my code not working" is looking at this list.
   */
  it('labels a code that is not live', async () => {
    await open([promoCode({ status: 'Inactive' })]);
    expect(element.textContent).toContain('Switched off');

    await open([promoCode({ status: 'Ended' })]);
    expect(element.textContent).toContain('Ended');

    await open([promoCode({ status: 'Scheduled' })]);
    expect(element.textContent).toContain('Starts later');
  });

  it('says what an empty list means rather than showing an empty page', async () => {
    await open([]);

    expect(element.querySelector('[data-testid="promo-empty"]')?.textContent).toContain(
      'nothing a shopper types at checkout will work',
    );
  });

  /**
   * **No example code anywhere.** Not as a value, not as a `placeholder`, not in the hint copy. The two
   * strings checked for are the ones doc 02 line 167 names, and the assertion is on the rendered DOM
   * rather than on the source so that a placeholder attribute counts.
   */
  it('names no example promo code', async () => {
    await open([]);

    const html = element.innerHTML;

    expect(html).not.toContain('TUASL10');
    expect(html).not.toContain('FREESHIP');
    expect(html).not.toContain('WELCOME');

    for (const input of element.querySelectorAll('input')) {
      expect(input.getAttribute('placeholder')).toBeNull();
    }
  });

  /**
   * **Every field, every time, and every one of them filled in.**
   *
   * The endpoint refuses a body that leaves a field out, and the one that would be dropped here is a
   * usage limit: a save that silently cleared `totalUsageLimit` would turn a fifty-use code into an
   * unlimited one.
   *
   * **Every box is given a distinct value on purpose.** An earlier version of this test filled in only
   * the three required fields, so every optional one was `null` in the expected body — and replacing any
   * of them with a hard-coded `null` in the component passed. Measured: with
   * `totalUsageLimit: toNumberOrNull(...)` replaced by `totalUsageLimit: null`, that version stayed
   * green. A test whose expected values are all the same value cannot tell a field that travels from one
   * that does not.
   */
  it('sends every field, including the blank ones, with the value typed into each', async () => {
    await open([]);

    type('promo-code', 'spring-26');
    type('promo-value', '15');
    type('promo-cap', '1500');
    type('promo-min-spend', '4000');
    type('promo-starts', '2026-10-13T00:00');
    type('promo-ends', '2026-10-20T12:30');
    type('promo-total-limit', '50');
    type('promo-per-customer', '2');
    type('promo-description', 'Spring mailshot.');
    click('promo-exclude-sale');
    click('promo-active');
    click('promo-save');

    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminPromoApi.promoCodesUrl && candidate.method === 'POST',
    );

    expect(request.request.body).toEqual({
      code: 'spring-26',
      description: 'Spring mailshot.',
      discountType: 'Percent',
      discountValue: 15,
      maxDiscountAmount: 1500,
      minSpendAmount: 4000,
      startsAt: '2026-10-13T00:00',
      endsAt: '2026-10-20T12:30',

      // Ticked, because the checkbox starts unticked and was clicked once — doc 11 P3 step 7's "exclude
      // sale items", which `PromoEvaluator.IsEligible` honours and which no staff member could set
      // before W3-05c.
      excludeSaleItems: true,
      totalUsageLimit: 50,
      perCustomerLimit: 2,

      // Unticked, because the checkbox started ticked and was clicked once. A code a staff member has not
      // switched on is not live, and the flag has to travel for that to be sayable.
      isActive: false,
    });

    request.flush(saved());
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();
  });

  /** A blank optional box is `null`, which is how a staff member says "no limit". */
  it('sends null for every box left blank', async () => {
    await open([]);

    type('promo-code', 'T4TEST');
    type('promo-value', '10');
    type('promo-starts', '2026-10-13T00:00');
    click('promo-save');

    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminPromoApi.promoCodesUrl && candidate.method === 'POST',
    );

    expect(request.request.body).toEqual({
      code: 'T4TEST',
      description: null,
      discountType: 'Percent',
      discountValue: 10,
      maxDiscountAmount: null,
      minSpendAmount: null,
      startsAt: '2026-10-13T00:00',
      endsAt: null,

      // False, not null and not absent: the column is a `bit NOT NULL` and the endpoint refuses a body
      // that leaves the key out. "Nothing is excluded" is a value, not a silence.
      excludeSaleItems: false,
      totalUsageLimit: null,
      perCustomerLimit: null,
      isActive: true,
    });

    request.flush(saved());
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();
  });

  /**
   * **The label says which items, and that is the whole test.**
   *
   * Doc 11 §7 G-07 and doc 11 line 464 separate two things a shopper would both call "on sale": a line a
   * sale campaign is pricing, which this flag removes, and a line that only carries a markdown, which it
   * keeps. A checkbox reading "Exclude sale items" would be read as covering both, and a staff member who
   * ticked it believing it skipped marked-down stock would have set a rule they did not mean.
   */
  it('says which items the exclusion skips, and which it does not', async () => {
    await open([]);

    const label = element.querySelector<HTMLElement>('[data-testid="promo-exclude-sale-field"]');
    const hint = element.querySelector<HTMLElement>('#promo-exclude-sale-hint');

    expect(label?.textContent).toContain('sale campaign');
    expect(hint?.textContent).toContain('marked down');
    expect(hint?.textContent).toContain('still counts');

    // The hint is attached to the control, not merely near it.
    expect(
      element
        .querySelector<HTMLInputElement>('[data-testid="promo-exclude-sale"]')
        ?.getAttribute('aria-describedby'),
    ).toBe('promo-exclude-sale-hint');
  });

  /**
   * Editing a code loads the flag from the row.
   *
   * Without this, a staff member editing a code that excludes campaign-priced lines would save it back
   * with the exclusion dropped — the form posts every field, so the box's state *is* the saved value.
   */
  it('loads the exclusion from the row being edited and sends it back unchanged', async () => {
    await open([promoCode({ excludeSaleItems: true })]);

    click('edit-7');
    await settle();

    expect(
      element.querySelector<HTMLInputElement>('[data-testid="promo-exclude-sale"]')?.checked,
    ).toBe(true);

    click('promo-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url.endsWith('/7') && candidate.method === 'PUT',
    );

    expect((request.request.body as { excludeSaleItems: boolean }).excludeSaleItems).toBe(true);

    request.flush(saved());
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();
  });

  /** The list says so, in the same words as the form, and only when it is on. */
  it('says on the row when a code skips campaign-priced lines', async () => {
    await open([promoCode({ excludeSaleItems: true })]);

    expect(element.querySelector('[data-testid="excludes-sale-7"]')?.textContent).toContain(
      'sale campaign',
    );

    await open([promoCode({ excludeSaleItems: false })]);

    expect(element.querySelector('[data-testid="excludes-sale-7"]')).toBeNull();
  });

  /**
   * **The date goes out with no offset and no `Z`.** The API's contract is the shop's own wall-clock time
   * and it refuses anything carrying an offset, because the server would otherwise have to guess whether
   * the caller meant Colombo — and guessing wrong moves a code's window by five and a half hours, which
   * is this project's existing UTC-versus-Colombo defect class.
   */
  it('sends the start as the shop own time, with no offset', async () => {
    await open([]);

    type('promo-code', 'T4TEST');
    type('promo-value', '10');
    type('promo-starts', '2026-10-13T23:59');
    click('promo-save');

    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminPromoApi.promoCodesUrl && candidate.method === 'POST',
    );

    const body = request.request.body as { startsAt: string };

    expect(body.startsAt).toBe('2026-10-13T23:59');
    expect(body.startsAt).not.toContain('Z');
    expect(body.startsAt).not.toContain('+');

    request.flush(saved());
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();
  });

  /**
   * A free-delivery code has no amount, and the server refuses a value sent with one — so the box is not
   * rendered and the body carries null.
   */
  it('sends no amount for a free-delivery code and does not show the box', async () => {
    await open([]);

    choose('promo-type', 'FreeDelivery');
    await settle();

    expect(element.querySelector('[data-testid="promo-value"]')).toBeNull();
    expect(element.querySelector('[data-testid="promo-cap"]')).toBeNull();

    type('promo-code', 'T4SHIP');
    type('promo-starts', '2026-10-13T00:00');
    click('promo-save');

    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) => candidate.url === AdminPromoApi.promoCodesUrl && candidate.method === 'POST',
    );

    expect((request.request.body as { discountValue: number | null }).discountValue).toBeNull();

    request.flush(saved());
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();
  });

  /**
   * **Changing the type clears the figures that belonged to the old one.** A percentage of 15 left in the
   * box and submitted as a fixed amount is Rs. 15 off, which the server would accept — so the figure has
   * to go when its meaning changes.
   */
  it('clears the amount when the type changes', async () => {
    await open([]);

    type('promo-value', '15');

    // A render between the two, because the `[value]` binding only writes when the bound value changes
    // from what the binding last wrote — without it the box would still hold what this test set by hand
    // and the assertion would be about Angular rather than about the handler.
    await settle();

    choose('promo-type', 'FixedAmount');
    await settle();

    expect(element.querySelector<HTMLInputElement>('[data-testid="promo-value"]')!.value).toBe('');
  });

  it('keeps the server per-field message rather than one banner', async () => {
    await open([]);

    type('promo-code', 'T4TEST');
    type('promo-value', '0');
    type('promo-starts', '2026-10-13T00:00');
    click('promo-save');

    answerAntiforgery();
    await settle();

    http
      .expectOne(
        (candidate) => candidate.url === AdminPromoApi.promoCodesUrl && candidate.method === 'POST',
      )
      .flush(
        {
          title: 'One or more fields need correcting.',
          status: 400,
          errors: { discountValue: ['Enter a percentage above 0 and up to 100.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );

    await settle();

    expect(element.querySelector('[data-testid="error-value"]')?.textContent).toContain(
      'above 0 and up to 100',
    );
    expect(
      element.querySelector('[data-testid="promo-value"]')?.getAttribute('aria-invalid'),
    ).toBe('true');
  });

  /**
   * Doc 08 §3.6 DB19: lowering the total limit below the uses already held is allowed **with a warning**.
   * The save worked, and the staff member still has to be told the code is now blocked for new
   * customers — a silent save there is the shape of defect this project has shipped three times.
   */
  it('reads out a warning about a save that succeeded', async () => {
    await open([]);

    type('promo-code', 'T4TEST');
    type('promo-value', '10');
    type('promo-starts', '2026-10-13T00:00');
    click('promo-save');

    answerAntiforgery();
    await settle();

    http
      .expectOne(
        (candidate) => candidate.url === AdminPromoApi.promoCodesUrl && candidate.method === 'POST',
      )
      .flush(
        saved({
          warnings: ['This code has already been used 9 times, which is above the new total limit of 5.'],
        }),
      );

    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();

    const said = element.querySelector('[data-testid="promo-said"]')?.textContent ?? '';

    expect(said).toContain('is saved');
    expect(said).toContain('above the new total limit of 5');
  });

  /** #109: the guard is in the handler and the button is never given the `disabled` attribute. */
  it('refuses the save in the handler rather than disabling the button', async () => {
    await open([]);

    const save = element.querySelector<HTMLButtonElement>('[data-testid="promo-save"]')!;

    expect(save.hasAttribute('disabled')).toBe(false);

    // An empty code is refused before the request, which is the one rule this screen owns.
    click('promo-save');
    await settle();

    http.expectNone(() => true);

    expect(element.querySelector('[data-testid="error-code"]')?.textContent).toContain(
      'Enter the code',
    );
  });

  /** An edit sends the row version in `If-Match`, so a save from a stale list is refused. */
  it('sends the row version as an ETag when editing', async () => {
    await open();

    click('edit-7');
    await settle();

    expect(element.querySelector<HTMLInputElement>('[data-testid="promo-code"]')!.value).toBe(
      'T4FIXTURE',
    );

    // The seconds the input does not render are trimmed, so a save that changes nothing posts back what
    // it was given.
    expect(element.querySelector<HTMLInputElement>('[data-testid="promo-starts"]')!.value).toBe(
      '2026-10-01T00:00',
    );

    click('promo-save');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) =>
        candidate.url === `${AdminPromoApi.promoCodesUrl}/7` && candidate.method === 'PUT',
    );

    expect(request.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');

    request.flush(saved());
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([promoCode()]);
    await settle();
  });

  /**
   * **Remove takes two presses, and the confirmation replaces the label on the same button.** A dialog
   * or a second button would move focus, which is #109 arriving by another route; this way focus stays
   * where the staff member put it.
   */
  it('asks once before removing, and removes on the second press', async () => {
    await open();

    click('remove-7');
    await settle();

    expect(element.querySelector('[data-testid="remove-7"]')?.textContent).toContain(
      'Yes, remove it',
    );
    http.expectNone(() => true);

    click('remove-7');
    answerAntiforgery();
    await settle();

    const request = http.expectOne(
      (candidate) =>
        candidate.url === `${AdminPromoApi.promoCodesUrl}/7` && candidate.method === 'DELETE',
    );

    expect(request.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');

    request.flush(null);
    http.expectOne(AdminPromoApi.promoCodesUrl).flush([]);
    await settle();

    const said = element.querySelector('[data-testid="promo-said"]')?.textContent ?? '';

    expect(said).toContain('is removed');

    // The sentence says the string comes back, because that is the thing a staff member would otherwise
    // have to discover: UX_PromoCodes_Code is filtered on IsDeleted = 0.
    expect(said).toContain('can be used again');
  });

  it('keeps the code when the confirmation is declined', async () => {
    await open();

    click('remove-7');
    await settle();

    click('remove-cancel-7');
    await settle();

    expect(element.querySelector('[data-testid="remove-7"]')?.textContent).toContain('Remove');
    expect(element.querySelector('[data-testid="promo-said"]')?.textContent).toContain(
      'Nothing was removed',
    );

    http.expectNone(() => true);
  });

  /**
   * `role="status"` announces a *change*, so a repeated identical sentence has to be written again
   * (ADM-22). Asserted with a real `MutationObserver` reading `oldValue`, because the callback is a
   * microtask and the target already holds the final text — reading the target would be a test that
   * could not fail.
   */
  it('re-announces the same refusal, so a repeated press is not met with silence', async () => {
    await open([]);

    click('promo-save');
    await settle();

    const said = element.querySelector('[data-testid="promo-said"]')!;
    const oldValues: (string | null)[] = [];
    const observer = new MutationObserver((records) => {
      for (const record of records) {
        oldValues.push(record.oldValue);
      }
    });

    observer.observe(said, {
      childList: true,
      characterData: true,
      characterDataOldValue: true,
      subtree: true,
    });

    click('promo-save');
    await settle();
    await settle();

    observer.disconnect();

    expect(oldValues.some((value) => (value ?? '').trim() === '')).toBe(true);
    expect(said.textContent).toContain('Enter the code');

    http.expectNone(() => true);
  });

  it('shows the server sentence when the list cannot be read', async () => {
    fixture = TestBed.createComponent(PromoCodesPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http
      .expectOne(AdminPromoApi.promoCodesUrl)
      .flush({ title: 'Forbidden', status: 403 }, { status: 403, statusText: 'Forbidden' });

    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('permission');
  });
});
