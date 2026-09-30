import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { TwoFactorApi } from '../../core/two-factor/two-factor-api';
import { TwoFactorSetupPage } from './two-factor-setup-page';

const uri =
  'otpauth://totp/Spencer%20Denim%20Admin:admin%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Spencer%20Denim%20Admin&digits=6';

describe('TwoFactorSetupPage', () => {
  let controller: HttpTestingController;
  let navigateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TwoFactorSetupPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
    navigateSpy = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(() => Promise.resolve(true));
  });

  afterEach(() => controller.verify());

  function flushStatus(enabled = false, required = true): void {
    controller.expectOne(TwoFactorApi.statusUrl).flush({ enabled, required, canDisable: false, recoveryCodesLeft: 0 });
  }

  function flushSetup(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'the-token' });
    const request = controller.expectOne(TwoFactorApi.setupUrl);
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    request.flush({ sharedKey: 'jbsw y3dp ehpk 3pxp jbsw y3dp ehpk 3pxp', authenticatorUri: uri });
  }

  async function createAtScan(required = true): Promise<ComponentFixture<TwoFactorSetupPage>> {
    const fixture = TestBed.createComponent(TwoFactorSetupPage);
    fixture.detectChanges();
    flushStatus(false, required);
    flushSetup();
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  function enableWith(fixture: ComponentFixture<TwoFactorSetupPage>, code: string) {
    fixture.componentInstance['code'] = code;
    fixture.componentInstance['confirm']();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 't' });
    return controller.expectOne(TwoFactorApi.enableUrl);
  }

  it('asks for the status before starting set-up, never both at once', () => {
    const fixture = TestBed.createComponent(TwoFactorSetupPage);
    fixture.detectChanges();

    controller.expectNone(TwoFactorApi.setupUrl);
    flushStatus();
    flushSetup();
  });

  it('shows a QR code drawn in the page, the key to type and why it is required', async () => {
    const root = (await createAtScan(true)).nativeElement as HTMLElement;

    const svg = root.querySelector('svg.setup__qr');
    expect(svg?.getAttribute('role')).toBe('img');
    expect(svg?.querySelector('title')?.textContent).toContain('QR code');
    expect(svg?.querySelector('path')?.getAttribute('d')?.length).toBeGreaterThan(100);
    expect(root.querySelector('.setup__key')?.textContent).toContain('jbsw y3dp');
    expect(root.textContent).toContain('Super Admin accounts must use two-factor authentication');
    expect(root.querySelector('img')).toBeNull();
  });

  it('asks for the code with a labelled numeric one-time-code field', async () => {
    const root = (await createAtScan()).nativeElement as HTMLElement;
    const input = root.querySelector('input[name="code"]') as HTMLInputElement;

    expect(input.getAttribute('autocomplete')).toBe('one-time-code');
    expect(input.getAttribute('inputmode')).toBe('numeric');
    expect(input.closest('label')?.textContent?.trim().length).toBeGreaterThan(0);
  });

  it('shows a wrong code as an error on the field and focuses it', async () => {
    const fixture = await createAtScan();

    enableWith(fixture, '000000').flush(
      { error: 'invalid_code', detail: 'That code is not valid.' },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();
    await fixture.whenStable();

    const input = (fixture.nativeElement as HTMLElement).querySelector('input[name="code"]') as HTMLInputElement;
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toContain('setup-error');
    expect(document.activeElement).toBe(input);
  });

  it('shows the recovery codes once enabled and continues only after the user says they are saved', async () => {
    const fixture = await createAtScan();

    const request = enableWith(fixture, '123 456');
    expect(request.request.body).toEqual({ code: '123 456' });
    request.flush({ recoveryCodes: ['AAAAA-BBBBB', 'CCCCC-DDDDD'] });
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('.codes__code').length).toBe(2);
    expect(root.querySelector('svg.setup__qr')).toBeNull();
    const continueButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.includes('Continue')) as HTMLButtonElement;
    expect(continueButton.disabled).toBe(true);

    fixture.componentInstance['saved'].set(true);
    fixture.detectChanges();
    continueButton.click();

    expect(navigateSpy).toHaveBeenCalledWith('/');
  });

  it('says so when two-factor authentication is already on, without starting set-up', () => {
    const fixture = TestBed.createComponent(TwoFactorSetupPage);
    fixture.detectChanges();
    flushStatus(true);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('already on');
    controller.expectNone(TwoFactorApi.setupUrl);
  });

  it('sends a signed-out user to the sign-in page', () => {
    const fixture = TestBed.createComponent(TwoFactorSetupPage);
    fixture.detectChanges();

    controller.expectOne(TwoFactorApi.statusUrl).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(navigateSpy).toHaveBeenCalledWith('/login');
  });
});
