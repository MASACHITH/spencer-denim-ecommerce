import { CATEGORICAL_TOKENS, STATUS_TOKENS, readPalette } from './chart-palette';

/**
 * #186 tester: the palette reader and the status-to-token table, which no spec read before. Mutations that
 * read the wrong token for the accent or the grid, stopped the categorical colours cycling, or gave a status
 * another status's colour all passed the developer's suite.
 */
describe('chart-palette (#186 tester)', () => {
  let host: HTMLElement;

  beforeEach(() => {
    host = document.createElement('div');
    // On the element itself: jsdom does not inherit custom properties from :root.
    CATEGORICAL_TOKENS.forEach((token, index) => host.style.setProperty(token, `#00000${index + 1}`));
    host.style.setProperty('--chart-accent', '#aa0001');
    host.style.setProperty('--chart-grid', '#aa0002');
    host.style.setProperty('--chart-ink', '#aa0003');
    host.style.setProperty('--color-surface', '#aa0004');
    document.body.appendChild(host);
  });

  afterEach(() => host.remove());

  it('reads the accent, grid, ink and surface each from its own token', () => {
    const palette = readPalette(host);

    expect(palette.accent).toBe('#aa0001');
    expect(palette.grid).toBe('#aa0002');
    expect(palette.ink).toBe('#aa0003');
    expect(palette.surface).toBe('#aa0004');
    expect(palette.token('--chart-3')).toBe('#000003');
  });

  it('gives the categorical colours in order and cycles round after the eighth, either way', () => {
    const palette = readPalette(host);

    expect([0, 1, 7].map(palette.series)).toEqual(['#000001', '#000002', '#000008']);
    expect(palette.series(8)).toBe('#000001');
    expect(palette.series(9)).toBe('#000002');
    expect(palette.series(-1)).toBe('#000008');
  });

  it("gives each of the API's eight order statuses its own fixed token", () => {
    expect(STATUS_TOKENS).toEqual({
      PendingPayment: '--status-awaiting',
      Paid: '--status-paid',
      Processing: '--status-processing',
      Shipped: '--status-dispatched',
      Delivered: '--status-delivered',
      Cancelled: '--status-cancelled',
      Expired: '--status-expired',
      Refunded: '--status-refunded',
    });
    expect(new Set(Object.values(STATUS_TOKENS)).size).toBe(8);
  });
});
