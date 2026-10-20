/**
 * The inventory screen's payloads (ADM-12, #190), field for field with
 * `SpencerDenim.Application.Inventory.Contracts`. `AdminInventoryShapeTests` holds the two copies — and the
 * Playwright stub, the third — to the same names in both directions.
 */

/** Doc 11 P2's two permissions: the list and history need View, every change needs Edit. */
export const INVENTORY_PERMISSIONS = {
  view: 'Inventory.View',
  edit: 'Inventory.Edit',
} as const;

export type InventoryStatus = 'InStock' | 'LowStock' | 'OutOfStock';

export type InventoryStatusFilter = 'All' | InventoryStatus;

export const INVENTORY_STATUS_FILTERS: readonly InventoryStatusFilter[] = ['All', 'InStock', 'LowStock', 'OutOfStock'];

export type InventorySort = 'Product' | 'Sku' | 'OnHand' | 'Reserved' | 'Available';

export type InventorySortDirection = 'Asc' | 'Desc';

export type StockChangeKind = 'Receive' | 'Adjust' | 'SetCount';

export type StockChangeOutcome = 'Applied' | 'Refused' | 'Changed' | 'NotFound';

export type InventoryMovementType =
  | 'Initial'
  | 'Receive'
  | 'AdjustIn'
  | 'AdjustOut'
  | 'Reserve'
  | 'Release'
  | 'Sale'
  | 'Restock'
  | 'CountCorrection';

/** The server's limits (`StockChangeRules`), mirrored so the form can say them before a round trip. */
export const STOCK_LIMITS = {
  maxQuantity: 100_000,
  maxOnHand: 1_000_000,
  maxThreshold: 10_000,
  maxBulkRows: 500,
  reasonMaxLength: 250,
} as const;

export interface InventoryStatusCounts {
  readonly inStock: number;
  readonly lowStock: number;
  readonly outOfStock: number;
}

export interface InventoryListPage {
  readonly rows: readonly InventoryRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly defaultThreshold: number;
  readonly counts: InventoryStatusCounts;
}

export interface InventoryRow {
  readonly variantId: number;
  readonly productId: number;
  readonly productName: string;
  readonly productCode: string;
  readonly departmentName: string;
  readonly categoryName: string | null;
  readonly sku: string;
  readonly colour: string;
  readonly size: string;
  readonly hasStockRecord: boolean;
  readonly onHand: number;
  readonly reserved: number;
  readonly available: number;
  /** The threshold that applies: the variant's own, or the shop default. */
  readonly threshold: number;
  /** The variant's own threshold; null means it uses the shop default. */
  readonly ownThreshold: number | null;
  readonly stockStatus: InventoryStatus;
  readonly updatedAtUtc: string | null;
}

export interface InventoryFilterOptions {
  readonly defaultThreshold: number;
  readonly departments: readonly InventoryDepartmentOption[];
  readonly categories: readonly InventoryCategoryOption[];
}

export interface InventoryDepartmentOption {
  readonly id: number;
  readonly name: string;
}

export interface InventoryCategoryOption {
  readonly id: number;
  readonly departmentId: number;
  readonly name: string;
  readonly depth: number;
}

export interface InventoryHistoryPage {
  readonly variant: InventoryRow;
  readonly rows: readonly InventoryMovementRow[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

export interface InventoryMovementRow {
  readonly movementId: number;
  readonly movementType: InventoryMovementType;
  readonly onHandDelta: number;
  readonly reservedDelta: number;
  readonly onHandAfter: number;
  readonly reservedAfter: number;
  readonly orderNumber: string | null;
  readonly batchId: string | null;
  readonly reason: string | null;
  readonly createdBy: string | null;
  readonly createdAtUtc: string;
}

export interface StockChangeRow {
  readonly variantId: number;
  readonly kind: StockChangeKind;
  readonly quantity: number;
  /** The on-hand figure on screen, for a SetCount only; null otherwise. */
  readonly expectedOnHand: number | null;
}

export interface StockChangeRequest {
  readonly reason: string;
  readonly rows: readonly StockChangeRow[];
}

export interface StockChangeResult {
  readonly batchId: string | null;
  readonly appliedCount: number;
  readonly notAppliedCount: number;
  readonly rows: readonly StockChangeRowResult[];
}

export interface StockChangeRowResult {
  readonly variantId: number;
  readonly outcome: StockChangeOutcome;
  readonly message: string | null;
  readonly onHandDelta: number;
  readonly onHand: number | null;
  readonly reserved: number | null;
  readonly available: number | null;
}

/** What the list asks for. Empty values are left off the query string. */
export interface InventoryQuery {
  readonly search: string;
  readonly status: InventoryStatusFilter;
  readonly departmentId: number | null;
  readonly categoryId: number | null;
  readonly sort: InventorySort;
  readonly direction: InventorySortDirection;
  readonly page: number;
  readonly pageSize: number;
}

export const STATUS_LABELS: Record<InventoryStatusFilter, string> = {
  All: 'Every variant',
  InStock: 'In stock',
  LowStock: 'Low stock',
  OutOfStock: 'Out of stock',
};

export const MOVEMENT_LABELS: Record<InventoryMovementType, string> = {
  Initial: 'First count',
  Receive: 'Received',
  AdjustIn: 'Adjusted in',
  AdjustOut: 'Adjusted out',
  Reserve: 'Held by an order',
  Release: 'Released by an order',
  Sale: 'Sold',
  Restock: 'Restocked after a refund',
  CountCorrection: 'Stock-take',
};

/**
 * Status with the rule the server uses (doc 11 P2 step 9): out of stock at 0 available, low at 1 to the
 * threshold, in stock above. Used only to re-label a row after a change, from figures the server returned.
 */
export function statusOf(available: number, threshold: number): InventoryStatus {
  if (available <= 0) {
    return 'OutOfStock';
  }

  return available <= threshold ? 'LowStock' : 'InStock';
}
