using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Marketing;

/// <summary>
/// The Colombo conversion and the derived status (ADM-13; doc 06, doc 11 P3).
/// <para>
/// <b>This is the file that closes a defect class rather than a defect.</b> This project has shipped a
/// UTC-versus-Colombo mistake before, and a promo code's window is where it would land next: a staff
/// member who sets a code to end on 12 October at 23:59 means 23:59 in the shop, and taken as UTC the
/// code would have died at 18:29 — five and a half hours into the evening of the sale. The conversion is
/// asserted here; that the column holds the converted instant and the evaluator compares UTC with UTC is
/// asserted by <c>CheckoutPromoBehaviourTests</c>, because either half alone would pass over a conversion
/// applied twice or not at all.
/// </para>
/// </summary>
public sealed class PromoCodeScheduleTests
{
    /// <summary>Colombo is UTC+05:30 (<see cref="ColomboTime.Offset"/>), with no daylight saving.</summary>
    private const int OffsetMinutes = 330;

    /// <summary>
    /// Midnight in Colombo on 13 October is 18:30 UTC on the 12th — and <b>not</b> midnight UTC, which is
    /// the whole point.
    /// </summary>
    [Fact]
    public void Colombo_midnight_is_half_past_six_the_previous_evening_in_UTC()
    {
        var typed = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateTime(2026, 10, 12, 18, 30, 0, DateTimeKind.Utc), PromoCodeSchedule.ToUtc(typed));
    }

    /// <summary>
    /// The famous one: "ends 12 October at 23:59" is 18:29 UTC, so a code with that end is still live at
    /// 18:00 UTC on the 12th. A stored wall-clock value would have made it dead by then.
    /// </summary>
    [Fact]
    public void A_code_that_ends_at_two_minutes_to_midnight_Colombo_is_live_at_six_in_the_evening_UTC()
    {
        var endsAtUtc = PromoCodeSchedule.ToUtc(new DateTime(2026, 10, 12, 23, 59, 0));

        Assert.Equal(new DateTime(2026, 10, 12, 18, 29, 0, DateTimeKind.Utc), endsAtUtc);

        // 18:00 UTC is 23:30 in the shop: still open.
        Assert.Equal(
            PromoCodeStatus.Live,
            PromoCodeSchedule.StatusOf(
                isActive: true,
                startsAtUtc: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                endsAtUtc,
                utcNow: new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc)));

        // 18:30 UTC is midnight in the shop: closed.
        Assert.Equal(
            PromoCodeStatus.Ended,
            PromoCodeSchedule.StatusOf(
                isActive: true,
                startsAtUtc: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                endsAtUtc,
                utcNow: new DateTime(2026, 10, 12, 18, 30, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// <b>An incoming <c>Utc</c> kind is treated as Colombo wall-clock all the same</b>, because this
    /// function's input is <i>defined</i> to be what a form typed.
    /// </summary>
    /// <remarks>
    /// Asserted because the alternative is the defect: if the answer depended on whether a client
    /// happened to set the kind — or append a <c>Z</c> — then two callers sending the same characters
    /// would store two different instants. The API refuses an offset outright for the same reason, so no
    /// honest caller reaches this with a <c>Utc</c> kind; this test pins the behaviour of the one that
    /// does rather than leaving it to <c>DateTimeOffset</c>'s own interpretation, which would have
    /// subtracted nothing.
    /// </remarks>
    [Fact]
    public void The_kind_of_the_incoming_value_is_ignored()
    {
        var unspecified = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var local = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Local);

        Assert.Equal(PromoCodeSchedule.ToUtc(unspecified), PromoCodeSchedule.ToUtc(utc));
        Assert.Equal(PromoCodeSchedule.ToUtc(unspecified), PromoCodeSchedule.ToUtc(local));
    }

    /// <summary>
    /// The round trip is exact, so a form that saves without editing the dates stores the same instants.
    /// </summary>
    /// <remarks>
    /// <b>This is the defect an admin screen produces by itself.</b> A read that converted and a save that
    /// did not — or converted twice — would walk a code's window by five and a half hours every time a
    /// staff member pressed Save on an unrelated field. Asserted over a day either side of the offset
    /// boundary, which is where a sign error hides.
    /// </remarks>
    [Theory]
    [InlineData(2026, 10, 12, 18, 29)]
    [InlineData(2026, 10, 12, 18, 30)]
    [InlineData(2026, 10, 12, 18, 31)]
    [InlineData(2026, 1, 1, 0, 0)]
    [InlineData(2026, 12, 31, 23, 59)]
    public void Converting_to_Colombo_and_back_is_exact(int year, int month, int day, int hour, int minute)
    {
        var utc = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        Assert.Equal(utc, PromoCodeSchedule.ToUtc(PromoCodeSchedule.ToColombo(utc)));
    }

    /// <summary>The offset the whole file depends on, asserted rather than assumed.</summary>
    [Fact]
    public void Colombo_is_five_and_a_half_hours_ahead_of_UTC()
    {
        Assert.Equal(TimeSpan.FromMinutes(OffsetMinutes), ColomboTime.Offset);

        var utc = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(utc.AddMinutes(OffsetMinutes), PromoCodeSchedule.ToColombo(utc));
    }

    /// <summary>
    /// Doc 11 P3's derived statuses: Scheduled before the start, Live inside the window, Ended after it,
    /// and Inactive whatever the dates say.
    /// </summary>
    [Theory]
    [InlineData(true, -2, -1, PromoCodeStatus.Ended)]
    [InlineData(true, -1, 1, PromoCodeStatus.Live)]
    [InlineData(true, 1, 2, PromoCodeStatus.Scheduled)]
    [InlineData(false, -1, 1, PromoCodeStatus.Inactive)]
    [InlineData(false, 1, 2, PromoCodeStatus.Inactive)]
    [InlineData(false, -2, -1, PromoCodeStatus.Inactive)]
    public void The_status_is_derived_from_the_dates_and_the_flag(
        bool isActive,
        int startsInDays,
        int endsInDays,
        PromoCodeStatus expected)
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            expected,
            PromoCodeSchedule.StatusOf(
                isActive,
                now.AddDays(startsInDays),
                now.AddDays(endsInDays),
                now));
    }

    /// <summary>
    /// A code with no end is Live once it has started, not Ended. Null is "no bound on that side".
    /// </summary>
    [Fact]
    public void A_code_with_no_end_is_live_rather_than_ended()
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            PromoCodeStatus.Live,
            PromoCodeSchedule.StatusOf(true, now.AddYears(-1), null, now));
    }

    /// <summary>
    /// <b>The list's Live and the evaluator's "usable" are the same window</b>, which is the invariant
    /// that stops a staff member being shown "Live" beside a code no shopper can use.
    /// </summary>
    /// <remarks>
    /// Asserted by running both over the same bounds at the two instants where an inclusive start and an
    /// exclusive end disagree with their opposites. Two implementations of one rule is how a screen comes
    /// to contradict a shop.
    /// </remarks>
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void The_lists_Live_agrees_with_the_evaluator_at_the_start_instant(
        int millisecondsFromStart,
        bool usable)
    {
        var start = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var now = start.AddMilliseconds(millisecondsFromStart);

        var terms = new PromoCodeTerms(
            1,
            "TESTCODE",
            PromoDiscountType.FixedAmount,
            100m,
            null,
            null,
            false,
            start,
            null,
            null,
            null,
            0,
            true);

        var evaluation = PromoEvaluator.Evaluate(
            terms,
            [new OrderPricingLine(1, 1, 500m, 500m, 500m)],
            now);

        Assert.Equal(usable, evaluation.IsUsable);

        Assert.Equal(
            usable ? PromoCodeStatus.Live : PromoCodeStatus.Scheduled,
            PromoCodeSchedule.StatusOf(true, start, null, now));
    }
}
