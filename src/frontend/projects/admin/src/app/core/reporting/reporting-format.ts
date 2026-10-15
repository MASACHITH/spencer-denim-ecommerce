import { ComparedFigure, ReportPeriod } from './reporting.model';
import { PeriodQuery } from './reporting-api';

/**
 * Dates and comparisons as the dashboard and the reports word them.
 *
 * **Every day here is a Colombo day.** The shop has one clock (UTC+05:30, no daylight saving), and a staff
 * member's computer set to another zone must not move "today" — so today is worked out from the UTC instant
 * plus 330 minutes, never from the browser's own zone.
 */

const DAY_MS = 24 * 60 * 60 * 1000;
const COLOMBO_OFFSET_MS = 330 * 60 * 1000;

/** Today in Colombo, `yyyy-MM-dd`. */
export function colomboToday(nowMs: number = Date.now()): string {
  return new Date(nowMs + COLOMBO_OFFSET_MS).toISOString().slice(0, 10);
}

/** A `yyyy-MM-dd` day moved by whole days. Calendar arithmetic on UTC dates, so no zone can shift it. */
export function addDays(day: string, days: number): string {
  return new Date(Date.parse(`${day}T00:00:00Z`) + days * DAY_MS).toISOString().slice(0, 10);
}

export type PeriodPreset = 'last7' | 'last30' | 'thisMonth' | 'lastMonth' | 'custom';

export const PERIOD_PRESETS: readonly { readonly value: PeriodPreset; readonly label: string }[] = [
  { value: 'last7', label: 'Last 7 days' },
  { value: 'last30', label: 'Last 30 days' },
  { value: 'thisMonth', label: 'This month' },
  { value: 'lastMonth', label: 'Last month' },
  { value: 'custom', label: 'Choose dates' },
];

/**
 * The days a preset means on `today`. "Last 7 days" includes today, as the server's default "last 30 days"
 * does; "This month" is the 1st to today; "Last month" is the whole previous calendar month.
 */
export function presetPeriod(preset: Exclude<PeriodPreset, 'custom'>, today: string): PeriodQuery {
  switch (preset) {
    case 'last7':
      return { from: addDays(today, -6), to: today };
    case 'last30':
      return { from: addDays(today, -29), to: today };
    case 'thisMonth':
      return { from: `${today.slice(0, 8)}01`, to: today };
    case 'lastMonth': {
      const lastOfPrevious = addDays(`${today.slice(0, 8)}01`, -1);
      return { from: `${lastOfPrevious.slice(0, 8)}01`, to: lastOfPrevious };
    }
  }
}

/** `15 Oct 2026`. */
export function formatDay(day: string): string {
  return new Date(`${day}T00:00:00Z`).toLocaleDateString('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/** `1 Oct 2026 – 31 Oct 2026`, or one day when it is one day. */
export function formatPeriod(period: Pick<ReportPeriod, 'from' | 'to'>): string {
  return period.from === period.to ? formatDay(period.from) : `${formatDay(period.from)} – ${formatDay(period.to)}`;
}

/** `Oct 2026` for a month bucket, `6 Oct` for a week's start. */
export function bucketLabel(start: string, interval: 'Week' | 'Month' | 'Day'): string {
  const date = new Date(`${start}T00:00:00Z`);

  return interval === 'Month'
    ? date.toLocaleDateString('en-GB', { month: 'short', year: 'numeric', timeZone: 'UTC' })
    : date.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' });
}

/** A whole number with thousands separators. */
export function count(value: number | null): string {
  return value === null ? '—' : value.toLocaleString('en-LK', { maximumFractionDigits: 0 });
}

/**
 * How a figure moved, in words a shop owner reads without a calculator.
 *
 * - previous zero, current zero → "No change from the previous period";
 * - previous zero, current above → "Up from none in the previous period" (no percentage: a rise from
 *   nothing is not a percentage of anything);
 * - otherwise the signed percentage, "+12.5%" or "−100%".
 *
 * `null` figures (an average with nothing paid) say there is nothing to compare.
 */
export function changeText(figure: ComparedFigure): string {
  if (figure.current === null || figure.previous === null || figure.change === null) {
    return 'Nothing to compare with the previous period';
  }

  if (figure.change === 0) {
    return 'No change from the previous period';
  }

  if (figure.percentChange === null) {
    return figure.change > 0 ? 'Up from none in the previous period' : 'Down from the previous period';
  }

  const sign = figure.percentChange > 0 ? '+' : figure.percentChange < 0 ? '−' : '';
  const size = Math.abs(figure.percentChange).toLocaleString('en-LK', {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  });

  return `${sign}${size}% on the previous period`;
}

/** Which way a figure moved, for a non-colour cue beside the words. */
export function direction(figure: ComparedFigure): 'up' | 'down' | 'flat' {
  if (figure.change === null || figure.change === 0) {
    return 'flat';
  }

  return figure.change > 0 ? 'up' : 'down';
}
