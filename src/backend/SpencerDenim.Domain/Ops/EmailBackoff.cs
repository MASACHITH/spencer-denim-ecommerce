namespace SpencerDenim.Domain.Ops;

/// <summary>
/// The retry ladder for <c>ops.EmailOutbox</c>: <b>1 / 5 / 15 / 60 / 240 minutes</b>, which doc 08 §3.8
/// writes on <c>NextAttemptAtUtc</c> and doc 11 states twice — the SMTP actor row (§1.4) and the
/// notification matrix (§4), both as "retried with backoff 1/5/15/60/240 min".
/// <para>
/// <b>Five intervals therefore mean six attempts, and that reading is a decision.</b> An interval is the
/// wait <i>between</i> two attempts, so a ladder of five waits is the first attempt plus five retries:
/// try, wait 1, try, wait 5, try, wait 15, try, wait 60, try, wait 240, try — then
/// <see cref="EmailOutboxStatus.Dead"/>. The alternative reading, five attempts with the last interval
/// never used, would make one of the document's five figures dead text. Six attempts spread over
/// <b>5 hours 21 minutes</b> (1 + 5 + 15 + 60 + 240), which is also the useful answer for the failure
/// this ladder is really for — a mail server that is down for an afternoon — where stopping after 81
/// minutes would strand a customer's bank details for no reason.
/// </para>
/// </summary>
public static class EmailBackoff
{
    /// <summary>
    /// The waits, in order, after the first, second, third, fourth and fifth failed attempt.
    /// </summary>
    /// <remarks>
    /// Minutes as an <c>int</c> array rather than <c>TimeSpan</c>s so that the five figures read exactly
    /// as the two documents write them, and so a test can compare them to the document without
    /// arithmetic in between.
    /// </remarks>
    private static readonly int[] DelayMinutes = [1, 5, 15, 60, 240];

    /// <summary>
    /// Attempts in all before a message is <see cref="EmailOutboxStatus.Dead"/>: the first one, plus one
    /// per rung of the ladder.
    /// </summary>
    public static readonly short MaxAttempts = (short)(DelayMinutes.Length + 1);

    /// <summary>The ladder, for a test and for a log line that wants to state it.</summary>
    public static IReadOnlyList<int> LadderMinutes => DelayMinutes;

    /// <summary>
    /// How long to wait after <paramref name="attemptCount"/> attempts have been made and the last of
    /// them failed, or <see langword="null"/> when there is no attempt left and the message is dead.
    /// </summary>
    /// <param name="attemptCount">
    /// Attempts made so far, counting the one that has just failed. One after the first failure.
    /// </param>
    /// <param name="maxAttempts">
    /// The message's own ceiling (<see cref="EmailOutboxMessage.MaxAttempts"/>), which may be lower than
    /// <see cref="MaxAttempts"/> for a row queued under an older ladder.
    /// </param>
    public static TimeSpan? DelayAfter(short attemptCount, short maxAttempts)
    {
        if (attemptCount >= maxAttempts || attemptCount < 1)
        {
            // Out of attempts — or a nonsensical count, which is treated as out rather than as "wait the
            // first interval": a message whose counter is wrong must stop, not retry for ever.
            return null;
        }

        // attemptCount 1 takes the first rung. A count past the end of the ladder but below the row's own
        // ceiling (an older row with a higher MaxAttempts) stays on the last rung rather than throwing.
        var rung = Math.Min(attemptCount, (short)DelayMinutes.Length) - 1;

        return TimeSpan.FromMinutes(DelayMinutes[rung]);
    }
}
