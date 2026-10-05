namespace SpencerDenim.Domain.Common;

/// <summary>
/// Asia/Colombo, for the handful of places where a <i>calendar</i> answer is needed rather than an
/// instant: doc 06 stores every timestamp in UTC and displays it in Colombo, and doc 08 §3.5 says an
/// order number's year is "the Colombo year".
/// </summary>
/// <remarks>
/// <b>A fixed +05:30 offset, not <c>TimeZoneInfo</c>, and that is deliberate.</b>
/// <list type="bullet">
/// <item><description>
/// Sri Lanka has observed a constant UTC+05:30 since 2006-04-15 and has no daylight saving, so there is
/// no rule for a time-zone database to carry that this constant does not already express. The only dates
/// it would differ on are two decades before this shop existed.
/// </description></item>
/// <item><description>
/// <c>TimeZoneInfo.FindSystemTimeZoneById</c> needs an id that differs by platform
/// (<c>Sri Lanka Standard Time</c> on Windows, <c>Asia/Colombo</c> elsewhere) and throws when the host
/// runs with globalization invariant mode — which is the default in several slim container images. An
/// order number generator that throws on the server but not on the developer's machine is exactly the
/// failure this project cannot afford on the revenue path, and doc 03 requires Azure portability.
/// </description></item>
/// </list>
/// If Sri Lanka ever reintroduces daylight saving, this becomes a real time zone and the change is one
/// file plus its test.
/// </remarks>
public static class ColomboTime
{
    /// <summary>UTC+05:30.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);

    /// <summary>The local date and time in Colombo for a given instant.</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset instant) => instant.ToOffset(Offset);

    /// <summary>
    /// The calendar year in Colombo for a given instant. At 20:00 UTC on 31 December this is already the
    /// next year, which is the whole reason the conversion is not skipped.
    /// </summary>
    public static int YearOf(DateTimeOffset instant) => ToLocal(instant).Year;
}
