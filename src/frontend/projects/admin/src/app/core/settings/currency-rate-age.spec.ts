import { dayCount, describeRateAge } from './currency-rate-age';

/**
 * Issue #248, tester round: ONE rule for the words and for the thirty-day warning. Whole CALENDAR days on the shop's
 * calendar (Asia/Colombo, UTC+5:30) between the day the rate's number changed and today, and stale when that is more
 * than 30. Every row of the table is an instant the old rule (whole 24-hour periods, a browser-zone date) got wrong or
 * could get wrong; "Colombo" below is the shop's clock.
 */
interface Row {
  readonly name: string;
  readonly now: string;
  readonly stamp: string | null;
  readonly kind: 'known' | 'justNow' | 'unrecorded';
  readonly relative: string;
  readonly date: string | null;
  readonly days: number;
  readonly stale: boolean;
}

const NOW = '2026-10-24T08:00:00.000Z'; // 13:30 on 24 Oct in Colombo

const table: readonly Row[] = [
  // ---- the stamp is missing, unreadable or a default value: no day count, no date -----------------------------------
  { name: 'null', now: NOW, stamp: null, kind: 'unrecorded', relative: 'No change date recorded', date: null, days: 0, stale: false },
  { name: 'an empty string', now: NOW, stamp: '', kind: 'unrecorded', relative: 'No change date recorded', date: null, days: 0, stale: false },
  { name: 'text that is not a date', now: NOW, stamp: 'last Tuesday', kind: 'unrecorded', relative: 'No change date recorded', date: null, days: 0, stale: false },
  // The tester saw "739913 days ago (1 Jan 1)" for this one.
  { name: 'year 1 (a default date)', now: NOW, stamp: '0001-01-01T00:00:00Z', kind: 'unrecorded', relative: 'No change date recorded', date: null, days: 0, stale: false },
  { name: 'the Unix epoch', now: NOW, stamp: '1970-01-01T00:00:00Z', kind: 'unrecorded', relative: 'No change date recorded', date: null, days: 0, stale: false },
  { name: 'the last second of 1999', now: NOW, stamp: '1999-12-31T23:59:59Z', kind: 'unrecorded', relative: 'No change date recorded', date: null, days: 0, stale: false },

  // ---- a stamp ahead of this computer's clock: just now, and never a date in the future -----------------------------
  { name: 'five minutes ahead', now: NOW, stamp: '2026-10-24T08:05:00Z', kind: 'justNow', relative: 'just now', date: null, days: 0, stale: false },
  { name: 'one second ahead', now: NOW, stamp: '2026-10-24T08:00:01Z', kind: 'justNow', relative: 'just now', date: null, days: 0, stale: false },
  { name: 'a month ahead', now: NOW, stamp: '2026-11-24T08:00:00Z', kind: 'justNow', relative: 'just now', date: null, days: 0, stale: false },
  { name: 'years ahead', now: NOW, stamp: '2090-01-01T00:00:00Z', kind: 'justNow', relative: 'just now', date: null, days: 0, stale: false },

  // ---- today, yesterday, and the midnight that is Colombo's, not UTC's or the browser's -------------------------------
  { name: 'this very instant', now: NOW, stamp: NOW, kind: 'known', relative: 'today', date: '24 Oct 2026', days: 0, stale: false },
  { name: 'an hour ago', now: NOW, stamp: '2026-10-24T07:00:00Z', kind: 'known', relative: 'today', date: '24 Oct 2026', days: 0, stale: false },
  {
    name: 'two minutes ago but across Colombo midnight (23:59 yesterday, 00:01 now)',
    now: '2026-10-24T18:31:00Z',
    stamp: '2026-10-24T18:29:00Z',
    kind: 'known',
    relative: 'yesterday',
    date: '24 Oct 2026',
    days: 1,
    stale: false,
  },
  {
    name: '23 h 29 m old but on the same Colombo day (00:01 and 23:30)',
    now: '2026-10-24T18:00:00Z',
    stamp: '2026-10-23T18:31:00Z',
    kind: 'known',
    relative: 'today',
    date: '24 Oct 2026',
    days: 0,
    stale: false,
  },
  {
    // 19:16 UTC on the 24th is 00:46 on the 25th in Colombo: the date the admin must read is the 25th, "today".
    name: 'a stamp at 19:16 UTC reads as the next day in Colombo',
    now: '2026-10-25T01:00:00Z',
    stamp: '2026-10-24T19:16:00Z',
    kind: 'known',
    relative: 'today',
    date: '25 Oct 2026',
    days: 0,
    stale: false,
  },
  {
    name: 'a day ago, across the month boundary',
    now: '2026-03-01T10:00:00Z',
    stamp: '2026-02-28T10:00:00Z',
    kind: 'known',
    relative: 'yesterday',
    date: '28 Feb 2026',
    days: 1,
    stale: false,
  },
  { name: 'three days ago', now: NOW, stamp: '2026-10-21T08:00:00Z', kind: 'known', relative: '3 days ago', date: '21 Oct 2026', days: 3, stale: false },
  {
    name: 'a leap day, counted as a day',
    now: '2028-03-01T10:00:00Z',
    stamp: '2028-02-28T10:00:00Z',
    kind: 'known',
    relative: '2 days ago',
    date: '28 Feb 2028',
    days: 2,
    stale: false,
  },

  // ---- the thirty-day line: more than 30 calendar days, and the words say the same number ----------------------------
  { name: 'exactly 30 calendar days', now: NOW, stamp: '2026-09-24T08:00:00Z', kind: 'known', relative: '30 days ago', date: '24 Sep 2026', days: 30, stale: false },
  { name: '31 calendar days', now: NOW, stamp: '2026-09-23T08:00:00Z', kind: 'known', relative: '31 days ago', date: '23 Sep 2026', days: 31, stale: true },
  {
    // 30 days 23 hours old: the old rule floored it to 30, said "30 days ago" and did not warn. By the calendar it is
    // 31 days ago (23 Sep, 14:30 in Colombo, to 24 Oct), and it warns.
    name: '30 days 23 hours old is 31 calendar days: said and warned',
    now: NOW,
    stamp: '2026-09-23T09:00:00Z',
    kind: 'known',
    relative: '31 days ago',
    date: '23 Sep 2026',
    days: 31,
    stale: true,
  },
  {
    name: '30 days and a few minutes old but on the 30th calendar day',
    now: '2026-10-24T08:00:00Z',
    stamp: '2026-09-24T07:55:00Z',
    kind: 'known',
    relative: '30 days ago',
    date: '24 Sep 2026',
    days: 30,
    stale: false,
  },
  { name: 'a long time ago', now: NOW, stamp: '2025-10-24T08:00:00Z', kind: 'known', relative: '365 days ago', date: '24 Oct 2025', days: 365, stale: true },
  { name: 'the first day of 2000', now: NOW, stamp: '2000-01-01T00:00:00Z', kind: 'known', relative: '9793 days ago', date: '1 Jan 2000', days: 9793, stale: true },
];

