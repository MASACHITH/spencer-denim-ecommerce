/**
 * `ops.AdminAlerts` as `/api/admin/alerts` sends it (doc 08 §3.8 N2; doc 11 §4.3).
 *
 * **Mirrored field for field from `AdminAlertDto`, and that is a rule rather than a coincidence.** Issue
 * #126: Playwright never starts the .NET API, so the browser tests run against Node stubs — a field this
 * interface and the stub both omit is a field no browser test can miss. The stub in
 * `e2e/support/api-stub.ts` is written from this file, and `AdminAlertsContractTests` on the API side
 * pins the payload so the two cannot drift apart silently.
 */
export interface AdminAlert {
  readonly id: number;
  /** One of the seven `AdminAlertTypes`. Only `EmailDeliveryFailed` has a producer in this build. */
  readonly alertType: string;
  /** 1 Info, 2 Warning, 3 Critical — the database's own numbers (doc 08 §3.8). */
  readonly severity: number;
  /** The facts, in one sentence. What can be done about them is {@link alertAdvice}'s. */
  readonly title: string;
  readonly createdAtUtc: string;
  readonly relatedEntityType: string | null;
  readonly relatedEntityId: string | null;
  /** The order to open, when the alert names one. The token `/orders/:orderPublicId` takes. */
  readonly orderPublicId: string | null;
  /** That order's number, so a link can say where it goes. */
  readonly orderNumber: string | null;
}

/** The banner's payload: what this user may see, and how many there are in all. */
export interface AdminAlertBanner {
  readonly totalOpen: number;
  readonly alerts: AdminAlert[];
}

/** What acknowledging answered. */
export interface AdminAlertAcknowledgement {
  readonly alertId: number;
  readonly alertType: string;
  readonly acknowledgedAtUtc: string;
  /** True when somebody else had already cleared it. Not a failure (doc 08 §3.8). */
  readonly alreadyAcknowledged: boolean;
}

/** The three severities, as a word a staff member reads. */
export function severityLabel(severity: number): string {
  switch (severity) {
    case 3:
      return 'Critical';
    case 1:
      return 'Information';
    default:
      // 2 is Warning, and so is anything a later migration adds that this build does not know: a banner
      // that printed "Unknown" for a severity it had not met would hide a real alert behind a word about
      // the software. The check constraint keeps the column to 1–3 (doc 08 §3.8).
      return 'Warning';
  }
}

/**
 * What a staff member can actually do about an alert **today**.
 *
 * **This is the sentence that has to stay honest, and it is why it lives in the screen and not in the
 * alert row.** Two Majors on this project came from promising something nothing could do, and W3-02 had
 * to strip "we will email your order number" off the checkout page. There is **no resend action anywhere
 * in this build**: doc 11 §6.1 step 6 says to "resend from the order", but that action is doc 11 §7 G-42,
 * still unspecified — the admin order screen has no resend control and the API has no route for one.
 * Telling a Super Admin to resend would send them looking for a button that does not exist. So the advice
 * is what the order page can really give them: the customer's name, email and telephone number, which
 * `AdminOrderDetailDto` carries and the order screen shows.
 *
 * The day G-42 lands, this one function changes and every alert ever written — including the ones already
 * in the table — starts giving the new advice.
 */
export function alertAdvice(alert: AdminAlert): string {
  if (alert.alertType !== 'EmailDeliveryFailed') {
    // The other six types have no producer in this build (the settings, PayPal and reconciliation tasks
    // own them). Rather than invent advice for an alert that cannot exist yet, the banner shows the row's
    // own title and says where the detail is.
    return 'Open the record this alert names and deal with it. Acknowledging only records that you have seen it.';
  }

  if (alert.orderPublicId !== null) {
    return (
      'Nothing will try to send this email again. Open the order, then telephone or WhatsApp the customer' +
      ' — this build has no way to resend an email, and a wrong address on a placed order cannot be' +
      ' corrected.'
    );
  }

  return (
    'Nothing will try to send this email again, and it is not about an order, so there is nothing to' +
    ' open. Ask the customer to request a new link themselves: a link that has been sitting in the queue' +
    ' for five hours has usually expired anyway.'
  );
}
