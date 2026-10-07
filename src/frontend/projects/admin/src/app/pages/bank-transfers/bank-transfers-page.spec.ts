import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BankTransfersPage } from './bank-transfers-page';
import { BankTransferApi } from '../../core/orders/bank-transfer-api';
import {
  BankTransferQueuePage,
  BankTransferQueueRow,
} from '../../core/orders/bank-transfer.model';

const ORDER = '44444444-4444-4444-8444-444444444444';

/**
 * Every field carries a real value.
 *
 * W1-12's blocker survived 1,571 passing tests because every fixture set the columns it cleared to
 * `null`, so the round trip compared null with null and agreed. A fixture whose reference and slip are
 * real is the only kind that can fail when the row stops rendering them.
 */
function row(overrides: Partial<BankTransferQueueRow> = {}): BankTransferQueueRow {
  return {
    orderPublicId: ORDER,
    orderNumber: 'SD-2026-000123',
    customerName: 'Nimali Perera',
    customerEmail: 'nimali@example.com',
    amountLkr: 15_596.6,
    currency: 'LKR',
    payerReference: 'CBC-778812',
    slipAssetPublicId: '66666666-6666-4666-8666-666666666666',
    slipContentType: 'image/jpeg',
    placedAtUtc: '2026-10-05T04:30:00Z',
    paymentDueAtUtc: '2026-10-07T04:30:00Z',
    secondsUntilExpiry: 40 * 60 * 60,
    ...overrides,
  };
}

function page(overrides: Partial<BankTransferQueuePage> = {}): BankTransferQueuePage {
  return {
    rows: [row()],
    page: 1,
    pageSize: 20,
    totalCount: 1,
    ...overrides,
  };
}

/**
 * The bank-transfer queue screen (PAY-01, ADM-09, doc 11 P7 step 4).
 *
 * **The outgoing request bodies and the DOM after a decision are the heart of this file**, for the reason
 * `settings-page.spec.ts` states: the API stores whatever it is handed, so the thing that can be wrong is
 * what the *client* sends and what it then shows.
 */
