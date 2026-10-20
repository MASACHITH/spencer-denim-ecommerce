/**
 * The dashboard's and the reports' payloads (ADM-04, ADM-05, ADM-07, ADM-08, ADM-19), field for field with
 * `SpencerDenim.Application.Admin.Reporting.Contracts`. `AdminReportingShapeTests` pins the pairing in both
 * directions, and pins the Playwright stub's copies too.
 *
 * Every date is a **Colombo** calendar day, `yyyy-MM-dd`, both ends included. Every money figure is LKR.
 */

/** A run of Colombo days. */
export interface ReportPeriod {
  readonly from: string;
  readonly to: string;
  readonly days: number;
}

/**
 * One figure against the same figure for the previous period.
 *
 * `percentChange` is `null` when the previous value is zero or absent — a rise from nothing is not a
 * percentage of anything — so the screen words that case from `change` instead. `current` is `null` only for
 * an average with nothing to divide by.
 */
export interface ComparedFigure {
  readonly current: number | null;
  readonly previous: number | null;
  readonly change: number | null;
  readonly percentChange: number | null;
}

export interface DashboardKpis {
  readonly grossRevenue: ComparedFigure;
  readonly refundsCompleted: ComparedFigure;
  readonly netRevenue: ComparedFigure;
  readonly orders: ComparedFigure;
  readonly paidOrders: ComparedFigure;
  readonly averageOrderValue: ComparedFigure;
}

/** A department or a category: line revenue after promo, without delivery. */
export interface SalesShareRow {
  readonly id: number;
  readonly name: string;
  readonly units: number;
  readonly revenue: number;
  readonly sharePercent: number;
}

export interface TopProductRow {
  readonly productId: number;
  readonly name: string;
  readonly productCode: string;
  readonly units: number;
  readonly revenue: ComparedFigure;
}

export interface OrderStatusCount {
  readonly status: string;
  readonly label: string;
  readonly count: number;
}

export interface PaymentMethodRow {
  readonly code: string;
  readonly name: string;
  readonly paidOrders: number;
  readonly revenue: number;
}

/** Where buyers had their orders delivered — not the default district on a customer account. */
export interface BuyersByDistrict {
  readonly distinctBuyers: number;
  readonly districts: readonly DistrictBuyersRow[];
}

export interface DistrictBuyersRow {
  readonly districtId: number;
  readonly name: string;
  readonly buyers: number;
  readonly paidOrders: number;
  readonly revenue: number;
}

export interface AdminDashboard {
  readonly period: ReportPeriod;
  readonly previousPeriod: ReportPeriod;
  readonly currency: string;
  readonly kpis: DashboardKpis;
  readonly departments: readonly SalesShareRow[];
  readonly topCategories: readonly SalesShareRow[];
  readonly topProducts: readonly TopProductRow[];
  readonly orderStatuses: readonly OrderStatusCount[];
  readonly paymentMethods: readonly PaymentMethodRow[];
  readonly buyersByDistrict: BuyersByDistrict;
}

export type TrendInterval = 'Week' | 'Month';

export interface SalesTrendPoint {
  readonly start: string;
  readonly end: string;
  readonly grossRevenue: number;
  readonly paidOrders: number;
  readonly orders: number;
}

export interface SalesTrend {
  readonly interval: TrendInterval;
  readonly window: ReportPeriod;
  readonly currency: string;
  readonly points: readonly SalesTrendPoint[];
}

export interface LowStockRow {
  readonly variantId: number;
  readonly productId: number;
  readonly productName: string;
  readonly productCode: string;
  readonly sku: string;
  readonly colour: string;
  readonly size: string;
  readonly available: number;
  readonly threshold: number;
}

export interface LowStock {
  readonly defaultThreshold: number;
  readonly totalCount: number;
  readonly rows: readonly LowStockRow[];
}

// ---- reports --------------------------------------------------------------------------------------------

export type SalesGrouping = 'Day' | 'Week' | 'Month';

export interface SalesReportRow {
  readonly start: string;
  readonly end: string;
  readonly orders: number;
  readonly paidOrders: number;
  readonly grossRevenue: number;
  readonly itemsRevenue: number;
  readonly deliveryRevenue: number;
  readonly refundsCompleted: number;
  readonly netRevenue: number;
}

export interface SalesReport {
  readonly period: ReportPeriod;
  readonly groupBy: SalesGrouping;
  readonly currency: string;
  readonly rows: readonly SalesReportRow[];
  readonly totals: SalesReportRow;
}

export type InventoryStockFilter = 'All' | 'InStock' | 'LowStock' | 'OutOfStock';

export const INVENTORY_FILTERS: readonly InventoryStockFilter[] = ['All', 'InStock', 'LowStock', 'OutOfStock'];

export interface InventoryStatusCounts {
  readonly inStock: number;
  readonly lowStock: number;
  readonly outOfStock: number;
}

export interface InventoryReportRow {
  readonly variantId: number;
  readonly productId: number;
  readonly productName: string;
  readonly productCode: string;
  readonly department: string;
  readonly sku: string;
  readonly colour: string;
  readonly size: string;
  readonly onHand: number;
  readonly reserved: number;
  readonly available: number;
  readonly threshold: number;
  readonly thresholdIsDefault: boolean;
  readonly stockStatus: Exclude<InventoryStockFilter, 'All'>;
}

export interface InventoryReportPage {
  readonly rows: readonly InventoryReportRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly defaultThreshold: number;
  readonly counts: InventoryStatusCounts;
}

export interface CustomerReportRow {
  readonly email: string;
  readonly name: string;
  readonly district: string;
  readonly hasAccount: boolean;
  readonly paidOrders: number;
  readonly grossRevenue: number;
  readonly firstPaidOn: string;
  readonly lastPaidOn: string;
}

export interface CustomerReportPage {
  readonly period: ReportPeriod;
  readonly currency: string;
  readonly rows: readonly CustomerReportRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly totalRevenue: number;
}

/**
 * The customers report's chart (#186b; `CustomerBuyersDto`): buyers per day, ISO week or month, first-time and
 * returning. Same period and search as the report, so `buyers` is its `totalCount`.
 *
 * A **buyer** is a distinct email address with at least one paid order in the days counted (an order refunded
 * later still counts, as everywhere in the reports). **First-time**: their first paid order here, ever, is in
 * those days. **Returning**: they had paid for an order here before those days began.
 */
export interface CustomerBuyers {
  readonly period: ReportPeriod;
  /** Chosen by the server from the period's length: Day up to 31 days, Week up to 182, Month beyond. */
  readonly interval: SalesGrouping;
  /** Distinct buyers over the period — not the sum of the points (one buyer, two weeks). */
  readonly buyers: number;
  /** The sum of the points' first-time figures: a first order happens once. */
  readonly firstTimeBuyers: number;
  readonly returningBuyers: number;
  readonly points: readonly CustomerBuyersPoint[];
}

/** One day, week or month, cut to the period. `firstTimeBuyers + returningBuyers = buyers`. */
export interface CustomerBuyersPoint {
  readonly start: string;
  readonly end: string;
  readonly buyers: number;
  readonly firstTimeBuyers: number;
  readonly returningBuyers: number;
}

/** A downloaded export, ready to save. */
export interface ReportFile {
  readonly blob: Blob;
  readonly fileName: string;
}

/** The permissions these screens read from the session (`Permissions.cs`). */
export const REPORTING_PERMISSIONS = {
  dashboard: 'Dashboard.View',
  reports: 'Reports.View',
  export: 'Reports.Export',
  orders: 'Orders.View',
} as const;
