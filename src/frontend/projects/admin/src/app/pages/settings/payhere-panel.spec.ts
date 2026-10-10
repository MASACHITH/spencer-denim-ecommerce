import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import {
  AdminSetting,
  AdminSettingGroup,
  PayHereStatus,
  SETTING_GROUPS,
  SETTING_KEYS,
} from '../../core/settings/admin-settings.model';
import { PayHerePanel, toPayHereForm } from './payhere-panel';

/**
 * The PayHere panel of the settings screen (doc 11 P8b; `Settings.Payments`).
 *
 * **The merchant secret is the subject.** Every test that touches it asserts the same three things from a
 * different side: the server's payload never carries one, the box never shows one, and what goes on the wire
 * is the secret only when someone typed it. A fixture that holds a *real-looking* saved value in every other
 * field is deliberate - a panel that dropped a field would otherwise round-trip empty to empty and pass.
 */
describe('PayHerePanel (doc 11 P8b)', () => {
  const ANTIFORGERY = '/api/admin/auth/antiforgery-token';

  let controller: HttpTestingController;
  let fixture: ComponentFixture<PayHerePanel>;
  let element: HTMLElement;
  let saved: number;

  function setting(key: string, value: string | null, overrides: Partial<AdminSetting> = {}): AdminSetting {
    return {
      key,
      valueType: 'String',
      value,
      isSecret: false,
      hasValue: value !== null,
      requiredPermission: 'Settings.Payments',
      description: `What ${key} is for.`,
      updatedAtUtc: null,
      ...overrides,
    };
  }

  /** The group as the server sends it: the secret is a flag and never a value. */
  function group(
    options: { secretSet?: boolean; rowVersion?: string; values?: Record<string, string | null> } = {},
  ): AdminSettingGroup {
    const values: Record<string, string | null> = {
      [SETTING_KEYS.payHereMode]: 'live',
      [SETTING_KEYS.payHereMerchantId]: '1211149',
      [SETTING_KEYS.payHerePendingMaxDays]: '4',
      [SETTING_KEYS.payHereExpiryMinutes]: '90',
      ...options.values,
    };

    return {
      groupKey: SETTING_GROUPS.payHere,
      rowVersion: options.rowVersion ?? 'payhere-rv-1',
      settings: [
        setting(SETTING_KEYS.payHereMode, values[SETTING_KEYS.payHereMode]),
        setting(SETTING_KEYS.payHereMerchantId, values[SETTING_KEYS.payHereMerchantId]),
        setting(SETTING_KEYS.payHereMerchantSecret, null, {
          valueType: 'Secret',
          isSecret: true,
          hasValue: options.secretSet ?? true,
        }),
        setting(SETTING_KEYS.payHerePendingMaxDays, values[SETTING_KEYS.payHerePendingMaxDays], {
          valueType: 'Int',
        }),
        setting(SETTING_KEYS.payHereExpiryMinutes, values[SETTING_KEYS.payHereExpiryMinutes], {
          valueType: 'Int',
        }),
      ],
    };
  }

  function status(overrides: Partial<PayHereStatus> = {}): PayHereStatus {
    return {
      notifyUrl: 'https://shop.example.test/api/payments/payhere/notify',
      providerEnabled: true,
      offeredAtCheckout: true,
      mode: 'live',
      problems: [],
      ...overrides,
    };
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function open(
    settings: AdminSettingGroup = group(),
    current: PayHereStatus | 'fail' = status(),
  ): Promise<void> {
    fixture = TestBed.createComponent(PayHerePanel);
    element = fixture.nativeElement as HTMLElement;
    saved = 0;

    fixture.componentRef.setInput('group', settings);
    fixture.componentInstance.saved.subscribe(() => saved++);
    fixture.detectChanges();

    const request = controller.expectOne(AdminSettingsApi.payHereStatusUrl);

    if (current === 'fail') {
      request.flush({ title: 'no' }, { status: 500, statusText: 'Server Error' });
    } else {
      request.flush(current);
    }

    await settle();
  }

  function field(name: string): HTMLInputElement {
    return element.querySelector(`[data-testid="field-payhere-${name}"]`) as HTMLInputElement;
  }

  function byTestId(id: string): HTMLElement | null {
    return element.querySelector(`[data-testid="${id}"]`);
  }

  async function type(name: string, value: string): Promise<void> {
    const input = field(name);
    input.value = value;
    input.dispatchEvent(new Event(input.tagName === 'SELECT' ? 'change' : 'input'));
    await settle();
  }

  async function save(): Promise<void> {
    element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  function writeRequest(): TestRequest {
    controller.expectOne(ANTIFORGERY).flush({ token: 'xsrf-token' });

    return controller.expectOne(AdminSettingsApi.payHereUrl);
  }

  /** Answers a successful save, and the status re-read that every successful save triggers. */
  async function finish(request: TestRequest): Promise<void> {
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    controller.expectOne(AdminSettingsApi.payHereStatusUrl).flush(status());
    await settle();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [PayHerePanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  // ---- what is shown -----------------------------------------------------------------------------

  it('fills the form from the group, and the secret box is empty with a note that one is saved', async () => {
    await open();

    expect(field('mode').value).toBe('live');
    expect(field('merchantId').value).toBe('1211149');
    expect(field('pendingMaxDays').value).toBe('4');
    expect(field('expiryMinutes').value).toBe('90');

    const secret = field('merchantSecret');

    expect(secret.type).toBe('password');
    expect(secret.value).toBe('');
    expect(secret.placeholder).toContain('A secret is saved');
    expect(byTestId('payhere-secret-set')).not.toBeNull();
    expect(byTestId('payhere-secret-unset')).toBeNull();
  });

  it('says no secret is saved when there is none', async () => {
    await open(group({ secretSet: false }));

    expect(byTestId('payhere-secret-unset')).not.toBeNull();
    expect(field('merchantSecret').placeholder).not.toContain('A secret is saved');
  });

  it('never puts a secret anywhere in the markup', async () => {
    await open();

    // The payload carries a flag, so there is nothing to render - the assertion is that it stays that way.
    expect(element.innerHTML).not.toMatch(/CfDJ8|ciphertext/i);
    expect(field('merchantSecret').getAttribute('value')).toBeNull();
    expect(field('merchantSecret').autocomplete).toBe('new-password');
  });

  it('shows the notify address to register, with the domain-approval reminder', async () => {
    await open();

    expect((byTestId('payhere-notify-url') as HTMLInputElement).value).toBe(
      'https://shop.example.test/api/payments/payhere/notify',
    );
    expect((byTestId('payhere-notify-url') as HTMLInputElement).readOnly).toBe(true);
    expect(byTestId('payhere-copy-notify-url')).not.toBeNull();

    const reminder = byTestId('payhere-domain-reminder')?.textContent ?? '';

    expect(reminder).toContain('domain');
    expect(reminder).toContain('approve');
    expect(reminder).toContain('internet');
  });

  it('says there is no address to give PayHere when the server does not know its public address', async () => {
    await open(group(), status({ notifyUrl: null, offeredAtCheckout: false, problems: ['Public address missing.'] }));

    expect(byTestId('payhere-notify-url')).toBeNull();
    expect(byTestId('payhere-no-notify-url')?.textContent).toContain('Email__StorefrontBaseUrl');
  });

  it('tells the admin whether PayHere is offered, what is missing, and that the switch is not on this screen', async () => {
    await open(
      group(),
      status({
        providerEnabled: false,
        offeredAtCheckout: false,
        problems: ['The merchant secret is not set.'],
      }),
    );

    expect(byTestId('payhere-not-offered')).not.toBeNull();
    expect(byTestId('payhere-offered')).toBeNull();
    expect(byTestId('payhere-switched-off')?.textContent).toContain('no switch on this screen');
    expect(byTestId('payhere-problems')?.textContent).toContain('The merchant secret is not set.');
  });

  it('says PayHere is offered when it is', async () => {
    await open();

    expect(byTestId('payhere-offered')).not.toBeNull();
    expect(byTestId('payhere-switched-off')).toBeNull();
    expect(byTestId('payhere-problems')).toBeNull();
  });

  it('still shows the form when the status could not be read', async () => {
    await open(group(), 'fail');

    expect(byTestId('payhere-status')).toBeNull();
    expect(field('merchantId').value).toBe('1211149');
  });

  // ---- what goes on the wire ---------------------------------------------------------------------

  it('sends no secret at all when none was typed, so the saved one is kept', async () => {
    await open();

    await save();

    const request = writeRequest();
    const body = request.request.body as Record<string, unknown>;

    expect(Object.keys(body).sort()).toEqual(['expiryMinutes', 'merchantId', 'mode', 'pendingMaxDays', 'rowVersion']);
    expect(body['rowVersion']).toBe('payhere-rv-1');
    expect(body['mode']).toBe('live');
    expect(body['merchantId']).toBe('1211149');
    expect(body['pendingMaxDays']).toBe('4');
    expect(body['expiryMinutes']).toBe('90');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');

    await finish(request);
  });

  it('sends a blank secret as no secret', async () => {
    await open();
    await type('merchantSecret', '   ');

    await save();

    const request = writeRequest();

    expect(Object.keys(request.request.body as object)).not.toContain('merchantSecret');

    await finish(request);
  });

  it('sends a typed secret once, empties the box, and tells the page to re-read', async () => {
    await open();

    await type('merchantSecret', 'a-typed-secret');
    await type('mode', 'sandbox');
    await type('expiryMinutes', '120');

    await save();

    const request = writeRequest();
    const body = request.request.body as Record<string, unknown>;

    expect(body['merchantSecret']).toBe('a-typed-secret');
    expect(body['mode']).toBe('sandbox');
    expect(body['expiryMinutes']).toBe('120');

    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    const reRead = controller.expectOne(AdminSettingsApi.payHereStatusUrl);

    // The secret left the component with the request.
    expect(field('merchantSecret').value).toBe('');
    expect(element.innerHTML).not.toContain('a-typed-secret');

    expect(saved).toBe(1);
    expect(byTestId('payhere-announcement')?.textContent).toContain('PayHere settings saved');

    // And the status is read again, because a save can change whether PayHere is offered.
    reRead.flush(status());
  });

  it('does not send a second write while one is in flight', async () => {
    await open();

    await save();
    const request = writeRequest();

    await save();
    await save();

    controller.expectNone(ANTIFORGERY);
    controller.expectNone(AdminSettingsApi.payHereUrl);

    expect(element.querySelector('[data-testid="save-payhere"]')?.getAttribute('aria-disabled')).toBe('true');

    await finish(request);
  });

  // ---- refusals ----------------------------------------------------------------------------------

  it('puts each validation message beside its own field and keeps what was typed', async () => {
    await open();

    await type('merchantSecret', 'a-typed-secret');
    await type('expiryMinutes', '5');

    await save();

    writeRequest().flush(
      {
        title: 'One or more fields are invalid.',
        errors: { expiryMinutes: ['Enter a whole number of minutes between 15 and 1440.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(byTestId('error-payhere-expiryMinutes')?.textContent).toContain('between 15 and 1440');
    expect(byTestId('error-payhere-mode')).toBeNull();
    expect(field('expiryMinutes').getAttribute('aria-invalid')).toBe('true');
    expect(field('expiryMinutes').value).toBe('5');
    expect(field('merchantSecret').value).toBe('a-typed-secret');
    expect(saved).toBe(0);

    // Typing in the field clears its message.
    await type('expiryMinutes', '45');
    expect(byTestId('error-payhere-expiryMinutes')).toBeNull();
  });

  it('shows a conflict as an alert and does not claim to have saved', async () => {
    await open();

    await save();

    writeRequest().flush(
      { title: 'This change cannot be applied', detail: 'Reload the page.', code: 'stale_row_version' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    expect(byTestId('payhere-error')).not.toBeNull();
    expect(saved).toBe(0);
    expect(byTestId('payhere-announcement')?.textContent?.trim()).toBe('');
  });

  it('shows a forbidden answer as an alert', async () => {
    await open();

    await save();

    writeRequest().flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(byTestId('payhere-error')?.textContent).toContain('permission');
  });

  // ---- the mapping -------------------------------------------------------------------------------

  it('builds the form from the group, with an empty secret whatever the group holds', () => {
    expect(toPayHereForm(group())).toEqual({
      mode: 'live',
      merchantId: '1211149',
      merchantSecret: '',
      pendingMaxDays: '4',
      expiryMinutes: '90',
    });
  });

  it('falls back to sandbox and empty boxes for a group with nothing in it', () => {
    expect(toPayHereForm({ groupKey: SETTING_GROUPS.payHere, rowVersion: 'rv', settings: [] })).toEqual({
      mode: 'sandbox',
      merchantId: '',
      merchantSecret: '',
      pendingMaxDays: '',
      expiryMinutes: '',
    });
  });
});
