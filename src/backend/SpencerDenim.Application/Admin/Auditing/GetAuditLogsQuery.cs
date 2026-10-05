using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Admin.Auditing;

/// <summary>
/// A validated, normalised <c>ops.AuditLogs</c> listing query (ADM-03, doc 08 §3.8). Everything in it is
/// already trimmed and inside its bounds, so the read store composes predicates without re-checking
/// anything — the same shape <c>ProductListQuery</c> uses for the catalogue listing.
/// <para>
/// Rows are always returned <c>OccurredAtUtc DESC</c> (matching <c>IX_AuditLogs_Occurred</c> /
/// <c>IX_AuditLogs_Entity</c> / <c>IX_AuditLogs_User</c>): there is no sort option, because doc 08 gives
/// this screen no other sort to offer and an unindexed one would defeat the point of those indexes.
/// </para>
/// </summary>
public sealed record GetAuditLogsQuery
{
    /// <summary><c>ops.AuditLogs.EntityType varchar(100)</c> (doc 08 §3.8).</summary>
    public const int MaxEntityTypeLength = 100;

    /// <summary><c>ops.AuditLogs.EntityId varchar(100)</c> (doc 08 §3.8).</summary>
    public const int MaxEntityIdLength = 100;

    /// <summary><c>ops.AuditLogs.Action varchar(30)</c> (doc 08 §3.8).</summary>
    public const int MaxActionLength = 30;

    /// <summary>Default rows per page when none is given.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Highest rows per page a caller may ask for, so a crafted request cannot force a huge scan.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Highest page number, so <c>OFFSET</c> stays sane over three years of retained rows (doc 08 §3.8).</summary>
    public const int MaxPage = 100_000;

    private GetAuditLogsQuery()
    {
    }

    /// <summary>
    /// The signed-in staff user asking. The handler resolves their effective permissions from this id to decide
    /// whether rows about, or written by, a Super Admin are visible to them at all.
    /// </summary>
    public int ActorUserId { get; private init; }

    public string? EntityType { get; private init; }

    public string? EntityId { get; private init; }

    public int? UserId { get; private init; }

    public string? Action { get; private init; }

    public DateTime? FromUtc { get; private init; }

    public DateTime? ToUtc { get; private init; }

    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; } = DefaultPageSize;

    /// <summary>Rows to skip. Offset paging, exactly like the catalogue listing (doc 08 §7.1).</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Validates and normalises a raw query string. Every problem is collected, so one 400 answers the
    /// whole request instead of the first mistake (same contract as <c>ProductListQuery.Create</c>).
    /// </summary>
    public static Result<GetAuditLogsQuery> Create(AuditLogListCriteria criteria, int actorUserId)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var errors = new List<ValidationError>();

        var entityType = NormaliseText(criteria.EntityType, "entityType", MaxEntityTypeLength, errors);
        var entityId = NormaliseText(criteria.EntityId, "entityId", MaxEntityIdLength, errors);
        var action = NormaliseText(criteria.Action, "action", MaxActionLength, errors);
        var userId = ParseWholeNumber(criteria.UserId, "userId", 1, int.MaxValue, errors);

        // A bare date (no time-of-day, e.g. "2026-09-30" — what an HTML <input type="date"> sends) means
        // a whole calendar day. As the lower bound that is already midnight; as the upper bound it has to
        // become the last instant of that day, or a "To" filter would exclude almost the entire day the
        // caller actually meant to include (it would only match rows at exactly 00:00:00.000).
        var fromUtc = ParseUtcDate(criteria.FromUtc, "fromUtc", endOfDayIfBareDate: false, errors);
        var toUtc = ParseUtcDate(criteria.ToUtc, "toUtc", endOfDayIfBareDate: true, errors);
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
        {
            errors.Add(new ValidationError("fromUtc", "fromUtc must not be later than toUtc."));
        }

        var page = ParseWholeNumber(criteria.Page, "page", 1, MaxPage, errors) ?? 1;
        var pageSize = ParseWholeNumber(criteria.PageSize, "pageSize", 1, MaxPageSize, errors) ?? DefaultPageSize;

        if (errors.Count > 0)
        {
            return Result.Failure<GetAuditLogsQuery>(errors.AsReadOnly());
        }

        return Result.Success(new GetAuditLogsQuery
        {
            ActorUserId = actorUserId,
            EntityType = entityType,
            EntityId = entityId,
            UserId = userId,
            Action = action,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Page = page,
            PageSize = pageSize,
        });
    }

    private static string? NormaliseText(string? raw, string field, int maxLength, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length > maxLength)
        {
            errors.Add(new ValidationError(field, $"{field} must be {maxLength} characters or fewer."));
            return null;
        }

        return value;
    }

    /// <summary>Reads an integer parameter; binding it as <c>int?</c> on the endpoint would 500 on a bad value.</summary>
    private static int? ParseWholeNumber(string? raw, string field, int minimum, int maximum, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
        {
            errors.Add(new ValidationError(field, $"{field} must be a whole number between {minimum} and {maximum}."));
            return null;
        }

        return value;
    }

    /// <summary>The exact shape an HTML <c>&lt;input type="date"&gt;</c> sends, so a bare date can be told apart from a full timestamp.</summary>
    private const string BareDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Reads a date/time boundary as UTC. Accepts any ISO 8601 form <see cref="DateTime.TryParse(string?, IFormatProvider?, DateTimeStyles, out DateTime)"/>
    /// understands (a bare date, or a full timestamp with an offset), and always returns a <see cref="DateTimeKind.Utc"/> value.
    /// <para>
    /// A bare date is a whole calendar day, not one instant. As a lower bound, midnight already is the
    /// start of that day. As an upper bound (<paramref name="endOfDayIfBareDate"/>), it is rolled to the
    /// last tick of that day — otherwise <c>toUtc=2026-09-30</c> would match only rows at exactly
    /// 2026-09-30T00:00:00.000 and silently drop the rest of the day a caller plainly meant to include.
    /// A timestamp that already carries a time-of-day or an offset is trusted exactly as given.
    /// </para>
    /// </summary>
    private static DateTime? ParseUtcDate(string? raw, string field, bool endOfDayIfBareDate, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();

        if (endOfDayIfBareDate
            && DateTime.TryParseExact(trimmed, BareDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var bareDate))
        {
            return DateTime.SpecifyKind(bareDate.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
        }

        if (!DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var value))
        {
            errors.Add(new ValidationError(field, $"{field} must be a valid date (ISO 8601), e.g. 2026-09-01 or 2026-09-01T00:00:00Z."));
            return null;
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