describe('describeRateAge (issue #248, tester round)', () => {
  it.each(table.map((row) => [row.name, row] as const))('%s', (_name, row) => {
    const age = describeRateAge(row.stamp, Date.parse(row.now));

    expect({
      kind: age.kind,
      relative: age.relative,
      date: age.date,
      days: age.days,
      stale: age.stale,
    }).toEqual({
      kind: row.kind,
      relative: row.relative,
      date: row.date,
      days: row.days,
      stale: row.stale,
    });
  });

  it('has a table that covers every kind, both sides of the thirty-day line, and the Colombo midnight', () => {
    expect(new Set(table.map((row) => row.kind))).toEqual(new Set(['known', 'justNow', 'unrecorded']));
    expect(table.some((row) => row.days === 30 && !row.stale)).toBe(true);
    expect(table.some((row) => row.days === 31 && row.stale)).toBe(true);
    expect(table.length).toBeGreaterThanOrEqual(20);
  });

  it('never says a day count or a date it does not have', () => {
    for (const row of table.filter((entry) => entry.kind !== 'known')) {
      const age = describeRateAge(row.stamp, Date.parse(row.now));

      expect(age.date, row.name).toBeNull();
      expect(age.relative, row.name).not.toMatch(/\d/);
      expect(age.stale, row.name).toBe(false);
    }
  });

  it('says the same number in the words and in the warning: stale exactly when the days are more than 30', () => {
    const now = Date.parse(NOW);

    for (let back = 0; back <= 45; back += 1) {
      const age = describeRateAge(new Date(now - back * 86_400_000).toISOString(), now);

      // Going back whole 24-hour periods from 13:30 Colombo lands on the same time of day, so the calendar days equal it.
      expect(age.days, `${back} days back`).toBe(back);
      expect(age.stale, `${back} days back`).toBe(back > 30);

      if (back >= 2) {
        expect(age.relative).toBe(`${back} days ago`);
      }
    }
  });
});

describe('dayCount (issue #248, tester round)', () => {
  it.each([
    [0, '0 days'],
    [1, '1 day'],
    [2, '2 days'],
    [31, '31 days'],
  ])('writes %i as %j', (days, expected) => {
    expect(dayCount(days)).toBe(expected);
  });
});
