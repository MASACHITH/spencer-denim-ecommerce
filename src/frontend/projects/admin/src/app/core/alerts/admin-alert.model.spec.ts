import { AdminAlert, alertAdvice, severityLabel } from './admin-alert.model';

function alert(overrides: Partial<AdminAlert> = {}): AdminAlert {
  return {
    id: 7,
    alertType: 'EmailDeliveryFailed',
    severity: 2,
    title: 'Email not delivered: BankTransferInstructions for order SD-2026-000123 (outbox row 4821)',
    createdAtUtc: '2026-10-09T03:11:00Z',
    relatedEntityType: 'EmailOutbox',
    relatedEntityId: '4821',
    orderPublicId: '44444444-4444-4444-8444-444444444444',
    orderNumber: 'SD-2026-000123',
    ...overrides,
  };
}

describe('severityLabel (doc 08 §3.8)', () => {
  it('names the three the check constraint allows', () => {
    expect(severityLabel(1)).toBe('Information');
    expect(severityLabel(2)).toBe('Warning');
    expect(severityLabel(3)).toBe('Critical');
  });

  it('calls anything else a warning rather than printing a word about the software', () => {
    // CK_AdminAlerts_Severity keeps the column to 1-3, so this is unreachable from the API today. It is
    // pinned because the failure mode of the obvious alternative is bad: a banner that read "Unknown" for
    // a severity a later migration added would hide a real alert behind a complaint about itself.
    expect(severityLabel(0)).toBe('Warning');
    expect(severityLabel(9)).toBe('Warning');
  });
});

/**
 * **The sentence that must not promise anything this build cannot do.**
 *
 * Doc 11 §6.1 step 6 says to "resend from the order", and that action is doc 11 §7 G-42 — unspecified and
 * unbuilt. Two Majors on this project came from promising emails nothing could send. So the advice names
 * the telephone, and these tests assert the absence of the word as well as the presence of the advice,
 * because a later edit that helpfully added "or resend it from the order" would otherwise be silent.
 */
describe('alertAdvice (doc 11 §4, §7 G-37 and G-42)', () => {
  it('sends a Super Admin to the order and the telephone, and never to a resend button', () => {
    const advice = alertAdvice(alert());

    expect(advice).toContain('Open the order');
    expect(advice).toContain('telephone or WhatsApp');
    expect(advice).toContain('no way to resend an email');
    expect(advice.toLowerCase()).not.toContain('resend it');
    expect(advice.toLowerCase()).not.toContain('send it again from');
  });

  it('says there is nothing to open for a dead email that is not about an order', () => {
    const advice = alertAdvice(alert({ orderPublicId: null, orderNumber: null }));

    expect(advice).toContain('nothing to');
    expect(advice).toContain('request a new link');
    expect(advice).not.toContain('Open the order');
  });

  it('does not invent advice for the six alert types nothing raises yet', () => {
    // The settings, PayPal and reconciliation tasks own SensitiveSettingsChanged, PaymentException,
    // CaptureRecoveryFailed, ReconciliationDrift, BreakGlassUsed and DashboardRefund. Each of them will
    // want its own sentence; until then the banner shows the row's own title and says only what is true
    // of every alert.
    const advice = alertAdvice(alert({ alertType: 'PaymentException' }));

    expect(advice).toContain('Acknowledging only records that you have seen it');
    expect(advice).not.toContain('telephone');
  });

  it('never claims that acknowledging fixes anything', () => {
    for (const type of ['EmailDeliveryFailed', 'PaymentException']) {
      for (const orderPublicId of ['44444444-4444-4444-8444-444444444444', null]) {
        const advice = alertAdvice(alert({ alertType: type, orderPublicId }));

        expect(advice.toLowerCase()).not.toContain('resolve the');
        expect(advice.toLowerCase()).not.toContain('will be sent');
      }
    }
  });
});
