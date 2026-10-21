using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Analytics.Search;

/// <summary>
/// Search statistics (doc 08 DB25, §7.6; SRCH-01 "popular searches"; issue #78), bound from the <c>SearchStats</c>
/// section — <c>SearchStats__RecordingEnabled</c>, <c>SearchStats__PopularMinimumSearches</c> and so on.
/// <para>
/// <b>Configuration, not <c>ops.Settings</c></b>, like <c>TrafficOptions</c>: these are how the machinery runs.
/// The retention period is doc 08 §8.4's fixed 13 months and is not a setting at all.
/// </para>
/// </summary>
public sealed class SearchStatsOptions
{
    public const string SectionName = "SearchStats";

    /// <summary>Doc 08 DB25: "flushed every 60 s".</summary>
    public const int DefaultFlushSeconds = 60;

    /// <summary>
    /// The owner-decision default for <see cref="PopularMinimumSearches"/> (journal 2026-10-21-78-118-search):
    /// a term is shown to other shoppers only once it has been searched, with results, at least this many times
    /// in the window.
    /// </summary>
    public const int DefaultPopularMinimumSearches = 5;

    /// <summary>Off means the endpoint still answers 204 and nothing is counted.</summary>
    public bool RecordingEnabled { get; set; } = true;

    /// <summary>How long counts wait in memory before the upsert writes them.</summary>
    public int FlushSeconds { get; set; } = DefaultFlushSeconds;

    /// <summary>
    /// How many distinct (day, term) pairs may wait for the next flush. A full buffer still counts every term it
    /// already holds and drops new ones, so memory stays bounded however a script floods the endpoint.
    /// </summary>
    public int MaxBufferedTerms { get; set; } = 5000;

    /// <summary>The fewest searches with results in the window before a term may be shown publicly.</summary>
    public int PopularMinimumSearches { get; set; } = DefaultPopularMinimumSearches;

    public TimeSpan FlushInterval => TimeSpan.FromSeconds(FlushSeconds);
}

/// <summary>The fixed rules of the public list (doc 08 §7.6: "TOP 8 SearchQueriesDaily rows from the last 30 days with results").</summary>
public static class PopularSearchRules
{
    /// <summary>Doc 08 §7.6: "TOP 8".</summary>
    public const int Take = 8;

    /// <summary>Doc 08 §7.6: "from the last 30 days" — today and the 29 before it, Colombo days.</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// The longest term shown publicly. Up to 100 characters are counted (DB25), but a sentence is not a
    /// suggestion, and a long term is where an abusive one would hide (owner decision, journal 2026-10-21).
    /// </summary>
    public const int MaxShownLength = 40;

    /// <summary>The window ending on <paramref name="today"/>.</summary>
    public static (DateOnly From, DateOnly To) Window(DateOnly today) => (today.AddDays(-(WindowDays - 1)), today);
}

/// <summary>
/// The request-path half of DB25: "Buffered in memory". <b>Never blocks, never throws, never touches the
/// database</b>, for the reason <c>IPageViewRecorder</c> gives — a search must never wait on its own statistics.
/// </summary>
public interface ISearchQueryRecorder
{
    /// <summary>Counts one submitted search of an already normalised <paramref name="term"/> on the Colombo day of <paramref name="occurredAtUtc"/>.</summary>
    bool TryRecord(string term, DateTimeOffset occurredAtUtc);
}

/// <summary>Decides whether one submitted search is counted, and as what. Pure.</summary>
public static class SearchSubmissionClassifier
{
    /// <summary>
    /// The normalised term to count, or <see langword="null"/> for a bot (by the same user-agent rule and
    /// automation flag the traffic recorder uses, <see cref="UserAgentClassifier.IsBot"/>) or a term
    /// <see cref="SearchQueryText"/> refuses. Bots' searches are not counted at all: unlike page views, doc 08 has
    /// no bot column on this table, and a crawler walking search links is not what other shoppers look for.
    /// </summary>
    public static string? Accept(string? rawTerm, string? userAgent, bool automated) =>
        UserAgentClassifier.IsBot(userAgent, automated) ? null : SearchQueryText.Normalise(rawTerm);
}

/// <summary>One term on the public list, with what put it there.</summary>
public sealed record PopularSearchRow(string Term, int Searches);

/// <summary>A term staff have hidden.</summary>
public sealed record HiddenSearchTermRow(string Term, DateTime HiddenAtUtc);

/// <summary>Reads <c>analytics.SearchQueriesDaily</c> for the lists (Infrastructure).</summary>
public interface IPopularSearchReader
{
    /// <summary>
    /// The public list: up to <see cref="PopularSearchRules.Take"/> terms by searches with results in the window,
    /// at least <paramref name="minimumSearches"/>, no longer than <see cref="PopularSearchRules.MaxShownLength"/>,
    /// and none that staff have hidden. Cached briefly; see the implementation.
    /// </summary>
    Task<IReadOnlyList<PopularSearchRow>> GetPublicAsync(DateOnly today, int minimumSearches, CancellationToken cancellationToken);

    /// <summary>The same list read fresh, for the admin screen, which must show what is true now.</summary>
    Task<IReadOnlyList<PopularSearchRow>> GetFreshAsync(DateOnly today, int minimumSearches, CancellationToken cancellationToken);

    Task<IReadOnlyList<HiddenSearchTermRow>> GetHiddenAsync(CancellationToken cancellationToken);
}

