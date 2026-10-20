using System.Globalization;
using System.Text;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Catalog;

/// <summary>One refusal of a review field, keyed by the request field it is about.</summary>
public sealed record ReviewProblem(string Field, string Message);

/// <summary>A review as it will be stored, after <see cref="ReviewRules.Clean"/>.</summary>
public sealed record CleanReview(byte Rating, string? Title, string? Body);

/// <summary>The two moderation actions of doc 11 P12 step 5.</summary>
public enum ReviewModeration
{
    /// <summary>Show it (<c>Reviews.Approve</c>): from Pending, or from Hidden ("approve a hidden one").</summary>
    Approve = 1,

    /// <summary>Take it off the site (<c>Reviews.Edit</c>): from Pending, or from Approved.</summary>
    Hide = 2,
}

/// <summary>
/// The rules a review must pass before it reaches the database, and the status rules around it (PDP-08,
/// ADM-16; doc 08 §3.2, doc 11 P12).
/// <para>
/// <b>What is not here, on purpose: "one review per customer per product" and "Verified buyer".</b> Both depend
/// on rows another request may be writing, so they are decided in <c>ProductReviewStore</c>'s transaction under
/// the product's stats-row lock, with <c>UX_ProductReviews_Product_Customer</c> as the backstop. What this class
/// checks is everything that depends only on what the customer sent.
/// </para>
/// </summary>
public static class ReviewRules
{
    public const int MinRating = 1;

    public const int MaxRating = 5;

    /// <summary>Doc 08 §3.2: <c>Title nvarchar(120)</c>.</summary>
    public const int TitleMaxLength = 120;

    /// <summary>Doc 08 §3.2: <c>Body nvarchar(4000)</c>.</summary>
    public const int BodyMaxLength = 4000;

    /// <summary>Doc 08 §3.2: <c>DisplayName nvarchar(60)</c>.</summary>
    public const int DisplayNameMaxLength = 60;

    /// <summary>Doc 08 §3.2: <c>ModerationNote nvarchar(500)</c>.</summary>
    public const int ModerationNoteMaxLength = 500;

    /// <summary>
    /// The most consecutive line breaks a body keeps. Paragraphs survive; a screenful of blank lines pushing
    /// every other review off the page does not.
    /// </summary>
    public const int MaxConsecutiveLineBreaks = 2;

    /// <summary>
    /// Validates and normalises a submission. <see langword="null"/> rating means "not sent".
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Rating: a whole number from 1 to 5, required (doc 11 P12 step 1).</description></item>
    /// <item><description>Title: optional, one line, up to 120 characters after trimming.</description></item>
    /// <item><description>Body: optional, up to 4,000 characters after trimming; <c>\r\n</c> and <c>\r</c>
    /// become <c>\n</c> and runs of more than two line breaks shrink to two.</description></item>
    /// <item><description>U+0085, U+2028 and U+2029 are line breaks too: a title refuses them like <c>\n</c>, and a
    /// body stores them as <c>\n</c> (#189 D2).</description></item>
    /// <item><description>Both refuse control characters (other than the body's line breaks and tabs), the
    /// bidirectional override and isolate characters (text that displays differently from how it reads — the
    /// "Trojan Source" class), and unpaired surrogates (which SQL Server would store as garbage).</description></item>
    /// </list>
    /// Markup is <b>not</b> refused or stripped: it is stored as typed and rendered as text, so "&lt;b&gt;great&lt;/b&gt;"
    /// appears with its angle brackets. Removing it would change what the customer wrote; rendering it as
    /// text is what makes it harmless.
    /// </remarks>
    public static (CleanReview? Review, IReadOnlyList<ReviewProblem> Problems) Clean(int? rating, string? title, string? body)
    {
        var problems = new List<ReviewProblem>();

        if (rating is null)
        {
            problems.Add(new("rating", "Choose a star rating from 1 to 5."));
        }
        else if (rating < MinRating || rating > MaxRating)
        {
            problems.Add(new("rating", "The rating must be a whole number from 1 to 5."));
        }

        var cleanTitle = CleanText(title, "title", TitleMaxLength, multiline: false, problems);
        var cleanBody = CleanText(body, "body", BodyMaxLength, multiline: true, problems);

        return problems.Count > 0
            ? (null, problems)
            : (new CleanReview((byte)rating!.Value, cleanTitle, cleanBody), problems);
    }

    /// <summary>
    /// A moderation note: optional, trimmed, up to 500 characters, the same character rules as a body.
    /// </summary>
    public static (string? Note, ReviewProblem? Problem) CleanNote(string? note)
    {
        var problems = new List<ReviewProblem>();
        var clean = CleanText(note, "note", ModerationNoteMaxLength, multiline: true, problems);

        return problems.Count > 0 ? (null, problems[0]) : (clean, null);
    }

    /// <summary>
    /// The statuses an action may move a review from (doc 11 P12: "Pending → Approved ↔ Hidden (or Pending →
    /// Hidden)"). Approving an approved review, or hiding a hidden one, is not a transition.
    /// </summary>
    public static IReadOnlyList<ReviewStatus> AllowedFrom(ReviewModeration action) => action switch
    {
        ReviewModeration.Approve => [ReviewStatus.Pending, ReviewStatus.Hidden],
        ReviewModeration.Hide => [ReviewStatus.Pending, ReviewStatus.Approved],
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown moderation action."),
    };

