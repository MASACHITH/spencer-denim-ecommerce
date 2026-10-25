import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminSettingGroup,
  CurrencyField,
  DISPLAY_CURRENCIES,
} from '../../core/settings/admin-settings.model';
import { CurrencyRatesPanel } from './currency-rates-panel';

// The sentences the API gives are one shared table, which the C# handler is held to by CurrencyRateRulesParityTests.
// Node's own modules are loaded by a computed name (this project's spec types do not include Node's; see the rules spec).
declare const process: { cwd(): string };

const nodeFs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as {
  readFileSync(path: string, encoding: 'utf8'): string;
};

const sentences = (
  JSON.parse(nodeFs.readFileSync(`${process.cwd()}/e2e/support/currency-rate-cases.json`, 'utf8')) as {
    messages: Readonly<Record<'tooManyDecimals' | 'outOfRange' | 'notANumber', string>>;
  }
).messages;

const DAY_MS = 86_400_000;
const NOW = new Date('2026-10-24T08:00:00.000Z');

/**
 * The display currencies panel (issue #248). As with the rest of the settings screen, the outgoing request bodies are
 * the point: only the boxes that changed are sent, an empty box is sent as an empty string (it clears), and a rate the
 * screen can already see is wrong never leaves the browser. Time is fixed so "3 days ago" and the thirty-day line are
 * the same on every machine on every day.
 */
