/**
 * The admin customers screen's payloads (ADM-15, doc 11 P11 step 16), field for field with
 * `SpencerDenim.Application.Admin.Customers.Contracts`. `AdminCustomerShapeTests` pins the pairing in both
 * directions, so a field added on one side and not the other fails a test rather than reading `undefined`.
 */

/** One page of the customer list. */
export interface AdminCustomerListPage {
  readonly rows: readonly AdminCustomerListRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  /** The district filter's options, from the server, so the screen invents none. */
  readonly districts: readonly AdminDistrictOption[];
  /** The country filter's options: Sri Lanka and every country a customer's default address is in. */
  readonly countries: readonly AdminCountryOption[];
}

export interface AdminDistrictOption {
  readonly id: number;
  readonly name: string;
}

/** A country the list can be filtered by (UAT feedback, 2026-10-24). */
export interface AdminCountryOption {
  /** ISO 3166-1 alpha-2, e.g. `GB`. */
  readonly code: string;
  readonly name: string;
}

/** One registered customer. */
export interface AdminCustomerListRow {
  readonly customerId: number;
  readonly name: string;
  readonly email: string;
  readonly emailConfirmed: boolean;
  readonly phone: string | null;
  /** The customer's default delivery district, or null when they have not chosen one. */
  readonly districtName: string | null;
  readonly isActive: boolean;
  readonly registeredAtUtc: string;
  /** Every order on the account, paid or not. */
  readonly orderCount: number;
  /** The total of the account's paid orders. */
  readonly lifetimeSpend: number;
  readonly currency: string;
  readonly lastOrderAtUtc: string | null;
  /**
   * The country of the customer's default saved address, or null when they have none (owner decision
   * 2026-10-21).
   */
  readonly countryName: string | null;
}

/** One customer in full. There is no anonymise action: that is doc 11 P17, a separate task. */
export interface AdminCustomerDetail {
  readonly customerId: number;
  readonly firstName: string;
  readonly lastName: string | null;
  readonly email: string;
  readonly emailConfirmed: boolean;
  readonly phone: string | null;
  readonly districtName: string | null;
  readonly isActive: boolean;
  readonly registeredAtUtc: string;
  readonly lastSignInAtUtc: string | null;
  readonly orderCount: number;
  readonly lifetimeSpend: number;
  readonly currency: string;
  readonly lastOrderAtUtc: string | null;
  readonly addresses: readonly AdminCustomerAddress[];
}

/** One saved address. Default first. */
export interface AdminCustomerAddress {
  readonly addressId: number;
  readonly label: string | null;
  readonly recipientName: string;
  readonly phone: string;
  readonly line1: string;
  readonly line2: string | null;
  readonly city: string;
  /** The Sri Lankan district, or null for an overseas address. */
  readonly districtName: string | null;
  readonly postalCode: string | null;
  readonly landmark: string | null;
  readonly isDefault: boolean;
  /** ISO 3166-1 alpha-2, `LK` for Sri Lanka (owner decision 2026-10-21). */
  readonly countryCode: string;
  readonly countryName: string;
  /** State, province or region of an overseas address; null for Sri Lanka. */
  readonly region: string | null;
}

/** One page of a customer's orders, newest first. */
export interface AdminCustomerOrderPage {
  readonly rows: readonly AdminCustomerOrderRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

/** One order on the account. Opened on the admin order screen by its public token. */
export interface AdminCustomerOrderRow {
  readonly orderPublicId: string;
  readonly orderNumber: string;
  readonly status: string;
  readonly statusLabel: string;
  readonly grandTotal: number;
  readonly currency: string;
  readonly placedAtUtc: string;
  readonly paidAtUtc: string | null;
}
