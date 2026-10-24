import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AdminAlertsApi } from './admin-alerts-api';
import { AdminAlert, AdminAlertBanner } from './admin-alert.model';
import { AlertBell } from './alert-bell';

const ORDER = '44444444-4444-4444-8444-444444444444';

/**
 * Every field carries a real value — W1-12's lesson: a fixture whose nullable fields are all null
 * compares null with null and agrees even when the screen has stopped rendering them.
 */
function alert(overrides: Partial<AdminAlert> = {}): AdminAlert {
  return {
    id: 7,
    alertType: 'EmailDeliveryFailed',
    severity: 2,
    title: 'Email not delivered: BankTransferInstructions for order SD-2026-000123 (outbox row 4821)',
    createdAtUtc: '2026-10-09T03:11:00Z',
    relatedEntityType: 'EmailOutbox',
    relatedEntityId: '4821',
    orderPublicId: ORDER,
    orderNumber: 'SD-2026-000123',
    templateCode: 'BankTransferInstructions',
    ...overrides,
  };
}

function banner(overrides: Partial<AdminAlertBanner> = {}): AdminAlertBanner {
  return { moreThanShown: false, alerts: [alert()], ...overrides };
}

/**
 * The admin shell's alert bell (doc 08 §3.8 N2, doc 11 §4.3, §7 G-37), which opens the panel of alerts that used to be
 * a banner above every page (UAT feedback, 2026-10-24).
 *
 * **What this file is mostly about is the four accessibility rules**, each of which is a defect this
 * project has already shipped: an acknowledged alert that is not torn out from under the focus, no
 * `disabled` attribute anywhere, a confirmation in a node that survives the acknowledgement, and an
 * identical announcement that re-fires.
 */
