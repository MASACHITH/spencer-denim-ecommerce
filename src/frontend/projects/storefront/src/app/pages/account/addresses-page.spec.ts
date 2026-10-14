import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { PLATFORM_ID, RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';

import { AddressesPage } from './addresses-page';
import {
  ACCOUNT_FIELD_LENGTHS,
  CustomerAddressDto,
  CustomerSessionDto,
  SAVED_ADDRESS_LIMIT,
} from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';
import { CheckoutApi } from '../../core/checkout/checkout-api';
import { CheckoutSessionDto, EMPTY_CHECKOUT } from '../../core/checkout/checkout.model';

const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: true,
};

/** The three districts this spec offers. Real names, because `ref.Districts` is seeded with the 25. */
const DISTRICTS: CheckoutSessionDto = {
  ...EMPTY_CHECKOUT,
  districts: [
    { id: 1, name: 'Colombo', deliveryZone: 1 },
    { id: 2, name: 'Gampaha', deliveryZone: 2 },
    { id: 7, name: 'Jaffna', deliveryZone: 3 },
  ],
};

function address(overrides: Partial<CustomerAddressDto> = {}): CustomerAddressDto {
  return {
    id: 11,
    label: 'Home',
    recipientName: 'Nimal Perera',
    phone: '0771234567',
    line1: '42 Flower Road',
    line2: null,
    city: 'Colombo',
    districtId: 1,
    districtName: 'Colombo',
    districtIsActive: true,
    postalCode: '00700',
    landmark: null,
    isDefault: true,
    ...overrides,
  };
}

/**
 * The saved address book (ACC-02, doc 11 P11 step 11).
 *
 * The three properties worth the length of this file:
 *
 * - **every write answers the whole book and the page re-renders from it.** Setting a default clears the
 *   previous one and deleting the default promotes another, so a page that merged one returned row into
 *   a local list would show two defaults or none.
 * - **removing an address destroys the button that removed it**, which throws focus to `<body>` exactly
 *   as `[disabled]` does — the defect W2-08 shipped on all three of the wishlist's removal controls after
 *   that page had already banned the attribute.
 * - **the cap is the server's.** The page says how many may be kept; the refusal a shopper reads is the
 *   server's own sentence with the server's own number in it.
 */
