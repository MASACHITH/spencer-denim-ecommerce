using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Application.Ops;

/// <summary>
/// The retry ladder of doc 08 §3.8 and doc 11 §1.4 / §4: <b>1 / 5 / 15 / 60 / 240 minutes</b>.
/// <para>
/// <b>The figures are written out here on purpose.</b> This is the anchor: every other assertion about
/// the backoff — the job's, the store's — is relative to <see cref="EmailBackoff"/>, so a typo in the
/// ladder would be invisible everywhere at once. The same argument
/// <c>BrandNameConventionTests.The_shop_name_is_the_one_the_owner_decided</c> makes about the shop's name.
/// </para>
/// </summary>
public sealed class EmailBackoffTests
{
    [Fact]
    public void The_ladder_is_the_one_both_documents_state()
    {
        Assert.Equal([1, 5, 15, 60, 240], EmailBackoff.LadderMinutes);
    }

    /// <summary>
    /// Five intervals mean six attempts: the first, plus one per rung. See
    /// <see cref="EmailBackoff"/> for why that reading was chosen over five attempts with the last
    /// interval unused.
    /// </summary>
    [Fact]
    public void Six_attempts_in_all()
    {
        Assert.Equal(6, EmailBackoff.MaxAttempts);
        Assert.Equal(EmailBackoff.LadderMinutes.Count + 1, EmailBackoff.MaxAttempts);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 240)]
    public void Each_failed_attempt_waits_its_own_rung(short attemptCount, int expectedMinutes)
    {
        var delay = EmailBackoff.DelayAfter(attemptCount, EmailBackoff.MaxAttempts);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    /// <summary>
    /// The sixth failure has no rung left, which is what turns a message
    /// <see cref="EmailOutboxStatus.Dead"/>.
    /// </summary>
    [Fact]
    public void The_last_attempt_has_no_next_one()
    {
        Assert.Null(EmailBackoff.DelayAfter(EmailBackoff.MaxAttempts, EmailBackoff.MaxAttempts));
    }

    /// <summary>
    /// The whole ladder spans 5 h 21 min, which is the figure the class summary states and the only
    /// reason the six-attempt reading is worth having.
    /// </summary>
    [Fact]
    public void The_ladder_spans_five_hours_and_twenty_one_minutes()
    {
        var total = TimeSpan.Zero;

        for (short attempt = 1; attempt < EmailBackoff.MaxAttempts; attempt++)
        {
            total += EmailBackoff.DelayAfter(attempt, EmailBackoff.MaxAttempts)!.Value;
        }

        Assert.Equal(TimeSpan.FromMinutes(321), total);
    }

    /// <summary>
    /// A row queued under a lower ceiling stops at that ceiling, not at the ladder's length.
    /// <para>
    /// <c>EmailOutboxMessage.MaxAttempts</c> is stored per row so a message is not re-judged under a
    /// ladder that changed after it was queued. Without this branch a row with two attempts allowed would
    /// keep being retried to six.
    /// </para>
    /// </summary>
    [Fact]
    public void A_row_with_its_own_lower_ceiling_stops_there()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), EmailBackoff.DelayAfter(1, maxAttempts: 2));
        Assert.Null(EmailBackoff.DelayAfter(2, maxAttempts: 2));
    }

    /// <summary>
    /// A count past the ladder's length but below the row's own ceiling stays on the last rung.
    /// </summary>
    /// <remarks>
    /// Reachable only by a row queued when the ladder was longer, and it must not throw an
    /// <see cref="IndexOutOfRangeException"/> out of a background job: the outcome of that would be one
    /// message taking the whole sweep down with it.
    /// </remarks>
    [Fact]
    public void A_count_past_the_ladder_stays_on_the_last_rung()
    {
        Assert.Equal(TimeSpan.FromMinutes(240), EmailBackoff.DelayAfter(7, maxAttempts: 9));
    }

    /// <summary>
    /// A nonsensical counter stops rather than restarting the ladder.
    /// </summary>
    /// <remarks>
    /// Zero would mean "nothing has been attempted", which the failure path cannot be reached with — the
    /// claim increments before the send. Answering the first rung would make a row with a broken counter
    /// retry for ever, so it answers null and the row dies.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_broken_counter_stops(short attemptCount)
    {
        Assert.Null(EmailBackoff.DelayAfter(attemptCount, EmailBackoff.MaxAttempts));
    }
}
