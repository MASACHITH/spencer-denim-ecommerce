import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { TwoFactorApi, type TwoFactorStatus } from '../../core/two-factor/two-factor-api';
import { SecurityPage } from './security-page';

describe('SecurityPage', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SecurityPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function status(overrides: Partial<TwoFactorStatus> = {}): TwoFactorStatus {
    return { enabled: true, required: false, canDisable: true, recoveryCodesLeft: 10, ...overrides };
  }

  function create(initial: TwoFactorStatus): ComponentFixture<SecurityPage> {
    const fixture = TestBed.createComponent(SecurityPage);
    fixture.detectChanges();
    controller.expectOne(TwoFactorApi.statusUrl).flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  function post(fixture: ComponentFixture<SecurityPage>, url: string, password: string) {
    fixture.componentInstance['password'] = password;
    fixture.componentInstance['submit']();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 't' });
    return controller.expectOne(url);
  }

  it('offers set-up when two-factor authentication is off', () => {
    const root = create(status({ enabled: false, canDisable: false })).nativeElement as HTMLElement;

    expect(root.textContent).toContain('Off.');
    expect(root.querySelector('a[href="/two-factor/setup"]')).not.toBeNull();
    expect(root.querySelector('input[name="currentPassword"]')).toBeNull();
  });

  it('does not offer to turn it off for a Super Admin', () => {
    const root = create(status({ required: true, canDisable: false })).nativeElement as HTMLElement;

    expect(root.querySelector('input[value="disable"]')).toBeNull();
    expect(root.textContent).toContain('cannot be turned off for a Super Admin');
  });

  it('warns when few recovery codes are left', () => {
    const root = create(status({ recoveryCodesLeft: 1 })).nativeElement as HTMLElement;

    expect(root.textContent).toContain('running out of recovery codes');
  });

  it('regenerates recovery codes with the password and shows the new ones', async () => {
    const fixture = create(status());

    const request = post(fixture, TwoFactorApi.recoveryCodesUrl, 'Current-1!');
    expect(request.request.body).toEqual({ currentPassword: 'Current-1!' });
    request.flush({ recoveryCodes: ['AAAAA-BBBBB'] });
    controller.expectOne(TwoFactorApi.statusUrl).flush(status());
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.codes__code')?.textContent).toContain('AAAAA-BBBBB');
    expect(root.textContent).toContain('old codes no longer work');
    expect(fixture.componentInstance['password']).toBe('');
  });

  it('turns it off with the password', () => {
    const fixture = create(status());
    fixture.componentInstance['chooseAction']('disable');

    post(fixture, TwoFactorApi.disableUrl, 'Current-1!').flush(null, { status: 204, statusText: 'No Content' });
    controller.expectOne(TwoFactorApi.statusUrl).flush(status({ enabled: false, canDisable: false, recoveryCodesLeft: 0 }));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Two-factor authentication is off.');
  });

  it('marks a wrong password invalid, links the error and focuses the field', async () => {
    const fixture = create(status());

    post(fixture, TwoFactorApi.recoveryCodesUrl, 'wrong').flush(
      { error: 'invalid_current_password', detail: 'The current password is incorrect.' },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();
    await fixture.whenStable();

    const input = (fixture.nativeElement as HTMLElement).querySelector('input[name="currentPassword"]') as HTMLInputElement;
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toContain('security-error');
    expect(document.activeElement).toBe(input);
  });

  it('moves focus to the message, not the field, when the account is locked', async () => {
    const fixture = create(status());

    post(fixture, TwoFactorApi.recoveryCodesUrl, 'wrong').flush(
      { error: 'locked_out', detail: 'Locked.' },
      { status: 423, statusText: 'Locked' },
    );
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(document.activeElement).toBe(root.querySelector('#security-error'));
    expect(root.querySelector('input[name="currentPassword"]')?.getAttribute('aria-invalid')).toBeNull();
  });
});