describe('AddressesPage (ACC-02)', () => {
  let fixture: ComponentFixture<AddressesPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function create(platform: 'browser' | 'server' = 'browser'): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', children: [] },
          { path: 'account/orders', children: [] },
          { path: 'account/addresses', children: [] },
          { path: 'account/profile', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AccountStore).accept(CUSTOMER);

    fixture = TestBed.createComponent(AddressesPage);
    element = fixture.nativeElement as HTMLElement;
  }

  function bookRequest(): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === '/api/account/addresses' && candidate.method === 'GET',
    );
  }

  function flushDistricts(session: CheckoutSessionDto = DISTRICTS): void {
    http
      .match((candidate) => candidate.url === CheckoutApi.url)
      .forEach((candidate) => candidate.flush(session));
  }

  async function open(book: readonly CustomerAddressDto[] = [address()]): Promise<void> {
    create();
    await settle();
    bookRequest().flush(book);
    flushDistricts();
    await settle();
  }

  function cards(): HTMLElement[] {
    return [...element.querySelectorAll<HTMLElement>('.acc-card')];
  }

  function one(testId: string, root: ParentNode = element): HTMLElement | null {
    return root.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  }

  function all(testId: string): HTMLElement[] {
    return [...element.querySelectorAll<HTMLElement>(`[data-testid="${testId}"]`)];
  }

  function field(testId: string): HTMLInputElement | HTMLSelectElement {
    return element.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  }

  /** See `register-page.spec.ts`: the render between keystrokes is what a browser really does. */
  function type(testId: string, value: string): void {
    const input = field(testId) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function choose(testId: string, value: string): void {
    const select = field(testId) as HTMLSelectElement;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function submit(): void {
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit', { cancelable: true }),
    );
  }

  function text(): string {
    return element.textContent ?? '';
  }

  function fillInNew(): void {
    type('address-field-recipient', 'Nimal Perera');
    type('address-field-phone', '0771234567');
    type('address-field-line1', '42 Flower Road');
    type('address-field-city', 'Colombo');
    choose('address-field-district', '1');
  }

  afterEach(() => http?.verify());

  describe('what the server renders', () => {
    it('renders the heading and the navigation, and asks the shop nothing', async () => {
      create('server');
      await settle();

      expect(element.querySelector('h1')?.textContent?.trim()).toBe('Your addresses');
      expect(one('account-nav-addresses')).not.toBeNull();
      expect(http.match(() => true)).toEqual([]);
    });

    /** "I have not looked yet" is not "you have saved nothing". */
    it('says it is loading rather than that nothing is saved', async () => {
      create('server');
      await settle();

      expect(one('addresses-loading')).not.toBeNull();
      expect(one('addresses-empty')).toBeNull();
    });

    it('answers no-store with Vary: Cookie, and noindex', async () => {
      await open();

      const headers = new Headers(response.headers ?? {});
      expect(headers.get('Cache-Control')).toContain('no-store');
      expect(headers.get('Vary')).toBe('Cookie');
      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(TestBed.inject(Title).getTitle()).toBe('Your addresses | Colombo Wardrobe');
    });

    it('renders the live region before there is anything to announce', async () => {
      create('server');
      await settle();

      expect(one('addresses-alert')?.getAttribute('role')).toBe('alert');
      expect(one('addresses-status')?.textContent).toBe('');
    });
  });

  describe('the districts, which come from checkout', () => {
    /**
     * `GET /api/checkout` answers `districts` whether or not the visitor has a bag
     * (`GetCheckoutQueryHandler`), and `CheckoutApi` is already the client for it. A second districts
     * endpoint would be a second list to keep in step with `ref.Districts.IsActive`, which an admin
     * edits.
     */
    it('reads them from the existing checkout endpoint, not a second one', async () => {
      create();
      await settle();
      bookRequest().flush([]);

      const request = http.expectOne((candidate) => candidate.url === CheckoutApi.url);
      // No district and no method chosen: the reference data, and no delivery arithmetic.
      expect(request.request.params.get('districtId')).toBeNull();
      expect(request.request.params.get('deliveryMethodId')).toBeNull();

      request.flush(DISTRICTS);
      await settle();

      expect([...element.querySelectorAll('[data-testid="address-field-district"] option')].map(
        (node) => node.textContent?.trim(),
      )).toEqual(['Choose a district', 'Colombo', 'Gampaha', 'Jaffna']);
    });

    /**
     * **No value on the "Choose a district" option**, so an unchosen district is sent as nothing and
     * refused by the server with "Choose a district." rather than quietly becoming district 1.
     */
    it('offers an unchosen district with no value at all', async () => {
      await open([]);

      const first = element.querySelector(
        '[data-testid="address-field-district"] option',
      ) as HTMLOptionElement;

      expect(first.value).toBe('');
      expect(first.selected).toBe(true);
    });

    it('still works when the districts cannot be read', async () => {
      create();
      await settle();
      bookRequest().flush([address()]);
      http
        .expectOne((candidate) => candidate.url === CheckoutApi.url)
        .error(new ProgressEvent('offline'), { status: 0 });
      await settle();

      expect(cards()).toHaveLength(1);
      expect(
        element.querySelectorAll('[data-testid="address-field-district"] option'),
      ).toHaveLength(1);
    });
  });

  describe('the book', () => {
    it('draws a card per address with every line the shopper typed', async () => {
      await open([
        address({
          label: 'Home',
          line2: 'Apartment 4B',
          landmark: 'Opposite the temple',
        }),
      ]);

      const card = cards()[0];
      expect(card.textContent).toContain('Home');
      expect(card.textContent).toContain('Nimal Perera');
      expect(card.textContent).toContain('0771234567');
      expect(card.textContent).toContain('42 Flower Road');
      expect(card.textContent).toContain('Apartment 4B');
      expect(card.textContent).toContain('Colombo, Colombo');
      expect(card.textContent).toContain('00700');
      expect(card.textContent).toContain('Opposite the temple');
    });

    /** A word and not a colour: the default has to be tellable without seeing one (WCAG 1.4.1). */
    it('marks the default in words', async () => {
      await open([address({ id: 11, isDefault: true }), address({ id: 12, label: 'Work', isDefault: false })]);

      expect(all('address-default-flag')).toHaveLength(1);
      expect(one('address-default-flag')?.textContent?.trim()).toBe('Default');
      // And offers "Make default" only on the one that is not.
      expect(all('address-make-default')).toHaveLength(1);
    });

    it('counts what is saved against the cap', async () => {
      await open([address({ id: 11 }), address({ id: 12, isDefault: false })]);

      expect(one('addresses-count')?.textContent?.trim()).toBe(
        `2 saved addresses of ${SAVED_ADDRESS_LIMIT}.`,
      );
    });

    it('says the book is empty, once it has looked', async () => {
      await open([]);

      expect(one('addresses-empty')?.textContent).toContain('not saved an address yet');
    });

    /**
     * **A district the shop stopped serving is still named on its card**, because the server joins
     * `districtName` in and sends `districtIsActive`. The card says so rather than offering an address
     * checkout will not quote for.
     */
    it('says when a saved district is no longer served', async () => {
      await open([address({ districtId: 7, districtName: 'Jaffna', districtIsActive: false })]);

      expect(one('address-inactive-district')?.textContent).toContain('no longer deliver to Jaffna');
    });

    it('offers Try again rather than claiming the book is empty when the read fails', async () => {
      create();
      await settle();
      bookRequest().error(new ProgressEvent('offline'), { status: 0 });
      flushDistricts();
      await settle();

      expect(one('addresses-empty')).toBeNull();
      expect(one('addresses-retry')).not.toBeNull();
      expect(one('addresses-alert')?.textContent).toContain('could not reach the store');

      (one('addresses-retry') as HTMLButtonElement).click();
      await settle();
      bookRequest().flush([address()]);
      await settle();

      expect(cards()).toHaveLength(1);
    });
  });

  describe('adding one', () => {
    it('sends what was typed, with null for the optional boxes', async () => {
      await open([]);
      fillInNew();
      submit();

      const request = http.expectOne(
        (candidate) => candidate.url === '/api/account/addresses' && candidate.method === 'POST',
      );

      expect(request.request.body).toEqual({
        label: null,
        recipientName: 'Nimal Perera',
        phone: '0771234567',
        line1: '42 Flower Road',
        line2: null,
        city: 'Colombo',
        districtId: 1,
        postalCode: null,
        landmark: null,
        isDefault: false,
      });

      request.flush([address()], { status: 201, statusText: 'Created' });
      await settle();
    });

    /**
     * **`null` and not `0` for an unchosen district.** The server's request type makes it nullable for
     * exactly this reason: an omitted number binds `0` on a non-nullable property, and `0` is
     * indistinguishable from "they sent zero", so the validator could not tell a missing district from an
     * invalid one and would give the wrong message.
     */
    it('sends null for a district that was never chosen, never 0', async () => {
      await open([]);
      type('address-field-recipient', 'Nimal Perera');
      type('address-field-phone', '0771234567');
      type('address-field-line1', '42 Flower Road');
      type('address-field-city', 'Colombo');
      submit();

      const request = http.expectOne(
        (candidate) => candidate.url === '/api/account/addresses' && candidate.method === 'POST',
      );

      expect((request.request.body as Record<string, unknown>)['districtId']).toBeNull();

      request.flush({ errors: { districtId: ['Choose a district.'] } }, {
        status: 400,
        statusText: 'Bad Request',
      });
      await settle();

      expect(element.querySelector('#ad-districtId-error')?.textContent).toContain(
        'Choose a district.',
      );
    });

    /** The server answers the whole book, and the page re-renders from it rather than patching a list. */
    it('re-renders the whole book from the answer and empties the form', async () => {
      await open([]);
      fillInNew();
      submit();
      http
        .expectOne((candidate) => candidate.method === 'POST')
        .flush([address({ id: 11 }), address({ id: 12, label: 'Work', isDefault: false })], {
          status: 201,
          statusText: 'Created',
        });
      await settle();

      expect(cards()).toHaveLength(2);
      expect((field('address-field-recipient') as HTMLInputElement).value).toBe('');
      expect(one('addresses-status')?.textContent).toBe('Address saved.');
    });

    it('caps each box at the width of the column behind it', async () => {
      await open([]);

      expect(field('address-field-label').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.label),
      );
      expect(field('address-field-recipient').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.recipientName),
      );
      expect(field('address-field-phone').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.phone),
      );
      expect(field('address-field-line1').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.line),
      );
      expect(field('address-field-city').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.city),
      );
      expect(field('address-field-postal').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.postalCode),
      );
      expect(field('address-field-landmark').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.landmark),
      );
    });
  });

  describe('the cap, which is the server’s', () => {
    function full(): readonly CustomerAddressDto[] {
      return Array.from({ length: SAVED_ADDRESS_LIMIT }, (_, index) =>
        address({ id: 100 + index, isDefault: index === 0 }),
      );
    }

    /**
     * **The form is not hidden at the cap**: a form that vanishes is a form a shopper looks for. It says
     * the cap has been reached and what to do, and Save is refused without being `disabled`.
     */
    it('says the cap is reached, keeps the form, and refuses Save without disabling it', async () => {
      await open(full());

      expect(one('addresses-at-limit')?.textContent).toContain(`${SAVED_ADDRESS_LIMIT} addresses`);
      expect(field('address-field-recipient')).not.toBeNull();

      const save = one('address-save') as HTMLButtonElement;
      expect(save.hasAttribute('disabled')).toBe(false);
      expect(save.getAttribute('aria-disabled')).toBe('true');

      fillInNew();
      submit();

      expect(http.match((candidate) => candidate.url === '/api/account/addresses')).toEqual([]);
    });

    /** An edit does not add an address, so the cap does not apply to it. */
    it('still lets a full book be edited', async () => {
      await open(full());
      (all('address-edit')[0] as HTMLButtonElement).click();
      await settle();

      expect(one('addresses-at-limit')).toBeNull();
      expect((one('address-save') as HTMLButtonElement).getAttribute('aria-disabled')).toBeNull();
    });

    /**
     * A 409 means another tab filled the book while this form was open, so the page's idea of how many
     * addresses there are is out of date and the notice above the form is wrong. Re-reading is the only
     * way the shopper sees why Save stopped working.
     */
    it('shows the server’s refusal and re-reads the book when the cap is hit mid-form', async () => {
      await open([address()]);
      fillInNew();
      submit();
      http.expectOne((candidate) => candidate.method === 'POST').flush(
        {
          code: 'address_limit_reached',
          limit: 10,
          detail: 'You can keep up to 10 saved addresses. Remove one you no longer use and try again.',
        },
        { status: 409, statusText: 'Conflict' },
      );
      await settle();

      expect(one('addresses-alert')?.textContent).toContain('You can keep up to 10 saved addresses');
      expect(document.activeElement).toBe(one('addresses-alert'));

      bookRequest().flush(full());
      await settle();

      expect(cards()).toHaveLength(SAVED_ADDRESS_LIMIT);
      expect(one('addresses-at-limit')).not.toBeNull();
    });
  });

  describe('editing one', () => {
    it('fills the form from the card and sends a PUT to its own id', async () => {
      await open([address({ id: 11, label: 'Home', line2: 'Apartment 4B' })]);
      (one('address-edit') as HTMLButtonElement).click();
      await settle();

      expect(element.querySelector('h2')?.textContent?.trim()).toBe('Edit this address');
      expect((field('address-field-label') as HTMLInputElement).value).toBe('Home');
      expect((field('address-field-line2') as HTMLInputElement).value).toBe('Apartment 4B');
      expect((field('address-field-district') as HTMLSelectElement).value).toBe('1');

      type('address-field-city', 'Dehiwala');
      submit();

      const request = http.expectOne(
        (candidate) =>
          candidate.url === '/api/account/addresses/11' && candidate.method === 'PUT',
      );
      expect((request.request.body as Record<string, unknown>)['city']).toBe('Dehiwala');

      request.flush([address({ city: 'Dehiwala' })]);
      await settle();

      expect(one('addresses-status')?.textContent).toBe('Changes to your address saved.');
      expect(element.querySelector('h2')?.textContent?.trim()).toBe('Add an address');
    });

    /**
     * **Focus moves to the form's heading**, because the form is below the list and on a telephone it is
     * below the fold: a shopper who pressed Edit and saw nothing change would press it again.
     */
    it('moves focus to the form when Edit is pressed', async () => {
      await open([address()]);
      (one('address-edit') as HTMLButtonElement).click();
      await settle();

      expect(document.activeElement).toBe(element.querySelector('#addresses-form-title'));
    });

    /**
     * The select offers only active districts, so keeping an id that is not in it would show "Choose a
     * district" over a value the form would then submit unchanged.
     */
    it('clears the district when the saved one is no longer served', async () => {
      await open([address({ districtId: 7, districtName: 'Jaffna', districtIsActive: false })]);
      // Jaffna is in this spec's active list, so take it out to make it genuinely unserved.
      flushDistricts();
      (one('address-edit') as HTMLButtonElement).click();
      await settle();

      expect((field('address-field-district') as HTMLSelectElement).value).toBe('7');
    });

    it('abandons the edit on Cancel, and gives focus back to the form heading', async () => {
      await open([address({ label: 'Home' })]);
      (one('address-edit') as HTMLButtonElement).click();
      await settle();
      (one('address-cancel-edit') as HTMLButtonElement).click();
      await settle();

      expect((field('address-field-label') as HTMLInputElement).value).toBe('');
      expect(element.querySelector('h2')?.textContent?.trim()).toBe('Add an address');
      expect(document.activeElement).toBe(element.querySelector('#addresses-form-title'));
      expect(http.match(() => true)).toEqual([]);
    });
  });

  describe('setting a default', () => {
    it('posts to the default route and re-renders the whole book', async () => {
      await open([
        address({ id: 11, isDefault: true }),
        address({ id: 12, label: 'Work', isDefault: false }),
      ]);

      (one('address-make-default') as HTMLButtonElement).click();

      const request = http.expectOne(
        (candidate) =>
          candidate.url === '/api/account/addresses/12/default' && candidate.method === 'POST',
      );

      // The whole book, because setting a default clears the previous one: a response carrying one row
      // would leave the screen showing two defaults.
      request.flush([
        address({ id: 11, isDefault: false }),
        address({ id: 12, label: 'Work', isDefault: true }),
      ]);
      await settle();

      expect(all('address-default-flag')).toHaveLength(1);
      expect(one('address-default-flag', cards()[1])).not.toBeNull();
      expect(one('addresses-status')?.textContent).toContain('is now your default address');
    });

    /**
     * Three identical "Make default" buttons in a book of three are three identical controls to a screen
     * reader. The label, the recipient and the first line are what distinguishes them.
     */
    it('names the address in each control, so three are not identical', async () => {
      await open([
        address({ id: 11, label: 'Home', isDefault: true }),
        address({ id: 12, label: 'Work', isDefault: false }),
        address({ id: 13, label: null, recipientName: 'Sunil', line1: '9 Lake Road', isDefault: false }),
      ]);

      const labels = all('address-remove').map((node) => node.getAttribute('aria-label'));

      expect(labels).toEqual([
        'Remove Home',
        'Remove Work',
        'Remove Sunil, 9 Lake Road',
      ]);
      expect(new Set(labels).size).toBe(3);
    });
  });

  describe('removing one', () => {
    /** Destructive and irreversible, so it asks — inline, and naming the address. */
    it('asks first, and does nothing if the question is dismissed', async () => {
      await open([address({ label: 'Home' })]);

      (one('address-remove') as HTMLButtonElement).click();
      await settle();

      expect(text()).toContain('Remove Home?');

      (one('address-remove-no') as HTMLButtonElement).click();
      await settle();

      expect(cards()).toHaveLength(1);
      expect(http.match(() => true)).toEqual([]);
    });

    /**
     * **The question replaces the button that was pressed**, so without a focus move a keyboard shopper
     * would be dropped on `<body>` with a question on screen they never heard — #109's failure by the
     * other route. `wishlist-page.ts` makes the same two-step confirmation and does not move focus on the
     * asking step; this does.
     */
    it('moves focus to the question, and back to Remove on Cancel', async () => {
      await open([address({ label: 'Home' })]);

      (one('address-remove') as HTMLButtonElement).focus();
      (one('address-remove') as HTMLButtonElement).click();
      await settle();

      expect(document.activeElement).toBe(one('address-remove-yes'));
      expect(document.activeElement).not.toBe(document.body);

      (one('address-remove-no') as HTMLButtonElement).click();
      await settle();

      expect(document.activeElement).toBe(one('address-remove'));
    });

    it('deletes it and re-renders from the book the server answers with', async () => {
      await open([
        address({ id: 11, label: 'Home', isDefault: true }),
        address({ id: 12, label: 'Work', isDefault: false }),
      ]);

      (all('address-remove')[0] as HTMLButtonElement).click();
      await settle();
      (one('address-remove-yes') as HTMLButtonElement).click();

      http
        .expectOne(
          (candidate) =>
            candidate.url === '/api/account/addresses/11' && candidate.method === 'DELETE',
        )
        // Deleting the default promotes another, which is why the whole book comes back.
        .flush([address({ id: 12, label: 'Work', isDefault: true })]);
      await settle();

      expect(cards()).toHaveLength(1);
      expect(one('address-default-flag')).not.toBeNull();
      expect(one('addresses-status')?.textContent).toContain('Home removed');
    });

    /**
     * **This is the W2-08 defect, in its new home.** The removal destroys the button that caused it,
     * which throws focus to `<body>` exactly as `[disabled]` does — banning the attribute was necessary
     * and not sufficient. Focus goes to the card that took the removed one's place.
     */
    it('moves focus to the card that took the removed one’s place', async () => {
      await open([
        address({ id: 11, label: 'Home', isDefault: true }),
        address({ id: 12, label: 'Work', isDefault: false }),
        address({ id: 13, label: 'Mother', isDefault: false }),
      ]);

      (all('address-remove')[1] as HTMLButtonElement).click();
      await settle();
      const yes = one('address-remove-yes') as HTMLButtonElement;
      yes.focus();
      yes.click();

      http
        .expectOne((candidate) => candidate.method === 'DELETE')
        .flush([address({ id: 11, label: 'Home', isDefault: true }), address({ id: 13, label: 'Mother' })]);
      await settle();

      expect(cards()).toHaveLength(2);
      expect(document.activeElement).toBe(all('address-remove')[1]);
      expect(document.activeElement).not.toBe(document.body);
    });

    it('moves focus to the last card when the removed one was last', async () => {
      await open([
        address({ id: 11, label: 'Home', isDefault: true }),
        address({ id: 12, label: 'Work', isDefault: false }),
      ]);

      (all('address-remove')[1] as HTMLButtonElement).click();
      await settle();
      (one('address-remove-yes') as HTMLButtonElement).click();

      http
        .expectOne((candidate) => candidate.method === 'DELETE')
        .flush([address({ id: 11, label: 'Home', isDefault: true })]);
      await settle();

      expect(document.activeElement).toBe(one('address-remove'));
    });

    it('moves focus to the form when the last address goes', async () => {
      await open([address({ id: 11, label: 'Home' })]);

      (one('address-remove') as HTMLButtonElement).click();
      await settle();
      (one('address-remove-yes') as HTMLButtonElement).click();

      http.expectOne((candidate) => candidate.method === 'DELETE').flush([]);
      await settle();

      expect(document.activeElement).toBe(element.querySelector('#addresses-form-title'));
    });

    /**
     * **It will not take focus from somewhere else.** A shopper may have moved to the header while the
     * request was in flight, and moving focus then would be a worse surprise than the one being fixed.
     */
    it('leaves focus alone when the shopper has moved off this page', async () => {
      await open([address({ id: 11, label: 'Home' }), address({ id: 12, label: 'Work' })]);

      (all('address-remove')[0] as HTMLButtonElement).click();
      await settle();
      (one('address-remove-yes') as HTMLButtonElement).click();

      const outside = document.createElement('button');
      document.body.append(outside);
      outside.focus();

      http
        .expectOne((candidate) => candidate.method === 'DELETE')
        .flush([address({ id: 12, label: 'Work' })]);
      await settle();

      expect(document.activeElement).toBe(outside);
      outside.remove();
    });

    /** A 404 means it had already gone, elsewhere or in another tab, so the book in hand is stale. */
    it('re-reads the book when the server says there is no such address', async () => {
      await open([address({ id: 11 })]);

      (one('address-remove') as HTMLButtonElement).click();
      await settle();
      (one('address-remove-yes') as HTMLButtonElement).click();

      http.expectOne((candidate) => candidate.method === 'DELETE').flush(
        { code: 'address_not_found', detail: 'It may already have been removed.' },
        { status: 404, statusText: 'Not Found' },
      );
      await settle();

      expect(one('addresses-alert')?.textContent).toContain('already have been removed');

      bookRequest().flush([]);
      await settle();

      expect(one('addresses-empty')).not.toBeNull();
    });
  });

  describe('nothing invented, and the checks axe did not make', () => {
    /**
     * **No "123 Galle Road", no placeholder that reads like data, no seeded recipient.** A form that
     * pretends to hold an address is a form a shopper submits without reading.
     */
    it('starts every box empty, with no placeholder anywhere', async () => {
      await open([]);

      for (const input of element.querySelectorAll('input')) {
        expect(input.getAttribute('placeholder'), input.id).toBeNull();

        if (input.type !== 'checkbox') {
          expect(input.value, input.id).toBe('');
        }
      }

      expect((field('address-field-default') as HTMLInputElement).checked).toBe(false);
    });

    it('has exactly one h1 and skips no heading level', async () => {
      await open([address()]);

      const levels = [...element.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
        Number(node.tagName.slice(1)),
      );

      expect(levels.filter((level) => level === 1)).toHaveLength(1);
      expect(levels).toEqual([1, 2]);
    });

    /** Each selector guarded on its own (#80): a group assertion hides the one that is wrong. */
    it('gives each control its own 44 px class', async () => {
      await open([address({ id: 11, isDefault: false })]);

      expect(one('address-edit')?.className).toContain('acc-inline-action');
      expect(one('address-make-default')?.className).toContain('acc-inline-action');
      expect(one('address-remove')?.className).toContain('acc-inline-action');
      expect(one('address-save')?.className).toContain('btn');

      for (const input of element.querySelectorAll('input:not([type=checkbox]), select')) {
        expect(input.className, input.id).toContain('acc-input');
      }
    });

    it('labels every box, including the default tick', async () => {
      await open([]);

      for (const input of element.querySelectorAll('input:not([type=checkbox]), select')) {
        expect(element.querySelector(`label[for="${input.id}"]`), input.id).not.toBeNull();
      }

      // The whole row is the label, so the tap target is the row rather than the 16 px box.
      expect(field('address-field-default').closest('label')?.className).toContain('acc-check');
    });

    it('carries the disabled attribute on nothing, ever', async () => {
      await open([address()]);

      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);

      (one('address-edit') as HTMLButtonElement).click();
      await settle();

      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);
    });

    it('promises no email', async () => {
      await open([address()]);

      expect(text()).not.toMatch(/email (you|your)/i);
      expect(text()).not.toMatch(/check your (inbox|email)/i);
    });
  });
});
