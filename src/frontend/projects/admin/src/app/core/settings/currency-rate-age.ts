import { CURRENCY_RATE_STALE_AFTER_DAYS } from './admin-settings.model';

const DAY_MS = 86_400_000;

/** The shop's time zone: the day a rate changed is the day on the shop's calendar, not the browser's or UTC's. */
const SHOP_TIME_ZONE = 'Asia/Colombo';

/** A stamp before this is a default value (0001-01-01, 1970-01-01), not a date anybody changed a rate on. */
const EARLIEST_REAL_STAMP_MS = Date.UTC(2000, 0, 1);

const calendarDay = new Intl.DateTimeFormat('en-CA', {
  timeZone: SHOP_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/** Fixed, because the browser's own abbreviations differ ("Sep" in one, "Sept" in another) and the text is tested. */
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const;

/**
 * How old a saved rate is, for the display currencies panel (issue #248).
 *
 * `known`: the stamp is a real date in the past. `days` is the number of CALENDAR days on the shop's calendar
 * between the day it changed and today, and it is the one number behind both the words ("3 days ago") and the
 * warning. `justNow`: the stamp is ahead of this computer's clock (the two clocks disagree by a little), so it is
 * said to be just now and no date is printed. `unrecorded`: there is no usable stamp, so no day count and no date
 * are printed.
 */
export interface RateAge {
  readonly kind: 'known' | 'justNow' | 'unrecorded';

  /** Calendar days on the shop's calendar; 0 unless `kind` is `known`. */
  readonly days: number;

  /** "today", "yesterday", "5 days ago", "just now" or "No change date recorded". */
  readonly relative: string;

  /** The day it changed, such as "21 Oct 2026", on the shop's calendar. Null unless `kind` is `known`. */
  readonly date: string | null;

  /** More than {@link CURRENCY_RATE_STALE_AFTER_DAYS} calendar days without a change. */
  readonly stale: boolean;
}

/**
 * One rule for the words and for the warning, so they can never disagree: whole calendar days in Asia/Colombo
 * between the stamp's date and today's date, and stale when that is more than 30. A rate changed at 23:50 and read at
 * 00:10 is "yesterday" although twenty minutes old, and one that is 30 days 23 hours old is 31 calendar days old,
 * said so and warned about, not "30 days ago" without a warning.
 */
export function describeRateAge(updatedAtUtc: string | null | undefined, nowMs: number): RateAge {
  const unrecorded: RateAge = {
    kind: 'unrecorded',
    days: 0,
    relative: 'No change date recorded',
    date: null,
    stale: false,
  };

  if (!updatedAtUtc) {
    return unrecorded;
  }

  const changed = Date.parse(updatedAtUtc);

  if (Number.isNaN(changed) || changed < EARLIEST_REAL_STAMP_MS) {
    return unrecorded;
  }

  if (changed > nowMs) {
    return { kind: 'justNow', days: 0, relative: 'just now', date: null, stale: false };
  }

  const day = shopDay(changed);
  const days = dayNumber(shopDay(nowMs)) - dayNumber(day);

  return {
    kind: 'known',
    days,
    relative: days === 0 ? 'today' : days === 1 ? 'yesterday' : `${days} days ago`,
    date: `${day.day} ${MONTHS[day.month - 1]} ${day.year}`,
    stale: days > CURRENCY_RATE_STALE_AFTER_DAYS,
  };
}

/** "1 day" or "5 days". */
export function dayCount(days: number): string {
  return days === 1 ? '1 day' : `${days} days`;
}

/** The shop's calendar date at an instant. */
function shopDay(ms: number): { readonly year: number; readonly month: number; readonly day: number } {
  const parts = Object.fromEntries(calendarDay.formatToParts(ms).map((part) => [part.type, part.value]));

  return { year: Number(parts['year']), month: Number(parts['month']), day: Number(parts['day']) };
}

/** A running number for a calendar date, so two of them subtract to whole calendar days. */
function dayNumber(date: { readonly year: number; readonly month: number; readonly day: number }): number {
  return Date.UTC(date.year, date.month - 1, date.day) / DAY_MS;
}
