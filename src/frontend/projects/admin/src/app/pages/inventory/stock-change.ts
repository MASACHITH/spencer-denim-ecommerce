import { InventoryRow, STOCK_LIMITS, StockChangeKind, StockChangeRow } from '../../core/inventory/inventory.model';

/**
 * What a staff member chooses on the stock forms. **"Add" and "Remove" are two choices, not one signed
 * number**: a minus sign is the easiest thing in the world to leave off, and a delivery booked as a loss is
 * the kind of mistake nobody notices until the shelf count is wrong. The API's `Adjust` takes the signed
 * figure; this is where the sign is decided.
 */
export type StockAction = 'receive' | 'add' | 'remove' | 'count';

export const STOCK_ACTIONS: readonly { readonly value: StockAction; readonly label: string }[] = [
  { value: 'receive', label: 'Received from a supplier or production' },
  { value: 'add', label: 'Add units (found, returned)' },
  { value: 'remove', label: 'Remove units (damaged, lost)' },
  { value: 'count', label: 'Set the count after a stock-take' },
];

/** The quantity box's label for each action. */
export function quantityLabel(action: StockAction): string {
  switch (action) {
    case 'receive':
      return 'Units received';
    case 'add':
      return 'Units to add';
    case 'remove':
      return 'Units to remove';
    default:
      return 'Units counted on the shelf';
  }
}

/** The API row for one variant. A count carries the on-hand figure the screen is showing (doc 08 §5.8). */
export function changeRow(action: StockAction, quantity: number, row: Pick<InventoryRow, 'variantId' | 'onHand'>): StockChangeRow {
  const kind: StockChangeKind = action === 'receive' ? 'Receive' : action === 'count' ? 'SetCount' : 'Adjust';

  return {
    variantId: row.variantId,
    kind,
    quantity: action === 'remove' ? -quantity : quantity,
    expectedOnHand: action === 'count' ? row.onHand : null,
  };
}

/**
 * The quantity as typed, or why it cannot be sent. Mirrors `StockChangeRules` so the refusal arrives before
 * a round trip; the server checks again and remains the authority.
 */
export function readQuantity(action: StockAction, raw: string): { value: number } | { problem: string } {
  const text = raw.trim();

  if (!/^\d+$/.test(text)) {
    return { problem: action === 'count' ? 'Enter the count as a whole number, 0 or more.' : 'Enter a whole number of units, 1 or more.' };
  }

  const value = Number(text);

  if (action === 'count') {
    return value > STOCK_LIMITS.maxOnHand ? { problem: 'Set a count between 0 and 1,000,000.' } : { value };
  }

  if (value < 1) {
    return { problem: 'Enter a whole number of units, 1 or more.' };
  }

  return value > STOCK_LIMITS.maxQuantity ? { problem: 'Change at most 100,000 units at a time.' } : { value };
}

/** The reason as it will be sent, or why it cannot be. */
export function readReason(raw: string): { value: string } | { problem: string } {
  const text = raw.trim();

  if (text === '') {
    return { problem: 'Give a reason, for example "Delivery from the factory" or "Damaged in store".' };
  }

  return text.length > STOCK_LIMITS.reasonMaxLength
    ? { problem: `Keep the reason to ${STOCK_LIMITS.reasonMaxLength} characters.` }
    : { value: text };
}

/** On hand after the change, for the preview line. */
export function onHandAfter(action: StockAction, quantity: number, onHand: number): number {
  switch (action) {
    case 'count':
      return quantity;
    case 'remove':
      return onHand - quantity;
    default:
      return onHand + quantity;
  }
}

/**
 * Doc 11 P2 step 3's floor, said before the round trip: on hand can never go below what unpaid orders
 * hold. The server's conditional update is what enforces it; this only saves a refusal.
 */
export function floorProblem(after: number, reserved: number): string | null {
  if (after >= reserved) {
    return null;
  }

  return reserved === 1
    ? '1 unit is reserved for an unpaid order, so on hand cannot go below 1.'
    : `${reserved} units are reserved for unpaid orders, so on hand cannot go below ${reserved}.`;
}
