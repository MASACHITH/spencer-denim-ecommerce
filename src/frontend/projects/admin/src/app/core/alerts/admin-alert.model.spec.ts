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
    templateCode: 'BankTransferInstructions',
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

  /**
   * **The order the message names is not in the database.**
   *
   * `DeadEmailAlerts` could not resolve the public id, so the number is null and the id is not. There is
   * no order to open — the link would 404 — and nobody to telephone, because the customer's name and
   * number live on the order. The first version of this advice said "Open the order, then telephone or
   * WhatsApp the customer" directly beneath a title reading "for an order that can no longer be found".
   */
  it('does not send anybody to an order that is not there', () => {
    const advice = alertAdvice(alert({ orderNumber: null }));

    expect(advice).toContain('not in the database');
    expect(advice).toContain('Tell the technical owner');
    expect(advice).not.toContain('Open the order');
    expect(advice).not.toContain('telephone or WhatsApp');
  });

  /**
   * **Doc 11 §4 carves the account emails out one by one, and the first version of this did not.**
   *
   * It said "ask the customer to request a new link themselves" for all of them. Doc 11 gives
   * `StaffInvite` its own remedy (P14 step 7: a Super Admin resets that user's password, which sends a
   * fresh set-password link) and `PasswordReset` is customer-match only (§4.1), so an invited staff
   * member cannot self-serve at all. The "has usually expired" clause is doc 11's about the two
   * token-bearing templates, not about `Welcome` or `AccountExists`.
   *
   * None of these templates exists yet — all five are W3-01's — so none of this is reachable today. It is
   * committed advice that goes live the day accounts land.
   */
  describe('a dead account email (doc 11 §4)', () => {
    function noOrder(templateCode: string | null): string {
      return alertAdvice(alert({ orderPublicId: null, orderNumber: null, templateCode }));
    }

    it('sends a dead StaffInvite to a Super Admin, not to the invited user', () => {
      const advice = noOrder('StaffInvite');

      expect(advice).toContain('A Super Admin resets');
      expect(advice).toContain('set-password link');
      expect(advice).toContain('cannot request one themselves');
      expect(advice).not.toContain('Ask the customer');
    });

    it('asks the customer for a fresh link only for the two token-bearing templates', () => {
      for (const templateCode of ['PasswordReset', 'EmailConfirmation']) {
        const advice = noOrder(templateCode);

        expect(advice).toContain('Ask the customer to request a fresh link');
        expect(advice).toContain('single-use token');
      }
    });

    it('does not claim an expired link for the two that carry no link', () => {
      for (const templateCode of ['Welcome', 'AccountExists']) {
        const advice = noOrder(templateCode);

        expect(advice).toContain('no link in it to re-issue');
        expect(advice).not.toContain('expired');
        expect(advice).not.toContain('Ask the customer to request');
      }
    });

    it('invents no remedy for a template it has never met, or for a missing template code', () => {
      for (const templateCode of ['SomethingW301Adds', null]) {
        const advice = noOrder(templateCode);

        expect(advice).toContain('nothing to open');
        expect(advice).toContain('template code above');
        expect(advice).not.toContain('Super Admin resets');
        expect(advice).not.toContain('Ask the customer');
        expect(advice).not.toContain('expired');
      }
    });
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

  /**
   * Every branch of the advice, swept for the two things none of them may say. Enumerated rather than
   * sampled, because a branch added later is a sentence nobody re-read.
   */
  it('never claims that acknowledging fixes anything, on any branch', () => {
    const everyBranch = [
      alert(),
      alert({ orderNumber: null }),
      alert({ alertType: 'PaymentException' }),
      ...['StaffInvite', 'PasswordReset', 'EmailConfirmation', 'Welcome', 'AccountExists', null].map(
        (templateCode) => alert({ orderPublicId: null, orderNumber: null, templateCode }),
      ),
    ];

    for (const candidate of everyBranch) {
      const advice = alertAdvice(candidate).toLowerCase();

      expect(advice).not.toContain('resolve the');
      expect(advice).not.toContain('will be sent');
      expect(advice).not.toContain('resend it');
    }

    // The sweep has to actually cover every branch, so the distinct sentences are counted: four order
    // cases and four account-email cases, with the two token-bearing and the two link-less templates
    // sharing one sentence each.
    expect(new Set(everyBranch.map(alertAdvice)).size).toBe(7);
  });
});
