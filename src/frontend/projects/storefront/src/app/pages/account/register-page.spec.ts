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

import { RegisterPage } from './register-page';
import { ACCOUNT_FIELD_LENGTHS } from '../../core/account/account.model';
import { flushContentPages } from '../../core/content/content-pages.testing';

/**
 * The server's one sentence for every address, which registration answers with.
 *
 * This is the real string from `CustomerAuthEndpoints.RegistrationReply`, because the point of the tests
 * below is that this page prints the server's wording and never its own — and a made-up reply in the
 * fixture would let a page that wrote its own sentence pass.
 */
const SERVER_REPLY =
  'If that email address can be used, we have queued a link to it. You can sign in now with the ' +
  'password you chose.';

/**
 * Create an account (ACC-01, doc 11 P11 step A).
 *
 * The two properties that matter most are both about what the page does *not* do: it must not reveal
 * whether an address is already registered (doc 08 §3.1 DB10), and it must not promise an email (doc 08
 * §3.8's transport is unconfigured). Both are properties of the copy, so both are tested by reading the
 * page and asserting absences.
 */
describe('RegisterPage (ACC-01)', () => {
  let fixture: ComponentFixture<RegisterPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(platform: 'browser' | 'server' = 'browser'): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', children: [] },
          { path: 'account/sign-in', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RegisterPage);
    element = fixture.nativeElement as HTMLElement;
  }

  function field(testId: string): HTMLInputElement {
    return element.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  }

  /**
   * Types into a box the way a browser does, **change detection included**.
   *
   * **The `detectChanges()` is load-bearing and finding that out was worth the test.** Without it the
   * bound signal goes `''` -> `'Nimal'` -> `''` with no render in between, so Angular's binding diff sees
   * the same value it last wrote and never touches the DOM — the box keeps the typed text and the
   * "it empties itself" test fails against a page that is correct. In a real browser every keystroke's
   * signal write schedules a render, so the stored binding really is `'Nimal'` by the time the form is
   * submitted. A helper that skipped it would be testing a browser that does not exist.
   */
  function type(testId: string, value: string): void {
    const input = field(testId);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(): void {
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit', { cancelable: true }),
    );
  }

  function registerRequest(): TestRequest {
    return http.expectOne(
      (request) => request.url === '/api/account/register' && request.method === 'POST',
    );
  }

  function statusRegion(): HTMLElement {
    return element.querySelector('[data-testid="register-status"]') as HTMLElement;
  }

  function alertRegion(): HTMLElement {
    return element.querySelector('[data-testid="register-alert"]') as HTMLElement;
  }

  function text(): string {
    return element.textContent ?? '';
  }

  function fillIn(): void {
    type('register-first-name', 'Nimal');
    type('register-email', 'nimal@example.invalid');
    type('register-password', 'a long enough password');
  }

  beforeEach(() => create());

  afterEach(() => {
    // The consent line asks for the published-page index (issue #252), as the footer does on every page.
    if (http) {
      flushContentPages(http);
      http.verify();
    }
  });

  /** Everything the page asked for except the published-page index, which the shell asks for on every page anyway. */
  function otherRequests(): TestRequest[] {
    return http.match((request) => request.url !== '/api/content/pages');
  }

  describe('what the server renders', () => {
    it('renders the whole form and asks the shop nothing', async () => {
      create('server');
      await settle();

      expect(element.querySelector('h1')?.textContent?.trim()).toBe('Create an account');
      expect(field('register-first-name')).not.toBeNull();
      expect(field('register-email')).not.toBeNull();
      expect(field('register-password')).not.toBeNull();
      expect(otherRequests()).toEqual([]);
    });

    it('renders both live regions before there is anything to announce', async () => {
      create('server');
      await settle();

      expect(statusRegion().getAttribute('role')).toBe('status');
      expect(alertRegion().getAttribute('role')).toBe('alert');
      expect(statusRegion().textContent).toBe('');
    });

    it('asks not to be indexed and not to be stored', async () => {
      await settle();

      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(new Headers(response.headers ?? {}).get('Cache-Control')).toContain('no-store');
      expect(TestBed.inject(Title).getTitle()).toBe('Create an account | Colombo Wardrobe');
    });
  });

  /**
   * Issue #252: where an account is made, the shop says what creating one means, and points at the pages that say it
   * - the ones that are published. With neither there is no sentence, rather than one that points nowhere.
   */
  describe('the consent line', () => {
    function consent(): string {
      return (element.querySelector('[data-testid="register-consent"]')?.textContent ?? '')
        .replace(/\s+/g, ' ')
        .trim();
    }

    it('is empty while neither page is published', async () => {
      await settle();
      flushContentPages(http, []);
      await settle();

      expect(consent()).toBe('');
      expect(element.querySelector('[data-testid="register-consent"] a')).toBeNull();
    });

    it('links the terms and the privacy policy by their own addresses once both are published', async () => {
      await settle();
      flushContentPages(http, [
        { slug: 'terms-of-sale', title: 'Terms and conditions', systemKey: 'terms' },
        { slug: 'privacy', title: 'Privacy policy', systemKey: 'privacy' },
      ]);
      await settle();

      expect(consent()).toBe('By creating an account you agree to our Terms and conditions and Privacy policy.');
      expect(
        [...element.querySelectorAll('[data-testid="register-consent"] a')].map((link) => link.getAttribute('href')),
      ).toEqual(['/terms-of-sale', '/privacy']);
    });

    it('names only the page that exists when one of them is unpublished', async () => {
      await settle();
      flushContentPages(http, [{ slug: 'privacy', title: 'Privacy policy', systemKey: 'privacy' }]);
      await settle();

      expect(consent()).toBe('By creating an account you agree to our Privacy policy.');
    });
  });

  describe('what it sends', () => {
    it('sends the two required fields and the two optional ones', async () => {
      await settle();
      fillIn();
      type('register-last-name', 'Perera');
      type('register-phone', '0771234567');
      submit();

      expect(registerRequest().request.body).toEqual({
        firstName: 'Nimal',
        lastName: 'Perera',
        email: 'nimal@example.invalid',
        password: 'a long enough password',
        phone: '0771234567',
      });
    });

    /**
     * An empty optional box is **null and not `''`**: the server's validator trims and treats blank as
     * absent, so `''` would store an empty string in a nullable column — "no phone" stored as something
     * rather than as nothing.
     */
    it('sends null for an optional box left empty, never an empty string', async () => {
      await settle();
      fillIn();
      submit();

      const body = registerRequest().request.body as Record<string, unknown>;
      expect(body['lastName']).toBeNull();
      expect(body['phone']).toBeNull();
    });

    it('trims an optional box rather than storing the spaces', async () => {
      await settle();
      fillIn();
      type('register-last-name', '  Perera  ');
      submit();

      expect((registerRequest().request.body as Record<string, unknown>)['lastName']).toBe('Perera');
    });

    it('caps each box at the width of the column behind it', async () => {
      await settle();

      expect(field('register-first-name').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.name),
      );
      expect(field('register-email').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.email),
      );
      expect(field('register-phone').getAttribute('maxlength')).toBe(
        String(ACCOUNT_FIELD_LENGTHS.phone),
      );
    });
  });

  describe('the one sentence every address gets (doc 08 §3.1 DB10)', () => {
    /**
     * **The page prints the server's wording and writes none of its own.** The sentence was chosen on
     * the server for two reasons the storefront cannot see: it has to be true of a free address, a
     * taken one and a staff address alike, and it says "queued" rather than "sent" because the mail
     * transport is unconfigured. A page that wrote "Check your inbox" would break both at once.
     */
    it('shows the server’s sentence, word for word', async () => {
      await settle();
      fillIn();
      submit();
      registerRequest().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(statusRegion().textContent).toBe(SERVER_REPLY);
    });

    /**
     * W3-02 removed "we will email your order number" from checkout because nothing could send it, and
     * `checkout-page.spec.ts` pins its absence with a live assertion. This is the same assertion for the
     * page that has the most reason to make the promise.
     */
    it('promises no email, before or after a successful registration', async () => {
      await settle();
      expect(text()).not.toMatch(/email (you|your)/i);
      expect(text()).not.toMatch(/check your (inbox|email)/i);
      expect(text()).not.toMatch(/\bwe (have |)sent\b/i);

      fillIn();
      submit();
      registerRequest().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(text()).not.toMatch(/check your (inbox|email)/i);
      expect(text()).not.toMatch(/\bwe (have |)sent\b/i);
    });

    /**
     * **No hint anywhere that an address might already be taken.** A duplicate check as the shopper
     * types, a "you may already have an account" line, or a different outcome for a taken address are
     * all enumeration oracles however politely they are worded.
     */
    it('says nothing about an address being taken, and checks nothing as it is typed', async () => {
      await settle();
      type('register-email', 'already@example.invalid');
      await settle();

      expect(otherRequests()).toEqual([]);
      expect(text()).not.toMatch(/taken|in use|already registered|already has an account/i);
    });

    /** The sentence is the outcome, so reading it is the next thing the shopper does. */
    it('moves focus to the confirmation', async () => {
      await settle();
      fillIn();
      submit();
      registerRequest().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(document.activeElement).toBe(statusRegion());
    });

    /**
     * A filled form behind a success message invites a second submit, which the server's rate limit —
     * five registrations per /64 per ten minutes — would refuse with a sentence about too many attempts
     * that would read as a fault. The password goes too, because a password left in the DOM is a
     * password left in the DOM.
     */
    it('empties every box, password included', async () => {
      await settle();
      fillIn();
      type('register-phone', '0771234567');
      submit();
      registerRequest().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
      await settle();

      for (const input of element.querySelectorAll('input')) {
        expect(input.value, input.id).toBe('');
      }
    });
  });

  describe('when a field is refused', () => {
    /**
     * **The password rule comes from the server, which is the thing that enforces it.**
     * `PasswordPolicy`'s own remarks say the front end must not hold a second copy of the
     * number: #126 shipped a `maxlength` of 120 against a 100-character column, and a form promising
     * eight characters against an API refusing nine is the same defect. So the sentence a shopper reads
     * is the server's, and this test uses the server's real wording.
     */
    it('shows the server’s password rule beside the password box', async () => {
      await settle();
      fillIn();
      submit();
      registerRequest().flush(
        {
          errors: {
            password: [
              'Use at least 10 characters, including an upper-case letter, a lower-case letter, a ' +
                'number and a symbol.',
            ],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(field('register-password').getAttribute('aria-invalid')).toBe('true');
      expect(element.querySelector('#rg-password-error')?.textContent).toContain(
        'Use at least 10 characters',
      );
    });

    /**
     * The rule is stated **before** the shopper presses anything, and it is the server's own sentence.
     *
     * ## This reverses an earlier decision in this file, and the reversal is the point
     *
     * The first version of this spec asserted the opposite — that the form states no rule of its own,
     * because `PasswordPolicy`'s remarks say the front end should not hold a second copy of the
     * number. That reading was right about the hazard (#126: a form promising one rule while the server
     * enforces another passed the form, the stub and sixteen browser tests) and wrong about the cure.
     * Leaving the rule out means a shopper learns it only by being refused — WCAG 2.2 **3.3.2 Labels or
     * Instructions** — and on a password field they cannot see what they typed, so the refusal is doubly
     * opaque.
     *
     * The cure is a copy that cannot drift: `PASSWORD_RULE` in `account.model.ts`, pinned to
     * `PasswordPolicy.Requirement` by `PasswordHintConventionTests` in the C# unit
     * suite, which fails the build if the two are not byte-identical. That is the technique
     * `BrandNameConventionTests` already uses across the three source trees for the shop's name.
     *
     * ## What is asserted here, and what is asserted there
     *
     * This test owns the *markup*: the sentence is on the page, it is tied to the input with
     * `aria-describedby` so a screen reader reads it as part of the field, and it is there before any
     * refusal. Whether the sentence matches the server is the C# test's job — asserting it here as well
     * would be a third copy of the words.
     */
    it('states the password rule up front, tied to the field', async () => {
      await settle();

      const hint = element.querySelector('[data-testid="register-password-rule"]');
      expect(hint).not.toBeNull();
      expect(hint?.textContent).toMatch(/at least \d+ characters/i);

      // Tied to the input, not loose text after it.
      const described = field('register-password').getAttribute('aria-describedby') ?? '';
      expect(described.split(/\s+/)).toContain(hint?.id);

      // Still no `minlength`: the browser's own bubble would state a rule in wording nobody here
      // controls, and would refuse before the server's four character classes were ever checked.
      expect(field('register-password').getAttribute('minlength')).toBeNull();
    });

    /**
     * And the refusal is read first, then the rule.
     *
     * `aria-describedby` is read in the order the ids appear, so a shopper who has just been refused
     * hears what went wrong before hearing the rule again — which is the useful order, and the reverse
     * of what putting the hint's id first would give.
     */
    it('reads the refusal before the rule once there is one', async () => {
      await settle();

      fillIn();
      submit();
      registerRequest().flush(
        { errors: { password: ['Use at least 10 characters.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      const described = field('register-password').getAttribute('aria-describedby') ?? '';

      expect(described.split(/\s+/)).toEqual(['rg-password-error', 'rg-password-rule']);
    });

    it('writes the refusal into the region that was already on the page', async () => {
      await settle();
      const before = alertRegion();

      fillIn();
      submit();
      registerRequest().flush({ errors: { email: ['Enter an email address we can send to.'] } }, {
        status: 400,
        statusText: 'Bad Request',
      });
      await settle();

      expect(alertRegion()).toBe(before);
      expect(before.textContent).toContain('Enter an email address we can send to.');
      expect(document.activeElement).toBe(before);
    });

    it('keeps what was typed, so nothing has to be retyped', async () => {
      await settle();
      fillIn();
      submit();
      registerRequest().flush({ errors: { password: ['Too short.'] } }, {
        status: 400,
        statusText: 'Bad Request',
      });
      await settle();

      expect(field('register-first-name').value).toBe('Nimal');
      expect(field('register-email').value).toBe('nimal@example.invalid');
    });

    it('shows the server’s sentence when it is refused for asking too often', async () => {
      await settle();
      fillIn();
      submit();
      registerRequest().flush(
        {
          code: 'rate_limited',
          detail: 'Too many accounts have been created from here recently. Please try again shortly.',
        },
        { status: 429, statusText: 'Too Many Requests' },
      );
      await settle();

      expect(alertRegion().textContent).toContain('Too many accounts have been created');
    });
  });

  describe('the submit button and the form', () => {
    it('never carries the disabled attribute', async () => {
      await settle();
      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);

      fillIn();
      submit();
      await settle();

      expect(element.querySelectorAll('[disabled]')).toHaveLength(0);
      expect(
        element.querySelector('[data-testid="register-submit"]')?.getAttribute('aria-disabled'),
      ).toBe('true');

      registerRequest().flush({ message: SERVER_REPLY }, { status: 202, statusText: 'Accepted' });
    });

    it('refuses a second press while the first is in flight', async () => {
      await settle();
      fillIn();
      submit();
      submit();

      expect(http.match((request) => request.url === '/api/account/register')).toHaveLength(1);
    });
  });

  describe('nothing invented, and the checks axe did not make', () => {
    it('puts no placeholder and no value in any box', async () => {
      await settle();

      for (const input of element.querySelectorAll('input')) {
        expect(input.getAttribute('placeholder'), input.id).toBeNull();
        expect(input.value, input.id).toBe('');
      }
    });

    it('has one h1, skips no level, and labels every box', async () => {
      await settle();

      const levels = [...element.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
        Number(node.tagName.slice(1)),
      );

      expect(levels.filter((level) => level === 1)).toHaveLength(1);
      expect(levels[0]).toBe(1);

      for (let i = 1; i < levels.length; i += 1) {
        expect(levels[i] - levels[i - 1]).toBeLessThanOrEqual(1);
      }

      for (const input of element.querySelectorAll('input')) {
        expect(input.className, input.id).toContain('acc-input');
        expect(element.querySelector(`label[for="${input.id}"]`), input.id).not.toBeNull();
      }
    });

    it('marks the two optional fields as optional in their labels', async () => {
      await settle();

      expect(element.querySelector('label[for="rg-lastName"]')?.textContent).toContain('(optional)');
      expect(element.querySelector('label[for="rg-phone"]')?.textContent).toContain('(optional)');
      expect(element.querySelector('label[for="rg-email"]')?.textContent).not.toContain('(optional)');
    });
  });
});