    /// <summary>The status an action leaves a review in.</summary>
    public static ReviewStatus TargetOf(ReviewModeration action) => action switch
    {
        ReviewModeration.Approve => ReviewStatus.Approved,
        ReviewModeration.Hide => ReviewStatus.Hidden,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown moderation action."),
    };

    /// <summary>
    /// The status a customer's submission or edit lands in.
    /// </summary>
    /// <param name="current">The review's status before the edit, or <see langword="null"/> for a new review.</param>
    /// <param name="requireApproval"><c>Reviews.RequireApproval</c>; unreadable means <see langword="true"/>.</param>
    /// <remarks>
    /// <b>A hidden review stays hidden when its author edits it.</b> Staff hid it for a reason (offensive content,
    /// personal data — doc 11 P12's exceptions); an edit that published it again with approval switched off would
    /// let the author undo a moderator's decision. Otherwise doc 11 §7 G-32's answer: an edit returns to Pending
    /// while approval is required, and a new review is Pending or Approved by the same switch (P12 step 3).
    /// </remarks>
    public static ReviewStatus StatusAfterCustomerWrite(ReviewStatus? current, bool requireApproval) =>
        current == ReviewStatus.Hidden
            ? ReviewStatus.Hidden
            : requireApproval ? ReviewStatus.Pending : ReviewStatus.Approved;

    /// <summary>
    /// The public name a review shows: the first name and the initial of the last name ("Nimal P."), trimmed to
    /// 60 characters. Never the email address, and never the full surname.
    /// </summary>
    public static string DisplayNameFor(string? firstName, string? lastName)
    {
        // Shown to everyone beside the Verified buyer badge. A name saved before the account rules refused them may
        // hold control or bidi characters (#189 D1), and a U+202E would reverse the badge and date after it, so they
        // are removed here whatever the profile holds; nothing left falls back to "Customer".
        var first = CollapseWhitespace(PlainText.StripUnsafeForOneLine(firstName));
        var last = CollapseWhitespace(PlainText.StripUnsafeForOneLine(lastName));

        var name = first.Length == 0 ? "Customer" : first;

        if (last.Length > 0)
        {
            var initial = char.IsSurrogate(last[0]) && last.Length > 1 ? last[..2] : last[..1];
            name = string.Create(CultureInfo.InvariantCulture, $"{name} {initial.ToUpperInvariant()}.");
        }

        if (name.Length > DisplayNameMaxLength)
        {
            var cut = DisplayNameMaxLength;

            if (char.IsHighSurrogate(name[cut - 1]))
            {
                cut--;
            }

            name = name[..cut];
        }

        return name;
    }

    /// <summary>
    /// The average <c>ProductStats.RatingAverage</c> stores for these ratings: two decimal places, halves away
    /// from zero (SQL Server's <c>ROUND</c>), <see langword="null"/> when there are none. The SQL recompute is
    /// the authority; this is the same arithmetic for the tests and the read model to agree with.
    /// </summary>
    public static decimal? AverageOf(IEnumerable<byte> ratings)
    {
        ArgumentNullException.ThrowIfNull(ratings);

        var list = ratings.ToList();

        return AverageOf(list.Sum(rating => (long)rating), list.Count);
    }

    /// <summary>The same average from a sum of ratings and a count, as the summary's star buckets give them.</summary>
    public static decimal? AverageOf(long ratingSum, int count) =>
        count <= 0 ? null : Math.Round((decimal)ratingSum / count, 2, MidpointRounding.AwayFromZero);

    private static string? CleanText(
        string? raw,
        string field,
        int maxLength,
        bool multiline,
        List<ReviewProblem> problems)
    {
        if (raw is null)
        {
            return null;
        }

        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // U+0085, U+2028 and U+2029 are line breaks too (#189 D2): a title refuses them exactly as it refuses \n,
        // and a body stores them as the one line break it keeps, so both treat every line break the same way.
        text = text
            .Replace(PlainText.NextLine, '\n')
            .Replace(PlainText.LineSeparator, '\n')
            .Replace(PlainText.ParagraphSeparator, '\n');

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
                continue;
            }

            if (char.IsSurrogate(character))
            {
                problems.Add(new(field, "This contains a character that cannot be stored. Please retype it."));
                return null;
            }

            if (character == '\n')
            {
                if (!multiline)
                {
                    problems.Add(new(field, "Write the title on one line."));
                    return null;
                }

                continue;
            }

            if (character == '\t' && multiline)
            {
                continue;
            }

            if (char.IsControl(character) || PlainText.IsBidiControl(character))
            {
                problems.Add(new(field, "This contains a hidden control character. Please retype it."));
                return null;
            }
        }

        text = text.Trim();

        if (multiline)
        {
            text = LimitLineBreaks(text);
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            problems.Add(new(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Keep this to {maxLength:N0} characters or fewer (it is {text.Length:N0}).")));
            return null;
        }

        return text;
    }

    private static string LimitLineBreaks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var run = 0;

        foreach (var character in text)
        {
            if (character == '\n')
            {
                run++;

                if (run > MaxConsecutiveLineBreaks)
                {
                    continue;
                }
            }
            else
            {
                run = 0;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