describe('CurrencyRatesPanel (issue #248)', () => {
  let controller: HttpTestingController;
  let fixture: ComponentFixture<CurrencyRatesPanel>;
  let element: HTMLElement;

  type Saved = Partial<Record<CurrencyField, { value: string | null; daysAgo?: number | null }>>;

  /** A group as the server sends it: the five rows in order, a value and a date for those the test names. */
  function group(saved: Saved = {}, rowVersion = 'cur-rv-1'): AdminSettingGroup {
    return {
      groupKey: 'Currency.Display',
      rowVersion,
      settings: DISPLAY_CURRENCIES.map((currency) => {
        const entry = saved[currency.field];
        const value = entry?.value ?? null;
        const daysAgo = entry?.daysAgo;

        return {
          key: currency.key,
          valueType: 'Decimal' as const,
          value,
          isSecret: false,
          hasValue: value !== null,
          requiredPermission: 'Settings.Edit',
          description: `What ${currency.key} is for.`,
          updatedAtUtc:
            value === null || daysAgo === null || daysAgo === undefined
              ? null
              : new Date(NOW.getTime() - daysAgo * DAY_MS).toISOString(),
        };
      }),
    };
  }

  async function render(input: AdminSettingGroup): Promise<void> {
    TestBed.configureTestingModule({
      imports: [CurrencyRatesPanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CurrencyRatesPanel);
    fixture.componentRef.setInput('group', input);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  }

  async function reread(input: AdminSettingGroup): Promise<void> {
    fixture.componentRef.setInput('group', input);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function box(field: string): HTMLInputElement {
    return element.querySelector(`[data-testid="currency-field-${field}"]`) as HTMLInputElement;
  }

  function text(testId: string): string | null {
    return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
  }

  async function type(field: string, value: string): Promise<void> {
    const input = box(field);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function leave(field: string): Promise<void> {
    box(field).dispatchEvent(new Event('blur'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function press(): void {
    (element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function answerToken(): void {
    controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'xsrf' });
  }

  function expectNoRequest(): void {
    controller.expectNone(AdminSettingsApi.currencyRatesUrl);
    controller.expectNone('/api/admin/auth/antiforgery-token');
  }

  beforeEach(() => {
    // Only Date is faked: Angular's own timers must keep running or whenStable never settles.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    // If verify() throws (a test left a request open), the test module must still be reset and the clock restored, or
    // the next test dies with "Cannot configure the test module when the test module has already been
    // instantiated" and one real failure turns into thirty. Found by a mutation that made a second press send twice.
    vi.useRealTimers();

    try {
      controller.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  describe('what it draws', () => {
    it('draws the five currencies in the API order, each box labelled and with a decimal keyboard', async () => {
      await render(group());

      const rows = [...element.querySelectorAll('[data-testid^="currency-row-"]')];
      expect(rows.map((row) => row.getAttribute('data-testid'))).toEqual([
        'currency-row-usd',
        'currency-row-gbp',
        'currency-row-eur',
        'currency-row-aud',
        'currency-row-aed',
      ]);

      for (const currency of DISPLAY_CURRENCIES) {
        const input = box(currency.field);
        const label = element.querySelector(`label[for="currency-${currency.field}"]`);

        expect(input.id).toBe(`currency-${currency.field}`);
        expect(input.getAttribute('inputmode')).toBe('decimal');
        expect(input.getAttribute('autocomplete')).toBe('off');
        // A sentence, not a dangling "rupees for one": the name as the API writes it, then the code.
        expect(label?.textContent?.replace(/\s+/g, ' ').trim()).toBe(`Rupees for one ${currency.name} (${currency.code})`);
        // Enter submits the form, so the keyboard's key says "done", not "next".
        expect(input.getAttribute('enterkeyhint')).toBe('done');
      }
    });

    it('explains the number, that PayPal still charges rupees, and that the rates are typed by hand', async () => {
      await render(group());

      const body = (element.textContent ?? '').replace(/\s+/g, ' ');
      expect(body).toContain('399.50 for the pound means one pound costs 399.50 rupees');
      expect(body).toContain('Leave a box empty to leave that currency out');
      expect(body).toContain('PayPal still charges the rupee total');
      expect(body).toContain('typed by hand and are never updated automatically');
      expect(body).toContain("The shop's currency switcher will use these rates when it is released.");
    });

    /**
     * Tester round, Major 2. There is no switcher on this branch, and the order page, the PayPal step and the price
     * range filter stay in rupees whatever is typed here, so the panel may promise nothing about what shoppers see.
     * Every state the panel can be in is read, not just the first.
     */
    describe('promises nothing about the storefront', () => {
      const FORBIDDEN = [/shopper/i, /every price/i, /approximate/i, /can choose/i, /converted at your rate/i];

      function everythingItSays(): string {
        // Text only: the placeholders and titles are part of what a reader is told.
        const parts = [element.textContent ?? ''];
        element.querySelectorAll('[title],[placeholder],[aria-label]').forEach((node) => {
          parts.push(
            node.getAttribute('title') ?? '',
            node.getAttribute('placeholder') ?? '',
            node.getAttribute('aria-label') ?? '',
          );
        });

        return parts.join(' ').replace(/\s+/g, ' ');
      }

      function expectClean(where: string): void {
        const said = everythingItSays();

        for (const word of FORBIDDEN) {
          expect(said, `${where}: ${word}`).not.toMatch(word);
        }
      }

      it('is clean with nothing set, with rates set, stale, unsaved, cleared and in error', async () => {
        await render(group());
        expectClean('nothing set');

        await reread(
          group(
            {
              usd: { value: '321.5', daysAgo: 1 },
              gbp: { value: '399.5', daysAgo: 45 },
              eur: { value: 'abc', daysAgo: 2 },
              aud: { value: '205', daysAgo: null },
            },
            'cur-rv-2',
          ),
        );
        expectClean('rates set, stale, unrecorded and garbage');

        await type('usd', '330');
        await type('gbp', '');
        await type('aed', '87.5');
        expectClean('unsaved, cleared and new');

        await type('aed', '87.123456');
        press();
        expectClean('in error');

        await type('aed', '87.5');
        press();
        answerToken();
        controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush(null, { status: 204, statusText: 'No Content' });
        fixture.detectChanges();
        expectClean('saved');

        await type('aed', '88');
        press();
        answerToken();
        controller
          .expectOne(AdminSettingsApi.currencyRatesUrl)
          .flush({ detail: 'Somebody else changed the currency rates while you were editing them.' }, { status: 409, statusText: 'Conflict' });
        fixture.detectChanges();
        expectClean('in conflict');
      });

      it('says that the switcher is not here yet, once, and not in the words of a promise', async () => {
        await render(group({ usd: { value: '321.5', daysAgo: 1 } }));

        const said = everythingItSays();
        expect(said.match(/currency switcher/g)?.length).toBe(1);
        expect(said).toContain('will use these rates when it is released');
      });
    });

    it('shows the saved rates and names the currencies that have one', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 1 }, gbp: { value: '399.5', daysAgo: 1 } }));

      expect(box('usd').value).toBe('321.5');
      expect(box('gbp').value).toBe('399.5');
      expect(box('eur').value).toBe('');
      expect(text('currency-offered')).toBe('Rates set: USD, GBP.');
    });

    it('says plainly when no rate is set, and that an empty box leaves its currency out', async () => {
      await render(group());

      expect(text('currency-offered')).toBe('No rates set yet.');
      expect(text('currency-line-aud')).toBe('No rate set. AUD is left out until you type one.');
      expect(element.querySelector('[data-testid^="currency-stale-"]')).toBeNull();
    });
  });

  describe("a rate's age", () => {
    it('says how long ago it changed, with the date in the shop time zone, in the text and the tooltip', async () => {
      await render(group({ eur: { value: '345', daysAgo: 3 } }));

      expect(text('currency-line-eur')).toBe('Last changed 3 days ago (21 Oct 2026).');
      expect(element.querySelector('[data-testid="currency-line-eur"]')?.getAttribute('title')).toBe(
        '3 days ago, on 21 Oct 2026',
      );
    });

    it('says today and yesterday in words', async () => {
      await render(group({ usd: { value: '321', daysAgo: 0 }, gbp: { value: '399', daysAgo: 1 } }));

      expect(text('currency-line-usd')).toContain('Last changed today');
      expect(text('currency-line-gbp')).toContain('Last changed yesterday');
    });

    it('does not warn at exactly thirty days, and does at thirty-one, with the count in the sentence', async () => {
      await render(
        group({
          usd: { value: '321', daysAgo: 30 },
          gbp: { value: '399', daysAgo: 31 },
          eur: { value: '345', daysAgo: 90 },
        }),
      );

      expect(element.querySelector('[data-testid="currency-stale-usd"]')).toBeNull();
      expect(text('currency-stale-gbp')).toBe('This rate has not changed for 31 days. Check it is still right.');
      expect(text('currency-stale-eur')).toBe('This rate has not changed for 90 days. Check it is still right.');
    });

    it('ties the warning to the box, so a screen reader reads it with the label', async () => {
      await render(group({ gbp: { value: '399', daysAgo: 45 } }));

      expect(box('gbp').getAttribute('aria-describedby')).toBe('currency-line-gbp currency-stale-gbp');
      expect(element.querySelector('#currency-stale-gbp')).not.toBeNull();
    });

    it('has no age and no warning for a rate nobody has dated, and none for an empty box', async () => {
      await render(group({ aed: { value: '87.5', daysAgo: null } }));

      expect(text('currency-line-aed')).toBe('No change date recorded.');
      expect(element.querySelector('[data-testid="currency-line-aed"]')?.getAttribute('title')).toBeNull();
      expect(element.querySelector('[data-testid="currency-stale-aed"]')).toBeNull();
      expect(element.querySelector('[data-testid="currency-stale-usd"]')).toBeNull();
    });
  });

  describe('saving', () => {
    it('sends only the boxes that changed, with the group row version, and leaves the rest out', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 2 } }));

      await type('gbp', '399.50');
      press();
      answerToken();

      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ gbp: '399.50', rowVersion: 'cur-rv-1' });
      expect('usd' in request.request.body).toBe(false);
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('sends an emptied box as an empty string, which clears that rate', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 2 }, gbp: { value: '399.5', daysAgo: 2 } }));

      await type('usd', '');
      press();
      answerToken();

      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(request.request.body).toEqual({ usd: '', rowVersion: 'cur-rv-1' });
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('trims the ends of what was typed', async () => {
      await render(group());

      await type('aed', '  87.5  ');
      press();
      answerToken();

      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(request.request.body).toEqual({ aed: '87.5', rowVersion: 'cur-rv-1' });
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('sends several changed boxes together and no others', async () => {
      await render(group({ eur: { value: '345', daysAgo: 2 } }));

      await type('usd', '321.5');
      await type('aud', '205');
      press();
      answerToken();

      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(request.request.body).toEqual({ usd: '321.5', aud: '205', rowVersion: 'cur-rv-1' });
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('sends nothing when the same rate is typed in another form, and says so', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 2 } }));

      await type('usd', '321.50');
      press();

      expectNoRequest();
      expect(text('currency-saved')).toBe('There is nothing to save: no rate has changed.');
    });

    it('sends nothing when no box was touched', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 2 } }));

      press();

      expectNoRequest();
      expect(text('currency-saved')).toBe('There is nothing to save: no rate has changed.');
    });

    it('says Rates saved on the screen, in a status region, and tells the page to re-read, once', async () => {
      await render(group());
      const saved = vi.fn();
      fixture.componentInstance.saved.subscribe(saved);

      await type('gbp', '399.5');
      press();
      answerToken();
      controller
        .expectOne(AdminSettingsApi.currencyRatesUrl)
        .flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const message = element.querySelector('[data-testid="currency-saved"]') as HTMLElement;
      expect(message.textContent?.trim()).toBe('Rates saved.');
      expect(saved).toHaveBeenCalledTimes(1);

      // The same words reach a screen reader through the region that is always in the page.
      const region = element.querySelector('[data-testid="currency-announcement"]') as HTMLElement;
      expect(region.getAttribute('role')).toBe('status');
      expect(region.textContent?.trim()).toBe('Rates saved.');
    });

    it('is busy while the request is in flight, and a second press sends nothing more', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();

      const button = element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement;
      fixture.detectChanges();
      expect(button.getAttribute('aria-disabled')).toBe('true');
      expect(button.textContent?.trim()).toBe('Saving…');

      press();
      fixture.detectChanges();

      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      request.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(button.getAttribute('aria-disabled')).toBeNull();
      expect(button.textContent?.trim()).toBe('Save display currencies');
    });

    it('takes the saved message away as soon as the admin types again', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();
      controller
        .expectOne(AdminSettingsApi.currencyRatesUrl)
        .flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expect(text('currency-saved')).toBe('Rates saved.');

      await type('gbp', '400');

      expect(text('currency-saved')).toBeNull();
    });
  });

  describe('a rate the screen can already see is wrong', () => {
    const bad = ['1e3', '0', '1,000', '399,5', '-5', '+5', 'abc', '1.00001', '1000001', '.5', '5.', '1 000'];

    it.each(bad)('does not send %j, names the box and says why', async (value) => {
      await render(group());

      await type('gbp', value);
      press();

      expectNoRequest();
      expect(element.querySelector('[data-testid="currency-error-gbp"]')?.textContent?.trim().length).toBeGreaterThan(10);
      expect(box('gbp').getAttribute('aria-invalid')).toBe('true');
      expect(box('gbp').value).toBe(value);
    });

    it.each(DISPLAY_CURRENCIES.map((currency) => [currency.code, currency] as const))(
      'says what the API says for %s, word for word, for each kind of fault',
      async (_code, currency) => {
        await render(group());
        const said = (kind: keyof typeof sentences) =>
          sentences[kind].replace('{name}', currency.name).replace('{code}', currency.code);

        await type(currency.field, '1.00001');
        press();
        expect(text(`currency-error-${currency.field}`)).toBe(said('tooManyDecimals'));

        await type(currency.field, '0');
        press();
        expect(text(`currency-error-${currency.field}`)).toBe(said('outOfRange'));

        await type(currency.field, '1e3');
        press();
        expect(text(`currency-error-${currency.field}`)).toBe(said('notANumber'));
      },
    );

    it('keeps the capitals of US dollar and UAE dirham in the middle of its sentences', async () => {
      await render(group());

      await type('usd', 'x');
      await type('aed', 'y');
      press();

      expect(text('currency-error-usd')).toContain('Enter the US dollar rate');
      expect(text('currency-error-aed')).toContain('Enter the UAE dirham rate');
      expect(text('currency-error-aed')).toContain('Leave it empty to stop offering AED.');
    });

    it('moves focus to the first wrong box and lists the error with the box for a screen reader', async () => {
      await render(group());

      await type('usd', '321.5');
      await type('eur', 'x');
      await type('aud', 'y');
      press();

      expect(document.activeElement).toBe(box('eur'));
      expect(box('eur').getAttribute('aria-describedby')).toContain('currency-error-eur');
      expect(box('usd').getAttribute('aria-invalid')).toBeNull();
    });

    it('checks a box when the admin leaves it, before any save', async () => {
      await render(group());

      await type('aed', '87.123456');
      await leave('aed');

      expect(text('currency-error-aed')).toBe('Use at most 4 decimal places.');
    });

    it('does not check a box when focus is moving to Save: the error would push the button from under the pointer', async () => {
      await render(group());

      await type('aed', '87.123456');
      const save = element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement;
      box('aed').dispatchEvent(new FocusEvent('blur', { relatedTarget: save }));
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(element.querySelector('[data-testid="currency-error-aed"]')).toBeNull();

      // Pressing Save then runs the same check, so nothing is missed: the error arrives with the press.
      press();
      expectNoRequest();
      expect(text('currency-error-aed')).toBe('Use at most 4 decimal places.');
    });

    it('does check a box when focus is moving anywhere else', async () => {
      await render(group());

      await type('aed', '87.123456');
      const other = box('usd');
      box('aed').dispatchEvent(new FocusEvent('blur', { relatedTarget: other }));
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(text('currency-error-aed')).toBe('Use at most 4 decimal places.');
    });

    it('does not complain about an empty box: that means leave the currency out', async () => {
      await render(group({ aed: { value: '87.5', daysAgo: 2 } }));

      await type('aed', '');
      await leave('aed');

      expect(element.querySelector('[data-testid="currency-error-aed"]')).toBeNull();
      expect(text('currency-line-aed')).toBe('Saving with this box empty leaves AED out.');
    });

    it("clears a box's error the moment the admin types in it", async () => {
      await render(group());

      await type('gbp', '1e3');
      press();
      expect(element.querySelector('[data-testid="currency-error-gbp"]')).not.toBeNull();

      await type('gbp', '1e');

      expect(element.querySelector('[data-testid="currency-error-gbp"]')).toBeNull();
      expect(box('gbp').getAttribute('aria-invalid')).toBeNull();
    });

    it('sends nothing for a good box while another is wrong: the whole save waits', async () => {
      await render(group());

      await type('usd', '321.5');
      await type('gbp', 'x');
      press();

      expectNoRequest();
    });
  });

  describe('when the server refuses', () => {
    async function pressAndAnswer(
      status: number,
      body: Record<string, unknown>,
      statusText: string,
    ): Promise<void> {
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush(body, { status, statusText });
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    }

    it('shows its field error beside the box it names and keeps what was typed', async () => {
      await render(group());

      await type('gbp', '399.5');
      await type('usd', '321.5');
      await pressAndAnswer(
        400,
        { title: 'One or more fields are invalid.', errors: { gbp: ['Use at most 4 decimal places.'] } },
        'Bad Request',
      );

      expect(text('currency-error-gbp')).toBe('Use at most 4 decimal places.');
      expect(box('gbp').getAttribute('aria-invalid')).toBe('true');
      expect(box('gbp').value).toBe('399.5');
      expect(box('usd').value).toBe('321.5');
      expect(element.querySelector('[data-testid="currency-error"]')).toBeNull();
      expect(element.querySelector('[data-testid="currency-saved"]')).toBeNull();
    });

    it('on a stale version says so, keeps the typing, and offers the latest rates', async () => {
      await render(group());
      const reload = vi.fn();
      fixture.componentInstance.reload.subscribe(reload);

      await type('gbp', '399.5');
      await pressAndAnswer(
        409,
        { detail: 'Somebody else changed the display currency rates while you were editing them.', code: 'stale_row_version' },
        'Conflict',
      );

      const alert = element.querySelector('[data-testid="currency-error"]') as HTMLElement;
      expect(alert.getAttribute('role')).toBe('alert');
      expect(alert.textContent).toContain('Somebody else changed the display currency rates');
      expect(box('gbp').value).toBe('399.5');

      (element.querySelector('[data-testid="currency-reload"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(reload).toHaveBeenCalledTimes(1);

      // Tester round, minor 1: the conflict has been dealt with, so neither the alert nor its button stays behind.
      expect(element.querySelector('[data-testid="currency-error"]')).toBeNull();
      expect(element.querySelector('[data-testid="currency-reload"]')).toBeNull();
      expect(box('gbp').value).toBe('399.5');
    });

    it('says the admin may not change these, on a 403', async () => {
      await render(group());

      await type('gbp', '399.5');
      await pressAndAnswer(403, { title: 'You cannot change these settings' }, 'Forbidden');

      expect(text('currency-error')).toBe('You do not have permission to do that.');
      expect(element.querySelector('[data-testid="currency-reload"]')).toBeNull();
    });

    it('says the server could not be reached, and keeps the typing, when the connection fails', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).error(new ProgressEvent('error'), { status: 0 });
      fixture.detectChanges();

      expect(text('currency-error')).toContain('could not reach the server');
      expect(box('gbp').value).toBe('399.5');
    });

    it('can be pressed again after a failure', async () => {
      await render(group());

      await type('gbp', '399.5');
      await pressAndAnswer(500, { title: 'x' }, 'Server Error');
      expect(element.querySelector('[data-testid="currency-error"]')).not.toBeNull();

      press();
      answerToken();
      const retry = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(element.querySelector('[data-testid="currency-error"]')).toBeNull();
      retry.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  describe('when the page re-reads the settings', () => {
    it('keeps a box the admin has typed in and not saved, and refreshes the boxes they have not touched', async () => {
      await render(group({ usd: { value: '300', daysAgo: 2 }, gbp: { value: '399.5', daysAgo: 2 } }));

      await type('gbp', '410');
      await reread(group({ usd: { value: '305', daysAgo: 0 }, gbp: { value: '399.5', daysAgo: 2 } }, 'cur-rv-2'));

      expect(box('usd').value).toBe('305');
      expect(box('gbp').value).toBe('410');
    });

    it('stops treating a box as unsaved once the server holds what was typed', async () => {
      await render(group({ gbp: { value: '399.5', daysAgo: 2 } }));

      await type('gbp', '410');
      await reread(group({ gbp: { value: '410', daysAgo: 0 } }, 'cur-rv-2'));
      expect(box('gbp').value).toBe('410');

      await reread(group({ gbp: { value: '420', daysAgo: 0 } }, 'cur-rv-3'));
      expect(box('gbp').value).toBe('420');
    });

    it('sends the new row version on the next save, not the first one it was given', async () => {
      await render(group());

      await reread(group({}, 'cur-rv-2'));
      await type('gbp', '399.5');
      press();
      answerToken();

      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(request.request.body.rowVersion).toBe('cur-rv-2');
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('shows a rate that changed on the server with its new age and offers the new currency', async () => {
      await render(group());

      await reread(group({ aud: { value: '205', daysAgo: 0 } }, 'cur-rv-2'));

      expect(text('currency-line-aud')).toContain('Last changed today');
      expect(text('currency-offered')).toBe('Rates set: AUD.');
    });
  });

  /** A group with the named currencies' rows replaced by exactly what a (possibly broken) server could send. */
  function groupWith(
    rows: Partial<Record<CurrencyField, { key?: string; value?: unknown; updatedAtUtc?: unknown }>>,
  ): AdminSettingGroup {
    const base = group();

    return {
      ...base,
      settings: base.settings.map((setting) => {
        const currency = DISPLAY_CURRENCIES.find((candidate) => candidate.key === setting.key)!;
        const row = rows[currency.field];

        return row ? ({ ...setting, ...row, hasValue: row.value != null } as typeof setting) : setting;
      }),
    };
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  describe('the outcome of Save is under the button and in view (tester round, major 1)', () => {
    const noContent = { status: 204, statusText: 'No Content' };
    let scrollIntoView: ReturnType<typeof vi.fn>;
    const originalScroll = Element.prototype.scrollIntoView as unknown;
    const originalMatchMedia = window.matchMedia as unknown;

    beforeEach(() => {
      scrollIntoView = vi.fn();
      Element.prototype.scrollIntoView = scrollIntoView as unknown as typeof Element.prototype.scrollIntoView;
    });

    afterEach(() => {
      (Element.prototype as unknown as Record<string, unknown>)['scrollIntoView'] = originalScroll;
      (window as unknown as Record<string, unknown>)['matchMedia'] = originalMatchMedia;
    });

    function saveButton(): HTMLButtonElement {
      return element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement;
    }

    function follows(first: Element, second: Element): boolean {
      return (first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    }

    async function saveGoodRate(): Promise<void> {
      await type('gbp', '399.5');
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush(null, noContent);
      await settle();
    }

    async function failWith(status: number, body: Record<string, unknown>, statusText: string): Promise<void> {
      await type('gbp', '399.5');
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush(body, { status, statusText });
      await settle();
    }

    it('draws "Rates saved." after the Save button, in the same container, and below every box', async () => {
      await render(group());
      await saveGoodRate();

      const message = element.querySelector('[data-testid="currency-saved"]') as HTMLElement;
      expect(message).not.toBeNull();
      expect(saveButton().closest('.cur-save')).not.toBeNull();
      expect(message.closest('.cur-save')).toBe(saveButton().closest('.cur-save'));
      expect(follows(saveButton(), message)).toBe(true);
      expect(follows(box('aed'), message)).toBe(true);
    });

    it.each([
      [403, { title: 'No' }, 'Forbidden'],
      [409, { detail: 'Somebody else changed the currency rates while you were editing them.' }, 'Conflict'],
      [500, { title: 'x' }, 'Server Error'],
    ])('draws the %i alert after the Save button, in the same container', async (status, body, statusText) => {
      await render(group());
      await failWith(status, body, statusText);

      const alert = element.querySelector('[data-testid="currency-error"]') as HTMLElement;
      expect(alert).not.toBeNull();
      expect(alert.closest('.cur-save')).toBe(saveButton().closest('.cur-save'));
      expect(follows(saveButton(), alert)).toBe(true);
    });

    it('brings "Rates saved." into view, the least that shows it, smoothly', async () => {
      await render(group());
      await saveGoodRate();

      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest', behavior: 'smooth' });
      expect((scrollIntoView.mock.contexts[0] as HTMLElement).getAttribute('data-testid')).toBe('currency-saved');
    });

    it('does not animate the scroll for a visitor who asked for less motion', async () => {
      (window as unknown as Record<string, unknown>)['matchMedia'] = (query: string) => ({
        matches: query.includes('prefers-reduced-motion'),
        media: query,
      });
      await render(group());
      await saveGoodRate();

      expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest', behavior: 'auto' });
    });

    it.each([
      ['a 403', 403, { title: 'No' }, 'Forbidden'],
      ['a 409', 409, { detail: 'Somebody else changed the currency rates while you were editing them.' }, 'Conflict'],
      ['a 500', 500, { title: 'x' }, 'Server Error'],
      ['a 401', 401, {}, 'Unauthorized'],
    ])('brings the alert into view after %s', async (_name, status, body, statusText) => {
      await render(group());
      await failWith(status, body, statusText);

      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest', behavior: 'smooth' });
      expect((scrollIntoView.mock.contexts[0] as HTMLElement).getAttribute('data-testid')).toBe('currency-error');
    });

    it('brings the alert into view when the connection fails', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).error(new ProgressEvent('error'), { status: 0 });
      await settle();

      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      expect((scrollIntoView.mock.contexts[0] as HTMLElement).getAttribute('data-testid')).toBe('currency-error');
    });

    it('brings "nothing to save" into view too', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 2 } }));

      press();
      await settle();

      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      expect((scrollIntoView.mock.contexts[0] as HTMLElement).getAttribute('data-testid')).toBe('currency-saved');
    });

    it('does not scroll away from a box the screen has just focused for a wrong rate', async () => {
      await render(group());

      await type('eur', 'x');
      press();
      await settle();

      expect(document.activeElement).toBe(box('eur'));
      expect(scrollIntoView).not.toHaveBeenCalled();
    });

    it('does not scroll away from the box the server named either', async () => {
      await render(group());

      await type('aud', '205');
      press();
      answerToken();
      controller
        .expectOne(AdminSettingsApi.currencyRatesUrl)
        .flush({ errors: { aud: ['Use at most 4 decimal places.'] } }, { status: 400, statusText: 'Bad Request' });
      await settle();

      expect(document.activeElement).toBe(box('aud'));
      expect(scrollIntoView).not.toHaveBeenCalled();
    });

    /**
     * Round 2, survivor P7. The code does not scroll on input today, but nothing pinned it: a `reveal()` run on every
     * keystroke would drag the page back to the alert each time the admin typed, away from the box they are typing in.
     */
    it('does not scroll to the alert again for every character typed while it is on screen', async () => {
      await render(group());
      await failWith(500, { title: 'x' }, 'Server Error');

      expect(element.querySelector('[data-testid="currency-error"]')).not.toBeNull();
      expect(scrollIntoView).toHaveBeenCalledTimes(1);

      let typed = '';
      for (const character of '399123456789') {
        typed += character;
        await type('gbp', typed);
      }

      expect(typed.length).toBe(12);
      expect(element.querySelector('[data-testid="currency-error"]')).not.toBeNull();
      expect(scrollIntoView).toHaveBeenCalledTimes(1);
    });

    it('leaves the focus where it was when a save fails: it never moves it to the message', async () => {
      await render(group());

      await type('gbp', '399.5');
      saveButton().focus();
      expect(document.activeElement).toBe(saveButton());

      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush({ title: 'x' }, { status: 500, statusText: 'Server Error' });
      await settle();

      expect(element.querySelector('[data-testid="currency-error"]')).not.toBeNull();
      expect(document.activeElement).toBe(saveButton());
    });
  });

  describe('after "Load the latest rates" (round 2)', () => {
    const LOADED = 'The latest rates are loaded. What you typed is kept.';

    function saveButton(): HTMLButtonElement {
      return element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement;
    }

    /** A save refused with a conflict, and a keyboard user standing on the button that offers the latest rates. */
    async function conflictWithFocusOnReload(): Promise<HTMLButtonElement> {
      await render(group());
      await type('gbp', '399.5');
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush(
        { detail: 'Somebody else changed the currency rates while you were editing them.', code: 'stale_row_version' },
        { status: 409, statusText: 'Conflict' },
      );
      await settle();

      const button = element.querySelector('[data-testid="currency-reload"]') as HTMLButtonElement;
      button.focus();
      expect(document.activeElement).toBe(button);

      return button;
    }

    it('keeps keyboard focus on Save when the button that had it goes, instead of dropping it to the page', async () => {
      const button = await conflictWithFocusOnReload();

      button.click();
      await settle();

      expect(element.querySelector('[data-testid="currency-reload"]')).toBeNull();
      expect(document.activeElement).toBe(saveButton());
      expect(document.activeElement).not.toBe(document.body);
    });

    it('announces that the latest rates are loaded once the page has handed the panel the new group, and not before', async () => {
      const button = await conflictWithFocusOnReload();

      button.click();
      await settle();
      expect(text('currency-announcement')).toBe('');

      await reread(group({ usd: { value: '321', daysAgo: 0 } }, 'cur-rv-2'));

      expect(text('currency-announcement')).toBe(LOADED);
      expect(text('currency-saved')).toBe(LOADED);
      expect(box('usd').value).toBe('321');
      expect(box('gbp').value).toBe('399.5');
      expect(document.activeElement).toBe(saveButton());
    });

    it('says nothing of the kind for an ordinary re-read', async () => {
      await render(group());

      await reread(group({ usd: { value: '321', daysAgo: 0 } }, 'cur-rv-2'));

      expect(text('currency-announcement')).toBe('');
      expect(element.querySelector('[data-testid="currency-saved"]')).toBeNull();
    });

    it('does not announce it later for an unrelated re-read when the reload itself never brought a group', async () => {
      const button = await conflictWithFocusOnReload();

      button.click();
      await settle();

      // The page's fetch failed, so no group came. The admin goes on: types, saves, and the page re-reads after that.
      await type('gbp', '400');
      press();
      answerToken();
      controller
        .expectOne(AdminSettingsApi.currencyRatesUrl)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      await reread(group({ gbp: { value: '400', daysAgo: 0 } }, 'cur-rv-3'));

      expect(text('currency-announcement')).toBe('Rates saved.');
    });
  });

  describe('what is announced (tester round, minor 4)', () => {
    it('has the status region in the page before anything has happened', async () => {
      await render(group());

      const region = element.querySelector('[data-testid="currency-announcement"]') as HTMLElement;
      expect(region).not.toBeNull();
      expect(region.getAttribute('role')).toBe('status');
      expect(region.classList.contains('sr-only')).toBe(true);
      expect(region.textContent?.trim()).toBe('');
    });

    it('announces a second identical "nothing to save" again: the message is a new element, with the same words', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 2 } }));
      const region = element.querySelector('[data-testid="currency-announcement"]') as HTMLElement;

      press();
      await settle();
      const firstVisible = element.querySelector('[data-testid="currency-saved"]');
      const firstSpoken = region.querySelector('span');
      expect(firstVisible?.textContent?.trim()).toBe('There is nothing to save: no rate has changed.');

      press();
      await settle();
      const secondVisible = element.querySelector('[data-testid="currency-saved"]');
      const secondSpoken = region.querySelector('span');

      expect(secondVisible?.textContent?.trim()).toBe('There is nothing to save: no rate has changed.');
      expect(secondSpoken?.textContent?.trim()).toBe('There is nothing to save: no rate has changed.');
      expect(secondVisible).not.toBe(firstVisible);
      expect(secondSpoken).not.toBe(firstSpoken);
    });

    it('announces every "Rates saved." the same way, each as a new element', async () => {
      await render(group());
      const region = element.querySelector('[data-testid="currency-announcement"]') as HTMLElement;
      const spoken: Element[] = [];

      for (const rate of ['399.5', '400.5']) {
        await type('gbp', rate);
        press();
        answerToken();
        controller
          .expectOne(AdminSettingsApi.currencyRatesUrl)
          .flush(null, { status: 204, statusText: 'No Content' });
        await settle();

        spoken.push(region.querySelector('span')!);
        expect(spoken.at(-1)?.textContent?.trim()).toBe('Rates saved.');
      }

      expect(spoken[1]).not.toBe(spoken[0]);
    });
  });

  describe('typing while a save is in flight (tester round, minor 2)', () => {
    it('keeps what was typed again in the same box, and the re-read does not overwrite it', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();
      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);

      await type('gbp', '410');
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // The page re-reads: the server holds what was SENT, not what was typed afterwards.
      await reread(group({ gbp: { value: '399.5', daysAgo: 0 } }, 'cur-rv-2'));

      expect(box('gbp').value).toBe('410');
      expect(text('currency-line-gbp')).toContain('Unsaved change. The saved rate is 399.5.');
    });

    it('still counts a box as saved when the typing in flight was the same rate written another way', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();
      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);

      await type('gbp', '399.50');
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      await reread(group({ gbp: { value: '399.5', daysAgo: 0 } }, 'cur-rv-2'));

      expect(box('gbp').value).toBe('399.5');
    });

    it('does not lose a box typed in while the save was in flight that was not part of it', async () => {
      await render(group());

      await type('gbp', '399.5');
      press();
      answerToken();
      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);

      await type('usd', '321.5');
      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      await reread(group({ gbp: { value: '399.5', daysAgo: 0 } }, 'cur-rv-2'));

      expect(box('usd').value).toBe('321.5');
      expect(box('gbp').value).toBe('399.5');
    });
  });

  describe('answers the panel cannot place (tester round, minors 3 and 11)', () => {
    async function refuse(status: number, body: Record<string, unknown>, statusText: string): Promise<void> {
      await type('gbp', '399.5');
      press();
      answerToken();
      controller.expectOne(AdminSettingsApi.currencyRatesUrl).flush(body, { status, statusText });
      await settle();
    }

    it.each([
      ['a field it has no box for', { rowVersion: ['Bad.'] }],
      ['the right name in capitals', { GBP: ['Bad.'] }],
      ['no names at all', {}],
    ])('says so in words when a 400 names %s, rather than nothing', async (_name, errors) => {
      await render(group());

      await refuse(400, { title: 'One or more fields are invalid.', errors }, 'Bad Request');

      expect(text('currency-error')).toBe('The server did not accept these rates. Check each one and try again.');
      expect(box('gbp').value).toBe('399.5');
      expect(element.querySelector('[data-testid="currency-error-gbp"]')).toBeNull();
    });

    it('still shows the message beside the box when the 400 names one that exists, and no alert', async () => {
      await render(group());

      await refuse(400, { errors: { gbp: ['Use at most 4 decimal places.'], rowVersion: ['x'] } }, 'Bad Request');

      expect(text('currency-error-gbp')).toBe('Use at most 4 decimal places.');
      expect(element.querySelector('[data-testid="currency-error"]')).toBeNull();
    });

    it('says the session has ended on a 401, in the words the sign-in screens use', async () => {
      await render(group());

      await refuse(401, {}, 'Unauthorized');

      expect(text('currency-error')).toBe('Your session has ended. Please sign in again.');
      expect(box('gbp').value).toBe('399.5');
    });
  });

  describe('what the settings row holds (tester round, minors 10 and 12)', () => {
    const GARBAGE = ['abc', '0', '-5', '12345678901234567890123456789012', '1e3', '1,000'];

    it.each(GARBAGE)('shows the stored value %j as not valid, does not count it, and does not warn about it', async (value) => {
      await render(groupWith({ usd: { value, updatedAtUtc: '2026-01-01T00:00:00Z' } }));

      expect(box('usd').value).toBe(value);
      expect(text('currency-line-usd')).toBe('Stored value is not a valid rate.');
      expect(text('currency-offered')).toBe('No rates set yet.');
      expect(element.querySelector('[data-testid="currency-stale-usd"]')).toBeNull();
    });

    it('counts only the rates the feed would take, among garbage', async () => {
      await render(
        groupWith({
          usd: { value: 'abc', updatedAtUtc: '2026-10-20T00:00:00Z' },
          gbp: { value: '399.5', updatedAtUtc: '2026-10-20T00:00:00Z' },
          eur: { value: '0', updatedAtUtc: '2026-10-20T00:00:00Z' },
        }),
      );

      expect(text('currency-offered')).toBe('Rates set: GBP.');
    });

    it('does not send an untouched stored value back, and can clear it', async () => {
      await render(groupWith({ usd: { value: 'abc', updatedAtUtc: null }, gbp: { value: '399.5', updatedAtUtc: null } }));

      await type('gbp', '400');
      press();
      answerToken();
      const first = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(first.request.body).toEqual({ gbp: '400', rowVersion: 'cur-rv-1' });
      first.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      await reread(groupWith({ usd: { value: 'abc', updatedAtUtc: null }, gbp: { value: '400', updatedAtUtc: null } }));

      await type('usd', '');
      expect(text('currency-line-usd')).toBe('Saving with this box empty clears the stored value.');
      press();
      answerToken();
      const second = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(second.request.body).toEqual({ usd: '', rowVersion: 'cur-rv-1' });
      second.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('says what a stored value that was replaced was, once the admin types over it', async () => {
      await render(groupWith({ usd: { value: 'abc', updatedAtUtc: null } }));

      await type('usd', '321.5');

      expect(text('currency-line-usd')).toBe('Unsaved change. The stored value abc is not a valid rate.');
    });

    it('matches a row key without regard to case, as the server does', async () => {
      await render(
        groupWith({
          gbp: { key: 'currency.display.GBPLKRPERUNIT', value: '399.5', updatedAtUtc: '2026-10-23T08:00:00Z' },
        }),
      );

      expect(box('gbp').value).toBe('399.5');
      expect(text('currency-offered')).toBe('Rates set: GBP.');
      expect(text('currency-line-gbp')).toBe('Last changed yesterday (23 Oct 2026).');
    });

    it.each([
      ['a number', 399.5],
      ['a boolean', true],
      ['an object', { rate: 399.5 }],
      ['an array', ['399.5']],
    ])('does not take the screen down for a value that is %s: it is unreadable, not shown, not counted', async (_name, value) => {
      await render(groupWith({ usd: { value, updatedAtUtc: '2026-10-20T00:00:00Z' } }));

      expect(box('usd').value).toBe('');
      expect(text('currency-line-usd')).toBe('The stored value could not be read. Type a rate to replace it.');
      expect(text('currency-offered')).toBe('No rates set yet.');
      expect(element.querySelector('[data-testid="currency-stale-usd"]')).toBeNull();

      // The other boxes still work, and so does saving a rate over it.
      await type('usd', '321.5');
      press();
      answerToken();
      const request = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(request.request.body).toEqual({ usd: '321.5', rowVersion: 'cur-rv-1' });
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('draws five empty boxes for a group that came without its settings list', async () => {
      await render({ groupKey: 'Currency.Display', rowVersion: 'cur-rv-1' } as unknown as AdminSettingGroup);

      expect(element.querySelectorAll('[data-testid^="currency-field-"]').length).toBe(5);
      expect(text('currency-offered')).toBe('No rates set yet.');
    });
  });

  describe('the age as the one rule says it (tester round, minor 7 and survivors M13, M30)', () => {
    it('says "just now", with no date, for a stamp ahead of this computer clock, and never warns', async () => {
      await render(groupWith({ gbp: { value: '399.5', updatedAtUtc: '2026-10-25T08:00:00.000Z' } }));

      expect(text('currency-line-gbp')).toBe('Last changed just now.');
      expect(element.querySelector('[data-testid="currency-line-gbp"]')?.getAttribute('title')).toBeNull();
      expect(element.querySelector('[data-testid="currency-stale-gbp"]')).toBeNull();
      expect(text('currency-line-gbp')).not.toMatch(/2026|Oct/);
    });

    it('prints no day count and no date for a stamp that is a default value (year 1)', async () => {
      await render(groupWith({ gbp: { value: '399.5', updatedAtUtc: '0001-01-01T00:00:00Z' } }));

      expect(text('currency-line-gbp')).toBe('No change date recorded.');
      expect(text('currency-line-gbp')).not.toMatch(/\d{4}|days/);
      expect(element.querySelector('[data-testid="currency-stale-gbp"]')).toBeNull();
    });

    it('says 31 days ago, and warns, for a rate 30 days 23 hours old: the words and the warning agree', async () => {
      const stamp = new Date(NOW.getTime() - (30 * 24 + 23) * 3_600_000).toISOString();
      await render(groupWith({ gbp: { value: '399.5', updatedAtUtc: stamp } }));

      expect(text('currency-line-gbp')).toBe('Last changed 31 days ago (23 Sep 2026).');
      expect(text('currency-stale-gbp')).toBe('This rate has not changed for 31 days. Check it is still right.');
    });

    it('says yesterday for a rate changed just before Colombo midnight, though it is only 13 hours old', async () => {
      // NOW is 13:30 on 24 Oct in Colombo. 18:29 UTC on the 23rd is 23:59 on the 23rd there: yesterday, 13.5 hours old.
      await render(groupWith({ gbp: { value: '399.5', updatedAtUtc: '2026-10-23T18:29:00.000Z' } }));

      expect(text('currency-line-gbp')).toBe('Last changed yesterday (23 Oct 2026).');
    });

    it('reads the date on the shop calendar, Asia/Colombo, not the UTC one (a stamp at 19:16 UTC)', async () => {
      // 19:16 UTC on the 23rd is 00:46 on the 24th in Colombo: the date is the 24th and it is "today". Read in UTC it
      // would be the 23rd and "yesterday".
      await render(groupWith({ gbp: { value: '399.5', updatedAtUtc: '2026-10-23T19:16:00.000Z' } }));

      expect(text('currency-line-gbp')).toBe('Last changed today (24 Oct 2026).');
      expect(element.querySelector('[data-testid="currency-line-gbp"]')?.getAttribute('title')).toBe('today, on 24 Oct 2026');
    });
  });
});
