import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminSettingsApi } from '../../core/settings/admin-settings-api';
import { AdminSettingGroup, SETTING_KEYS } from '../../core/settings/admin-settings.model';
import { EmailAccountPanel } from './email-account-panel';

/**
 * The email account panel (ADM-22, 2026-10-21). As with the rest of the settings screen, the outgoing request
 * bodies are the point: the password must be sent only when typed, every text field must be sent, and the switch as
 * a boolean.
 */
describe('EmailAccountPanel (ADM-22)', () => {
  let controller: HttpTestingController;
  let fixture: ComponentFixture<EmailAccountPanel>;
  let element: HTMLElement;

  function group(
    values: Partial<Record<string, string | null>> = {},
    passwordSet = false,
  ): AdminSettingGroup {
    const keys = [
      SETTING_KEYS.smtpHost,
      SETTING_KEYS.smtpPort,
      SETTING_KEYS.smtpUsername,
      SETTING_KEYS.smtpPassword,
      SETTING_KEYS.smtpFromAddress,
      SETTING_KEYS.smtpFromName,
      SETTING_KEYS.smtpUseStartTls,
    ];

    return {
      groupKey: 'Email.Smtp',
      rowVersion: 'smtp-rv-1',
      settings: keys.map((key) => ({
        key,
        valueType: key === SETTING_KEYS.smtpPassword ? ('Secret' as const) : ('String' as const),
        value: key === SETTING_KEYS.smtpPassword ? null : (values[key] ?? null),
        isSecret: key === SETTING_KEYS.smtpPassword,
        hasValue: key === SETTING_KEYS.smtpPassword ? passwordSet : Boolean(values[key]),
        requiredPermission: 'Settings.Security',
        description: null,
        updatedAtUtc: null,
      })),
    };
  }

  const saved = {
    [SETTING_KEYS.smtpHost]: 'smtp.saved.test',
    [SETTING_KEYS.smtpPort]: '587',
    [SETTING_KEYS.smtpUsername]: 'saved-user',
    [SETTING_KEYS.smtpFromAddress]: 'shop@saved.test',
    [SETTING_KEYS.smtpFromName]: 'Saved Name',
    [SETTING_KEYS.smtpUseStartTls]: 'true',
  };

  async function render(input: AdminSettingGroup): Promise<void> {
    TestBed.configureTestingModule({
      imports: [EmailAccountPanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(EmailAccountPanel);
    fixture.componentRef.setInput('group', input);
    fixture.detectChanges();
    await fixture.whenStable();
    element = fixture.nativeElement as HTMLElement;
  }

  function box(name: string): HTMLInputElement {
    return element.querySelector(`[data-testid="smtp-field-${name}"]`) as HTMLInputElement;
  }

  function type(name: string, value: string): void {
    const input = box(name);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function click(testId: string): void {
    (element.querySelector(`[data-testid="${testId}"]`) as HTMLElement).click();
    fixture.detectChanges();
  }

  function answerToken(): void {
    controller.expectOne('/api/admin/auth/antiforgery-token').flush({ token: 'xsrf' });
  }

  afterEach(() => controller.verify());

  it('shows the saved values and never a password, only whether one is saved', async () => {
    await render(group(saved, true));

    expect(box('host').value).toBe('smtp.saved.test');
    expect(box('port').value).toBe('587');
    expect(box('fromName').value).toBe('Saved Name');
    expect(box('password').value).toBe('');
    expect(box('password').type).toBe('password');
    expect(box('password').getAttribute('autocomplete')).toBe('new-password');
    expect(element.querySelector('[data-testid="smtp-password-state"]')?.textContent).toContain(
      'A password is saved.',
    );
    expect(box('useStartTls').checked).toBe(true);
  });

  it('counts an unsaved encryption switch as on', async () => {
    await render(group({}));

    expect(box('useStartTls').checked).toBe(true);
  });

  it('saves every text field and the switch, and leaves the password out when the box is empty', async () => {
    await render(group(saved, true));
    const emitted = vi.fn();
    fixture.componentInstance.saved.subscribe(emitted);

    type('host', 'smtp.new.test');
    click('save-smtp');
    answerToken();

    const request = controller.expectOne(AdminSettingsApi.smtpUrl);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      host: 'smtp.new.test',
      port: '587',
      username: 'saved-user',
      fromAddress: 'shop@saved.test',
      fromName: 'Saved Name',
      useStartTls: true,
      rowVersion: 'smtp-rv-1',
    });
    expect('password' in request.request.body).toBe(false);
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect(emitted).toHaveBeenCalledTimes(1);
  });

  it('sends a typed password as typed, then forgets it', async () => {
    await render(group(saved));

    type('password', ' new pass ');
    click('save-smtp');
    answerToken();

    const request = controller.expectOne(AdminSettingsApi.smtpUrl);
    expect(request.request.body.password).toBe(' new pass ');
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();
    await fixture.whenStable();

    expect(box('password').value).toBe('');
  });

  it('sends the switch off as false', async () => {
    await render(group(saved));

    box('useStartTls').click();
    fixture.detectChanges();
    click('save-smtp');
    answerToken();

    const request = controller.expectOne(AdminSettingsApi.smtpUrl);
    expect(request.request.body.useStartTls).toBe(false);
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('shows a field error beside its box', async () => {
    await render(group(saved));

    click('save-smtp');
    answerToken();
    controller
      .expectOne(AdminSettingsApi.smtpUrl)
      .flush(
        {
          title: 'One or more fields are invalid.',
          errors: { port: ['Enter the port as a whole number between 1 and 65535.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="smtp-error-port"]')?.textContent).toContain(
      'whole number',
    );
    expect(box('port').getAttribute('aria-invalid')).toBe('true');
  });

  it('tests the account as typed, without a row version, and says where the test went', async () => {
    await render(group(saved));

    type('fromName', 'Typed Name');
    click('test-smtp');
    answerToken();

    const request = controller.expectOne(AdminSettingsApi.smtpTestUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.fromName).toBe('Typed Name');
    expect('rowVersion' in request.request.body).toBe(false);
    expect('password' in request.request.body).toBe(false);
    request.flush({ sentTo: 'me@shop.test' });
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="smtp-test-sent"]')?.textContent).toContain(
      'me@shop.test',
    );
  });

  it("shows the server's safe sentence when a test fails", async () => {
    await render(group(saved));

    click('test-smtp');
    answerToken();
    controller
      .expectOne(AdminSettingsApi.smtpTestUrl)
      .flush(
        { detail: 'The mail server refused the user name or password.', code: 'smtp_test_failed' },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
    fixture.detectChanges();

    expect(element.querySelector('[data-testid="smtp-error"]')?.textContent).toContain(
      'refused the user name or password',
    );
  });

  it('ignores a second press while busy', async () => {
    await render(group(saved));

    click('save-smtp');
    click('save-smtp');
    click('test-smtp');
    answerToken();

    controller
      .expectOne(AdminSettingsApi.smtpUrl)
      .flush(null, { status: 204, statusText: 'No Content' });
    controller.expectNone(AdminSettingsApi.smtpTestUrl);
  });
});
