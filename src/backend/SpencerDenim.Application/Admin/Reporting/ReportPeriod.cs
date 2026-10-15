using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Application.Admin.Reporting;

/// <summary>
/// A run of whole <b>Colombo</b> calendar days, both ends included — the period every dashboard widget and
/// report is asked about (doc 11 P16 step 1, doc 08 §1.4 and §8.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A day is a Colombo day, not a UTC day.</b> Colombo is a fixed UTC+05:30 (<see cref="ColomboTime"/>), so
/// the shop's 15 October runs from 14 October 18:30 UTC to 15 October 18:30 UTC. An order paid at 19:00 UTC
/// on the 14th belongs to the 15th. <see cref="FromUtc"/> and <see cref="ToUtcExclusive"/> are those two
/// instants; a query compares <c>&gt;= FromUtc</c> and <c>&lt; ToUtcExclusive</c>, so the last millisecond of
/// the last day is included without anyone having to name it.
/// </para>
/// <para>
/// <b>The previous period is the same number of days immediately before</b> (doc 08 §8.1: "the same-length
/// window immediately before"; doc 11 P16 step 1). It is counted in days, never in months: for 1–31 October
/// (31 days) it is 31 August – 30 September, not "September". A calendar month would compare 31 days with
/// 30 and call the difference growth.
/// </para>
/// </remarks>
public sealed record ReportPeriod
{
    /// <summary>The longest period answered, in days. A year, plus the 29th of February.</summary>
    public const int MaxDays = 366;

    /// <summary>
    /// The default period when none is asked for: the last 30 days including today, doc 11 P16 step 1's own
    /// example ("e.g. last 30 days").
    /// </summary>
    public const int DefaultDays = 30;

    /// <summary>
    /// The earliest day accepted. Far before the shop opened, and far enough after <see cref="DateOnly.MinValue"/>
    /// that a previous period of <see cref="MaxDays"/> before it, converted to UTC, cannot underflow — a
    /// period starting on 0001-01-01 would otherwise answer 500 rather than 400.
    /// </summary>
    public static readonly DateOnly EarliestDay = new(2000, 1, 1);

    /// <summary>The latest day accepted, for the same reason at the other end.</summary>
    public static readonly DateOnly LatestDay = new(2099, 12, 31);

    public ReportPeriod(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "The period ends before it starts.");
        }

        From = from;
        To = to;
    }

    /// <summary>The first Colombo day, included.</summary>
    public DateOnly From { get; }

    /// <summary>The last Colombo day, included.</summary>
    public DateOnly To { get; }

    /// <summary>How many calendar days the period covers, both ends counted.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>Colombo midnight at the start of <see cref="From"/>, as UTC.</summary>
    public DateTime FromUtc => ColomboMidnightUtc(From);

    /// <summary>Colombo midnight at the start of the day <b>after</b> <see cref="To"/>, as UTC — exclusive.</summary>
    public DateTime ToUtcExclusive => ColomboMidnightUtc(To.AddDays(1));

    /// <summary>The same number of days, ending the day before <see cref="From"/>.</summary>
    public ReportPeriod Previous => new(From.AddDays(-Days), From.AddDays(-1));

    /// <summary>Every day in the period, in order.</summary>
    public IEnumerable<DateOnly> EachDay()
    {
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>Colombo midnight on <paramref name="day"/>, as a UTC <see cref="DateTime"/>.</summary>
    public static DateTime ColomboMidnightUtc(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), ColomboTime.Offset).UtcDateTime;

    /// <summary>The Colombo calendar day an instant falls on.</summary>
    public static DateOnly ColomboDayOf(DateTimeOffset instant) => DateOnly.FromDateTime(ColomboTime.ToLocal(instant).DateTime);

    /// <summary>
    /// Reads <c>?from=</c> and <c>?to=</c> (<c>yyyy-MM-dd</c>, Colombo days, both included).
    /// </summary>
    /// <param name="from">The first day, or blank for <see cref="DefaultDays"/> days ending on <paramref name="to"/>.</param>
    /// <param name="to">The last day, or blank for <paramref name="today"/>.</param>
    /// <param name="today">Today in Colombo.</param>
    /// <param name="errors">Where a refusal is written, keyed by the parameter it is about.</param>
    /// <returns>The period, or <see langword="null"/> with <paramref name="errors"/> filled in.</returns>
    public static ReportPeriod? Parse(string? from, string? to, DateOnly today, ICollection<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var before = errors.Count;
        var toDay = ReadDay(to, "to", errors) ?? today;
        var fromDay = ReadDay(from, "from", errors);

        if (errors.Count > before)
        {
            return null;
        }

        var start = fromDay ?? toDay.AddDays(-(DefaultDays - 1));

        if (start > toDay)
        {
            errors.Add(new ValidationError("to", "The end date is before the start date."));
            return null;
        }

        if (toDay.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            errors.Add(new ValidationError(
                "from",
                string.Create(CultureInfo.InvariantCulture, $"A period can be at most {MaxDays} days long.")));
            return null;
        }

        return new ReportPeriod(start, toDay);
    }

    /// <summary>Reads one <c>yyyy-MM-dd</c> day inside the accepted range, or records why not.</summary>
    public static DateOnly? ReadDay(string? raw, string field, ICollection<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            errors.Add(new ValidationError(field, "Use a date in the form yyyy-MM-dd."));
            return null;
        }

        if (day < EarliestDay || day > LatestDay)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Use a date between {EarliestDay:yyyy-MM-dd} and {LatestDay:yyyy-MM-dd}.")));
            return null;
        }

        return day;
    }
}
