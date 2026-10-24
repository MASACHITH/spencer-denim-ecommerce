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
        // The name starts the label, so it is capitalised there; in the middle of a sentence it is as the API writes it.
        expect(label?.textContent).toContain(currency.name.charAt(0).toUpperCase() + currency.name.slice(1));
        expect(label?.textContent).toContain(`(${currency.code})`);
      }
    });

    it('explains the number, what shoppers see, and that PayPal and the rates are not automatic', async () => {
      await render(group());

      const body = element.textContent ?? '';
      expect(body).toContain('399.50 for the pound means one pound costs 399.50 rupees');
      expect(body).toContain('marked as approximate');
      expect(body).toContain('PayPal still charges the rupee total');
      expect(body).toContain('typed by hand and are never updated automatically');
    });

    it('shows the saved rates and names the currencies shoppers can choose', async () => {
      await render(group({ usd: { value: '321.5', daysAgo: 1 }, gbp: { value: '399.5', daysAgo: 1 } }));

      expect(box('usd').value).toBe('321.5');
      expect(box('gbp').value).toBe('399.5');
      expect(box('eur').value).toBe('');
      expect(text('currency-offered')).toBe('Shoppers can choose to see prices in: USD, GBP.');
    });

    it('says plainly when nothing is offered, and that an empty box is not shown to shoppers', async () => {
      await render(group());

      expect(text('currency-offered')).toBe('No currency is offered to shoppers yet. Type a rate to offer one.');
      expect(text('currency-line-aud')).toBe('Not shown to shoppers until a rate is entered.');
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

      expect(text('currency-line-aed')).toBe('Saved.');
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
      expect(message.getAttribute('role')).toBe('status');
      expect(saved).toHaveBeenCalledTimes(1);
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

    it('does not complain about an empty box: that means stop offering it', async () => {
      await render(group({ aed: { value: '87.5', daysAgo: 2 } }));

      await type('aed', '');
      await leave('aed');

      expect(element.querySelector('[data-testid="currency-error-aed"]')).toBeNull();
      expect(text('currency-line-aed')).toBe('Saving with this box empty stops offering AED to shoppers.');
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
      expect(reload).toHaveBeenCalledTimes(1);
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
      expect(text('currency-offered')).toBe('Shoppers can choose to see prices in: AUD.');
    });
  });
});
