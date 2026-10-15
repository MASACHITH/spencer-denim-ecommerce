namespace SpencerDenim.Application.Admin.Reporting;

/// <summary>
/// One figure for the period, the same figure for the previous period, and how it moved (doc 11 P16 step 1:
/// "KPIs compare with the same-length period immediately before").
/// </summary>
/// <param name="Current">This period's value; <see langword="null"/> only for a ratio with nothing to divide by.</param>
/// <param name="Previous">The previous period's value, on the same rule.</param>
/// <param name="Change">
/// <see cref="Current"/> − <see cref="Previous"/>, or <see langword="null"/> when either is.
/// </param>
/// <param name="PercentChange">
/// The change as a percentage of <see cref="Previous"/>, rounded to one decimal place, or
/// <see langword="null"/> <b>when the previous value is zero</b> (or absent). A rise from nothing is not a
/// percentage of anything — "+∞%" and "+100%" are both false — so the screen says "up from nothing" or
/// "no change" from <see cref="Change"/> instead. A fall <i>to</i> zero is an ordinary −100%.
/// </param>
public sealed record ComparedFigure(decimal? Current, decimal? Previous, decimal? Change, decimal? PercentChange)
{
    public static ComparedFigure Of(decimal? current, decimal? previous)
    {
        if (current is not { } now || previous is not { } before)
        {
            return new ComparedFigure(current, previous, null, null);
        }

        var change = now - before;

        return new ComparedFigure(
            now,
            before,
            change,
            before == 0m ? null : Math.Round(change / before * 100m, 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>An average: <paramref name="total"/> ÷ <paramref name="count"/>, or null when nothing was counted.</summary>
    public static decimal? Average(decimal total, int count) =>
        count == 0 ? null : Math.Round(total / count, 2, MidpointRounding.AwayFromZero);
}
