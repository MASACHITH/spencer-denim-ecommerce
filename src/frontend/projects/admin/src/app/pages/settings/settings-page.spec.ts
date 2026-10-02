import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import { AdminSettings, SETTING_KEYS } from '../../core/settings/admin-settings.model';
import { SettingsPage, toForm } from './settings-page';

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
          ],
        },
      ],
      logo,
    };
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
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

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
      }),
    );

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
    ]);

    expect(body['whatsApp']).toBe('94771234567');

    // The value that was already there is sent back, not dropped: the API replaces the group.
    expect(body['facebookUrl']).toBe('https://www.facebook.com/kept');

    // And the ones nobody filled in are sent as null rather than omitted, so "clear it" is explicit.
    expect(body['instagramUrl']).toBeNull();
    expect(body['tikTokUrl']).toBeNull();
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
});

/**
 * The mapping from payload to form values, on its own.
 *
 * A key the server did not send stays null, which is correct: a caller who could not read `Social.*` would
 * otherwise have the three addresses cleared by their next save of the WhatsApp number. No such caller exists
 * today — both groups are `Settings.Edit` — which is exactly why the mapping is written to survive one.
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

    expect(form).toEqual({
      whatsApp: '94771234567',
      facebookUrl: null,
      instagramUrl: null,
      tikTokUrl: null,
    });
  });

  it('leaves every field null for a payload with no groups', async () => {
    expect(toForm({ groups: [], logo: null })).toEqual({
      whatsApp: null,
      facebookUrl: null,
      instagramUrl: null,
      tikTokUrl: null,
    });
  });
});
