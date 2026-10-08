import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CouriersPage } from './couriers-page';
import { AdminOrderApi } from '../../core/orders/admin-order-api';
import { AdminCourier } from '../../core/orders/admin-order.model';

function courier(overrides: Partial<AdminCourier> = {}): AdminCourier {
  return {
    id: 4,
    name: 'A carrier',
    trackingUrlTemplate: 'https://track.example.test/{trackingNumber}',
    phone: '011 2345678',
    isActive: true,
    sortOrder: 2,
    ordersShipped: 17,
    ...overrides,
  };
}

/**
 * Couriers and their tracking addresses (ADM-09 / ADM-22, doc 11 P9's precondition).
 *
 * **The assertion this file exists for is a negative one: nothing on this screen names a courier.**
 * W3-03 shipped a real bank's name and the client's own legal name as placeholder text and both were
 * Major findings, so there is a test for the *absence* of an example as well as for the form.
 */
describe('CouriersPage (ADM-09)', () => {
  let fixture: ComponentFixture<CouriersPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload: readonly AdminCourier[] = [courier()]): Promise<void> {
    fixture = TestBed.createComponent(CouriersPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(AdminOrderApi.couriersUrl).flush(payload);

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

  it('shows a courier, its template and how many orders went with it', async () => {
    await open();

    const text = element.textContent ?? '';

    expect(text).toContain('A carrier');
    expect(text).toContain('https://track.example.test/{trackingNumber}');
    expect(text).toContain('011 2345678');

    // The figure that answers "is anything of ours out with them" before a staff member deactivates one.
    expect(text).toContain('17');
  });

  it('says in words that a courier with no online tracking shows the number as text', async () => {
    await open([courier({ trackingUrlTemplate: null })]);

    expect(element.textContent).toContain('shown as text');
  });

  it('says what an empty list means for shipping, rather than just “none”', async () => {
    await open([]);

    const empty = element.querySelector('[data-testid="couriers-empty"]')?.textContent ?? '';

    // The normal state of a new installation. The honest sentence says what it stops, because the order
    // detail is about to refuse a dispatch for exactly this reason.
    expect(empty).toContain('no delivery order can be marked as shipped');
  });

  it('shows no example courier and no example address anywhere on the screen', async () => {
    await open([]);

    const text = element.textContent ?? '';

    // The only example allowed is the placeholder itself — a shape, not a value. A sample like
    // `https://example.invalid/track?no={trackingNumber}` would be an invented carrier's address rendered
    // beside the box the real one is typed into, which is the criterion both of this project's Major
    // findings about invented client data turned on.
    expect(text).toContain('{trackingNumber}');

    // **No host, which is the part that would be invented.** The scheme on its own is allowed and is on
    // the screen deliberately: "must start with https://" is an instruction, not a value. What is refused
    // is anything with a dot-domain in it, which is what a person could mistake for a real carrier.
    expect(text).not.toMatch(/https?:\/\/[a-z0-9-]+(\.[a-z0-9-]+)+/i);

    // And nothing is pre-filled, including through a `placeholder` attribute, which renders.
    for (const testId of ['courier-name', 'courier-template', 'courier-phone']) {
      const input = element.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;

      expect(input.value).toBe('');
      expect(input.getAttribute('placeholder')).toBeNull();
    }
  });

  it('sends every field, including the blank ones', async () => {
    await open([]);

    type('courier-name', '  A new carrier  ');
    click('courier-save');
    await settle();

    answerAntiforgery();
    await settle();

    const request = http.expectOne(AdminOrderApi.couriersUrl);

    // **Every key is present.** The endpoint refuses a body that leaves one out rather than reading its
    // absence as null, because a save that did not mention a field has silently cleared it four times in
    // this project (W1-12, W1-03, GLB-10). Sending them all is the client's half of that guarantee.
    expect(request.request.body).toEqual({
      name: 'A new carrier',
      trackingUrlTemplate: null,
      phone: null,
      isActive: true,
      sortOrder: 0,
    });

    request.flush(courier({ name: 'A new carrier' }));
    await settle();

    http.expectOne(AdminOrderApi.couriersUrl).flush([courier({ name: 'A new carrier' })]);
    await settle();
  });

  it('puts an existing courier in the form and saves it by its id', async () => {
    await open();

    click('edit-4');
    await settle();

    expect(
      element.querySelector<HTMLInputElement>('[data-testid="courier-name"]')!.value,
    ).toBe('A carrier');
    expect(
      element.querySelector<HTMLInputElement>('[data-testid="courier-template"]')!.value,
    ).toBe('https://track.example.test/{trackingNumber}');

    // Deactivating, which is the only way a courier leaves the picker: there is no delete, because
    // deleting the row would turn every live parcel's tracking link into plain text.
    element.querySelector<HTMLInputElement>('[data-testid="courier-active"]')!.click();
    click('courier-save');
    await settle();

    answerAntiforgery();
    await settle();

    const request = http.expectOne(`${AdminOrderApi.couriersUrl}/4`);

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toMatchObject({ name: 'A carrier', isActive: false });

    request.flush(courier({ isActive: false }));
    await settle();

    http.expectOne(AdminOrderApi.couriersUrl).flush([courier({ isActive: false })]);
    await settle();
  });

  it('keeps the server’s per-field message rather than one banner', async () => {
    await open([]);

    type('courier-name', 'A carrier');
    type('courier-template', 'https://example.test/track');
    click('courier-save');
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(AdminOrderApi.couriersUrl).flush(
      {
        title: 'One or more fields are invalid.',
        errors: {
          trackingUrlTemplate: ['The tracking address must contain {trackingNumber} where the number goes.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    await settle();

    // The field, not a banner: the field is the only thing that says where to look.
    expect(
      element.querySelector('[data-testid="error-template"]')?.textContent,
    ).toContain('{trackingNumber}');
    expect(
      element.querySelector<HTMLInputElement>('[data-testid="courier-template"]')!
        .getAttribute('aria-invalid'),
    ).toBe('true');
  });

  it('refuses the save in the handler rather than disabling the button', async () => {
    await open([]);

    const save = element.querySelector<HTMLElement>('[data-testid="courier-save"]')!;

    // The name is empty, so the press is refused — and `[disabled]` is never used, because `[disabled]`
    // on a control whose state changes under the user throws focus to `<body>` (#109, six instances).
    expect(save.hasAttribute('disabled')).toBe(false);

    save.click();
    await settle();

    http.expectNone(() => true);
    expect(element.querySelector('[data-testid="error-name"]')?.textContent).toContain('name');
  });

  /**
   * **ADM-22's defect, on the one path in this screen that reaches it.**
   *
   * `role="status"` announces a *change*, so setting the region to the text it already holds is silence —
   * on the second press, which is the one where the staff member is already confused. Pressing Save twice
   * with an empty name produces the same sentence both times, so the region has to be emptied and the
   * sentence put back.
   */
  it('re-announces the same sentence, so a repeated press is not met with silence', async () => {
    await open([]);

    const said = () => element.querySelector('[data-testid="couriers-said"]')?.textContent ?? '';

    click('courier-save');
    await settle();

    const first = said();
    expect(first).toContain('name');

    // **The mutation is watched rather than sampled, and the thing watched for is that there is one at
    // all.** Angular flushes `afterNextRender` inside the same change-detection pass, so reading the DOM
    // afterwards always finds the sentence back — a test that only compared before and after would pass
    // against a component that never cleared the region, which is exactly the test-that-cannot-fail shape.
    //
    // Without the clear-and-restore, setting a signal to the value it already holds produces **no DOM
    // mutation whatsoever**, so the observer records nothing: and no mutation is precisely the silence a
    // screen reader gives the staff member on their second press. `characterDataOldValue` then shows the
    // region passing through blank, which is what makes the restored sentence a change.
    const region = element.querySelector('[data-testid="couriers-said"]')!;
    const oldValues: (string | null)[] = [];
    const observer = new MutationObserver((records) => {
      for (const record of records) {
        oldValues.push(record.oldValue);
      }
    });
    observer.observe(region, { childList: true, characterData: true, characterDataOldValue: true, subtree: true });

    try {
      click('courier-save');
      await settle();
    } finally {
      observer.disconnect();
    }

    expect(oldValues.length).toBeGreaterThan(0);
    expect(oldValues).toContain('');
    expect(said()).toBe(first);

    http.expectNone(() => true);
  });

  it('shows the server’s sentence when the list cannot be read', async () => {
    fixture = TestBed.createComponent(CouriersPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http
      .expectOne(AdminOrderApi.couriersUrl)
      .flush({ detail: 'Nope.' }, { status: 403, statusText: 'Forbidden' });

    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('permission');
  });
});
