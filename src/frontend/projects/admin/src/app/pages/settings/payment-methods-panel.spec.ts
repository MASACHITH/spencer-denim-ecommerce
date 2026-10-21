import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import {
  PaymentProviderRow,
  PaymentProvidersApi,
  PaymentProvidersPanel,
} from '../../core/settings/payment-providers-api';
import { PaymentMethodsPanel } from './payment-methods-panel';

function provider(overrides: Partial<PaymentProviderRow> = {}): PaymentProviderRow {
  return {
    code: 'BankTransfer',
    displayName: 'Bank or online transfer',
    isEnabled: false,
    isReady: true,
    isOffered: false,
    rowVersion: 'AAAAAAAAA+E=',
    ...overrides,
  };
}

function panel(overrides: Partial<PaymentProvidersPanel> = {}): PaymentProvidersPanel {
  return {
    providers: [
      provider(),
      provider({ code: 'PayPal', displayName: 'PayPal', isReady: false, rowVersion: 'AAAAAAAAA+F=' }),
    ],
    anyOffered: false,
    ...overrides,
  };
}

/** The Payment methods panel (owner decision 2026-10-21: PayPal only, bank transfer "block only for now"). */
describe('PaymentMethodsPanel', () => {
  let fixture: ComponentFixture<PaymentMethodsPanel>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload: PaymentProvidersPanel = panel()): Promise<void> {
    fixture = TestBed.createComponent(PaymentMethodsPanel);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(PaymentProvidersApi.url).flush(payload);
    await settle();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  async function flip(code: string, checked: boolean): Promise<void> {
    const box = byTestId<HTMLInputElement>(`payment-method-switch-${code}`)!;
    box.checked = checked;
    box.dispatchEvent(new Event('change'));
    await settle();
    http.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'token' });
    await settle();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('warns that checkout refuses every order while no method is offered', async () => {
    await open();

    expect(byTestId('payment-methods-none')?.textContent).toContain('Checkout will refuse every order');
    expect(byTestId('payment-method-state-PayPal')?.textContent).toContain('not ready');
    expect(byTestId<HTMLInputElement>('payment-method-switch-BankTransfer')!.checked).toBe(false);
  });

  it('switches bank transfer back on with its row version and redraws from the reply', async () => {
    await open();

    await flip('BankTransfer', true);

    const request = http.expectOne(`${PaymentProvidersApi.url}/BankTransfer`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ isEnabled: true, rowVersion: 'AAAAAAAAA+E=' });

    request.flush(
      panel({
        providers: [provider({ isEnabled: true, isOffered: true, rowVersion: 'AAAAAAAAA+G=' }), panel().providers[1]],
        anyOffered: true,
      }),
    );
    await settle();

    expect(byTestId('payment-methods-none')).toBeNull();
    expect(byTestId('payment-method-state-BankTransfer')?.textContent).toContain('offered at checkout');
  });

  it('shows the refusal beside a method that is not ready, and puts the box back', async () => {
    await open();

    await flip('PayPal', true);

    http
      .expectOne(`${PaymentProvidersApi.url}/PayPal`)
      .flush(
        { title: 'One or more validation errors occurred.', errors: { isEnabled: ['PayPal is not ready to take payments yet.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle();

    expect(byTestId('payment-method-error-PayPal')?.textContent).toContain('not ready');
    expect(byTestId<HTMLInputElement>('payment-method-switch-PayPal')!.checked).toBe(false);
  });

  it('reloads the panel after a 409', async () => {
    await open();

    await flip('BankTransfer', true);
    http
      .expectOne(`${PaymentProvidersApi.url}/BankTransfer`)
      .flush({ detail: 'Somebody else changed this payment method.', code: 'stale_row_version' }, { status: 409, statusText: 'Conflict' });
    await settle();

    http.expectOne(PaymentProvidersApi.url).flush(panel());
    await settle();

    expect(byTestId('payment-method-error-BankTransfer')?.textContent).toContain('reloaded');
  });

  it('draws nothing for an admin the API refuses', async () => {
    fixture = TestBed.createComponent(PaymentMethodsPanel);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(PaymentProvidersApi.url).flush(null, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(byTestId('payment-methods-panel')).toBeNull();
  });
});