/// <summary>Writes the hide list (Infrastructure). Each call is one transaction with its audit row.</summary>
public interface IHiddenSearchTermStore
{
    /// <summary>Hides <paramref name="term"/>; false when it was already hidden (nothing written).</summary>
    Task<bool> HideAsync(string term, DateTime hiddenAtUtc, AuditLog audit, CancellationToken cancellationToken);

    /// <summary>Shows <paramref name="term"/> again; false when it was not hidden (nothing written).</summary>
    Task<bool> ShowAsync(string term, AuditLog audit, CancellationToken cancellationToken);
}

/// <summary>The storefront's list: terms only, no counts — how often something was searched is the shop's business.</summary>
public sealed record PopularSearchesDto(IReadOnlyList<string> Terms);

public sealed record GetPopularSearchesQuery;

public sealed class GetPopularSearchesQueryHandler(IPopularSearchReader reader, SearchStatsOptions options, IClock clock)
    : IQueryHandler<GetPopularSearchesQuery, PopularSearchesDto>
{
    public async Task<PopularSearchesDto> HandleAsync(GetPopularSearchesQuery query, CancellationToken cancellationToken)
    {
        var rows = await reader.GetPublicAsync(ReportPeriod.ColomboDayOf(clock.UtcNow), options.PopularMinimumSearches, cancellationToken);
        return new PopularSearchesDto(rows.Select(row => row.Term).ToList());
    }
}

/// <summary>The admin screen: what shoppers see now, with the counts, and what staff have hidden.</summary>
/// <param name="MinimumSearches">The threshold in force, so the screen can say why a term is not there yet.</param>
public sealed record AdminPopularSearchesDto(
    IReadOnlyList<PopularSearchRow> Shown,
    IReadOnlyList<HiddenSearchTermRow> Hidden,
    int MinimumSearches);

public sealed record GetAdminPopularSearchesQuery;

public sealed class GetAdminPopularSearchesQueryHandler(IPopularSearchReader reader, SearchStatsOptions options, IClock clock)
    : IQueryHandler<GetAdminPopularSearchesQuery, AdminPopularSearchesDto>
{
    public async Task<AdminPopularSearchesDto> HandleAsync(GetAdminPopularSearchesQuery query, CancellationToken cancellationToken)
    {
        var today = ReportPeriod.ColomboDayOf(clock.UtcNow);
        var shown = await reader.GetFreshAsync(today, options.PopularMinimumSearches, cancellationToken);
        var hidden = await reader.GetHiddenAsync(cancellationToken);

        return new AdminPopularSearchesDto(shown, hidden, options.PopularMinimumSearches);
    }
}

public sealed record HideSearchTermCommand(string? Term, AdminActor Actor);

public sealed record ShowSearchTermCommand(string? Term, AdminActor Actor);

/// <summary>
/// The admin hide list (issue #78, owner decision): take a term off the public list, or put it back. Audited, as
/// every admin write is (doc 08 §3.8), with the term as the entity id — it has already passed the privacy rule, so
/// it is not personal data.
/// <para>
/// <b>The term is normalised exactly as recording normalises it</b>, so a staff user who types "Black Jeans " hides
/// the row stored as "black jeans". A term the privacy rule would never have counted is refused rather than stored
/// in a second place.
/// </para>
/// </summary>
public sealed class HiddenSearchTermHandlers(IHiddenSearchTermStore store, IClock clock)
    : ICommandHandler<HideSearchTermCommand, Result<bool>>,
      ICommandHandler<ShowSearchTermCommand, Result<bool>>
{
    /// <summary><c>ops.AuditLogs.EntityType</c>.</summary>
    public const string EntityType = "HiddenSearchTerm";

    public async Task<Result<bool>> HandleAsync(HideSearchTermCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (Validate(command.Term) is not { } term)
        {
            return Invalid();
        }

        var now = clock.UtcNow.UtcDateTime;
        var changed = await store.HideAsync(term, now, Audit(AuditActions.Create, term, command.Actor, now), cancellationToken);
        return Result.Success(changed);
    }

    public async Task<Result<bool>> HandleAsync(ShowSearchTermCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (Validate(command.Term) is not { } term)
        {
            return Invalid();
        }

        var now = clock.UtcNow.UtcDateTime;
        var changed = await store.ShowAsync(term, Audit(AuditActions.Delete, term, command.Actor, now), cancellationToken);
        return Result.Success(changed);
    }

    private static string? Validate(string? raw) =>
        raw is null || raw.Length > SearchQueryDaily.QueryMaxLength * 2 ? null : SearchQueryText.Normalise(raw);

    private static Result<bool> Invalid() =>
        Result.Failure<bool>(
        [
            new ValidationError(
                "term",
                "Enter a search term of at least 2 characters, without an email address, phone number or order number."),
        ]);

    private static AuditLog Audit(string action, string term, AdminActor actor, DateTime now) => new()
    {
        OccurredAtUtc = now,
        UserId = actor.UserId,
        UserEmail = actor.Email,
        Action = action,
        EntityType = EntityType,
        EntityId = term,
        BeforeJson = action == AuditActions.Delete ? Json(term) : null,
        AfterJson = action == AuditActions.Create ? Json(term) : null,
        IpAddress = actor.IpAddress,
        UserAgent = actor.UserAgent,
        CorrelationId = actor.CorrelationId,
    };

    private static string Json(string term) =>
        System.Text.Json.JsonSerializer.Serialize(new { term, hidden = true }, JsonOptions);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
}