describe('AlertBell (doc 08 §3.8 N2)', () => {
  let fixture: ComponentFixture<AlertBell>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  /** Loads the bell, and presses it so the panel is open, unless the test is about the closed bell. */
  async function open(payload: AdminAlertBanner | 'forbidden' = banner(), panel = true): Promise<void> {
    fixture = TestBed.createComponent(AlertBell);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);

    await settle();

    const request = http.expectOne(AdminAlertsApi.alertsUrl);

    if (payload === 'forbidden') {
      request.flush(null, { status: 403, statusText: 'Forbidden' });
    } else {
      request.flush(payload);
    }

    await settle();

    if (panel && payload !== 'forbidden') {
      press('alert-bell');
      await settle();
    }
  }

  function press(testId: string): void {
    (element.querySelector(`[data-testid="${testId}"]`) as HTMLElement).click();
  }

  async function acknowledge(
    id: number,
    reply: { acknowledgedAtUtc: string; alreadyAcknowledged: boolean } | 'gone',
  ): Promise<void> {
    (element.querySelector(`[data-testid="alert-ack-${id}"]`) as HTMLButtonElement).click();
    await settle();

    http.expectOne((r) => r.url.includes('antiforgery')).flush({ token: 'token' });
    await settle();

    const post = http.expectOne(`${AdminAlertsApi.alertsUrl}/${id}/acknowledge`);

    if (reply === 'gone') {
      post.flush(null, { status: 404, statusText: 'Not Found' });
    } else {
      post.flush({ alertId: id, alertType: 'EmailDeliveryFailed', ...reply });
    }

    await settle();
  }

  function text(): string {
    return element.textContent ?? '';
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    element?.remove();
  });

  it('shows the alert, what it is about and what can actually be done about it', async () => {
    await open();

    expect(element.querySelector('[data-testid="alert-panel"]')).not.toBeNull();
    expect(text()).toContain('1 alert needs attention');
    expect(text()).toContain('BankTransferInstructions');
    expect(text()).toContain('SD-2026-000123');
    expect(text()).toContain('outbox row 4821');
    expect(text()).toContain('Warning');

    // The one promise this screen is allowed to make, because the order page really does carry the
    // customer's telephone number (AdminOrderDetailDto.Phone).
    expect(text()).toContain('telephone or WhatsApp');
    expect(text()).toContain('no way to resend an email');

    expect(
      element.querySelector('[data-testid="alert-order-7"]')?.getAttribute('href'),
    ).toBe(`/orders/${ORDER}`);
  });

  it('says plainly that acknowledging does not fix anything', async () => {
    await open();

    expect(text()).toContain('records that you have seen it');
    expect(text()).toContain('does not fix the problem');
  });

  it('draws no bell at all when this user has no alerts they may see', async () => {
    // Doc 08 §3.8 R10 gives most staff no path to any alert, so the API answers 403. A bell that said
    // so would be a permanent error message, on every admin screen, about a feature that is not theirs.
    await open('forbidden');

    expect(element.querySelector('[data-testid="alert-bell"]')).toBeNull();
    expect(element.querySelector('[data-testid="alert-panel"]')).toBeNull();
    expect(text().trim()).toBe('');
  });

  it('draws a quiet bell, with no count, when the list is empty, and says so when it is opened', async () => {
    await open(banner({ alerts: [] }), false);

    expect(element.querySelector('[data-testid="alert-bell"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="alert-count"]')).toBeNull();
    expect(text()).toContain('Alerts: nothing needs attention');

    press('alert-bell');
    await settle();

    expect(element.querySelector('[data-testid="alert-none"]')?.textContent).toContain('Nothing needs your attention');
  });

  describe('the bell itself', () => {
    it('shows a red count and stays shut until it is pressed', async () => {
      await open(banner({ alerts: [alert(), alert({ id: 8 }), alert({ id: 9 })] }), false);

      const bell = element.querySelector('[data-testid="alert-bell"]') as HTMLButtonElement;

      expect(element.querySelector('[data-testid="alert-count"]')?.textContent?.trim()).toBe('3');
      expect(bell.textContent).toContain('Alerts: 3 need attention');
      expect(bell.getAttribute('aria-expanded')).toBe('false');
      expect(bell.getAttribute('aria-controls')).toBe('alert-panel');
      expect(element.querySelector('[data-testid="alert-panel"]')).toBeNull();

      bell.click();
      await settle();

      expect(bell.getAttribute('aria-expanded')).toBe('true');
      expect(element.querySelector('#alert-panel')).not.toBeNull();

      bell.click();
      await settle();

      expect(bell.getAttribute('aria-expanded')).toBe('false');
      expect(element.querySelector('[data-testid="alert-panel"]')).toBeNull();
    });

    it('says "needs" for one, and adds a plus when the server says there are more than it sent', async () => {
      await open(banner({ moreThanShown: true, alerts: [alert()] }), false);

      expect(element.querySelector('[data-testid="alert-count"]')?.textContent?.trim()).toBe('1+');
      expect(text()).toContain('Alerts: 1 or more need attention');

      fixture.destroy();
      await open(banner({ alerts: [alert()] }), false);

      expect(text()).toContain('Alerts: 1 needs attention');
    });

    it('counts down as alerts are acknowledged, and the panel stays open while it does', async () => {
      await open(banner({ alerts: [alert(), alert({ id: 8 })] }));

      await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T04:00:00Z', alreadyAcknowledged: false });

      expect(element.querySelector('[data-testid="alert-count"]')?.textContent?.trim()).toBe('1');
      expect(element.querySelector('[data-testid="alert-panel"]')).not.toBeNull();
      expect(text()).toContain('1 alert needs attention');

      await acknowledge(8, { acknowledgedAtUtc: '2026-10-09T04:01:00Z', alreadyAcknowledged: false });

      expect(element.querySelector('[data-testid="alert-count"]')).toBeNull();
      expect(text()).toContain('Everything here is acknowledged');
    });

    it('closes on Escape and puts focus back on the bell', async () => {
      await open();

      const bell = element.querySelector('[data-testid="alert-bell"]') as HTMLButtonElement;
      (element.querySelector('[data-testid="alert-ack-7"]') as HTMLButtonElement).focus();

      element.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await settle();

      expect(element.querySelector('[data-testid="alert-panel"]')).toBeNull();
      expect(document.activeElement).toBe(bell);
    });

    it('closes when something else on the page is pressed, but not when the panel itself is', async () => {
      await open();

      (element.querySelector('[data-testid="alert-7"]') as HTMLElement).click();
      await settle();
      expect(element.querySelector('[data-testid="alert-panel"]')).not.toBeNull();

      document.body.click();
      await settle();
      expect(element.querySelector('[data-testid="alert-panel"]')).toBeNull();
    });

    it('closes when the staff member goes to another screen, and reads the list again there', async () => {
      const router = TestBed.inject(Router);
      router.resetConfig([{ path: '**', children: [] }]);

      await open();

      // The page's own first navigation: it re-reads the list and leaves the panel as it is.
      await router.navigateByUrl('/');
      await settle();
      http.expectOne(AdminAlertsApi.alertsUrl).flush(banner());
      await settle();
      expect(element.querySelector('[data-testid="alert-panel"]')).not.toBeNull();

      // A second, different address is a new screen.
      await router.navigateByUrl('/orders');
      await settle();

      http.expectOne(AdminAlertsApi.alertsUrl).flush(banner({ alerts: [] }));
      await settle();

      expect(element.querySelector('[data-testid="alert-panel"]')).toBeNull();
      expect(element.querySelector('[data-testid="alert-count"]')).toBeNull();
    });

    it("stays open when the page's first navigation finishes after the bell was pressed", async () => {
      // A slow screen chunk lets the first NavigationEnd arrive after the bell is drawn and pressed. Shutting
      // the panel then closed it under the staff member's finger: the browser suite hit it about one run in three.
      const router = TestBed.inject(Router);
      router.resetConfig([{ path: '**', children: [] }]);
      await open();

      await router.navigateByUrl('/orders');
      await settle();

      http.expectOne(AdminAlertsApi.alertsUrl).flush(banner());
      await settle();

      expect(element.querySelector('[data-testid="alert-panel"]')).not.toBeNull();
    });
  });

  it('says there are more when the server says it is not showing them all', async () => {
    await open(banner({ moreThanShown: true, alerts: [alert()] }));

    expect(text()).toContain('1 alert needs attention · more are waiting after these');
  });

  it('does not say there are more when it is showing them all', async () => {
    // The boundary the one-extra-row read exists for: a banner showing every open alert must not send a
    // Super Admin looking for one that is not there.
    await open(banner({ moreThanShown: false, alerts: [alert(), alert({ id: 8 })] }));

    expect(text()).toContain('2 alerts need attention');
    expect(text()).not.toContain('more are waiting');

    await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T04:00:00Z', alreadyAcknowledged: false });
    await acknowledge(8, { acknowledgedAtUtc: '2026-10-09T04:01:00Z', alreadyAcknowledged: false });
  });

  /**
   * **There is no note field to type into** (owner's decision, 2026-10-09).
   *
   * Doc 08 §3.8 gives the table an `AcknowledgementNote` column and nothing writes it:
   * `ops.AdminAlerts` is not in doc 08 §3.1 step 2's anonymisation deletion list and doc 11 §4 says these
   * rows are never scrubbed, so a 500-character box beside a customer's order number was the one place
   * in this feature where a telephone number could be pasted into a table nothing can clean — and
   * nothing read it back. Asserted as an absence, because a control that came back would come back
   * silently.
   */
  it('offers nowhere to type an acknowledgement note', async () => {
    await open(banner({ alerts: [alert(), alert({ id: 8 })] }));

    expect(element.querySelectorAll('input').length).toBe(0);
    expect(element.querySelectorAll('textarea').length).toBe(0);
    expect(element.querySelector('[data-testid="alert-note-7"]')).toBeNull();
    expect(text().toLowerCase()).not.toContain('note');

    await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T04:00:00Z', alreadyAcknowledged: false });
    await acknowledge(8, { acknowledgedAtUtc: '2026-10-09T04:01:00Z', alreadyAcknowledged: false });
  });

  it('keeps the acknowledged alert on screen with its outcome, and moves focus to it', async () => {
    // **Rule 1 and the reason for it.** Removing the item would destroy the node holding the focus, which
    // is the same harm as issue #109's `[disabled]`: focus lands on `<body>` and a screen-reader user is
    // told nothing about what happened.
    await open();

    await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T04:00:00Z', alreadyAcknowledged: false });

    const done = element.querySelector('[data-testid="alert-done-7"]');
    expect(done).not.toBeNull();
    expect(element.querySelector('[data-testid="alert-7"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="alert-ack-7"]')).toBeNull();
    expect(done?.textContent).toContain('The problem itself is not resolved');
    expect(document.activeElement).toBe(done);
  });

  it('writes the confirmation into a live region that the acknowledgement cannot destroy', async () => {
    // **Rule 3.** A branch last week put a live region inside a block that success removed, and a
    // blind customer pressed Cancel and heard nothing. The region is this component's first child,
    // outside every `@if`,
    // so it is in the DOM before any alert loads and after the last one is cleared.
    fixture = TestBed.createComponent(AlertBell);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    await settle();

    const region = element.querySelector('[data-testid="alert-announcement"]');
    expect(region).not.toBeNull();
    expect(region?.getAttribute('role')).toBe('status');

    http.expectOne(AdminAlertsApi.alertsUrl).flush(banner());
    await settle();

    press('alert-bell');
    await settle();

    await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T04:00:00Z', alreadyAcknowledged: false });

    const after = element.querySelector('[data-testid="alert-announcement"]');
    expect(after).not.toBeNull();
    expect(after?.textContent).toContain('Acknowledged');
    expect(after?.textContent).toContain('does not resolve it');
  });

  it('re-fires an identical announcement', async () => {
    // **Rule 4 (ADM-22).** `role="status"` announces a change, and two alerts with the same title cleared
    // one after the other produce the same sentence twice — which would be read out once. Unlike the bank
    // queue there is no "…ing" sentence in between to alternate with, so the trailing space is the
    // guarantee here and not a backstop.
    await open(banner({ alerts: [alert(), alert({ id: 8 })] }));

    await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T04:00:00Z', alreadyAcknowledged: false });

    const first = element.querySelector('[data-testid="alert-announcement"]')!.textContent;

    await acknowledge(8, { acknowledgedAtUtc: '2026-10-09T04:01:00Z', alreadyAcknowledged: false });

    const second = element.querySelector('[data-testid="alert-announcement"]')!.textContent;

    expect(second).not.toBe(first);
    expect(second!.trim()).toBe(first!.trim());
  });

  it('never puts `disabled` on a button, and refuses a second press in the handler', async () => {
    // **Rule 2 (issue #109).** The button's state changes under the user, so it carries
    // `aria-disabled` and the handler refuses — and it stays focusable the whole time.
    await open();

    const button = element.querySelector('[data-testid="alert-ack-7"]') as HTMLButtonElement;

    button.click();
    await settle();

    expect(button.hasAttribute('disabled')).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.textContent?.trim()).toBe('Recording…');

    // A second press while the first is in flight must send nothing. `http.verify()` in afterEach is what
    // proves it: a second antiforgery fetch would be an unexpected request.
    button.click();
    await settle();

    http.expectOne((r) => r.url.includes('antiforgery')).flush({ token: 'token' });
    await settle();

    http
      .expectOne(`${AdminAlertsApi.alertsUrl}/7/acknowledge`)
      .flush({
        alertId: 7,
        alertType: 'EmailDeliveryFailed',
        acknowledgedAtUtc: '2026-10-09T04:00:00Z',
        alreadyAcknowledged: false,
      });

    await settle();
  });

  it('tells the second Super Admin who got there first, and does not call it a failure', async () => {
    await open();

    await acknowledge(7, { acknowledgedAtUtc: '2026-10-09T03:59:00Z', alreadyAcknowledged: true });

    expect(
      element.querySelector('[data-testid="alert-done-7"]')?.textContent,
    ).toContain('Somebody else acknowledged this');
    expect(element.querySelector('[data-testid="alert-error-7"]')).toBeNull();
    expect(
      element.querySelector('[data-testid="alert-announcement"]')?.textContent,
    ).toContain('still not resolved');
  });

  it('keeps the alert and the button when the acknowledgement fails', async () => {
    await open();

    await acknowledge(7, 'gone');

    expect(
      element.querySelector('[data-testid="alert-error-7"]')?.textContent,
    ).toContain('no longer there');
    expect(element.querySelector('[data-testid="alert-ack-7"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="alert-done-7"]')).toBeNull();
    expect(
      element.querySelector('[data-testid="alert-announcement"]')?.textContent,
    ).toContain('no longer there');
  });

  it('shows no order link for a dead email that is not about an order', async () => {
    await open(banner({
      alerts: [alert({ orderPublicId: null, orderNumber: null, templateCode: 'PasswordReset' })],
    }));

    expect(element.querySelector('[data-testid="alert-order-7"]')).toBeNull();
    expect(text()).toContain('Ask the customer to request a fresh link');
  });

  /** A dead `StaffInvite` goes to a Super Admin, not to the invited user (doc 11 §4, P14 step 7). */
  it('sends a dead staff invitation to a Super Admin', async () => {
    await open(banner({
      alerts: [alert({ orderPublicId: null, orderNumber: null, templateCode: 'StaffInvite' })],
    }));

    expect(text()).toContain('A Super Admin resets');
    expect(text()).not.toContain('Ask the customer');
  });

  /**
   * **No link when the order is not there, and this test replaced one that asserted the opposite.**
   *
   * The first version said "falls back to the public id when the order number is missing" and claimed the
   * link still worked "because the route is keyed on the public id". It does not: a null number means
   * `DeadEmailAlerts` could not find that order at all, so `/orders/{id}` 404s — and the advice beside it
   * was telling a Super Admin to open it and telephone the customer whose name and number are on it.
   * Orders are never deleted (doc 08 §1.6), so the order cannot turn up later either.
   */
  it('offers no link, and no telephone call, when the order is not in the database', async () => {
    await open(banner({ alerts: [alert({ orderNumber: null })] }));

    expect(element.querySelector('[data-testid="alert-order-7"]')).toBeNull();
    expect(text()).toContain('not in the database');
    expect(text()).toContain('Tell the technical owner');
    expect(text()).not.toContain('telephone or WhatsApp');

    // The title still names all three of G-37's facts, which is what the technical owner needs.
    expect(text()).toContain('BankTransferInstructions');
    expect(text()).toContain('outbox row 4821');
  });
});
