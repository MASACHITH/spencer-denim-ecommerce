import { TestBed } from '@angular/core/testing';

import { SafeLocalStorage } from './safe-local-storage';

describe('SafeLocalStorage', () => {
  let storage: SafeLocalStorage;

  beforeEach(() => {
    window.localStorage.clear();
    TestBed.configureTestingModule({});
    storage = TestBed.inject(SafeLocalStorage);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    window.localStorage.clear();
  });

  it('reads back what it wrote', () => {
    expect(storage.write('sd.test', 'LK-11')).toBe(true);
    expect(storage.read('sd.test')).toBe('LK-11');
  });

  it('returns null for a key that was never stored', () => {
    expect(storage.read('sd.missing')).toBeNull();
  });

  it('removes a key', () => {
    storage.write('sd.test', 'x');
    expect(storage.remove('sd.test')).toBe(true);
    expect(storage.read('sd.test')).toBeNull();
  });

  // Lesson L1: a blocked or full store must degrade, never throw.
  it('returns null instead of throwing when reading is blocked', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('access denied');
    });

    expect(() => storage.read('sd.test')).not.toThrow();
    expect(storage.read('sd.test')).toBeNull();
  });

  it('reports failure instead of throwing when the quota is full', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError');
    });

    expect(storage.write('sd.test', 'x')).toBe(false);
  });
});
