/**
 * The shapes `/api/account` answers with (ACC-01, ACC-02, doc 11 P11).
 *
 * Each interface mirrors one server record and is named after it, so a field that moves on the server is
 * a compile error here rather than an `undefined` in a template:
 *
 * | here | server |
 * |---|---|
 * | {@link CustomerSessionDto} | `CustomerSessionResponse` (`CustomerAccountRequests.cs`) |
 * | {@link AccountMessageDto} | `AccountMessageResponse` |
 * | {@link EmailConfirmedDto} | `CustomerEmailConfirmedResponse` |
 * | {@link CustomerProfileDto} | `SpencerDenim.Application.Accounts.Contracts.CustomerProfile` |
 * | {@link CustomerAddressDto} | `CustomerAddressDto` |
 * | {@link CustomerOrderSummaryDto} | `CustomerOrderSummaryDto` |
 * | {@link CustomerOrderTrackingDto} | `CustomerOrderTrackingDto` |
 * | {@link CustomerOrderPageDto} | `CustomerOrderPageDto` |
 *
 * **No field here is optional because it was convenient.** `tracking` is the one `?` in the file, and it
 * is `?` because the server deliberately *omits* the property rather than sending `null` — see
 * {@link CustomerOrderSummaryDto.tracking}. Everything else that can be absent is typed `| null`,
 * because the server sends an explicit `null` for it and `undefined` would be a different bug.
 *
 * **There is no copy of the password rule in this file**, and that is deliberate rather than an
 * oversight. `CustomerPasswordPolicy` (Domain) says in its own remarks that "the front end reads it from
 * the registration endpoint's problem details rather than holding a second copy", because #126 shipped a
 * `maxlength` of 120 against a 100-character column. The cost is that a shopper learns the rule from a
 * refusal rather than before typing; the task report raises that as a question for the owner.
 */

/** Who is signed in, as the shell and the account pages need them. */
export interface CustomerSessionDto {
  readonly userId: number;
  readonly email: string;
  readonly firstName: string;
  readonly lastName: string | null;

  /**
   * Doc 11 §7 G-24: **never a gate.** An unconfirmed customer signs in, shops and checks out exactly as
   * a confirmed one does. It is what the "confirm your email" note is drawn from, and the only thing it
   * actually governs is whether earlier *guest* orders have been linked into the history (doc 08 §3.1
   * D5).
   */
  readonly emailConfirmed: boolean;
}

/**
 * One sentence, from the endpoints whose whole answer is one sentence.
 *
 * **The page prints this string and does not write its own.** Registration, forgot-password,
 * reset-password and resend-confirmation each answer wording that was chosen on the server for two
 * reasons the storefront cannot see: doc 08 §3.1 DB10's non-enumeration (one sentence true of a free
 * address, a taken one and a staff one alike), and the fact that the mail transport is unconfigured, so
 * the server says "queued" and never "sent". W3-02 removed "we will email your order number" from
 * checkout for the second reason and `checkout-page.spec.ts` pins its absence.
 */
export interface AccountMessageDto {
  readonly message: string;
}

/** What confirming an address did (doc 11 P11 step A.4). */
export interface EmailConfirmedDto {
  readonly customer: CustomerSessionDto;

  /**
   * The guest orders that became this customer's, newest first, and **often empty** — a customer who
   * registered before they ever ordered has none. Doc 11 P11's own example is the reason the page prints
   * them: "registers with the email used for 3 guest orders → after confirming, all 3 appear".
   */
  readonly linkedOrderNumbers: readonly string[];
}

/** A customer's own profile, as My account shows it (doc 11 P11 step 10). */
export interface CustomerProfileDto {
  readonly userId: number;

  /** Read-only on this screen: the change-email flow is not built on the server. */
  readonly email: string;
  readonly emailConfirmed: boolean;
  readonly firstName: string;
  readonly lastName: string | null;
  readonly phone: string | null;
  readonly defaultDistrictId: number | null;

  /** Joined in by the server so the screen can name the district without fetching the list of 25. */
  readonly defaultDistrictName: string | null;
}

/** What the profile form sends (`CustomerProfileRequest`). */
export interface CustomerProfileEdit {
  readonly firstName: string;
  readonly lastName: string | null;
  readonly phone: string | null;
  readonly defaultDistrictId: number | null;
}

