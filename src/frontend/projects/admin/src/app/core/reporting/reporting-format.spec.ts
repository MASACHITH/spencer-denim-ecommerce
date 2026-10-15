import { addDays, changeText, colomboToday, direction, formatPeriod, presetPeriod } from './reporting-format';
import { ComparedFigure } from './reporting.model';

function figure(current: number | null, previous: number | null, change: number | null, percentChange: number | null): ComparedFigure {
  return { current, previous, change, percentChange };
}

describe('reporting-format (W4-02)', () => {
  describe('today is a Colombo day', () => {
    it('is still the 14th at 18:29 UTC and already the 15th at 18:31 UTC', () => {
      expect(colomboToday(Date.parse('2026-10-14T18:29:00Z'))).toBe('2026-10-14');
      expect(colomboToday(Date.parse('2026-10-14T18:31:00Z'))).toBe('2026-10-15');
    });

    it('turns the year at 18:30 UTC on 31 December', () => {
      expect(colomboToday(Date.parse('2026-12-31T18:30:00Z'))).toBe('2027-01-01');
    });
  });

  describe('presets', () => {
    it('counts today in the last 7 and the last 30 days', () => {
      expect(presetPeriod('last7', '2026-10-15')).toEqual({ from: '2026-10-09', to: '2026-10-15' });
      expect(presetPeriod('last30', '2026-10-15')).toEqual({ from: '2026-09-16', to: '2026-10-15' });
    });

    it('takes this month from the 1st to today', () => {
      expect(presetPeriod('thisMonth', '2026-10-15')).toEqual({ from: '2026-10-01', to: '2026-10-15' });
    });

    it('takes last month whole, across a year and a leap February', () => {
      expect(presetPeriod('lastMonth', '2027-01-05')).toEqual({ from: '2026-12-01', to: '2026-12-31' });
      expect(presetPeriod('lastMonth', '2024-03-10')).toEqual({ from: '2024-02-01', to: '2024-02-29' });
    });

    it('moves days without any time zone shifting them', () => {
      expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
      expect(addDays('2026-10-15', 365)).toBe('2027-10-15');
    });
  });

  describe('vs previous period', () => {
    it('says a rise from nothing without a percentage', () => {
      expect(changeText(figure(1000, 0, 1000, null))).toBe('Up from none in the previous period');
    });

    it('says no change when both are zero', () => {
      expect(changeText(figure(0, 0, 0, null))).toBe('No change from the previous period');
    });

    it('says a fall to nothing as −100%', () => {
      expect(changeText(figure(0, 1000, -1000, -100))).toBe('−100.0% on the previous period');
    });

    it('signs a rise and keeps one decimal place', () => {
      expect(changeText(figure(1500, 1000, 500, 50))).toBe('+50.0% on the previous period');
    });

    it('says there is nothing to compare when an average has nothing to divide', () => {
      expect(changeText(figure(null, 300, null, null))).toBe('Nothing to compare with the previous period');
      expect(direction(figure(null, 300, null, null))).toBe('flat');
    });

    it('gives the direction for the arrow', () => {
      expect(direction(figure(2, 1, 1, 100))).toBe('up');
      expect(direction(figure(1, 2, -1, -50))).toBe('down');
      expect(direction(figure(1, 1, 0, 0))).toBe('flat');
    });
  });

  it('writes a one-day period once', () => {
    expect(formatPeriod({ from: '2026-10-15', to: '2026-10-15' })).toBe('15 Oct 2026');
    expect(formatPeriod({ from: '2026-10-01', to: '2026-10-31' })).toBe('1 Oct 2026 – 31 Oct 2026');
  });
});
