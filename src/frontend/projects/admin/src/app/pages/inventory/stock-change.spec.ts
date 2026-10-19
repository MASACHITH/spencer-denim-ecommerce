import { statusOf } from '../../core/inventory/inventory.model';
import { changeRow, floorProblem, onHandAfter, readQuantity, readReason } from './stock-change';

/** The stock forms' own rules (ADM-12): the sign, the count's expected figure, the limits and the floor. */
describe('stock change rules', () => {
  const row = { variantId: 7, onHand: 10 };

  it('turns each action into the API row, deciding the sign here', () => {
    expect(changeRow('receive', 3, row)).toEqual({ variantId: 7, kind: 'Receive', quantity: 3, expectedOnHand: null });
    expect(changeRow('add', 3, row)).toEqual({ variantId: 7, kind: 'Adjust', quantity: 3, expectedOnHand: null });
    expect(changeRow('remove', 3, row)).toEqual({ variantId: 7, kind: 'Adjust', quantity: -3, expectedOnHand: null });
    expect(changeRow('count', 3, row)).toEqual({ variantId: 7, kind: 'SetCount', quantity: 3, expectedOnHand: 10 });
  });

  it('accepts whole numbers in range and refuses the rest', () => {
    expect(readQuantity('receive', '1')).toEqual({ value: 1 });
    expect(readQuantity('receive', '100000')).toEqual({ value: 100000 });
    expect(readQuantity('count', '0')).toEqual({ value: 0 });
    expect(readQuantity('count', '1000000')).toEqual({ value: 1000000 });

    for (const bad of ['', ' ', '0', '-1', '1.5', '1e3', 'ten', '100001']) {
      expect('problem' in readQuantity('remove', bad)).toBe(true);
    }

    expect('problem' in readQuantity('count', '1000001')).toBe(true);
    expect('problem' in readQuantity('count', '-1')).toBe(true);
  });

  it('requires a reason of at most 250 characters, trimmed', () => {
    expect(readReason('  Damaged ')).toEqual({ value: 'Damaged' });
    expect('problem' in readReason('   ')).toBe(true);
    expect(readReason('r'.repeat(250))).toEqual({ value: 'r'.repeat(250) });
    expect('problem' in readReason('r'.repeat(251))).toBe(true);
  });

  it('works out on hand after each action', () => {
    expect(onHandAfter('receive', 4, 10)).toBe(14);
    expect(onHandAfter('add', 4, 10)).toBe(14);
    expect(onHandAfter('remove', 4, 10)).toBe(6);
    expect(onHandAfter('count', 4, 10)).toBe(4);
  });

  it('refuses to go below what unpaid orders hold, and allows exactly that', () => {
    expect(floorProblem(4, 4)).toBeNull();
    expect(floorProblem(3, 4)).toContain('4 units are reserved');
    expect(floorProblem(0, 1)).toContain('1 unit is reserved');
    expect(floorProblem(0, 0)).toBeNull();
  });

  it('labels out of stock, never low, at nothing available', () => {
    expect(statusOf(0, 5)).toBe('OutOfStock');
    expect(statusOf(-1, 5)).toBe('OutOfStock');
    expect(statusOf(1, 5)).toBe('LowStock');
    expect(statusOf(5, 5)).toBe('LowStock');
    expect(statusOf(6, 5)).toBe('InStock');
    expect(statusOf(1, 0)).toBe('InStock');
  });
});
