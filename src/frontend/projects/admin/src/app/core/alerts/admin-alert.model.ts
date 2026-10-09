/**
 * `ops.AdminAlerts` as `/api/admin/alerts` sends it (doc 08 §3.8 N2; doc 11 §4.3).
 *
 * **Mirrored field for field from `AdminAlertDto`, and that is a rule rather than a coincidence.** Issue
 * #126: Playwright never starts the .NET API, so the browser tests run against Node stubs — a field this
 * interface and the stub both omit is a field no browser test can miss. The stub in
 * `e2e/support/admin-catalog-stub.mjs` is written from this file, and `AdminAlertShapeTests` reads both
 * of them while `AdminAlertEndpointTests` reads the wire, so the three cannot drift apart silently.
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
  /**
   * That order's number, so a link can say where it goes.
   *
   * **Null with `orderPublicId` set means the order was not in the database when the alert was raised**,
   * and orders are never deleted (doc 08 §1.6), so it will not appear later. That pair is the
   * "order cannot be found" case: no link is offered for it.
   */
  readonly orderNumber: string | null;
  /**
   * The outbox template code of the message that died — G-37's third fact.
   *
   * Carried as a field rather than only inside {@link AdminAlert.title} because the advice branches on
   * it: doc 11 §4 gives a dead `StaffInvite` a different remedy from a dead `PasswordReset`. Null for an
   * alert type that is not about an email.
   */
  readonly templateCode: string | null;
}

/** The banner's payload: what this user may see, newest first, and whether there are more. */
export interface AdminAlertBanner {
  /**
   * True when this user has more open alerts than `alerts` holds.
   *
   * **A flag and not a total, for a measured reason.** A `COUNT(*)` over the same predicate cannot use
   * `IX_AdminAlerts_Open`: 5,020 logical reads against the list's 38, on 20,000 alerts, on every admin
   * page load (see `IAdminAlertReader` and `AdminAlertsPlanTests`). Doc 11 §6.1 step 6 tells a Super
   * Admin to open every alert, and this is what says whether they have finished.
   */
  readonly moreThanShown: boolean;
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
 * Template codes whose dead message has a remedy of its own (doc 11 §4).
 *
 * **Literals, and not constants exported from anywhere.** None of these templates exists yet — all five
 * are W3-01's (ACC-01, doc 11 P11) — and `EmailOutboxMessage.BodyClearedOnSend` made the same call on the
 * server for the same reason: a named constant for a template nothing can queue reads as one this build
 * sends. Every template code the six built order emails use goes down the order branches above these, so
 * nothing here is reachable today. It is written now because it is committed advice that goes live the day
 * accounts land, and wrong advice is worse than none.
 */
const STAFF_INVITE = 'StaffInvite';

/** The two whose link is a single-use token that has usually expired by the time anyone notices (doc 11 §4). */
const TOKEN_BEARING = ['PasswordReset', 'EmailConfirmation'];

/** The two that carry no link to re-issue at all. */
const NOTHING_TO_REISSUE = ['Welcome', 'AccountExists'];

/**
 * What a staff member can actually do about an alert **today**.
 *
 * **This is the sentence that has to stay honest, and it is why it lives in the screen and not in the
 * alert row.** Two Majors on this project came from promising something nothing could do, and W3-02 had
 * to strip "we will email your order number" off the checkout page. There is **no resend action anywhere
 * in this build**: doc 11 §6.1 step 6 says to "resend from the order", but that action is doc 11 §7 G-42,
 * still unspecified — the admin order screen has no resend control and the API has no route for one.
 * Telling a Super Admin to resend would send them looking for a button that does not exist.
 *
 * The day G-42 lands, this one function changes and every alert ever written — including the ones already
 * in the table — starts giving the new advice.
 */
export function alertAdvice(alert: AdminAlert): string {
  if (alert.alertType !== 'EmailDeliveryFailed') {
    // The other six types have no producer in this build (the settings, PayPal and reconciliation tasks
    // own them). Rather than invent advice for an alert that cannot exist yet, the banner shows the row's
    // own title and says only what is true of every alert.
    return 'Open the record this alert names and deal with it. Acknowledging only records that you have seen it.';
  }

  if (alert.orderNumber !== null) {
    // The order is there and the order screen carries the customer's name, email and telephone number
    // (`AdminOrderDetailDto`, rendered as a `tel:` link), which is what "telephone them" needs.
    return (
      'Nothing will try to send this email again. Open the order, then telephone or WhatsApp the customer' +
      ' — this build has no way to resend an email, and a wrong address on a placed order cannot be' +
      ' corrected.'
    );
  }

  if (alert.orderPublicId !== null) {
    // **The message named an order and the order was not there.** `DeadEmailAlerts.ReadOrderAsync` could
    // not resolve the public id, so there is no order to open — the link would 404 — and no customer to
    // telephone, because their name and number live on the order. Orders are never deleted (doc 08 §1.6)
    // and `ops.EmailOutbox` has no foreign key to them by design, so this is a message queued against an
    // order that was never saved: a fault in the shop rather than in the mail server.
    return (
      'Nothing will try to send this email again, and the order it names is not in the database — so' +
      ' there is no order to open and nobody to telephone from it. Tell the technical owner, with the' +
      ' outbox row and the template code above: an email queued against an order that is not there' +
      ' should not be possible.'
    );
  }

  return accountEmailAdvice(alert.templateCode);
}

/**
 * What to do about a dead message that is not about an order (doc 11 §4).
 *
 * **Doc 11 carves these out one by one and the first version of this function did not.** It said "ask the
 * customer to request a new link themselves", which is wrong twice over:
 *
 * - a dead **`StaffInvite`** is a new staff member who cannot self-serve at all — doc 11 §4 and P14 step 7
 *   say a Super Admin resets that user's password, which sends a fresh set-password link, and `PasswordReset`
 *   is **customer-match only** (§4.1) so the invited user asking for one gets nothing;
 * - the "has usually expired anyway" clause is doc 11's about **`PasswordReset` and `EmailConfirmation`**,
 *   the two whose link carries a single-use token. `Welcome` and `AccountExists` carry no link to re-issue,
 *   so there is nothing to ask for.
 */
function accountEmailAdvice(templateCode: string | null): string {
  if (templateCode === STAFF_INVITE) {
    return (
      'Nothing will try to send this invitation again, and it is not about an order. A Super Admin resets' +
      " that user's password, which sends them a fresh set-password link — they cannot request one" +
      ' themselves.'
    );
  }

  if (templateCode !== null && TOKEN_BEARING.includes(templateCode)) {
    return (
      'Nothing will try to send this email again, and it is not about an order, so there is nothing to' +
      ' open. Ask the customer to request a fresh link: the one in this message carries a single-use' +
      ' token and has usually expired by now.'
    );
  }

  if (templateCode !== null && NOTHING_TO_REISSUE.includes(templateCode)) {
    return (
      'Nothing will try to send this email again, and it is not about an order. There is no link in it to' +
      ' re-issue, so there is nothing to send for — but the customer did not get it, which is worth' +
      ' knowing if they get in touch.'
    );
  }

  // A template this build has never met, or an alert whose details carry no template code. Says what is
  // true of all of them and invents no remedy.
  return (
    'Nothing will try to send this email again, and it is not about an order, so there is nothing to open.' +
    ' The outbox row and the template code above are what identify it.'
  );
}
