using System.Globalization;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// <b>Admin form dates relative to today in the shop's time, never a calendar date.</b>
/// <para>
/// The API derives a campaign's or code's status, and whether it discounts anything, by comparing its
/// window with <c>IClock</c>, and the API host these suites start uses the real clock. A hard-coded
/// window is therefore a test that changes its answer as the calendar moves: <c>2026-10-19T00:00</c> was
/// "Scheduled" until that very day and "Live" from it, and a 2026-10-13 to 2026-10-20 window stopped being
/// "Live" at Colombo midnight on the 20th.
/// </para>
/// <para>
/// The value is what an admin screen's <c>datetime-local</c> box sends — Colombo wall-clock,
/// <c>yyyy-MM-ddTHH:mm</c>, no offset — read from the same clock the API's <c>SystemClock</c> reads
/// (<see cref="TimeProvider.System"/>) and converted with the product's own <see cref="ColomboTime"/>.
/// The same rule as the e2e suite's <c>shopMidnight(days)</c>.
/// </para>
/// </summary>
internal static class ShopTime
{
    /// <summary>
    /// Midnight in Colombo <paramref name="days"/> days from today's Colombo date. <c>0</c> has always
    /// begun (a window starting there is under way whenever the test runs, even if Colombo midnight
    /// passes mid-test); any positive value has not.
    /// </summary>
    public static string Midnight(int days) =>
        ColomboTime.ToLocal(TimeProvider.System.GetUtcNow())
            .Date
            .AddDays(days)
            .ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}
