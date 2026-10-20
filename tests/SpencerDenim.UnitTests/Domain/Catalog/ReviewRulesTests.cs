using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Domain.Catalog;

/// <summary>
/// What a review must be before it reaches the database, and the status rules around it (PDP-08, ADM-16;
/// doc 08 §3.2, doc 11 P12 and §7 G-32).
/// </summary>
public sealed class ReviewRulesTests
{
    // ---- rating -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void A_rating_at_either_end_of_the_scale_is_accepted(int rating)
    {
        var (review, problems) = ReviewRules.Clean(rating, null, null);

        Assert.Empty(problems);
        Assert.Equal((byte)rating, review!.Rating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_rating_outside_one_to_five_is_refused(int rating)
    {
        var (review, problems) = ReviewRules.Clean(rating, "Fine", "Fine");

        Assert.Null(review);
        Assert.Equal("rating", Assert.Single(problems).Field);
    }

    [Fact]
    public void A_missing_rating_is_refused_with_its_own_message()
    {
        var (_, problems) = ReviewRules.Clean(null, null, null);

        Assert.Equal("Choose a star rating from 1 to 5.", Assert.Single(problems).Message);
    }

    // ---- lengths ----------------------------------------------------------------------------------------

    [Fact]
    public void Title_and_body_at_their_limits_are_accepted_and_one_over_is_refused()
    {
        Assert.Empty(ReviewRules.Clean(4, new string('t', ReviewRules.TitleMaxLength), new string('b', ReviewRules.BodyMaxLength)).Problems);

        var (_, titleProblems) = ReviewRules.Clean(4, new string('t', ReviewRules.TitleMaxLength + 1), null);
        Assert.Equal("title", Assert.Single(titleProblems).Field);

        var (_, bodyProblems) = ReviewRules.Clean(4, null, new string('b', ReviewRules.BodyMaxLength + 1));
        Assert.Equal("body", Assert.Single(bodyProblems).Field);
    }

    /// <summary>Lengths are measured after trimming, so padding does not push a review over the limit.</summary>
    [Fact]
    public void Lengths_are_measured_after_trimming()
    {
        var (review, problems) = ReviewRules.Clean(3, "  " + new string('t', ReviewRules.TitleMaxLength) + "  ", null);

        Assert.Empty(problems);
        Assert.Equal(ReviewRules.TitleMaxLength, review!.Title!.Length);
    }

    [Fact]
    public void Blank_text_is_stored_as_nothing()
    {
        var (review, _) = ReviewRules.Clean(5, "   ", "\n\n  \t ");

        Assert.Null(review!.Title);
        Assert.Null(review.Body);
    }

    [Fact]
    public void Every_bad_field_is_reported_at_once()
    {
        var (_, problems) = ReviewRules.Clean(9, new string('t', 200), new string('b', 5000));

        Assert.Equal(["rating", "title", "body"], problems.Select(problem => problem.Field));
    }

    // ---- characters ---------------------------------------------------------------------------------------

    /// <summary>Markup is kept as typed: it is rendered as text, never interpreted, so nothing is stripped.</summary>
    [Fact]
    public void Markup_is_kept_exactly_as_typed()
    {
        const string body = "<script>alert(1)</script> & <b>great</b>";
        var (review, problems) = ReviewRules.Clean(5, "<i>Nice</i>", body);

        Assert.Empty(problems);
        Assert.Equal("<i>Nice</i>", review!.Title);
        Assert.Equal(body, review.Body);
    }

    [Fact]
    public void Line_breaks_are_normalised_and_long_runs_shrink_to_one_blank_line()
    {
        var (review, _) = ReviewRules.Clean(4, null, "First\r\nSecond\rThird\n\n\n\n\nFourth");

        Assert.Equal("First\nSecond\nThird\n\nFourth", review!.Body);
    }

    [Fact]
    public void A_title_must_be_one_line()
    {
        var (_, problems) = ReviewRules.Clean(4, "One\nTwo", null);

        Assert.Equal("Write the title on one line.", Assert.Single(problems).Message);
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u0007")]
    [InlineData("\u001B[31m")]
    [InlineData("\u202E")]
    [InlineData("\u2066")]
    [InlineData("\u200F")]
    public void Control_and_direction_override_characters_are_refused(string hidden)
    {
        var (_, bodyProblems) = ReviewRules.Clean(4, null, "Good" + hidden + "fit");
        Assert.Equal("body", Assert.Single(bodyProblems).Field);

        var (_, titleProblems) = ReviewRules.Clean(4, "Good" + hidden, null);
        Assert.Equal("title", Assert.Single(titleProblems).Field);
    }

    /// <summary>#189 D2: U+0085, U+2028 and U+2029 are line breaks, refused in a title and kept as \n in a body.</summary>
    [Theory]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void Unicode_line_breaks_are_refused_in_a_title_and_kept_as_line_breaks_in_a_body(int code)
    {
        var separator = ((char)code).ToString();

        var (_, titleProblems) = ReviewRules.Clean(4, "First" + separator + "second", null);
        Assert.Equal("Write the title on one line.", Assert.Single(titleProblems).Message);

        var (review, bodyProblems) = ReviewRules.Clean(4, null, "First" + separator + "second");
        Assert.Empty(bodyProblems);
        Assert.Equal("First\nsecond", review!.Body);
    }

    /// <summary>#189 D1: names saved before the account rules refused them never carry their hidden characters out.</summary>
    [Fact]
    public void The_display_name_drops_control_bidi_and_line_break_characters_from_the_profile()
    {
        var first = "Ni" + (char)0x202E + "mal" + (char)0x0007;
        var last = (char)0x2066 + "Perera" + (char)0x2069;

        Assert.Equal("Nimal P.", ReviewRules.DisplayNameFor(first, last));
        Assert.Equal("Ann Marie", ReviewRules.DisplayNameFor("Ann" + (char)0x2028 + "Marie", null));
        Assert.Equal("Customer", ReviewRules.DisplayNameFor(((char)0x202E).ToString() + (char)0x0007, null));
        Assert.Equal("Nimal", ReviewRules.DisplayNameFor("Nimal", ((char)0xD800).ToString()));
    }

    [Fact]
    public void A_tab_is_allowed_in_the_body_but_not_the_title()
    {
        Assert.Empty(ReviewRules.Clean(4, null, "a\tb").Problems);
        Assert.Single(ReviewRules.Clean(4, "a\tb", null).Problems);
    }

    [Fact]
    public void An_unpaired_surrogate_is_refused_and_a_paired_one_kept()
    {
        Assert.Single(ReviewRules.Clean(4, null, "bad \uD83D here").Problems);

        var (review, problems) = ReviewRules.Clean(4, "සුපිරි 👍", "நன்று");
        Assert.Empty(problems);
        Assert.Equal("සුපිරි 👍", review!.Title);
    }

    // ---- moderation and status -----------------------------------------------------------------------------

    [Theory]
    [InlineData(ReviewModeration.Approve, ReviewStatus.Pending, true)]
    [InlineData(ReviewModeration.Approve, ReviewStatus.Hidden, true)]
    [InlineData(ReviewModeration.Approve, ReviewStatus.Approved, false)]
    [InlineData(ReviewModeration.Hide, ReviewStatus.Pending, true)]
    [InlineData(ReviewModeration.Hide, ReviewStatus.Approved, true)]
    [InlineData(ReviewModeration.Hide, ReviewStatus.Hidden, false)]
    public void Moderation_moves_only_along_doc_11s_transitions(ReviewModeration action, ReviewStatus from, bool allowed) =>
        Assert.Equal(allowed, ReviewRules.AllowedFrom(action).Contains(from));

    [Fact]
    public void Approve_shows_and_hide_hides()
    {
        Assert.Equal(ReviewStatus.Approved, ReviewRules.TargetOf(ReviewModeration.Approve));
        Assert.Equal(ReviewStatus.Hidden, ReviewRules.TargetOf(ReviewModeration.Hide));
    }

    [Theory]
    [InlineData(null, true, ReviewStatus.Pending)]
    [InlineData(null, false, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.Approved, true, ReviewStatus.Pending)]   // G-32: an edit is re-moderated
    [InlineData(ReviewStatus.Approved, false, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.Pending, false, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.Hidden, true, ReviewStatus.Hidden)]      // the author cannot undo a moderator
    [InlineData(ReviewStatus.Hidden, false, ReviewStatus.Hidden)]
    public void A_customer_write_lands_in_the_status_the_switch_and_history_say(
        ReviewStatus? current, bool requireApproval, ReviewStatus expected) =>
        Assert.Equal(expected, ReviewRules.StatusAfterCustomerWrite(current, requireApproval));

    // ---- display name and average ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Nimal", "Perera", "Nimal P.")]
    [InlineData("  Nimal  ", "  perera ", "Nimal P.")]
    [InlineData("Nimal", null, "Nimal")]
    [InlineData("Ann Marie", "de Silva", "Ann Marie D.")]
    [InlineData("", "Perera", "Customer P.")]
    [InlineData(null, null, "Customer")]
    public void The_display_name_is_first_name_and_last_initial(string? first, string? last, string expected) =>
        Assert.Equal(expected, ReviewRules.DisplayNameFor(first, last));

    [Fact]
    public void The_display_name_never_exceeds_its_column()
    {
        var name = ReviewRules.DisplayNameFor(new string('a', 100), "Perera");

        Assert.Equal(ReviewRules.DisplayNameMaxLength, name.Length);
    }

    [Theory]
    [InlineData(new byte[] { 5, 4 }, "4.5")]
    [InlineData(new byte[] { 5, 5, 4 }, "4.67")]
    [InlineData(new byte[] { 1, 1, 2 }, "1.33")]
    [InlineData(new byte[] { 3 }, "3")]
    public void The_average_is_two_places_halves_away_from_zero(byte[] ratings, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ReviewRules.AverageOf(ratings));

    [Fact]
    public void No_ratings_have_no_average()
    {
        Assert.Null(ReviewRules.AverageOf([]));
        Assert.Null(ReviewRules.AverageOf(0, 0));
    }

    [Fact]
    public void The_moderation_note_is_optional_and_bounded()
    {
        Assert.Equal((null, null), ReviewRules.CleanNote("   "));
        Assert.Equal("Spam", ReviewRules.CleanNote("  Spam ").Note);
        Assert.NotNull(ReviewRules.CleanNote(new string('n', ReviewRules.ModerationNoteMaxLength + 1)).Problem);
    }
}