describe('BankTransfersPage (PAY-01, ADM-09)', () => {
  let fixture: ComponentFixture<BankTransfersPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload: BankTransferQueuePage = page()): Promise<void> {
    fixture = TestBed.createComponent(BankTransfersPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(`${BankTransferApi.queueUrl}?page=1&pageSize=20`).flush(payload);

    await settle();
  }

  /** The antiforgery token every mutating call fetches first. */
  function answerAntiforgery(): void {
    http
      .match((request) => request.url.includes('antiforgery'))
      .forEach((request) => request.flush({ token: 'token' }));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('shows what doc 11 P7 step 4 lists', async () => {
    await open();

    const text = element.textContent ?? '';

    expect(text).toContain('SD-2026-000123');
    expect(text).toContain('Nimali Perera');
    expect(text).toContain('nimali@example.com');
    expect(
      element.querySelector('[data-testid="amount-SD-2026-000123"]')?.textContent,
    ).toContain('15,596.60');
    expect(
      element.querySelector('[data-testid="reference-SD-2026-000123"]')?.textContent,
    ).toContain('CBC-778812');

    // The slip is a download keyed on the **order**, never on the asset: a private file has no public URL
    // and the only parties who may see it name an order (doc 08 §3.3).
    expect(
      element.querySelector('[data-testid="slip-SD-2026-000123"]')?.getAttribute('href'),
    ).toBe(`${BankTransferApi.queueUrl}/${ORDER}/slip`);
  });

  it('says how long is left, from the server figure', async () => {
    await open(page({ rows: [row({ secondsUntilExpiry: (5 * 60 * 60) + (30 * 60) })] }));

    expect(element.querySelector('[data-testid="time-left-SD-2026-000123"]')?.textContent).toContain(
      '5 h 30 min left',
    );
  });

  it('says how overdue a row is rather than clamping it to zero', async () => {
    await open(page({ rows: [row({ secondsUntilExpiry: -11 * 60 })] }));

    // "Overdue by eleven minutes" and "due now" are different facts to somebody deciding whether to
    // approve, which is why the server sends a negative number rather than a floor of zero.
    expect(element.querySelector('[data-testid="time-left-SD-2026-000123"]')?.textContent).toContain(
      'Overdue by 11 min',
    );

    // And an overdue row offers the action that will actually work (doc 11 P7 step 8).
    expect(element.querySelector('[data-testid="accept-late-SD-2026-000123"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="approve-SD-2026-000123"]')).toBeNull();
  });

  it('approves and reports the invoice number', async () => {
    await open();

    element.querySelector<HTMLButtonElement>('[data-testid="approve-SD-2026-000123"]')!.click();
    await settle();

    answerAntiforgery();
    await settle();

    const decision = http.expectOne(`${BankTransferApi.queueUrl}/${ORDER}/approve`);
    expect(decision.request.method).toBe('POST');
    expect(decision.request.body).toEqual({ note: null });

    decision.flush({ orderNumber: 'SD-2026-000123', invoiceNumber: 'INV-2026-000045' });
    await settle();

    const done = element.querySelector('[data-testid="done-SD-2026-000123"]');
    expect(done?.textContent).toContain('INV-2026-000045');

    // **The row keeps its place.** Removing the node the staff member's focus is inside does the same
    // harm as disabling it (issue #109's second half), so the decided row stays with its outcome and
    // leaves on the next Refresh.
    expect(element.querySelector('[data-testid="row-SD-2026-000123"]')).not.toBeNull();

    // And no re-read was issued, because a re-read is what would have torn the list out.
    http.expectNone(`${BankTransferApi.queueUrl}?page=1&pageSize=20`);
  });

  it('sends the note with a rejection and refuses an empty one without a request', async () => {
    await open();

    element.querySelector<HTMLButtonElement>('[data-testid="reject-SD-2026-000123"]')!.click();
    await settle();

    // Refused in the browser, so a round trip is not spent being told the box is empty. The server's
    // check is still the one that matters.
    expect(element.querySelector('[data-testid="refused-SD-2026-000123"]')?.textContent).toContain(
      'Say why',
    );
    http.expectNone(`${BankTransferApi.queueUrl}/${ORDER}/reject`);

    const note = element.querySelector<HTMLInputElement>('[data-testid="note-SD-2026-000123"]')!;
    note.value = '  The amount is Rs. 500 short.  ';
    note.dispatchEvent(new Event('input'));
    await settle();

    element.querySelector<HTMLButtonElement>('[data-testid="reject-SD-2026-000123"]')!.click();
    await settle();

    answerAntiforgery();
    await settle();

    const decision = http.expectOne(`${BankTransferApi.queueUrl}/${ORDER}/reject`);
    expect(decision.request.body).toEqual({ note: 'The amount is Rs. 500 short.' });

    decision.flush({ orderNumber: 'SD-2026-000123', invoiceNumber: null });
    await settle();

    expect(element.querySelector('[data-testid="done-SD-2026-000123"]')?.textContent).toContain(
      'stays open until its deadline',
    );
  });

  /**
   * Doc 11 P7's business rule: "if the expiry job or another staff member acted first, the approver is
   * told and the screen reloads."
   */
  it('offers Accept late payment when an approval is refused because the order expired', async () => {
    await open();

    element.querySelector<HTMLButtonElement>('[data-testid="approve-SD-2026-000123"]')!.click();
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(`${BankTransferApi.queueUrl}/${ORDER}/approve`).flush(
      {
        title: 'This decision could not be applied',
        detail:
          'This order expired before the transfer was approved. If the money has arrived, use Accept '
          + 'late payment and the stock will be re-checked.',
        code: 'order_expired',
        orderStatus: 'Expired',
        paymentStatus: 'Expired',
      },
      { status: 409, statusText: 'Conflict' },
    );

    await settle();

    // The server's own sentence, which says what to do next — and the button that does it.
    expect(element.querySelector('[data-testid="refused-SD-2026-000123"]')?.textContent).toContain(
      'Accept late payment',
    );
    expect(element.querySelector('[data-testid="accept-late-SD-2026-000123"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="approve-SD-2026-000123"]')).toBeNull();
  });

  it('names every short line when a late acceptance is refused', async () => {
    await open(page({ rows: [row({ secondsUntilExpiry: -60 })] }));

    element.querySelector<HTMLButtonElement>('[data-testid="accept-late-SD-2026-000123"]')!.click();
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(`${BankTransferApi.queueUrl}/${ORDER}/accept-late`).flush(
      {
        title: 'Some items are no longer in stock',
        detail: 'Some of its items have been sold since it expired.',
        code: 'short_stock',
        shortLines: { '470': 0, '471': 1 },
      },
      { status: 409, statusText: 'Conflict' },
    );

    await settle();

    const refused = element.querySelector('[data-testid="refused-SD-2026-000123"]')?.textContent ?? '';

    // Both, not just the first: a staff member told about one of two will press the button again and be
    // told about the other (doc 11 P7 step 8).
    expect(refused).toContain('variant 470 (0 left)');
    expect(refused).toContain('variant 471 (1 left)');
  });

  /**
   * Issue #109. Six instances so far, and every one of them was a control that said `disabled`.
   */
  it('marks the actions unavailable with aria-disabled and refuses a second press', async () => {
    await open();

    const approve = element.querySelector<HTMLButtonElement>('[data-testid="approve-SD-2026-000123"]')!;

    approve.click();
    await settle();

    expect(approve.getAttribute('aria-disabled')).toBe('true');
    expect(approve.hasAttribute('disabled')).toBe(false);

    // A second press while the first is in flight does nothing, which is what the attribute advertises.
    approve.click();
    await settle();

    answerAntiforgery();
    await settle();

    http
      .expectOne(`${BankTransferApi.queueUrl}/${ORDER}/approve`)
      .flush({ orderNumber: 'SD-2026-000123', invoiceNumber: 'INV-2026-000045' });

    await settle();
  });

  /**
   * ADM-22's defect: `role="status"` announces a *change*, so two identical sentences in a row are read
   * out once and then never again.
   *
   * **On this screen the guarantee comes from the alternation, not from the trailing space, and
   * establishing which cost this test two rewrites.** Every decision says "Approving the transfer for
   * SD-… " on the way in and its outcome on the way out, so two identical refusals are separated by a
   * different sentence and both are already changes — which is exactly ADM-22's own fix for the admin
   * screens. `announce`'s trailing space is the backstop for a sentence that has no counterpart, and on
   * this page nothing reaches it; `OrderPage` has one that does (copying the same value twice).
   *
   * So what is asserted is the property that matters — **both refusals are audible** — rather than a
   * mechanism that happens not to fire. The first version asserted the two strings differed and failed,
   * correctly, with the two refusals byte-identical.
   */
  it('announces both of two identical refusals, because a different sentence falls between them', async () => {
    await open();

    const region = element.querySelector('[data-testid="announcement"]')!;
    const announced: string[] = [];

    async function refuse(): Promise<void> {
      element.querySelector<HTMLButtonElement>('[data-testid="approve-SD-2026-000123"]')!.click();
      await settle();

      announced.push(region.textContent ?? '');

      answerAntiforgery();
      await settle();

      http.expectOne(`${BankTransferApi.queueUrl}/${ORDER}/approve`).flush(
        {
          detail: 'This order has already been paid.',
          code: 'order_already_paid',
          orderStatus: 'Paid',
          paymentStatus: 'Succeeded',
        },
        { status: 409, statusText: 'Conflict' },
      );

      await settle();
      announced.push(region.textContent ?? '');
    }

    await refuse();
    await refuse();

    expect(announced).toHaveLength(4);
    expect(announced[0]).toContain('Approving the transfer for SD-2026-000123');
    expect(announced[1].trim()).toBe('This order has already been paid.');
    expect(announced[3].trim()).toBe('This order has already been paid.');

    // Every consecutive pair is a change, which is the whole requirement: a region set to the text it
    // already holds is not announced, and nothing here is.
    for (let index = 1; index < announced.length; index += 1) {
      expect(announced[index]).not.toBe(announced[index - 1]);
    }
  });

  it('says so when nothing is waiting', async () => {
    await open(page({ rows: [], totalCount: 0 }));

    expect(element.querySelector('[data-testid="queue-empty"]')?.textContent).toContain(
      'Nothing is waiting',
    );
  });

  it('shows the refusal rather than an empty screen when the caller lacks the permission', async () => {
    fixture = TestBed.createComponent(BankTransfersPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http
      .expectOne(`${BankTransferApi.queueUrl}?page=1&pageSize=20`)
      .flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });

    await settle();

    // The API is the security boundary (doc 03 §3a); the route only asks for a session, so the page has
    // to be able to explain itself. And no "Try again" for a refusal trying again cannot fix.
    expect(element.querySelector('[data-testid="page-error"]')?.textContent).toContain('permission');
    expect(element.querySelector('[data-testid="page-error"] button')).toBeNull();
  });
});
