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

import { ProfilePage } from './profile-page';
import {
  ACCOUNT_FIELD_LENGTHS,
  CustomerProfileDto,
  CustomerSessionDto,
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

const DISTRICTS: CheckoutSessionDto = {
  ...EMPTY_CHECKOUT,
  districts: [
    { id: 1, name: 'Colombo', deliveryZone: 1 },
    { id: 2, name: 'Gampaha', deliveryZone: 2 },
  ],
};

function profile(overrides: Partial<CustomerProfileDto> = {}): CustomerProfileDto {
  return {
    userId: 7,
    email: 'nimal@example.invalid',
    emailConfirmed: true,
    firstName: 'Nimal',
    lastName: 'Perera',
    phone: '0771234567',
    defaultDistrictId: 1,
    defaultDistrictName: 'Colombo',
    ...overrides,
  };
}

/** The server's real sentence for a new confirmation link, from `ResendConfirmationAsync`. */
const RESEND_REPLY =
  'We have queued a new confirmation link to your email address. Only the newest link works.';

/**
 * The profile: name, phone, default district, password, and a new confirmation link (ACC-02, doc 11 P11
 * step 10).
 *
 * Three things here are decisions rather than plumbing, and each has its own block below: the email
 * address is read-only because the change-email flow **is not built on the server**; a district the shop
 * stopped serving is dropped rather than refusing the save, so the form has to re-seed from the answer;
 * and the confirmation-link button is an offer, never a gate (doc 11 §7 G-24).
 */
describe('ProfilePage (ACC-02)', () => {
  let fixture: ComponentFixture<ProfilePage>;
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

    fixture = TestBed.createComponent(ProfilePage);
    element = fixture.nativeElement as HTMLElement;
  }

  function profileRequest(): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === '/api/account/profile' && candidate.method === 'GET',
    );
  }

  function flushDistricts(): void {
    http
      .match((candidate) => candidate.url === CheckoutApi.url)
      .forEach((candidate) => candidate.flush(DISTRICTS));
  }

  async function open(current: CustomerProfileDto = profile()): Promise<void> {
    create();
    await settle();
    profileRequest().flush(current);
    flushDistricts();
    await settle();
  }

  function one(testId: string): HTMLElement | null {
    return element.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  }

  function field(testId: string): HTMLInputElement | HTMLSelectElement {
    return element.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  }

  /** See `register-page.spec.ts` for why the render between keystrokes is not cosmetic. */
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

  function submitForm(index: number): void {
    element.querySelectorAll('form')[index].dispatchEvent(new Event('submit', { cancelable: true }));
  }

  const submitDetails = (): void => submitForm(0);
  const submitPassword = (): void => submitForm(1);

  function text(): string {
    return element.textContent ?? '';
  }

  afterEach(() => http?.verify());

  describe('what the server renders', () => {
    it('renders the heading and the navigation, and asks the shop nothing', async () => {
      create('server');
      await settle();

      expect(element.querySelector('h1')?.textContent?.trim()).toBe('Your profile');
      expect(one('account-nav-profile')).not.toBeNull();
      expect(http.match(() => true)).toEqual([]);
    });

    it('says it is loading rather than drawing an empty form', async () => {
      create('server');
      await settle();

      expect(one('profile-loading')).not.toBeNull();
      expect(one('profile-first-name')).toBeNull();
    });

    it('answers no-store with Vary: Cookie, and noindex', async () => {
      await open();

      const headers = new Headers(response.headers ?? {});
      expect(headers.get('Cache-Control')).toContain('no-store');
      expect(headers.get('Vary')).toBe('Cookie');
      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(TestBed.inject(Title).getTitle()).toBe('Your profile | Colombo Wardrobe');
    });

    it('renders the live region before there is anything to announce', async () => {
      create('server');
      await settle();

      expect(one('profile-alert')?.getAttribute('role')).toBe('alert');
      expect(one('profile-status')?.textContent).toBe('');
    });

    it('offers Try again rather than an empty form when the read fails', async () => {
      create();
      await settle();
      profileRequest().error(new ProgressEvent('offline'), { status: 0 });
      flushDistricts();
      await settle();

      expect(one('profile-first-name')).toBeNull();
      expect(one('profile-retry')).not.toBeNull();
      expect(one('profile-alert')?.textContent).toContain('could not reach the store');

      (one('profile-retry') as HTMLButtonElement).click();
      await settle();
      profileRequest().flush(profile());
      await settle();

      expect((field('profile-first-name') as HTMLInputElement).value).toBe('Nimal');
    });
  });

  describe('the details form', () => {
    it('fills every box from what is stored', async () => {
      await open();

      expect((field('profile-first-name') as HTMLInputElement).value).toBe('Nimal');
      expect((field('profile-last-name') as HTMLInputElement).value).toBe('Perera');
      expect((field('profile-phone') as HTMLInputElement).value).toBe('0771234567');
      expect((field('profile-district') as HTMLSelectElement).value).toBe('1');
    });

    it('sends what was typed, with null for an emptied optional box', async () => {
      await open();
      type('profile-first-name', 'Nimali');
      type('profile-phone', '');
      submitDetails();

      const request = http.expectOne(
        (candidate) => candidate.url === '/api/account/profile' && candidate.method === 'PUT',
      );

      expect(request.request.body).toEqual({
        firstName: 'Nimali',
        lastName: 'Perera',
        phone: null,
        defaultDistrictId: 1,
      });

      request.flush(profile({ firstName: 'Nimali', phone: null }));
      await settle();
      http.match((candidate) => candidate.url === '/api/account/me').forEach((candidate) =>
        candidate.flush(CUSTOMER),
      );
    });

    it('sends null when the default district is cleared', async () => {
      await open();
      choose('profile-district', '');
      submitDetails();

      const request = http.expectOne((candidate) => candidate.method === 'PUT');
      expect((request.request.body as Record<string, unknown>)['defaultDistrictId']).toBeNull();

      request.flush(profile({ defaultDistrictId: null, defaultDistrictName: null }));
      await settle();
      http.match((candidate) => candidate.url === '/api/account/me').forEach((candidate) =>
        candidate.flush(CUSTOMER),
      );
    });

    /**
     * **Re-seeded from the answer, not from the draft.** The server drops a district it no longer serves
     * rather than refusing the save — refusing the whole save would lose a name change for a reason that
     * has nothing to do with the name — so the form has to show what was actually stored. A page that
     * kept the draft would show a default district the shopper does not have.
     */
    it('re-renders the form from the server’s answer, not from what was typed', async () => {
      await open();
      choose('profile-district', '2');
      submitDetails();

      // The server kept the name and dropped the district, which is its documented behaviour.
      http
        .expectOne((candidate) => candidate.method === 'PUT')
        .flush(profile({ defaultDistrictId: null, defaultDistrictName: null }));
      await settle();
      http.match((candidate) => candidate.url === '/api/account/me').forEach((candidate) =>
        candidate.flush(CUSTOMER),
      );

      expect((field('profile-district') as HTMLSelectElement).value).toBe('');
      expect(one('profile-status')?.textContent).toBe('Your details are saved.');
    });

    /**
     * The header greets the shopper by their first name, so a changed name has to reach it. The profile
     * answer is not a session — it has no security stamp — so the store is asked again rather than told.
     */
    it('refreshes the session so the header’s greeting is not stale', async () => {
      await open();
      type('profile-first-name', 'Nimali');
      submitDetails();
      http.expectOne((candidate) => candidate.method === 'PUT').flush(profile({ firstName: 'Nimali' }));
      await settle();

      http
        .expectOne('/api/account/me')
        .flush({ ...CUSTOMER, firstName: 'Nimali' });
      await settle();

      expect(TestBed.inject(AccountStore).customer()?.firstName).toBe('Nimali');
    });

    it('shows a refused field beside its own box and takes focus to the reason', async () => {
      await open();
      type('profile-first-name', '');
      submitDetails();
      http.expectOne((candidate) => candidate.method === 'PUT').flush(
        { errors: { firstName: ['Enter your first name.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(field('profile-first-name').getAttribute('aria-invalid')).toBe('true');
      expect(element.querySelector('#pr-firstName-error')?.textContent).toContain(
        'Enter your first name.',
      );
      expect(document.activeElement).toBe(one('profile-alert'));
    });

    it('caps each box at the width of the column behind it', async () => {
      await open();

      expect(field('profile-first-name').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.name),
      );
      expect(field('profile-last-name').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.name),
      );
      expect(field('profile-phone').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.phone),
      );
    });
  });

  describe('the email address, which cannot be changed here', () => {
    /**
     * Doc 11 P11 step 10 includes "changes email (confirmation to the new address required)" and **that
     * flow is not built**: `CustomerProfileEdit` on the server has no email field. A disabled input would
     * imply a control that could be enabled; text says what is true, and the page offers the way to ask.
     */
    it('shows it as text with no box at all', async () => {
      await open();

      expect(one('profile-email')?.textContent?.trim()).toBe('nimal@example.invalid');
      expect(element.querySelector('input[type=email]')).toBeNull();
      expect(element.querySelector('input[name=email]')).toBeNull();
      expect(text()).toContain('Get in touch with us to change it');
    });

    it('never sends an email address in the profile write', async () => {
      await open();
      submitDetails();

      const request = http.expectOne((candidate) => candidate.method === 'PUT');
      const body = request.request.body as Record<string, unknown>;

      // The four fields `CustomerProfileEdit` declares, and nothing else: an `email` key here would be
      // a field the server silently ignores, which reads to the next developer as a flow that works.
      expect(Object.keys(body).sort()).toEqual([
        'defaultDistrictId',
        'firstName',
        'lastName',
        'phone',
      ]);

      request.flush(profile());
      await settle();
      http.match((candidate) => candidate.url === '/api/account/me').forEach((candidate) =>
        candidate.flush(CUSTOMER),
      );
    });
  });

  describe('changing the password', () => {
    it('sends both passwords and empties both boxes on success', async () => {
      await open();
      type('profile-current-password', 'the old one');
      type('profile-new-password', 'a long enough new one');
      submitPassword();

      const request = http.expectOne(
        (candidate) => candidate.url === '/api/account/change-password' && candidate.method === 'POST',
      );
      expect(request.request.body).toEqual({
        currentPassword: 'the old one',
        newPassword: 'a long enough new one',
      });

      request.flush(CUSTOMER);
      await settle();

      expect((field('profile-current-password') as HTMLInputElement).value).toBe('');
      expect((field('profile-new-password') as HTMLInputElement).value).toBe('');
    });

    /**
     * The server bumps the security stamp and reissues *this* browser's cookie, so the shopper stays
     * signed in here and is signed out everywhere else. It answers with the whole session, so the store
     * is told rather than asked.
     */
    it('says every other browser has been signed out, and takes the new session', async () => {
      await open();
      type('profile-current-password', 'the old one');
      type('profile-new-password', 'a long enough new one');
      submitPassword();
      http.expectOne((candidate) => candidate.url === '/api/account/change-password').flush(CUSTOMER);
      await settle();

      expect(one('profile-status')?.textContent).toContain('signed out');
      expect(TestBed.inject(AccountStore).customer()).toEqual(CUSTOMER);
      expect(http.match((candidate) => candidate.url === '/api/account/me')).toEqual([]);
    });

    it('shows the wrong-current-password refusal beside its own box', async () => {
      await open();
      type('profile-current-password', 'wrong');
      type('profile-new-password', 'a long enough new one');
      submitPassword();
      http.expectOne((candidate) => candidate.url === '/api/account/change-password').flush(
        { errors: { currentPassword: ['That is not your current password.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(element.querySelector('#pr-current-error')?.textContent).toContain(
        'That is not your current password.',
      );
      expect(field('profile-current-password').getAttribute('aria-invalid')).toBe('true');
    });

    /**
     * **A wrong current password counts towards the same lockout as a wrong sign-in** (#146 extended the
     * guard to change-password for exactly this reason: an endpoint that verifies a password is a
     * password oracle whether or not it is called login). So this form can answer 423, and the sentence
     * is the server's.
     */
    it('shows the server’s lockout sentence', async () => {
      await open();
      type('profile-current-password', 'wrong');
      type('profile-new-password', 'a long enough new one');
      submitPassword();
      http.expectOne((candidate) => candidate.url === '/api/account/change-password').flush(
        { code: 'locked_out', detail: 'Too many wrong passwords. Try again in a few minutes.' },
        { status: 423, statusText: 'Locked' },
      );
      await settle();

      expect(one('profile-alert')?.textContent).toBe(
        'Too many wrong passwords. Try again in a few minutes.',
      );
    });

    it('shows the server’s password rule, and states none of its own', async () => {
      await open();

      expect(text()).not.toMatch(/at least \d+ characters/i);

      type('profile-current-password', 'the old one');
      type('profile-new-password', 'short');
      submitPassword();
      http.expectOne((candidate) => candidate.url === '/api/account/change-password').flush(
        {
          errors: {
            newPassword: [
              'Use at least 10 characters, including an upper-case letter, a lower-case letter, a ' +
                'number and a symbol.',
            ],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(element.querySelector('#pr-new-error')?.textContent).toContain(
        'Use at least 10 characters',
      );
    });
  });

  describe('the confirmation link (doc 11 §7 G-24)', () => {
    /**
     * **An offer and never a gate.** Confirmation governs only the linking of earlier guest orders, so
     * nothing on this page is withheld from an unconfirmed customer — and the button is offered only to
     * one, because the server answers the same 202 either way and a button that always says the same
     * thing teaches nothing.
     */
    it('is offered only to an unconfirmed customer, and withholds nothing from them', async () => {
      await open(profile({ emailConfirmed: false }));

      expect(one('profile-resend')).not.toBeNull();
      expect(one('profile-first-name')).not.toBeNull();
      expect(one('profile-change-password')).not.toBeNull();
      expect(text()).toContain('shop and check out as usual');
    });

    it('is not offered to a confirmed customer', async () => {
      await open();

      expect(one('profile-resend')).toBeNull();
    });

    /** The server's own wording: it says what was queued and promises no delivery. */
    it('shows the server’s sentence and moves focus to it', async () => {
      await open(profile({ emailConfirmed: false }));

      (one('profile-resend') as HTMLButtonElement).click();
      http
        .expectOne(
          (candidate) =>
            candidate.url === '/api/account/resend-confirmation' && candidate.method === 'POST',
        )
        .flush({ message: RESEND_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(one('profile-status')?.textContent).toBe(RESEND_REPLY);
      expect(document.activeElement).toBe(one('profile-status'));
    });

    it('refuses its own second press while the first is in flight', async () => {
      await open(profile({ emailConfirmed: false }));

      (one('profile-resend') as HTMLButtonElement).click();
      await settle();
      (one('profile-resend') as HTMLButtonElement).click();

      expect(
        http.match((candidate) => candidate.url === '/api/account/resend-confirmation'),
      ).toHaveLength(1);
      expect(one('profile-resend')?.getAttribute('aria-disabled')).toBe('true');
      expect(one('profile-resend')?.hasAttribute('disabled')).toBe(false);
    });

    it('promises no email anywhere on the page', async () => {
      await open(profile({ emailConfirmed: false }));

      expect(text()).not.toMatch(/check your (inbox|email)/i);
      expect(text()).not.toMatch(/\bwe (have |)sent\b/i);
    });
  });

  describe('nothing invented, and the checks axe did not make', () => {
    it('has one h1, three h2s, and skips no level', async () => {
      await open(profile({ emailConfirmed: false }));

      const levels = [...element.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
        Number(node.tagName.slice(1)),
      );

      expect(levels).toEqual([1, 2, 2, 2]);
    });

    it('names each section from its own heading', async () => {
      await open(profile({ emailConfirmed: false }));

      const sections = [...element.querySelectorAll('section')];

      expect(sections).toHaveLength(3);
      for (const section of sections) {
        const id = section.getAttribute('aria-labelledby');
        expect(id).not.toBeNull();
        expect(element.querySelector(`#${id}`), id ?? '').not.toBeNull();
      }
    });

    /** Each selector guarded on its own (#80). */
    it('gives each control its own 44 px class', async () => {
      await open(profile({ emailConfirmed: false }));

      expect(one('profile-save')?.className).toContain('btn');
      expect(one('profile-change-password')?.className).toContain('btn');
      expect(one('profile-resend')?.className).toContain('btn');

      for (const input of element.querySelectorAll('input, select')) {
        expect(input.className, input.id).toContain('acc-input');
        expect(element.querySelector(`label[for="${input.id}"]`), input.id).not.toBeNull();
      }
    });

    it('carries the disabled attribute on nothing', async () => {
      await open(profile({ emailConfirmed: false }));

      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);
    });

    it('puts no placeholder in any box', async () => {
      await open();

      for (const input of element.querySelectorAll('input')) {
        expect(input.getAttribute('placeholder'), input.id).toBeNull();
      }

      // And no password is pre-filled, which would be a password in the markup.
      expect((field('profile-current-password') as HTMLInputElement).value).toBe('');
      expect((field('profile-new-password') as HTMLInputElement).value).toBe('');
    });
  });
});