/** One entry in the address book (doc 11 P11 step 11). */
export interface CustomerAddressDto {
  readonly id: number;
  readonly label: string | null;
  readonly recipientName: string;
  readonly phone: string;
  readonly line1: string;
  readonly line2: string | null;
  readonly city: string;
  readonly districtId: number;

  /** Joined in, so a district the shop has since deactivated still reads as its own name. */
  readonly districtName: string;

  /**
   * False when the shop has stopped serving that district since the address was saved. An ordinary
   * state, not an error — `ref.Districts.IsActive` is admin-editable — and the card says so rather than
   * offering an address checkout will not quote for.
   */
  readonly districtIsActive: boolean;
  readonly postalCode: string | null;
  readonly landmark: string | null;
  readonly isDefault: boolean;
}

/** What the address form sends (`CustomerAddressRequest`). */
export interface CustomerAddressEdit {
  readonly label: string | null;
  readonly recipientName: string;
  readonly phone: string;
  readonly line1: string;
  readonly line2: string | null;
  readonly city: string;
  readonly districtId: number | null;
  readonly postalCode: string | null;
  readonly landmark: string | null;
  readonly isDefault: boolean;
}

/**
 * How to follow a shipped order (doc 11 P11 step 12's "courier tracking link").
 *
 * `courierName` and `trackingNumber` are a snapshot taken when the order shipped, so renaming or
 * deleting a carrier does not rewrite a past dispatch. `trackingUrl` is built from the courier's
 * **current** template and is null when that courier has no online tracking or has since been deleted —
 * in which case the page prints the number as text, which is still the thing the shopper needs.
 */
export interface CustomerOrderTrackingDto {
  readonly courierName: string;
  readonly trackingNumber: string;
  readonly trackingUrl: string | null;
  readonly shippedAtUtc: string;
}

/** One row of the order history (ACC-02, doc 11 P11 step 12). */
export interface CustomerOrderSummaryDto {
  readonly orderNumber: string;

  /**
   * The order's `PublicId`, which is the address of the order page: `/order/{token}` — the same route a
   * guest's emailed link uses, and deliberately the only one, so there is one ownership filter rather
   * than two. A `Guid` on the server and therefore a string in JSON.
   */
  readonly token: string;
  readonly status: string;

  /**
   * What the shopper reads, and **not** `status` lower-cased: doc 11 P9's pickup variant shows `Shipped`
   * as "Ready for collection" and `Delivered` as "Collected". It comes from the same server function the
   * two admin screens and the guest order page use, so the four cannot disagree — which is why this page
   * never builds a label of its own from `status`.
   */
  readonly statusLabel: string;
  readonly placedAtUtc: string;
  readonly grandTotal: number;
  readonly currency: string;

  /** Counting quantities, so a row reads "3 items" rather than a number of lines. */
  readonly itemCount: number;

  /** Null when the order's first line had no thumbnail: a gap, never a different picture. */
  readonly firstLineThumbnailUrl: string | null;

  /**
   * **Absent — not null — when there is nothing to track**, which is why this is the one `?` in the file.
   * An order that has not shipped and a store-pickup order, which has no courier at all, both come back
   * with no `tracking` property. `'tracking' in order` and `order.tracking != null` agree here; the
   * optional marker is what makes the *server's* choice visible to the next reader.
   */
  readonly tracking?: CustomerOrderTrackingDto;
}

