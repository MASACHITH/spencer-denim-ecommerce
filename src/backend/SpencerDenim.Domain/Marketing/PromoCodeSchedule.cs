using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// The status a staff member sees on the promo-code list. Doc 11 P3: "Scheduled (start in future) → Live
/// → Ended (by date) / Inactive (by staff) / Deleted (soft). <b>These are derived from dates and flags,
/// not stored statuses.</b>"
/// </summary>
public enum PromoCodeStatus : byte
{
    /// <summary>Active, and the start is still ahead. Nothing a shopper types will work yet.</summary>
    Scheduled = 1,

    /// <summary>Active and inside its window. The only status a shopper can use.</summary>
    Live = 2,

    /// <summary>Active, but the end has passed.</summary>
    Ended = 3,

    /// <summary>
    /// Switched off by staff, whatever the dates say. It takes precedence over the three above, because
    /// "we have turned this off" is the answer a staff member is looking for and the dates are then
    /// beside the point.
    /// </summary>
    Inactive = 4,
}

/// <summary>
/// The two time questions a promo code asks that <see cref="PromoEvaluator"/> deliberately does not: what
/// instant did a staff member mean, and what should the list say about this row right now.
/// </summary>
/// <remarks>
/// <b>Asia/Colombo is here and nowhere else, and that is the whole point of the file.</b> Doc 06 stores
/// every timestamp in UTC and displays it in Colombo, and the order emails already print Colombo times and
/// say so. A promo code's window is the one place where a <i>calendar</i> intention is typed in: a staff
/// member who sets a code to end on 12 October at 23:59 means 23:59 in the shop, which is 18:29 UTC. Left
/// as UTC the code would have died five and a half hours early, on the evening of the sale — which is this
/// project's existing UTC-versus-Colombo defect class, one feature over.
/// <para>
/// So the admin contract carries <b>Colombo wall-clock</b> date-times with no offset, this file converts
/// them, and the column stays UTC. <see cref="PromoEvaluator"/> then compares UTC with UTC and never has
/// to know.
/// </para>
/// </remarks>
public static class PromoCodeSchedule
{
    /// <summary>
    /// The instant a staff member meant by a wall-clock date and time in the shop.
    /// </summary>
    /// <param name="colomboLocal">
    /// A date and time as typed, with <see cref="DateTimeKind"/> ignored: the value is <i>defined</i> to be
    /// Colombo local, because that is what the admin form's <c>datetime-local</c> input produces and it
    /// carries no offset. Treating an incoming <c>Utc</c> kind as already-UTC would make the answer depend
    /// on whether a client happened to append a <c>Z</c>, which is exactly how this defect arrives.
    /// </param>
    public static DateTime ToUtc(DateTime colomboLocal) =>
        new DateTimeOffset(
                DateTime.SpecifyKind(colomboLocal, DateTimeKind.Unspecified),
                ColomboTime.Offset)
            .UtcDateTime;

    /// <summary>The wall-clock date and time in the shop for a stored instant, for the form to show back.</summary>
    public static DateTime ToColombo(DateTime utc) =>
        ColomboTime.ToLocal(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero))
            .DateTime;

    /// <summary>
    /// Doc 11 P3's derived status. <see cref="PromoCodeStatus.Inactive"/> wins over the dates; otherwise
    /// the window decides, with the same inclusive start and exclusive end
    /// <see cref="PromoEvaluator"/> applies — so a code the list calls Live is one a shopper can use, and
    /// the two can never disagree.
    /// </summary>
    public static PromoCodeStatus StatusOf(
        bool isActive,
        DateTime startsAtUtc,
        DateTime? endsAtUtc,
        DateTime utcNow)
    {
        if (!isActive)
        {
            return PromoCodeStatus.Inactive;
        }

        if (startsAtUtc > utcNow)
        {
            return PromoCodeStatus.Scheduled;
        }

        return endsAtUtc is { } endsAt && endsAt <= utcNow
            ? PromoCodeStatus.Ended
            : PromoCodeStatus.Live;
    }
}

/// <summary>
/// What a promo code string may be, and the one place it is normalised.
/// </summary>
/// <remarks>
/// <b>Doc 08 §3.6 gives the width and the case and nothing else</b> — "<c>varchar(40)</c>, Upper-case" —
/// so the character set below is a decision this task made rather than a rule it found, and it is in the
/// task report as one.
/// <list type="bullet">
/// <item><description>
/// <b>ASCII letters, digits, hyphen and underscore.</b> The column is <c>varchar</c> under
/// <c>Latin1_General_100_CI_AS_SC</c>, so a Sinhala or Tamil code could not round-trip through it at all;
/// storing one would be silent corruption. Hyphen and underscore are in because a code like
/// <c>NEW-YEAR-26</c> is the obvious thing a marketer writes.
/// </description></item>
/// <item><description>
/// <b>No spaces, inside or out.</b> A shopper retypes a code from a poster or an email, and
/// <c>FREE SHIP</c> would match or not depending on whether they copied the space — so it is refused on
/// the way in rather than becoming a support call.
/// </description></item>
/// </list>
/// The alternative, accepting anything that fits, was rejected because the cost of being wrong is
/// asymmetric: a marketer told "letters, numbers, - and _" retypes the code, while a shopper who cannot
/// make a code work has no way to find out why.
/// </remarks>
public static class PromoCodeText
{
    /// <summary>
    /// The trimmed, upper-cased form that is stored and matched. Null or blank in gives null out, so a
    /// caller can tell "no code" from "a code I could not use".
    /// </summary>
    /// <remarks>
    /// <c>ToUpperInvariant</c> and not the current culture: the Turkish dotless i would make <c>i</c>
    /// upper-case to <c>İ</c> on a machine with a <c>tr-TR</c> culture, and a code would then be stored
    /// differently depending on the server's locale. The same rule <c>Order.EmailNormalized</c> follows.
    /// </remarks>
    public static string? Normalise(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Whether a <b>normalised</b> code is one this shop will store: 1 to
    /// <see cref="PromoCode.MaxCodeLength"/> characters, each an ASCII letter, digit, hyphen or
    /// underscore.
    /// </summary>
    public static bool IsWellFormed(string? normalisedCode)
    {
        if (string.IsNullOrEmpty(normalisedCode) || normalisedCode.Length > PromoCode.MaxCodeLength)
        {
            return false;
        }

        foreach (var character in normalisedCode)
        {
            var allowed = character is >= 'A' and <= 'Z'
                || character is >= '0' and <= '9'
                || character is '-' or '_';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
