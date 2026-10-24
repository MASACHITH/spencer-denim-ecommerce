import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import { PaymentProvidersApi } from '../../core/settings/payment-providers-api';
import { AdminSettings, SETTING_KEYS } from '../../core/settings/admin-settings.model';
import {
  SettingsPage,
  formatStamp,
  toForm,
  toPayPalForm,
  toProductPageForm,
  toRequireApproval,
  toTrafficForm,
} from './settings-page';

/**
 * ADM-22 and GLB-02: the settings screen.
 *
 * **The outgoing request bodies are the heart of this file.** W1-12's blocker was a form model that had never
 * listed three columns, so every save cleared them — and 1,571 passing backend tests could not see it, because
 * the API stores whatever it is handed and the thing that was wrong was what the *client* sent. The two tests
 * that caught it were frontend tests asserting the PUT body. These are the same tests for this screen.
 */
describe('SettingsPage (ADM-22)', () => {
  let controller: HttpTestingController;
  let fixture: ComponentFixture<SettingsPage>;
  let element: HTMLElement;

  /**
   * The payload a freshly seeded database produces: every value null.
   *
   * Overridden per test where a value matters — and where it does, **every** field is non-null, because a
   * fixture with nulls in it lets a dropped field round-trip null to null unnoticed.
   */
  function payload(values: Partial<Record<string, string | null>> = {}, logo: AdminSettings['logo'] = null): AdminSettings {
    const value = (key: string) => values[key] ?? null;

    const setting = (key: string, valueType: 'String' | 'MediaAssetRef' = 'String') => ({
      key,
      valueType,
      value: value(key),
      isSecret: false,
      hasValue: Boolean(value(key)),
      requiredPermission: 'Settings.Edit',
      description: `What ${key} is for.`,
      updatedAtUtc: null,
    });

    return {
      groups: [
        { groupKey: 'Store', rowVersion: 'store-rv-1', settings: [setting(SETTING_KEYS.logoAssetId, 'MediaAssetRef')] },
        { groupKey: 'Contact', rowVersion: 'form-rv-1', settings: [setting(SETTING_KEYS.whatsApp)] },
        {
          groupKey: 'Social',
          rowVersion: 'form-rv-1',
          settings: [
            setting(SETTING_KEYS.facebookUrl),
            setting(SETTING_KEYS.instagramUrl),
            setting(SETTING_KEYS.tikTokUrl),
            setting(SETTING_KEYS.youTubeUrl),
          ],
        },
      ],
      logo,
    };
  }

  /**
   * The same, plus the `Bank` group — which needs `Settings.Payments` and so is **absent** from the
   * payload most admins receive.
   *
   * Every bank value is non-null, because a fixture with nulls in it lets a dropped field round-trip null
   * to null unnoticed, and the account number is the field where that costs the client real money.
   */
  function payloadWithBank(values: Partial<Record<string, string | null>> = {}): AdminSettings {
    const bank = {
      [SETTING_KEYS.bankName]: 'Example Bank PLC',
      [SETTING_KEYS.branch]: 'Example Branch',
      [SETTING_KEYS.accountName]: 'Example Shop (Pvt) Ltd',
      [SETTING_KEYS.accountNumber]: '0000 1111 2222',
      [SETTING_KEYS.instructions]: 'CDM deposits take a working day.',
      ...values,
    } as Record<string, string | null>;

    const base = payload(values);

    return {
      ...base,
      groups: [
        ...base.groups,
        {
          groupKey: 'Bank',
          rowVersion: 'bank-rv-1',
          settings: Object.keys(bank).map((key) => ({
            key,
            valueType: 'String' as const,
            value: bank[key] ?? null,
            isSecret: false,
            hasValue: Boolean(bank[key]),
            requiredPermission: 'Settings.Payments',
            description: `What ${key} is for.`,
            updatedAtUtc: null,
          })),
        },
      ],
    };
  }


  /**
   * The ordinary payload plus the `Payments.PayPal` group, which needs `Settings.Payments`.
   *
   * **Every value is non-null and the secret has no value at all**, as the server sends it: `value` is always
   * null for a secret and `hasValue` is the only thing it says. `expiryMinutes` is doc 08's
   * `Orders.PayPalExpiryMinutes` (`Settings.Edit`), carried in the `Orders` group when asked for.
   */
  function payloadWithPayPal(
    values: Partial<Record<string, string | null>> = {},
    options: { secretSet?: boolean; withExpiry?: boolean } = {},
  ): AdminSettings {
    const { secretSet = true, withExpiry = true } = options;

    const paypal = {
      [SETTING_KEYS.payPalMode]: 'sandbox',
      [SETTING_KEYS.payPalClientId]: 'client-id-123',
      [SETTING_KEYS.payPalWebhookId]: 'WEBHOOK-ID-9',
      [SETTING_KEYS.payPalLkrPerUsd]: '300.00',
      [SETTING_KEYS.payPalFxBufferPercent]: '3.00',
      [SETTING_KEYS.payPalRateMaxAgeDays]: '7',
      [SETTING_KEYS.payPalRateUpdatedAtUtc]: new Date(Date.now() - 86_400_000).toISOString(),
      [SETTING_KEYS.payPalCaptureLockHours]: '3',
      [SETTING_KEYS.payPalCaptureRecoveryMaxHours]: '24',
      [SETTING_KEYS.payPalPendingCaptureMaxDays]: '5',
      ...values,
    } as Record<string, string | null>;

    const base = payload();

    const row = (key: string, permission: string) => ({
      key,
      valueType: 'String' as const,
      value: paypal[key] ?? null,
      isSecret: false,
      hasValue: Boolean(paypal[key]),
      requiredPermission: permission,
      description: `What ${key} is for.`,
      updatedAtUtc: null,
    });

    const groups = [
      ...base.groups,
      {
        groupKey: 'Payments.PayPal',
        rowVersion: 'paypal-rv-1',
        settings: [
          ...Object.keys(paypal).map((key) => row(key, 'Settings.Payments')),

          // The secret: never a value, whoever asks. `hasValue` is all the screen can learn.
          {
            key: SETTING_KEYS.payPalClientSecret,
            valueType: 'Secret' as const,
            value: null,
            isSecret: true,
            hasValue: secretSet,
            requiredPermission: 'Settings.Payments',
            description: 'The secret.',
            updatedAtUtc: null,
          },
        ],
      },
    ];

    if (withExpiry) {
      groups.push({
        groupKey: 'Orders',
        rowVersion: 'store-rv-1',
        settings: [
          {
            key: SETTING_KEYS.payPalExpiryMinutes,
            valueType: 'Int' as never,
            value: values[SETTING_KEYS.payPalExpiryMinutes] ?? '60',
            isSecret: false,
            hasValue: true,
            requiredPermission: 'Settings.Edit',
            description: 'How long to pay.',
            updatedAtUtc: null,
          },
        ],
      });
    }

    return { ...base, groups };
  }

  /**
   * The ordinary payload plus the `Orders` group and the four `Store.*` legal keys, which is what ADM-09's
   * invoice form needs.
   *
   * **Every value is non-null**, because a fixture of nulls lets a dropped field round-trip null to null
   * unnoticed — the reason W1-12's blocker survived 1,571 passing tests — and a registered address is a
   * field where losing it costs the client a legal document.
   */
  function payloadWithInvoice(values: Partial<Record<string, string | null>> = {}): AdminSettings {
    const invoice = {
      [SETTING_KEYS.legalName]: 'A Registered Company (Pvt) Ltd',
      [SETTING_KEYS.storeAddress]: 'A registered address\nA city',
      [SETTING_KEYS.brNumber]: 'BR-0000',
      [SETTING_KEYS.vatNumber]: 'VAT-0000',
      [SETTING_KEYS.autoDeliverAfterDays]: '7',
      ...values,
    } as Record<string, string | null>;

    const base = payload(values);
    const legalKeys = [
      SETTING_KEYS.legalName,
      SETTING_KEYS.storeAddress,
      SETTING_KEYS.brNumber,
      SETTING_KEYS.vatNumber,
    ];

    return {
      ...base,
      groups: [
        // The legal keys join the group the logo is already in, which is what the server sends: one
        // group, one save unit, one token.
        {
          ...base.groups[0],
          settings: [
            ...base.groups[0].settings,
            ...legalKeys.map((key) => ({
              key,
              valueType: 'String' as const,
              value: invoice[key] ?? null,
              isSecret: false,
              hasValue: Boolean(invoice[key]),
              requiredPermission: 'Settings.Edit',
              description: `What ${key} is for.`,
              updatedAtUtc: null,
            })),
          ],
        },
        ...base.groups.slice(1),
        {
          groupKey: 'Orders',

          // The same token the Store group publishes, because they are one save unit
          // (SettingCatalog.SaveUnits). Publishing two would be ADM-22's 409-on-every-save.
          rowVersion: base.groups[0].rowVersion,
          settings: [
            {
              key: SETTING_KEYS.autoDeliverAfterDays,
              valueType: 'Int' as const,
              value: invoice[SETTING_KEYS.autoDeliverAfterDays] ?? null,
              isSecret: false,
              hasValue: Boolean(invoice[SETTING_KEYS.autoDeliverAfterDays]),
              requiredPermission: 'Settings.Edit',
              description: 'How long before an order is closed.',
              updatedAtUtc: null,
            },
          ],
        },
      ],
    };
  }

  /** Presses ADM-09's own Save, which is not the first form on the page. */
  async function clickSaveInvoice(): Promise<void> {
    (element.querySelector('[data-testid="save-invoice"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(settings: AdminSettings = payload()): Promise<void> {
    fixture = TestBed.createComponent(SettingsPage);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    controller.expectOne(AdminSettingsApi.settingsUrl).flush(settings);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function field(name: string): HTMLInputElement {
    return element.querySelector(`[data-testid="field-${name}"]`) as HTMLInputElement;
  }

  /**
   * Types into a field the way a person does, and waits.
   *
   * `await fixture.whenStable()` is not decoration: these are one-way `[ngModel]` bindings with
   * `(ngModelChange)`, and `NgModel` writes the model back into the view on a microtask. Without the wait the
   * next assertion reads the input before Angular has settled — which is how the first version of this file
   * reported `null` for a value the screen had taken perfectly well.
   */
  async function type(name: string, value: string): Promise<void> {
    const input = field(name);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function save(): Promise<void> {
    element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  /** Answers the antiforgery fetch every mutating call makes, then returns the write request. */
  function writeTo(url: string) {
    controller.expectOne(AdminAuthApiUrl).flush({ token: 'xsrf-token' });

    return controller.expectOne(url);
  }

  const AdminAuthApiUrl = '/api/admin/auth/antiforgery-token';

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SettingsPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The Payment methods panel (owner decision 2026-10-21) fetches for itself wherever the bank panel is
    // drawn; its own spec covers it, so its read is set aside here rather than answered in every bank test.
    controller.match(PaymentProvidersApi.url);
    controller.verify();
  });

  // ---- the read ---------------------------------------------------------------------------------

  it('shows both panels when the API returns both groups', async () => {
    await open();

    expect(element.querySelector('[data-testid="logo-empty"]')).not.toBeNull();
    expect(field('whatsApp')).not.toBeNull();
    expect(element.querySelector('[data-testid="nothing-to-edit"]')).toBeNull();
  });

  /**
   * A group the caller cannot read is absent from the payload, so the screen asks the payload rather than a
   * permission list — which is what stops it disagreeing with the server about who may do what.
   */
  it('hides the logo panel when the API did not send the Store group', async () => {
    const without = payload();
    await open({ ...without, groups: without.groups.filter((group) => group.groupKey !== 'Store') });

    expect(element.querySelector('[data-testid="logo-empty"]')).toBeNull();
    expect(element.querySelector('[data-testid="logo-file"]')).toBeNull();
    expect(field('whatsApp')).not.toBeNull();
  });

  /**
   * The screen and `toForm` have to agree about what it is writing. A box for a group the server did not send
   * is a box the admin can type into and never save — and, before the omission fix, one whose empty value would
   * have cleared the client's value on the next save.
   */
  it('renders no social boxes when the API did not send the Social group', async () => {
    const without = payload();
    await open({ ...without, groups: without.groups.filter((group) => group.groupKey !== 'Social') });

    expect(field('whatsApp')).not.toBeNull();
    expect(element.querySelector('[data-testid="field-facebookUrl"]')).toBeNull();
    expect(element.querySelector('[data-testid="field-instagramUrl"]')).toBeNull();
    expect(element.querySelector('[data-testid="field-tikTokUrl"]')).toBeNull();
  });

  /**
   * And the save body omits them, so the API leaves those keys alone. This is the end-to-end version of the
   * `toForm` omission test: the W1-12 shape is about what reaches the wire.
   */
  it('omits the social fields from the save when the API did not send that group', async () => {
    const without = payload();
    await open({ ...without, groups: without.groups.filter((group) => group.groupKey !== 'Social') });

    await type('whatsApp', '94771234567');
    await save();

    const body = writeTo(AdminSettingsApi.contactSocialUrl).request.body as Record<string, unknown>;

    expect(Object.keys(body).sort()).toEqual(['rowVersion', 'whatsApp']);
  });

  it('renders no WhatsApp box when the API did not send the Contact group', async () => {
    const without = payload();
    await open({ ...without, groups: without.groups.filter((group) => group.groupKey !== 'Contact') });

    expect(element.querySelector('[data-testid="field-whatsApp"]')).toBeNull();
    expect(element.querySelector('[data-testid="field-facebookUrl"]')).not.toBeNull();
  });

  it('says there is nothing to edit when no group came back at all', async () => {
    await open({ groups: [], logo: null });

    expect(element.querySelector('[data-testid="nothing-to-edit"]')).not.toBeNull();
  });

  it('fills the form from the values the API sent', async () => {
    await open(
      payload({
        [SETTING_KEYS.whatsApp]: '94771234567',
        [SETTING_KEYS.facebookUrl]: 'https://www.facebook.com/shop',
        [SETTING_KEYS.instagramUrl]: 'https://www.instagram.com/shop',
        [SETTING_KEYS.tikTokUrl]: 'https://www.tiktok.com/@shop',
        [SETTING_KEYS.youTubeUrl]: 'https://www.youtube.com/@shop',
      }),
    );

    expect(field('youTubeUrl').value).toBe('https://www.youtube.com/@shop');
    expect(field('whatsApp').value).toBe('94771234567');
    expect(field('facebookUrl').value).toBe('https://www.facebook.com/shop');
    expect(field('instagramUrl').value).toBe('https://www.instagram.com/shop');
    expect(field('tikTokUrl').value).toBe('https://www.tiktok.com/@shop');
  });

  it('shows the logo and warns when it has no alternative text', async () => {
    await open(
      payload({}, {
        publicId: 'abc',
        url: '/media/logo.webp',
        width: 600,
        height: 150,
        altText: null,
        originalFileName: 'logo.png',
      }),
    );

    const preview = element.querySelector<HTMLImageElement>('[data-testid="logo-preview"] img');
    expect(preview?.getAttribute('src')).toBe('/media/logo.webp');

    // A public image needs alt text before the content using it is published (doc 11 P13) — and for a logo
    // the content using it is every page.
    expect(element.querySelector('[data-testid="logo-no-alt"]')).not.toBeNull();
  });

  it('does not warn when the logo has alternative text', async () => {
    await open(
      payload({}, {
        publicId: 'abc',
        url: '/media/logo.webp',
        width: 600,
        height: 150,
        altText: 'Colombo Wardrobe',
        originalFileName: 'logo.png',
      }),
    );

    expect(element.querySelector('[data-testid="logo-no-alt"]')).toBeNull();
  });

  // ---- the write body --------------------------------------------------------------------------

  /**
   * **Every field, every save.** This is the W1-12 test, for this screen: a body that omitted a field would be
   * a body the API reads as "clear it".
   */
  it('sends every contact and social field, including the ones left empty', async () => {
    await open(payload({ [SETTING_KEYS.facebookUrl]: 'https://www.facebook.com/kept' }));

    await type('whatsApp', '94771234567');
    await save();

    const body = writeTo(AdminSettingsApi.contactSocialUrl).request.body as Record<string, unknown>;

    expect(Object.keys(body).sort()).toEqual([
      'facebookUrl',
      'instagramUrl',
      'rowVersion',
      'tikTokUrl',
      'whatsApp',
      'youTubeUrl',
    ]);

    expect(body['whatsApp']).toBe('94771234567');

    // The value that was already there is sent back, not dropped: the API replaces the group.
    expect(body['facebookUrl']).toBe('https://www.facebook.com/kept');

    // And the ones nobody filled in are sent as null rather than omitted, so "clear it" is explicit.
    expect(body['instagramUrl']).toBeNull();
    expect(body['tikTokUrl']).toBeNull();
    expect(body['youTubeUrl']).toBeNull();
  });

  /** Issue #193: the fourth social box. Typed in, it reaches the PUT under the name the API reads. */
  it('sends the YouTube channel typed into its box', async () => {
    await open();

    await type('youTubeUrl', 'https://www.youtube.com/@colombowardrobe');
    await save();

    const body = writeTo(AdminSettingsApi.contactSocialUrl).request.body as Record<string, unknown>;

    expect(body['youTubeUrl']).toBe('https://www.youtube.com/@colombowardrobe');
    expect(field('youTubeUrl').getAttribute('placeholder')).toBe('https://www.youtube.com/@yourshop');
  });

  it('sends the row version the API handed it', async () => {
    await open();

    await type('whatsApp', '94771234567');
    await save();

    const request = writeTo(AdminSettingsApi.contactSocialUrl);

    expect((request.request.body as Record<string, unknown>)['rowVersion']).toBe('form-rv-1');
  });

  it('carries the antiforgery token on the save', async () => {
    await open();

    await type('whatsApp', '94771234567');
    await save();

    const request = writeTo(AdminSettingsApi.contactSocialUrl);

    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');
  });

  /**
   * The screen reloads after a successful save, which is not laziness: the save moved the group's row version,
   * so keeping the one on screen would make the admin's *next* save fail with a conflict they did not cause.
   */
  it('reloads after a successful save so the next one is not stale', async () => {
    await open();

    await type('whatsApp', '94771234567');
    await save();

    writeTo(AdminSettingsApi.contactSocialUrl).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    controller.expectOne(AdminSettingsApi.settingsUrl).flush(payload({ [SETTING_KEYS.whatsApp]: '94771234567' }));
    fixture.detectChanges();

    // The reload's values reach the inputs on a microtask, like every other `[ngModel]` write here.
    await fixture.whenStable();
    fixture.detectChanges();

    expect(field('whatsApp').value).toBe('94771234567');
  });

  it('shows a field error against the box the API named', async () => {
    await open();

    await type('facebookUrl', 'javascript:alert(1)');
    await save();

    writeTo(AdminSettingsApi.contactSocialUrl).flush(
      {
        title: 'One or more fields are invalid.',
        status: 400,
        errors: { facebookUrl: ['The Facebook address must be a full web address starting with https://.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="error-facebookUrl"]')?.textContent).toContain('https://');
    expect(field('facebookUrl').getAttribute('aria-invalid')).toBe('true');

    // And it clears as soon as the admin edits that box, so the message cannot sit there contradicting it.
    await type('facebookUrl', 'https://www.facebook.com/shop');
    expect(element.querySelector('[data-testid="error-facebookUrl"]')).toBeNull();
  });

  it('tells the admin to reload on a conflict rather than blaming a field', async () => {
    await open();

    await type('whatsApp', '94771234567');
    await save();

    writeTo(AdminSettingsApi.contactSocialUrl).flush(
      {
        title: 'This change cannot be applied',
        status: 409,
        detail: 'Somebody else changed these settings while you were editing them.',
        code: 'stale_row_version',
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="page-error"]')?.textContent).toContain('Somebody else');
  });

  // ---- the logo ---------------------------------------------------------------------------------

  /**
   * Two requests, in order: the file to the media library, then the setting pointed at the result. A failed
   * upload therefore changes nothing and the previous logo is still the live one.
   */
  it('uploads the file first and then points the setting at it', async () => {
    await open();

    await choose(new File([new Uint8Array([1, 2, 3])], 'logo.png', { type: 'image/png' }));

    const upload = writeTo(AdminSettingsApi.imagesUrl);
    const form = upload.request.body as FormData;

    expect(form.get('file')).toBeInstanceOf(File);

    // Alt text travels with the file, because a public image needs one before the content using it is
    // published — and it defaults to the shop's name, which is almost always right for a logo.
    expect(form.get('altText')).toBe('Colombo Wardrobe');

    upload.flush({
      publicId: 'new-asset',
      url: '/media/new.webp',
      width: 600,
      height: 150,
      altText: 'Colombo Wardrobe',
      originalFileName: 'logo.png',
    });
    fixture.detectChanges();

    const write = writeTo(AdminSettingsApi.storeLogoUrl);
    const body = write.request.body as Record<string, unknown>;

    expect(body['logoAssetPublicId']).toBe('new-asset');
    expect(body['rowVersion']).toBe('store-rv-1');
  });

  it('sends a null asset id to remove the logo', async () => {
    await open(
      payload({}, {
        publicId: 'abc',
        url: '/media/logo.webp',
        width: 600,
        height: 150,
        altText: 'Logo',
        originalFileName: 'logo.png',
      }),
    );

    element.querySelector<HTMLButtonElement>('[data-testid="logo-remove"]')!.click();
    fixture.detectChanges();

    const body = writeTo(AdminSettingsApi.storeLogoUrl).request.body as Record<string, unknown>;

    // Null rather than an omitted field: the API's record has a nullable Guid, and omitting it would read as
    // "no opinion" on a route whose whole job is to say "none".
    expect('logoAssetPublicId' in body).toBe(true);
    expect(body['logoAssetPublicId']).toBeNull();
  });

  it('refuses a file over the size limit without uploading it', async () => {
    await open();

    await choose(new File([new Uint8Array(3 * 1024 * 1024)], 'huge.png', { type: 'image/png' }));

    expect(element.querySelector('[data-testid="logo-error"]')?.textContent).toContain('2 MB');

    // Nothing was sent: `controller.verify()` in afterEach would fail on an outstanding request, and this says
    // so explicitly rather than relying on that.
    expect(controller.match(AdminSettingsApi.imagesUrl)).toHaveLength(0);
  });

  it('does not upload when no file was chosen', async () => {
    await open();

    const input = element.querySelector<HTMLInputElement>('[data-testid="logo-file"]')!;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(controller.match(AdminSettingsApi.imagesUrl)).toHaveLength(0);
  });

  /**
   * Puts a file on the upload control.
   *
   * `Object.defineProperty` rather than a `DataTransfer`, because jsdom does not implement `DataTransfer` at
   * all — the first version of this file used one and every logo test failed with `DataTransfer is not
   * defined`. `input.files` is read-only, so it is redefined; the component only ever reads `files?.[0]`.
   */
  async function choose(file: File): Promise<void> {
    const input = element.querySelector<HTMLInputElement>('[data-testid="logo-file"]')!;

    Object.defineProperty(input, 'files', {
      configurable: true,
      value: Object.assign([file], { item: (index: number) => [file][index] ?? null, length: 1 }),
    });

    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  // ---- the read failure ------------------------------------------------------------------------

  it('offers a retry when the settings cannot be read', async () => {
    fixture = TestBed.createComponent(SettingsPage);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    controller
      .expectOne(AdminSettingsApi.settingsUrl)
      .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="page-error"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="page-error"] button')).not.toBeNull();
  });

  it('offers no retry on a refusal, because retrying cannot help', async () => {
    fixture = TestBed.createComponent(SettingsPage);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    controller
      .expectOne(AdminSettingsApi.settingsUrl)
      .flush('no', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="page-error"]')?.textContent).toContain('permission');
    expect(element.querySelector('[data-testid="page-error"] button')).toBeNull();
  });

  // ---- the bank details (PAY-01, doc 11 P7) ------------------------------------------------------

  describe('bank details', () => {
    it('shows no bank panel when the server did not send the group', async () => {
      await open(payload());

      // `Settings.Payments` is held by no default role, so a Store Manager receives no Bank group — and a
      // box they can type into and never save would be worse than no box. The screen asks the payload,
      // which is the same question the server answers.
      expect(element.querySelector('[data-testid="field-accountNumber"]')).toBeNull();
      expect(element.querySelector('[data-testid="save-bank"]')).toBeNull();

      // Nor the Payment methods switch, which needs the same permission (owner decision 2026-10-21).
      expect(controller.match(PaymentProvidersApi.url)).toHaveLength(0);
      expect(element.querySelector('sda-payment-methods-panel')).toBeNull();
    });

    it('draws the Payment methods switch beside the bank panel, which needs the same permission', async () => {
      await open(payloadWithBank());

      expect(element.querySelector('sda-payment-methods-panel')).not.toBeNull();
      expect(controller.match(PaymentProvidersApi.url)).toHaveLength(1);
    });

    it('fills the boxes from the payload', async () => {
      await open(payloadWithBank());

      expect(field('bankName').value).toBe('Example Bank PLC');
      expect(field('branch').value).toBe('Example Branch');
      expect(field('accountName').value).toBe('Example Shop (Pvt) Ltd');
      expect(field('accountNumber').value).toBe('0000 1111 2222');
    });

    /**
     * **The W1-12 test for this group, on the field where it costs the client money.**
     *
     * The blocker was a form model that had never listed three columns, so every save cleared them, and
     * 1,571 passing backend tests could not see it: the API stores what it is handed and the thing that
     * was wrong was what the client sent. So this asserts the **body**.
     */
    it('sends every bank field the server sent, on every save', async () => {
      await open(payloadWithBank());

      await type('branch', 'Bambalapitiya');

      element.querySelector<HTMLButtonElement>('[data-testid="save-bank"]')!.click();
      fixture.detectChanges();

      controller.expectOne((request) => request.url.includes('antiforgery')).flush({ token: 't' });
      fixture.detectChanges();

      const saved = controller.expectOne(AdminSettingsApi.bankUrl);
      expect(saved.request.method).toBe('PUT');

      // All five, not just the edited one — and the unedited four carry the values the server sent rather
      // than null.
      expect(saved.request.body).toEqual({
        bankName: 'Example Bank PLC',
        branch: 'Bambalapitiya',
        accountName: 'Example Shop (Pvt) Ltd',
        accountNumber: '0000 1111 2222',
        instructions: 'CDM deposits take a working day.',
        rowVersion: 'bank-rv-1',
      });

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithBank());
      fixture.detectChanges();
    });

    /**
     * And the other half: a group the server did **not** send is a group the save does not mention.
     *
     * `toBankForm` leaves an absent key out of the form entirely, so `JSON.stringify` drops it and the API
     * leaves that setting alone. This is the test that fails if somebody "tidies" the optional fields into
     * required ones with null defaults — which is what W1-12's blocker was.
     */
    it('omits from the body every bank key the server did not send', async () => {
      // A payload holding only the account number: the shape a narrower read model would produce.
      const partial = payloadWithBank();
      const bankGroup = partial.groups.find((group) => group.groupKey === 'Bank')!;

      const narrowed: AdminSettings = {
        ...partial,
        groups: [
          ...partial.groups.filter((group) => group.groupKey !== 'Bank'),
          {
            ...bankGroup,
            settings: bankGroup.settings.filter(
              (setting) => setting.key === SETTING_KEYS.accountNumber,
            ),
          },
        ],
      };

      await open(narrowed);

      await type('accountNumber', '9002 3456 7890');

      element.querySelector<HTMLButtonElement>('[data-testid="save-bank"]')!.click();
      fixture.detectChanges();

      controller.expectOne((request) => request.url.includes('antiforgery')).flush({ token: 't' });
      fixture.detectChanges();

      const saved = controller.expectOne(AdminSettingsApi.bankUrl);

      // One field and the token. No `bankName: null`, which would tell the API to clear the shop's bank.
      expect(saved.request.body).toEqual({
        accountNumber: '9002 3456 7890',
        rowVersion: 'bank-rv-1',
      });

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(narrowed);
      fixture.detectChanges();
    });

    it('sends null for a box the admin emptied, because clearing one has to be sayable', async () => {
      await open(payloadWithBank());

      await type('branch', '');

      element.querySelector<HTMLButtonElement>('[data-testid="save-bank"]')!.click();
      fixture.detectChanges();

      controller.expectOne((request) => request.url.includes('antiforgery')).flush({ token: 't' });
      fixture.detectChanges();

      const saved = controller.expectOne(AdminSettingsApi.bankUrl);

      // Present and empty, which the server reads as "clear it" — as distinct from absent, which it reads
      // as "leave it alone". Those are two different instructions and the form has to express both.
      expect((saved.request.body as Record<string, unknown>)['branch']).toBe('');
      expect((saved.request.body as Record<string, unknown>)['accountNumber']).toBe('0000 1111 2222');

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithBank());
      fixture.detectChanges();
    });

    it('marks Save unavailable with aria-disabled rather than disabled', async () => {
      await open(payloadWithBank());

      const save = element.querySelector<HTMLButtonElement>('[data-testid="save-bank"]')!;
      save.click();
      fixture.detectChanges();

      // Issue #109: disabling the button under the admin's own finger throws their focus to <body>.
      expect(save.getAttribute('aria-disabled')).toBe('true');
      expect(save.hasAttribute('disabled')).toBe(false);

      controller.expectOne((request) => request.url.includes('antiforgery')).flush({ token: 't' });
      fixture.detectChanges();

      controller
        .expectOne(AdminSettingsApi.bankUrl)
        .flush(null, { status: 204, statusText: 'No Content' });

      fixture.detectChanges();
      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithBank());
      fixture.detectChanges();
    });

    it('shows the API message against the field it refused', async () => {
      await open(payloadWithBank());

      await type('accountNumber', 'to be confirmed');

      element.querySelector<HTMLButtonElement>('[data-testid="save-bank"]')!.click();
      fixture.detectChanges();

      controller.expectOne((request) => request.url.includes('antiforgery')).flush({ token: 't' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.bankUrl).flush(
        {
          title: 'One or more fields are invalid.',
          errors: {
            accountNumber: ['An account number can only hold digits, spaces and dashes.'],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );

      fixture.detectChanges();

      expect(element.querySelector('[data-testid="error-accountNumber"]')?.textContent).toContain(
        'digits, spaces and dashes',
      );
      expect(field('accountNumber').getAttribute('aria-invalid')).toBe('true');
    });
  });

  /**
   * ADM-09's own settings form.
   *
   * **It exists because the invoice is ADM-09's deliverable and these five keys could be set from no
   * screen at all** — not even read-only, because this screen renders named forms rather than a generic
   * key list. An invoice that cannot be completed short of SQL is not a deliverable.
   */
  // ---- PayPal (PAY-02, doc 08 section 3.8) -------------------------------------------------------

  describe('PayPal', () => {
    async function clickSavePayPal(): Promise<void> {
      element.querySelector<HTMLButtonElement>('[data-testid="save-paypal"]')!.click();
      fixture.detectChanges();
      await fixture.whenStable();
    }

    function flushAntiforgery(): void {
      controller.expectOne((request) => request.url.includes('antiforgery')).flush({ token: 't' });
      fixture.detectChanges();
    }

    function readiness(): HTMLElement {
      return element.querySelector('[data-testid="paypal-readiness"]') as HTMLElement;
    }

    it('shows no PayPal panel when the server did not send the group', async () => {
      await open(payload());

      // Settings.Payments is held by no default role, so a Store Manager receives no PayPal group - and
      // a box they could type into and never save would be worse than no box.
      expect(element.querySelector('[data-testid="paypal-panel"]')).toBeNull();
      expect(element.querySelector('[data-testid="field-clientId"]')).toBeNull();
      expect(element.querySelector('[data-testid="save-paypal"]')).toBeNull();
    });

    it('fills the boxes from the payload', async () => {
      await open(payloadWithPayPal());

      expect(field('mode').value).toBe('sandbox');
      expect(field('clientId').value).toBe('client-id-123');
      expect(field('webhookId').value).toBe('WEBHOOK-ID-9');
      expect(field('lkrPerUsd').value).toBe('300.00');
      expect(field('fxBufferPercent').value).toBe('3.00');
      expect(field('rateMaxAgeDays').value).toBe('7');
      expect(field('expiryMinutes').value).toBe('60');
      expect(field('captureLockHours').value).toBe('3');
      expect(field('captureRecoveryMaxHours').value).toBe('24');
      expect(field('pendingCaptureMaxDays').value).toBe('5');
    });

    /**
     * **The secret is write-only.** The server returns no value for it, so there is nothing to show - and the
     * box must say whether one is saved without ever holding it.
     */
    it('never puts a secret in the page: the box is empty and only says whether one is saved', async () => {
      await open(payloadWithPayPal({}, { secretSet: true }));

      const secret = field('clientSecret');
      expect(secret.value).toBe('');
      expect(secret.type).toBe('password');
      expect(secret.getAttribute('autocomplete')).toBe('new-password');
      expect(secret.placeholder).toContain('A secret is saved');
      expect(element.querySelector('[data-testid="paypal-secret-state"]')?.textContent).toContain('A secret is saved.');

      await open(payloadWithPayPal({}, { secretSet: false }));

      expect(field('clientSecret').placeholder).toBe('Not set');
      expect(element.querySelector('[data-testid="paypal-secret-state"]')?.textContent).toContain('No secret is saved yet.');
    });

    /**
     * **The W1-12 test for this group.** The body is what matters: the API stores what it is handed, and the
     * thing that was wrong in W1-12 was what the client sent. Every field the server sent goes back - and the
     * secret does not, because the admin did not type one.
     */
    it('sends every field the server sent on every save, and no secret when none was typed', async () => {
      const loaded = payloadWithPayPal();
      await open(loaded);

      await type('lkrPerUsd', '305.50');
      await clickSavePayPal();
      flushAntiforgery();

      const saved = controller.expectOne(AdminSettingsApi.payPalUrl);
      expect(saved.request.method).toBe('PUT');

      const body = saved.request.body as Record<string, unknown>;

      expect(body).toEqual({
        mode: 'sandbox',
        clientId: 'client-id-123',
        webhookId: 'WEBHOOK-ID-9',
        lkrPerUsd: '305.50',
        fxBufferPercent: '3.00',
        rateMaxAgeDays: '7',
        captureLockHours: '3',
        captureRecoveryMaxHours: '24',
        pendingCaptureMaxDays: '5',
        expiryMinutes: '60',
        rowVersion: 'paypal-rv-1',
      });

      // Not even as an empty string: a missing field is "keep it", and so is a blank one, but the first
      // is the one that cannot be misread.
      expect('clientSecret' in body).toBe(false);
      expect('rateUpdatedAtUtc' in body).toBe(false);
      expect('confirmRate' in body).toBe(false);

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(loaded);
      fixture.detectChanges();
    });

    it('sends the secret that was typed, once, and then forgets it', async () => {
      const loaded = payloadWithPayPal({}, { secretSet: false });
      await open(loaded);

      await type('clientSecret', '  EBWKjlELKMYqRNQ6sYvFo64FtaRLRR5BdHEESmha49TM ');
      await clickSavePayPal();
      flushAntiforgery();

      const saved = controller.expectOne(AdminSettingsApi.payPalUrl);

      expect((saved.request.body as Record<string, unknown>)['clientSecret']).toBe(
        'EBWKjlELKMYqRNQ6sYvFo64FtaRLRR5BdHEESmha49TM',
      );

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithPayPal({}, { secretSet: true }));
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      // Gone from the box, and from the page altogether.
      expect(field('clientSecret').value).toBe('');
      expect(element.textContent).not.toContain('EBWKjlELKMYqRNQ6sYvFo64FtaRLRR5BdHEESmha49TM');
      expect(element.querySelector('[data-testid="paypal-secret-state"]')?.textContent).toContain('A secret is saved.');
    });

    it('keeps what was typed in the secret box when the save is refused, so the admin can fix and retry', async () => {
      await open(payloadWithPayPal({}, { secretSet: false }));

      await type('clientSecret', 'abc 123');
      await clickSavePayPal();
      flushAntiforgery();

      controller.expectOne(AdminSettingsApi.payPalUrl).flush(
        { title: 'One or more fields are invalid.', errors: { clientSecret: ['That does not look like a PayPal secret.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(element.querySelector('[data-testid="error-clientSecret"]')?.textContent).toContain('PayPal secret');
      expect(field('clientSecret').value).toBe('abc 123');
    });

    /**
     * A group the server did not send is a field the save does not mention - here the one key, doc 08's
     * `Orders.PayPalExpiryMinutes`, that is `Settings.Edit` and so is absent for a caller with
     * `Settings.Payments` alone.
     */
    it('draws no expiry box and sends no expiry when the server did not send that key', async () => {
      const loaded = payloadWithPayPal({}, { withExpiry: false });
      await open(loaded);

      expect(element.querySelector('[data-testid="field-expiryMinutes"]')).toBeNull();

      await clickSavePayPal();
      flushAntiforgery();

      const saved = controller.expectOne(AdminSettingsApi.payPalUrl);

      expect('expiryMinutes' in (saved.request.body as Record<string, unknown>)).toBe(false);

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(loaded);
      fixture.detectChanges();
    });

    it('sends an emptied box as empty, because clearing one has to be sayable', async () => {
      const loaded = payloadWithPayPal();
      await open(loaded);

      await type('webhookId', '');
      await clickSavePayPal();
      flushAntiforgery();

      const saved = controller.expectOne(AdminSettingsApi.payPalUrl);

      expect((saved.request.body as Record<string, unknown>)['webhookId']).toBe('');
      expect((saved.request.body as Record<string, unknown>)['clientId']).toBe('client-id-123');

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(loaded);
      fixture.detectChanges();
    });

    it('can switch the environment', async () => {
      const loaded = payloadWithPayPal();
      await open(loaded);

      const mode = element.querySelector<HTMLSelectElement>('[data-testid="field-mode"]')!;
      mode.value = 'live';
      mode.dispatchEvent(new Event('change'));
      fixture.detectChanges();
      await fixture.whenStable();

      await clickSavePayPal();
      flushAntiforgery();

      const saved = controller.expectOne(AdminSettingsApi.payPalUrl);

      expect((saved.request.body as Record<string, unknown>)['mode']).toBe('live');

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(loaded);
      fixture.detectChanges();
    });

    it('shows the server\'s message beside the box it is about', async () => {
      await open(payloadWithPayPal());

      await type('lkrPerUsd', 'three hundred');
      await clickSavePayPal();
      flushAntiforgery();

      controller.expectOne(AdminSettingsApi.payPalUrl).flush(
        { title: 'One or more fields are invalid.', errors: { lkrPerUsd: ['Enter how many rupees one US dollar costs.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(element.querySelector('[data-testid="error-lkrPerUsd"]')?.textContent).toContain('rupees');
      expect(field('lkrPerUsd').getAttribute('aria-invalid')).toBe('true');

      await type('lkrPerUsd', '300');
      expect(element.querySelector('[data-testid="error-lkrPerUsd"]')).toBeNull();
    });

    it('marks Save unavailable with aria-disabled rather than disabled', async () => {
      await open(payloadWithPayPal());

      const save = element.querySelector<HTMLButtonElement>('[data-testid="save-paypal"]')!;
      save.click();
      fixture.detectChanges();

      // Issue #109: disabling the button under the admin's own finger throws their focus to body.
      expect(save.getAttribute('aria-disabled')).toBe('true');
      expect(save.hasAttribute('disabled')).toBe(false);

      flushAntiforgery();
      controller.expectOne(AdminSettingsApi.payPalUrl).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithPayPal());
      fixture.detectChanges();
    });

    // ---- the rate's age ----------------------------------------------------------------------------

    it('confirms the rate with a body of nothing but the confirmation and the version', async () => {
      const loaded = payloadWithPayPal();
      await open(loaded);

      element.querySelector<HTMLButtonElement>('[data-testid="confirm-paypal-rate"]')!.click();
      fixture.detectChanges();
      flushAntiforgery();

      const saved = controller.expectOne(AdminSettingsApi.payPalUrl);

      // Not the whole form: confirming the rate must not save anything else the admin half-typed.
      expect(saved.request.body).toEqual({ confirmRate: true, rowVersion: 'paypal-rv-1' });

      saved.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(loaded);
      fixture.detectChanges();
    });

    it('offers no confirmation while there is no rate to confirm', async () => {
      await open(payloadWithPayPal({ [SETTING_KEYS.payPalLkrPerUsd]: null }));

      const confirm = element.querySelector<HTMLButtonElement>('[data-testid="confirm-paypal-rate"]')!;
      expect(confirm.getAttribute('aria-disabled')).toBe('true');

      confirm.click();
      fixture.detectChanges();

      // Nothing went out: no antiforgery fetch, no PUT.
      controller.expectNone((request) => request.url.includes('antiforgery'));
      controller.expectNone(AdminSettingsApi.payPalUrl);
    });

    it('says when the rate was last updated', async () => {
      await open(payloadWithPayPal({ [SETTING_KEYS.payPalRateUpdatedAtUtc]: '2026-10-05T04:30:00.000Z' }));

      // 04:30 UTC is 10:00 in Colombo.
      expect(element.querySelector('[data-testid="paypal-rate-age"]')?.textContent).toContain('10:00');
      expect(element.querySelector('[data-testid="paypal-rate-age"]')?.textContent).toContain('Sri Lanka time');

      await open(payloadWithPayPal({ [SETTING_KEYS.payPalRateUpdatedAtUtc]: null }));

      expect(element.querySelector('[data-testid="paypal-rate-age"]')?.textContent).toContain('never');
    });

    // ---- readiness ---------------------------------------------------------------------------------

    it('says PayPal is ready when the credentials and a fresh rate are in place', async () => {
      await open(payloadWithPayPal());

      expect(readiness().dataset['state']).toBe('ready');
      expect(readiness().textContent).toContain('Ready.');
    });

    it('names what is still missing, in the order a person would fill it in', async () => {
      await open(
        payloadWithPayPal(
          {
            [SETTING_KEYS.payPalClientId]: null,
            [SETTING_KEYS.payPalWebhookId]: null,
            [SETTING_KEYS.payPalLkrPerUsd]: null,
          },
          { secretSet: false },
        ),
      );

      expect(readiness().dataset['state']).toBe('not-ready');
      expect(readiness().textContent).toContain(
        'the client ID, the client secret, the webhook ID, the exchange rate',
      );
    });

    it('warns when the rate is older than its limit', async () => {
      const old = new Date(Date.now() - 9 * 86_400_000).toISOString();
      await open(payloadWithPayPal({ [SETTING_KEYS.payPalRateUpdatedAtUtc]: old }));

      expect(readiness().dataset['state']).toBe('not-ready');
      expect(readiness().textContent).toContain('more than 7 days ago');
      expect(readiness().textContent).toContain('confirm it is still correct');
    });

    it('uses the limit the server holds, not a constant', async () => {
      const threeDaysAgo = new Date(Date.now() - 3 * 86_400_000).toISOString();

      await open(
        payloadWithPayPal({
          [SETTING_KEYS.payPalRateUpdatedAtUtc]: threeDaysAgo,
          [SETTING_KEYS.payPalRateMaxAgeDays]: '2',
        }),
      );

      expect(readiness().dataset['state']).toBe('not-ready');
      expect(readiness().textContent).toContain('more than 2 days ago');
    });
  });

  describe('display currencies (issue #248)', () => {
    /**
     * The ordinary payload plus the `Currency.Display` group (`Settings.Edit`), with its own token as the server sends
     * it. Present only for a caller who may read it: the read model filters every row by its own permission.
     */
    function payloadWithCurrencies(values: Record<string, string | null> = {}): AdminSettings {
      const base = payload();

      return {
        ...base,
        groups: [
          ...base.groups,
          {
            groupKey: 'Currency.Display',
            rowVersion: 'currency-rv-1',
            settings: [
              SETTING_KEYS.currencyUsd,
              SETTING_KEYS.currencyGbp,
              SETTING_KEYS.currencyEur,
              SETTING_KEYS.currencyAud,
              SETTING_KEYS.currencyAed,
            ].map((key) => ({
              key,
              valueType: 'Decimal' as const,
              value: values[key] ?? null,
              isSecret: false,
              hasValue: values[key] != null,
              requiredPermission: 'Settings.Edit',
              description: `What ${key} is for.`,
              updatedAtUtc: values[key] != null ? '2026-10-20T06:00:00.000Z' : null,
            })),
          },
        ],
      };
    }

    it('is absent for a caller who does not receive the Currency.Display group', async () => {
      await open(payload());

      expect(element.querySelector('[data-testid="currency-panel"]')).toBeNull();
      expect(element.querySelector('#currency-usd')).toBeNull();
    });

    it('is drawn, with its five boxes, for a caller who does', async () => {
      await open(payloadWithCurrencies({ [SETTING_KEYS.currencyGbp]: '399.5' }));

      expect(element.querySelector('[data-testid="currency-panel"]')).not.toBeNull();
      expect(element.querySelectorAll('[data-testid^="currency-field-"]').length).toBe(5);
      expect((element.querySelector('[data-testid="currency-field-gbp"]') as HTMLInputElement).value).toBe('399.5');
    });

    it('does not say there is nothing to edit when this is the only group the caller can edit', async () => {
      await open({ groups: [payloadWithCurrencies().groups.at(-1)!], logo: null });

      expect(element.querySelector('[data-testid="currency-panel"]')).not.toBeNull();
      expect(element.querySelector('[data-testid="nothing-to-edit"]')).toBeNull();
    });

    it('says there is nothing to edit when the caller receives no group at all', async () => {
      await open({ groups: [], logo: null });

      expect(element.querySelector('[data-testid="currency-panel"]')).toBeNull();
      expect(element.querySelector('[data-testid="nothing-to-edit"]')).not.toBeNull();
    });

    it('re-reads the settings after a save, so the next save carries the new row version', async () => {
      await open(payloadWithCurrencies());

      const input = element.querySelector('[data-testid="currency-field-usd"]') as HTMLInputElement;
      input.value = '321.5';
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      await fixture.whenStable();

      (element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'xsrf' });

      const save = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(save.request.body).toEqual({ usd: '321.5', rowVersion: 'currency-rv-1' });
      save.flush(null, { status: 204, statusText: 'No Content' });

      const reread = payloadWithCurrencies({ [SETTING_KEYS.currencyUsd]: '321.5' });
      controller.expectOne(AdminSettingsApi.settingsUrl).flush({
        ...reread,
        groups: reread.groups.map((group) =>
          group.groupKey === 'Currency.Display' ? { ...group, rowVersion: 'currency-rv-2' } : group,
        ),
      });
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect((element.querySelector('[data-testid="currency-field-usd"]') as HTMLInputElement).value).toBe('321.5');
      expect(element.querySelector('[data-testid="currency-saved"]')?.textContent?.trim()).toBe('Rates saved.');

      // The panel survived the re-read (it was not torn down and rebuilt), so its message is still there, and the
      // row version it will send next is the one the re-read brought.
      const again = element.querySelector('[data-testid="currency-field-gbp"]') as HTMLInputElement;
      again.value = '399.5';
      again.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      await fixture.whenStable();

      (element.querySelector('[data-testid="save-currency"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'xsrf' });
      const second = controller.expectOne(AdminSettingsApi.currencyRatesUrl);
      expect(second.request.body).toEqual({ gbp: '399.5', rowVersion: 'currency-rv-2' });
      second.flush(null, { status: 204, statusText: 'No Content' });
      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithCurrencies());
    });
  });

  describe('traffic (issue #193)', () => {
    /**
     * The ordinary payload plus the `Traffic` group (`Settings.Edit`), with its own token as the server sends it
     * (`SettingCatalog.SaveUnits` gives it a save unit of its own). Non-null values, for the reason `payload()`
     * gives.
     */
    function payloadWithTraffic(values: Partial<Record<string, string | null>> = {}): AdminSettings {
      const traffic = {
        [SETTING_KEYS.trafficRawRetentionDays]: '90',
        [SETTING_KEYS.trafficBotRetentionDays]: '7',
        ...values,
      } as Record<string, string | null>;

      const base = payload();

      return {
        ...base,
        groups: [
          ...base.groups,
          {
            groupKey: 'Traffic',
            rowVersion: 'traffic-rv-1',
            settings: Object.keys(traffic).map((key) => ({
              key,
              valueType: 'Int' as const,
              value: traffic[key] ?? null,
              isSecret: false,
              hasValue: Boolean(traffic[key]),
              requiredPermission: 'Settings.Edit',
              description: `What ${key} is for.`,
              updatedAtUtc: null,
            })),
          },
        ],
      };
    }

    async function clickSaveTraffic(): Promise<void> {
      (element.querySelector('[data-testid="save-traffic"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
    }

    it('is absent for a caller who does not receive the Traffic group', async () => {
      await open(payload());

      expect(element.querySelector('[data-testid="traffic-panel"]')).toBeNull();
      expect(field('rawRetentionDays')).toBeNull();
    });

    it('shows the two periods the server sent, with numeric keyboards and the bounds in the hints', async () => {
      await open(payloadWithTraffic({ [SETTING_KEYS.trafficRawRetentionDays]: '120', [SETTING_KEYS.trafficBotRetentionDays]: '14' }));

      expect(field('rawRetentionDays').value).toBe('120');
      expect(field('botRetentionDays').value).toBe('14');
      expect(field('rawRetentionDays').getAttribute('inputmode')).toBe('numeric');

      const panel = element.querySelector('[data-testid="traffic-panel"]')!.textContent!;
      expect(panel).toContain('Between 30 and 365 days');
      expect(panel).toContain('Between 7 and');
      expect(panel).toContain('90 days');
      expect(panel).toContain('kept for ever');
    });

    it('sends both periods with the Traffic token, then re-reads', async () => {
      await open(payloadWithTraffic());

      await type('rawRetentionDays', '180');
      await type('botRetentionDays', '21');
      await clickSaveTraffic();

      const saved = writeTo(AdminSettingsApi.trafficUrl);

      // The literal route, not only the constant: a mistyped constant would otherwise agree with itself.
      expect(saved.request.url).toBe('/api/admin/settings/traffic');
      expect(saved.request.method).toBe('PUT');
      expect(saved.request.body).toEqual({
        rawRetentionDays: '180',
        botRetentionDays: '21',
        rowVersion: 'traffic-rv-1',
      });

      saved.flush(null);
      fixture.detectChanges();
      await fixture.whenStable();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(
        payloadWithTraffic({ [SETTING_KEYS.trafficRawRetentionDays]: '180', [SETTING_KEYS.trafficBotRetentionDays]: '21' }),
      );
      fixture.detectChanges();
      await fixture.whenStable();

      expect(element.querySelector('[data-testid="announcement"]')!.textContent).toContain('Traffic settings saved');
    });

    it('omits a key the server did not send, rather than sending null', async () => {
      const full = payloadWithTraffic();
      const narrowed: AdminSettings = {
        ...full,
        groups: full.groups.map((group) =>
          group.groupKey === 'Traffic'
            ? { ...group, settings: group.settings.filter((row) => row.key === SETTING_KEYS.trafficBotRetentionDays) }
            : group,
        ),
      };

      await open(narrowed);
      await type('botRetentionDays', '30');
      await clickSaveTraffic();

      const saved = writeTo(AdminSettingsApi.trafficUrl);
      expect(saved.request.body).toEqual({ botRetentionDays: '30', rowVersion: 'traffic-rv-1' });
      saved.flush(null);
      controller.expectOne(AdminSettingsApi.settingsUrl).flush(narrowed);
    });

    it('shows the server\'s refusal beside the box it is about', async () => {
      await open(payloadWithTraffic());

      await type('rawRetentionDays', '10');
      await clickSaveTraffic();

      writeTo(AdminSettingsApi.trafficUrl).flush(
        {
          title: 'One or more fields are invalid.',
          status: 400,
          errors: { rawRetentionDays: ['Use between 30 and 365 days.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();
      await fixture.whenStable();

      expect(element.querySelector('[data-testid="error-rawRetentionDays"]')!.textContent).toContain('between 30 and 365');
      expect(field('rawRetentionDays').getAttribute('aria-invalid')).toBe('true');

      // Typing into the box clears the message about the value that is no longer there.
      await type('rawRetentionDays', '45');
      expect(element.querySelector('[data-testid="error-rawRetentionDays"]')).toBeNull();
    });

    it('does not send a second save while one is in flight', async () => {
      await open(payloadWithTraffic());

      await type('rawRetentionDays', '100');
      await clickSaveTraffic();
      await clickSaveTraffic();

      const saved = writeTo(AdminSettingsApi.trafficUrl);
      saved.flush(null);
      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithTraffic());
    });

    it('is something to edit on its own', async () => {
      const only: AdminSettings = {
        logo: null,
        groups: payloadWithTraffic().groups.filter((group) => group.groupKey === 'Traffic'),
      };

      await open(only);

      expect(element.querySelector('[data-testid="nothing-to-edit"]')).toBeNull();
      expect(element.querySelector('[data-testid="traffic-panel"]')).not.toBeNull();
    });
  });

  describe('reviews (issue #189)', () => {
    function payloadWithReviews(value: string | null): AdminSettings {
      const base = payload();

      return {
        ...base,
        groups: [
          ...base.groups,
          {
            groupKey: 'Reviews',
            rowVersion: 'reviews-rv-1',
            settings: [
              {
                key: SETTING_KEYS.reviewsRequireApproval,
                valueType: 'Bool' as const,
                value,
                isSecret: false,
                hasValue: value !== null,
                requiredPermission: 'Settings.Edit',
                description: 'Approve reviews before they appear.',
                updatedAtUtc: null,
              },
            ],
          },
        ],
      };
    }

    it('is absent for a caller who does not receive the Reviews group', async () => {
      await open(payload());

      expect(element.querySelector('[data-testid="reviews-panel"]')).toBeNull();
    });

    it('shows the switch as the server sent it, and on unless it reads false', async () => {
      await open(payloadWithReviews('false'));
      expect(field('requireApproval').checked).toBe(false);

      expect(toRequireApproval(payloadWithReviews('true'))).toBe(true);
      expect(toRequireApproval(payloadWithReviews(' FALSE '))).toBe(false);
      expect(toRequireApproval(payloadWithReviews(null))).toBe(true);
      expect(toRequireApproval(payloadWithReviews('junk'))).toBe(true);
      expect(toRequireApproval(payload())).toBe(true);
    });

    it('sends a JSON boolean with the Reviews token, then re-reads', async () => {
      await open(payloadWithReviews('true'));

      field('requireApproval').click();
      fixture.detectChanges();
      (element.querySelector('[data-testid="save-reviews"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();

      const saved = writeTo(AdminSettingsApi.reviewsUrl);
      expect(saved.request.url).toBe('/api/admin/settings/reviews');
      expect(saved.request.method).toBe('PUT');
      expect(saved.request.body).toEqual({ requireApproval: false, rowVersion: 'reviews-rv-1' });

      saved.flush(null);
      fixture.detectChanges();
      await fixture.whenStable();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithReviews('false'));
      fixture.detectChanges();
      await fixture.whenStable();

      expect(element.querySelector('[data-testid="announcement"]')!.textContent).toContain('appear as soon as they are written');
    });
  });

  describe('product page texts (issue #33)', () => {
    function payloadWithTexts(delivery: string | null, returns: string | null): AdminSettings {
      const base = payload();
      const row = (key: string, value: string | null) => ({
        key,
        valueType: 'TextHtml' as const,
        value,
        isSecret: false,
        hasValue: value !== null,
        requiredPermission: 'Settings.Edit',
        description: `What ${key} is for.`,
        updatedAtUtc: null,
      });

      return {
        ...base,
        groups: [
          ...base.groups,
          {
            groupKey: 'Content',
            rowVersion: 'content-rv-1',
            settings: [row(SETTING_KEYS.pdpDeliveryInfoHtml, delivery), row(SETTING_KEYS.pdpReturnsInfoHtml, returns)],
          },
        ],
      };
    }

    it('is absent for a caller who does not receive the Content group', async () => {
      await open(payload());

      expect(element.querySelector('[data-testid="pdp-texts-panel"]')).toBeNull();
    });

    it('reads only the keys the payload carries', () => {
      expect(toProductPageForm(payload())).toEqual({});
      expect(toProductPageForm(payloadWithTexts('<p>D</p>', null))).toEqual({ deliveryInfoHtml: '<p>D</p>', returnsInfoHtml: null });
    });

    it('shows both texts as the server sent them in labelled boxes', async () => {
      await open(payloadWithTexts('<ul><li>Island-wide.</li></ul>', '<p>14 days.</p>'));

      const delivery = field('deliveryInfoHtml') as unknown as HTMLTextAreaElement;
      await fixture.whenStable();
      expect(delivery.value).toBe('<ul><li>Island-wide.</li></ul>');
      expect(element.querySelector('label[for="pdp-deliveryInfoHtml"]')!.textContent).toContain('Delivery text');
      expect(element.querySelector('label[for="pdp-returnsInfoHtml"]')!.textContent).toContain('Returns text');
      expect(delivery.getAttribute('aria-describedby')).toBe('pdp-hint-deliveryInfoHtml');
    });

    it('sends both texts with the Content token, says what the sanitiser removed, then re-reads', async () => {
      await open(payloadWithTexts('<p>Old.</p>', '<p>14 days.</p>'));

      const delivery = field('deliveryInfoHtml') as unknown as HTMLTextAreaElement;
      delivery.value = '<p>New.</p><script>x()</script>';
      delivery.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      (element.querySelector('[data-testid="save-pdp-texts"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();

      const saved = writeTo(AdminSettingsApi.productPageInfoUrl);
      expect(saved.request.method).toBe('PUT');
      expect(saved.request.body).toEqual({
        deliveryInfoHtml: '<p>New.</p><script>x()</script>',
        returnsInfoHtml: '<p>14 days.</p>',
        rowVersion: 'content-rv-1',
      });

      saved.flush({ removed: ['<script>'] });
      fixture.detectChanges();
      await fixture.whenStable();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithTexts('<p>New.</p>', '<p>14 days.</p>'));
      fixture.detectChanges();
      await fixture.whenStable();

      expect(element.querySelector('[data-testid="pdp-removed"]')!.textContent).toContain('<script>');
      expect(element.querySelector('[data-testid="announcement"]')!.textContent).toContain('Some markup was removed');
    });

    it('puts a 400 on the box it names', async () => {
      await open(payloadWithTexts('<p>Old.</p>', '<p>14 days.</p>'));

      (element.querySelector('[data-testid="save-pdp-texts"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();

      writeTo(AdminSettingsApi.productPageInfoUrl).flush(
        { title: 'One or more fields are invalid.', status: 400, errors: { returnsInfoHtml: ['Too long.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();
      await fixture.whenStable();

      expect(element.querySelector('[data-testid="error-returnsInfoHtml"]')!.textContent).toContain('Too long.');
      expect(field('returnsInfoHtml').getAttribute('aria-invalid')).toBe('true');
    });

    // Tester (#33): what is typed while the post-save re-read is in flight is not overwritten by it.
    it('keeps a text typed while the re-read after a save is still in flight', async () => {
      await open(payloadWithTexts('<p>Old.</p>', '<p>14 days.</p>'));

      (element.querySelector('[data-testid="save-pdp-texts"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      writeTo(AdminSettingsApi.productPageInfoUrl).flush({ removed: [] });
      fixture.detectChanges();
      await fixture.whenStable();

      const returns = field('returnsInfoHtml') as unknown as HTMLTextAreaElement;
      returns.value = '<p>Typed during the re-read.</p>';
      returns.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithTexts('<p>Old.</p>', '<p>14 days.</p>'));
      fixture.detectChanges();
      await fixture.whenStable();

      expect((field('returnsInfoHtml') as unknown as HTMLTextAreaElement).value).toBe('<p>Typed during the re-read.</p>');
    });

    // Tester (#33): an earlier save's "markup was removed" notice must not sit beside a later save that failed.
    it('clears the removed-markup notice when the next save fails', async () => {
      await open(payloadWithTexts('<p>Old.</p>', '<p>14 days.</p>'));

      (element.querySelector('[data-testid="save-pdp-texts"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      writeTo(AdminSettingsApi.productPageInfoUrl).flush({ removed: ['<script>'] });
      fixture.detectChanges();
      await fixture.whenStable();
      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithTexts('<p>Old.</p>', '<p>14 days.</p>'));
      fixture.detectChanges();
      await fixture.whenStable();
      expect(element.querySelector('[data-testid="pdp-removed"]')).not.toBeNull();

      (element.querySelector('[data-testid="save-pdp-texts"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      writeTo(AdminSettingsApi.productPageInfoUrl).flush(
        { title: 'One or more fields are invalid.', status: 400, errors: { returnsInfoHtml: ['Too long.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();
      await fixture.whenStable();

      expect(element.querySelector('[data-testid="pdp-removed"]')).toBeNull();
    });
  });

  describe('toTrafficForm', () => {
    it('reads only the keys the payload carries', () => {
      expect(toTrafficForm({ groups: [], logo: null })).toEqual({});

      const form = toTrafficForm({
        logo: null,
        groups: [
          {
            groupKey: 'Traffic',
            rowVersion: 'rv',
            settings: [
              {
                key: SETTING_KEYS.trafficRawRetentionDays,
                valueType: 'Int',
                value: '60',
                isSecret: false,
                hasValue: true,
                requiredPermission: 'Settings.Edit',
                description: null,
                updatedAtUtc: null,
              },
            ],
          },
        ],
      });

      expect(form).toEqual({ rawRetentionDays: '60' });
    });
  });

  describe('invoice and fulfilment (ADM-09)', () => {
    it('is absent for a caller who does not receive the Orders group', async () => {
      await open(payload());

      // A group a caller may not read is absent from the payload, not empty (doc 08 §3.8 DB2), so the
      // form is absent too rather than drawn over nothing.
      expect(element.querySelector('[data-testid="save-invoice"]')).toBeNull();
      expect(element.querySelector('[data-testid="field-legalName"]')).toBeNull();
    });

    it('shows the company details the server sent', async () => {
      await open(payloadWithInvoice());

      expect(field('legalName').value).toBe('A Registered Company (Pvt) Ltd');
      expect(field('brNumber').value).toBe('BR-0000');
      expect(field('vatNumber').value).toBe('VAT-0000');
      expect(field('autoDeliverAfterDays').value).toBe('7');
    });

    /**
     * **No placeholder on any box.** A `placeholder` renders, and W3-03 shipped a real bank's name and the
     * client's own legal name as placeholder text — both Major findings. A registered name and a
     * registered address have no obviously-fake shape the way an account number does, so the boxes are
     * empty and the hint says what belongs in them.
     */
    it('offers no example company name or address anywhere', async () => {
      await open(
        payloadWithInvoice({ [SETTING_KEYS.legalName]: null, [SETTING_KEYS.storeAddress]: null }),
      );

      for (const name of ['legalName', 'registeredAddress', 'brNumber', 'vatNumber']) {
        expect(field(name).getAttribute('placeholder'), name).toBeNull();
      }

      // And the hint says what an empty one means for the document, which is the honest substitute.
      expect(element.textContent).toContain('prints nothing at all for a detail that is empty');
    });

    it('sends every field it was given, and sends an emptied box as empty', async () => {
      await open(payloadWithInvoice());

      await type('legalName', 'A Renamed Company (Pvt) Ltd');
      await type('vatNumber', '');
      await clickSaveInvoice();

      const saved = writeTo(AdminSettingsApi.invoiceAndFulfilmentUrl);

      expect(saved.request.method).toBe('PUT');
      expect(saved.request.body).toEqual({
        legalName: 'A Renamed Company (Pvt) Ltd',
        registeredAddress: 'A registered address\nA city',
        brNumber: 'BR-0000',
        vatNumber: '',
        autoDeliverAfterDays: '7',
        rowVersion: 'store-rv-1',
      });

      saved.flush(null);
      fixture.detectChanges();
      await fixture.whenStable();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(payloadWithInvoice());
    });

    /**
     * **A key the server did not send is not written.**
     *
     * The rule that keeps holding when a permission changes: a `PUT` carrying a field the screen never
     * received would tell the API to clear it, which is the shape that wiped every category image behind
     * 1,571 passing tests.
     */
    it('omits a key the server did not send, rather than sending null', async () => {
      // Rebuilt rather than mutated: `groups` is `readonly`, which is the contract saying out loud that a
      // payload is what the server sent and not a thing to edit in place.
      const full = payloadWithInvoice();
      const narrowed: AdminSettings = {
        ...full,
        groups: full.groups.map((group, index) =>
          index === 0
            ? {
                ...group,
                settings: group.settings.filter(
                  (setting) => setting.key !== SETTING_KEYS.vatNumber,
                ),
              }
            : group,
        ),
      };

      await open(narrowed);

      await type('legalName', 'A Renamed Company (Pvt) Ltd');
      await clickSaveInvoice();

      const saved = writeTo(AdminSettingsApi.invoiceAndFulfilmentUrl);
      const body = saved.request.body as Record<string, unknown>;

      expect('vatNumber' in body).toBe(false);
      expect(body['legalName']).toBe('A Renamed Company (Pvt) Ltd');

      saved.flush(null);
      fixture.detectChanges();
      await fixture.whenStable();

      controller.expectOne(AdminSettingsApi.settingsUrl).flush(narrowed);
    });

    it('shows the API’s message against the box it refused', async () => {
      await open(payloadWithInvoice());

      await type('autoDeliverAfterDays', '0');
      await clickSaveInvoice();

      writeTo(AdminSettingsApi.invoiceAndFulfilmentUrl).flush(
        {
          title: 'One or more fields are invalid.',
          errors: {
            autoDeliverAfterDays: [
              'Use between 1 and 90 days. Nought would mark an order delivered the moment it ships.',
            ],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );

      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(
        element.querySelector('[data-testid="error-autoDeliverAfterDays"]')?.textContent,
      ).toContain('between 1 and 90');
      expect(field('autoDeliverAfterDays').getAttribute('aria-invalid')).toBe('true');
    });
  });

});

/**
 * The mapping from payload to form values, on its own.
 *
 * **This header said the opposite of the test under it, and W3-03 corrected it.** It read "a key the server
 * did not send stays null, which is correct" — and the test below is called "omits a key the server did not
 * send, rather than sending null for it", because a null in a `PUT` body tells the API to *clear* the key.
 * The sentence was left over from the version `toForm` had before ADM-22's round 1 fixed it, and it is
 * exactly the hazard this project keeps finding: a wrong comment stops the next reader checking.
 *
 * What is true: a key the payload did not carry is **absent** from the form, so `JSON.stringify` drops it
 * and the API leaves that setting alone. A caller who cannot read a group therefore cannot clear it — and
 * that caller now exists, because W3-03's `Bank.*` keys need `Settings.Payments`, which no default role
 * holds. The mapping is no longer written to survive a hypothetical.
 */
describe('toForm (ADM-22)', () => {
  it('reads each value by key', async () => {
    const form = toForm({
      groups: [
        {
          groupKey: 'Contact',
          rowVersion: 'rv',
          settings: [
            {
              key: SETTING_KEYS.whatsApp,
              valueType: 'String',
              value: '94771234567',
              isSecret: false,
              hasValue: true,
              requiredPermission: 'Settings.Edit',
              description: null,
              updatedAtUtc: null,
            },
          ],
        },
      ],
      logo: null,
    });

    // Only the key the payload carried. See the omission test below for why the other three are absent
    // rather than null.
    expect(form).toEqual({ whatsApp: '94771234567' });
  });

  /**
   * **A key the server did not send is OMITTED, not sent back as null.**
   *
   * `PUT` replaces, so a `null` in the body tells the API to clear the key. The first version of `toForm`
   * returned `null` for a key the payload did not contain, and its comment claimed that was safe — the code
   * did the opposite of what the comment said. The moment #53 adds a `Settings.Edit`-without-`Social` role,
   * that caller's screen receives no social addresses, sends four nulls, and their first WhatsApp save wipes
   * the client's social links. W1-12's Blocker, one feature over.
   *
   * The key *set* is what is asserted, because that is what reaches the wire: `JSON.stringify` drops an
   * `undefined` property, so "absent" and "undefined" are the same thing to the API and a value of `null` is
   * not.
   */
  it('omits a key the server did not send, rather than sending null for it', async () => {
    const form = toForm({
      groups: [
        {
          groupKey: 'Contact',
          rowVersion: 'rv',
          settings: [
            {
              key: SETTING_KEYS.whatsApp,
              valueType: 'String',
              value: '94771234567',
              isSecret: false,
              hasValue: true,
              requiredPermission: 'Settings.Edit',
              description: null,
              updatedAtUtc: null,
            },
          ],
        },
      ],
      logo: null,
    });

    expect(Object.keys(form)).toEqual(['whatsApp']);
    expect(JSON.parse(JSON.stringify(form))).toEqual({ whatsApp: '94771234567' });
  });

  it('keeps an explicit null, because that is how a client clears a key', async () => {
    const form = toForm({
      groups: [
        {
          groupKey: 'Social',
          rowVersion: 'rv',
          settings: [
            {
              key: SETTING_KEYS.tikTokUrl,
              valueType: 'String',
              value: null,
              isSecret: false,
              hasValue: false,
              requiredPermission: 'Settings.Edit',
              description: null,
              updatedAtUtc: null,
            },
          ],
        },
      ],
      logo: null,
    });

    expect(Object.keys(form)).toEqual(['tikTokUrl']);
    expect(form.tikTokUrl).toBeNull();
  });

  it('omits every key for a payload with no groups', async () => {
    expect(Object.keys(toForm({ groups: [], logo: null }))).toEqual([]);
  });
});

describe('PayPal form mapping (PAY-02)', () => {
  /** A payload holding PayPal's keys, as the server sends them: the secret has a flag and no value. */
  function sample(withExpiry = true): AdminSettings {
    const row = (key: string, value: string | null, isSecret = false) => ({
      key,
      valueType: (isSecret ? 'Secret' : 'String') as 'Secret' | 'String',
      value,
      isSecret,
      hasValue: isSecret ? true : Boolean(value),
      requiredPermission: 'Settings.Payments',
      description: null,
      updatedAtUtc: null,
    });

    return {
      groups: [
        {
          groupKey: 'Payments.PayPal',
          rowVersion: 'rv',
          settings: [
            row(SETTING_KEYS.payPalMode, 'sandbox'),
            row(SETTING_KEYS.payPalClientId, 'client'),
            row(SETTING_KEYS.payPalClientSecret, null, true),
            row(SETTING_KEYS.payPalWebhookId, 'webhook'),
            row(SETTING_KEYS.payPalLkrPerUsd, '300.00'),
            row(SETTING_KEYS.payPalFxBufferPercent, '3.00'),
            row(SETTING_KEYS.payPalRateMaxAgeDays, '7'),
            row(SETTING_KEYS.payPalRateUpdatedAtUtc, '2026-10-05T04:30:00.000Z'),
            row(SETTING_KEYS.payPalCaptureLockHours, '3'),
            row(SETTING_KEYS.payPalCaptureRecoveryMaxHours, '24'),
            row(SETTING_KEYS.payPalPendingCaptureMaxDays, '5'),
          ],
        },
        ...(withExpiry
          ? [{ groupKey: 'Orders', rowVersion: 'rv', settings: [row(SETTING_KEYS.payPalExpiryMinutes, '60')] }]
          : []),
      ],
      logo: null,
    };
  }

  describe('toPayPalForm', () => {
    it('reads only the keys the payload contained, and never the secret', () => {
      const form = toPayPalForm(sample());

      expect(Object.keys(form).sort()).toEqual(
        [
          'mode',
          'clientId',
          'webhookId',
          'lkrPerUsd',
          'fxBufferPercent',
          'rateMaxAgeDays',
          'captureLockHours',
          'captureRecoveryMaxHours',
          'pendingCaptureMaxDays',
          'expiryMinutes',
        ].sort(),
      );
      expect('clientSecret' in form).toBe(false);
    });

    it('leaves out the keys an unreadable group would have carried, so a save cannot clear them', () => {
      expect(Object.keys(toPayPalForm({ groups: [], logo: null }))).toEqual([]);
      expect(Object.keys(toPayPalForm(sample(false))).includes('expiryMinutes')).toBe(false);
    });
  });

  describe('formatStamp', () => {
    it('writes a UTC instant as Colombo time with the offset named', () => {
      expect(formatStamp('2026-10-05T04:30:00.000Z')).toContain('10:00');
      expect(formatStamp('2026-10-05T04:30:00.000Z')).toContain('Sri Lanka time');
    });

    it('shows what it cannot read as it is, never as Invalid Date', () => {
      expect(formatStamp('not a date')).toBe('not a date');
    });
  });
});