/** A page of the order history. */
export interface CustomerOrderPageDto {
  readonly orders: readonly CustomerOrderSummaryDto[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;

  /** Computed by the server (`Page * PageSize < TotalCount`), so the two sides cannot disagree. */
  readonly hasMore: boolean;
}

/**
 * The page the order history asks for when nothing has been read yet.
 *
 * `page: 1` and `pageSize: 0` rather than 10: this value is never sent anywhere, and a page size written
 * here would be a second copy of `GetCustomerOrdersQueryHandler.DefaultPageSize`, which is the shape of
 * defect #126. The server decides the size and echoes it back, and the page prints what it echoed.
 */
export const EMPTY_ORDER_PAGE: CustomerOrderPageDto = {
  orders: [],
  page: 1,
  pageSize: 0,
  totalCount: 0,
  hasMore: false,
};

/**
 * The field widths the forms set `maxlength` from, read from the server's own columns.
 *
 * **Every number here is checked against the constant that owns the column**, named beside it, because
 * #126's defect was a `maxlength` of 120 against a 100-character column that passed the form, the stub
 * and sixteen browser tests. A `maxlength` wider than the column is a refusal a shopper cannot see
 * coming; one narrower is a field they cannot finish typing in. They are duplicated across the HTTP
 * boundary by necessity — the browser cannot read a C# constant — so they are gathered in one object
 * that one test can check rather than spread across eight templates.
 */
export const ACCOUNT_FIELD_LENGTHS = {
  /** `CustomerAccountValidator.NameMaxLength` — `identity.Users.FirstName`/`LastName`, nvarchar(80). */
  name: 80,

  /** `NewsletterEmail.MaxLength`, which `ValidateSignIn` and `ValidateRegistration` both use. */
  email: 256,

  /** `CustomerAccountValidator.PasswordMaxLength` — a ceiling on hashing cost, not a rule to meet. */
  password: 256,

  /** `CustomerAddress.PhoneMaxLength`. The profile's phone is validated against the same number. */
  phone: 20,

  /** `CustomerAddress.LabelMaxLength`. */
  label: 40,

  /** `CustomerAddress.RecipientNameMaxLength`. */
  recipientName: 120,

  /** `CustomerAddress.LineMaxLength`, for line 1 and line 2 alike. */
  line: 150,

  /** `CustomerAddress.CityMaxLength`. */
  city: 80,

  /** `CustomerAddress.PostalCodeMaxLength`. */
  postalCode: 10,

  /** `CustomerAddress.LandmarkMaxLength`. */
  landmark: 150,
} as const;

/**
 * Doc 08 §3.1's app cap, restated in doc 11 P11 step 11: ten saved addresses per customer.
 *
 * **The page never enforces it and never decides it.** The server counts inside the transaction that
 * inserts, so two tabs cannot both add an eleventh, and it answers 409 with its own `limit`. This
 * constant is used only to say *in advance* how many a shopper may keep, so the Add form can explain
 * itself before they fill it in — and the refusal the page prints is still the server's, with the
 * server's number in it.
 */
export const SAVED_ADDRESS_LIMIT = 10;

/** `firstName lastName`, or just the first name. The server's `lastName` is optional. */
export function customerName(customer: CustomerSessionDto | CustomerProfileDto): string {
  return customer.lastName ? `${customer.firstName} ${customer.lastName}` : customer.firstName;
}

/**
 * A UTC stamp as a shopper reads it: `7 October 2026`.
 *
 * `en-GB` with a long month, and the reasoning is `content-page.ts`'s: "7/10/2026" is ambiguous to half
 * the world, and an order date is one a shopper checks against a bank statement. A value the API could
 * not produce renders nothing rather than the string "Invalid Date".
 *
 * **Not `Intl.DateTimeFormat` with a timezone, deliberately.** The stamp is UTC and the shop is one time
 * zone (Asia/Colombo, UTC+5:30), so a browser set to another zone would print a different day from the
 * one the shop's own admin screens show for the same order. This takes the browser's zone anyway —
 * `toLocaleDateString` does — and that is the honest reading of "when did I place this", because the
 * alternative is a date that disagrees with the shopper's own calendar. It is a day, not a minute, so
 * the two differ only for an order placed within five and a half hours of midnight. Stated rather than
 * fixed: a `timeZone: 'Asia/Colombo'` here would need the same decision taken on the four admin screens
 * and the guest order page in the same change.
 */
export function accountDate(stamp: string | null | undefined): string | null {
  if (!stamp) {
    return null;
  }

  const parsed = new Date(stamp);

  return Number.isNaN(parsed.getTime())
    ? null
    : parsed.toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
}

/**
 * An empty or whitespace-only box as `null`.
 *
 * Every optional field on these forms goes through it, because the server's validator trims and reads
 * blank as absent: sending `''` would put an empty string into a nullable column, so a shopper who
 * cleared their phone number would have "no phone" stored as something rather than as nothing. One
 * function, so the registration form, the profile form and the address form cannot disagree about what
 * an emptied box means.
 */
export function blankToNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length === 0 ? null : trimmed;
}
