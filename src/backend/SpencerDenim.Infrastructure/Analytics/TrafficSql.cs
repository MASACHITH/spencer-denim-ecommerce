using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// The SQL behind the traffic rollup and the Traffic page's live figures (doc 08 §8.3), written once so the
/// nightly tables and "today" can never be counted by two different rules.
/// <para>
/// <b>The rules, in the SQL's own order.</b>
/// </para>
/// <list type="number">
/// <item><description><b>Colombo days.</b> A view's day is <c>CONVERT(date, DATEADD(minute, 330, OccurredAtUtc))</c>
/// (doc 08 §1.4), and a range of days is selected on the raw UTC column, from Colombo midnight to Colombo
/// midnight, so <c>IX_PageViews_Occurred</c> can seek it. 23:59 and 00:01 Colombo are different days.</description></item>
/// <item><description><b>Sessions are attributed to their first view of the day.</b> Every human view of a session
/// that day is counted under the channel, device and source of the session's earliest view
/// (<c>FIRST_VALUE … ORDER BY OccurredAtUtc, Id</c>). A click inside the shop has no outside referrer and is
/// Direct in the raw row; this is what turns "arrived from Google, then six clicks" into seven Organic page
/// views and one Organic session.</description></item>
/// <item><description><b>Bots are never visitors.</b> <c>PageViews</c> and <c>Sessions</c> count humans only
/// (doc 08 §8.3: "Sessions = COUNT(DISTINCT SessionId) where not a bot"); bots go to <c>BotPageViews</c> under
/// their own view's channel, device and source.</description></item>
/// <item><description><b>Sources:</b> the UTM source, else the referring host, else <c>(direct)</c> for a Direct
/// view and <c>(not set)</c> for anything else; the top 50 per day by sessions keep their own key and the rest
/// become <c>(other)</c> (doc 08 §8.3).</description></item>
/// </list>
/// <para>
/// Every statement reads at the connection's default isolation level; nothing here sets one.
/// </para>
/// </summary>
internal static class TrafficSql
{
    /// <summary>The window parameters every statement takes: UTC instants of two Colombo midnights.</summary>
    public const string FromUtc = "@fromUtc";

    public const string ToUtc = "@toUtc";

    private static readonly string SourceKeyExpression =
        $"LEFT(COALESCE(pv.[UtmSource], pv.[ReferrerHost], CASE WHEN pv.[Channel] = {(byte)TrafficChannel.Direct} "
        + $"THEN N'{TrafficDaily.DirectSourceKey}' ELSE N'{TrafficDaily.NotSetSourceKey}' END), {TrafficDaily.SourceKeyMaxLength})";

    /// <summary>
    /// The common table expressions <c>v</c>, <c>humans</c>, <c>grouped</c> and <c>merged</c>: one row per Colombo day,
    /// channel and device (and source, when <paramref name="withSources"/>) for the views in
    /// [<see cref="FromUtc"/>, <see cref="ToUtc"/>).
    /// <para>
    /// Without sources the statement touches only <c>OccurredAtUtc</c>, <c>SessionId</c>, <c>IsBot</c>,
    /// <c>Channel</c>, <c>DeviceType</c> and the key, which is exactly what <c>IX_PageViews_Occurred</c> carries,
    /// so the live figures are answered from the index alone (<c>TrafficQueryPlanTests</c> proves it). The source
    /// needs <c>UtmSource</c> and <c>ReferrerHost</c>, which doc 08 §3.8 left out of the index.
    /// </para>
    /// </summary>
    public static string Ctes(bool withSources)
    {
        var source = withSources ? $", {SourceKeyExpression} AS [SourceKey]" : string.Empty;
        var firstSource = withSources ? $", FIRST_VALUE([SourceKey]) OVER {FirstOfSessionDay} AS [SourceKey]" : string.Empty;
        var sourceColumn = withSources ? ", [SourceKey]" : string.Empty;

        return $"""
            [v] AS (
                SELECT pv.[Id], pv.[OccurredAtUtc], pv.[SessionId], pv.[IsBot], pv.[Channel], pv.[DeviceType],
                       CONVERT(date, DATEADD(minute, 330, pv.[OccurredAtUtc])) AS [Date]{source}
                FROM [analytics].[PageViews] AS pv
                WHERE pv.[OccurredAtUtc] >= {FromUtc} AND pv.[OccurredAtUtc] < {ToUtc}
            ),
            [humans] AS (
                SELECT [Date], [SessionId],
                       FIRST_VALUE([Channel]) OVER {FirstOfSessionDay} AS [Channel],
                       FIRST_VALUE([DeviceType]) OVER {FirstOfSessionDay} AS [DeviceType]{firstSource}
                FROM [v]
                WHERE [IsBot] = 0
            ),
            [grouped] AS (
                SELECT [Date], [Channel], [DeviceType]{sourceColumn},
                       COUNT(*) AS [PageViews], COUNT(DISTINCT [SessionId]) AS [Sessions], 0 AS [BotPageViews]
                FROM [humans]
                GROUP BY [Date], [Channel], [DeviceType]{sourceColumn}
                UNION ALL
                SELECT [Date], [Channel], [DeviceType]{sourceColumn}, 0, 0, COUNT(*)
                FROM [v]
                WHERE [IsBot] = 1
                GROUP BY [Date], [Channel], [DeviceType]{sourceColumn}
            ),
            [merged] AS (
                SELECT [Date], [Channel], [DeviceType]{sourceColumn},
                       SUM([PageViews]) AS [PageViews], SUM([Sessions]) AS [Sessions], SUM([BotPageViews]) AS [BotPageViews]
                FROM [grouped]
                GROUP BY [Date], [Channel], [DeviceType]{sourceColumn}
            )
            """;
    }

    /// <summary>
    /// The <c>rolled</c> CTE after <see cref="Ctes"/> with sources: the top 50 sources per day keep their key, the
    /// rest are <c>(other)</c>.
    /// </summary>
    public static readonly string RolledCte = $"""
        [ranked] AS (
            SELECT [Date], [SourceKey],
                   ROW_NUMBER() OVER (PARTITION BY [Date]
                       ORDER BY SUM([Sessions]) DESC, SUM([PageViews]) DESC, SUM([BotPageViews]) DESC, [SourceKey]) AS [SourceRank]
            FROM [merged]
            GROUP BY [Date], [SourceKey]
        ),
        [rolled] AS (
            SELECT m.[Date], m.[Channel], m.[DeviceType],
                   CASE WHEN r.[SourceRank] <= {TrafficDaily.TopSourcesPerDay} THEN m.[SourceKey] ELSE N'{TrafficDaily.OtherSourceKey}' END AS [SourceKey],
                   m.[PageViews], m.[Sessions], m.[BotPageViews]
            FROM [merged] AS m
            JOIN [ranked] AS r ON r.[Date] = m.[Date] AND r.[SourceKey] = m.[SourceKey]
        )
        """;

    /// <summary>Doc 08 §8.3's rebuild of <c>TrafficDaily</c> for the window (the caller deletes the days first).</summary>
    public static string InsertTrafficDaily() => $"""
        WITH {Ctes(withSources: true)},
        {RolledCte}
        INSERT INTO [analytics].[TrafficDaily] ([Date], [Channel], [DeviceType], [SourceKey], [PageViews], [Sessions], [BotPageViews])
        SELECT [Date], [Channel], [DeviceType], [SourceKey], SUM([PageViews]), SUM([Sessions]), SUM([BotPageViews])
        FROM [rolled]
        GROUP BY [Date], [Channel], [DeviceType], [SourceKey];
        """;

    /// <summary>
    /// The human views of each stored path per Colombo day — <c>PageDaily</c>'s rows. Grouped by the path itself;
    /// the key is its MD5 so a 400-character path is not a key column.
    /// </summary>
    public const string PageRows = """
        SELECT CONVERT(date, DATEADD(minute, 330, pv.[OccurredAtUtc])) AS [Date],
               MIN(pv.[Path]) AS [Path], MAX(pv.[PageType]) AS [PageType], MAX(pv.[ProductId]) AS [ProductId],
               COUNT(*) AS [PageViews], COUNT(DISTINCT pv.[SessionId]) AS [Sessions]
        FROM [analytics].[PageViews] AS pv
        WHERE pv.[OccurredAtUtc] >= @fromUtc AND pv.[OccurredAtUtc] < @toUtc AND pv.[IsBot] = 0
        GROUP BY CONVERT(date, DATEADD(minute, 330, pv.[OccurredAtUtc])), pv.[Path]
        """;

    public static string InsertPageDaily() => $"""
        WITH [pages] AS ({PageRows})
        INSERT INTO [analytics].[PageDaily] ([Date], [PathHash], [Path], [PageType], [ProductId], [PageViews], [Sessions])
        SELECT [Date], CAST(HASHBYTES('MD5', [Path]) AS binary(16)), [Path], [PageType], [ProductId], [PageViews], [Sessions]
        FROM [pages];
        """;

    /// <summary>
    /// A period's traffic by day, channel and device: the stored days before <c>@liveFrom</c> from
    /// <c>TrafficDaily</c>, and the days from <c>@liveFrom</c> computed live from the raw views by the same rules.
    /// The live half reads only what <c>IX_PageViews_Occurred</c> carries.
    /// </summary>
    public static string TrafficByDay() => $"""
        WITH {Ctes(withSources: false)},
        [stored] AS (
            SELECT t.[Date], t.[Channel], t.[DeviceType], t.[PageViews], t.[Sessions], t.[BotPageViews]
            FROM [analytics].[TrafficDaily] AS t
            WHERE t.[Date] >= @fromDay AND t.[Date] <= @toDay AND t.[Date] < @liveFrom
        ),
        [everything] AS (
            SELECT [Date], [Channel], [DeviceType], [PageViews], [Sessions], [BotPageViews] FROM [stored]
            UNION ALL
            SELECT [Date], [Channel], [DeviceType], [PageViews], [Sessions], [BotPageViews] FROM [merged]
        )
        SELECT [Date], [Channel], [DeviceType],
               SUM([PageViews]) AS [PageViews], SUM([Sessions]) AS [Sessions], SUM([BotPageViews]) AS [BotPageViews]
        FROM [everything]
        GROUP BY [Date], [Channel], [DeviceType]
        """;

    /// <summary>The period's top sources by human sessions, stored and live together.</summary>
    public static string TopSources() => $"""
        WITH {Ctes(withSources: true)},
        {RolledCte},
        [stored] AS (
            SELECT t.[SourceKey], t.[Channel], t.[PageViews], t.[Sessions]
            FROM [analytics].[TrafficDaily] AS t
            WHERE t.[Date] >= @fromDay AND t.[Date] <= @toDay AND t.[Date] < @liveFrom
        ),
        [everything] AS (
            SELECT [SourceKey], [Channel], [PageViews], [Sessions] FROM [stored]
            UNION ALL
            SELECT [SourceKey], [Channel], [PageViews], [Sessions] FROM [rolled]
        )
        SELECT TOP (@take) [SourceKey], SUM([Sessions]) AS [Sessions], SUM([PageViews]) AS [PageViews]
        FROM [everything]
        GROUP BY [SourceKey]
        HAVING SUM([PageViews]) > 0
        ORDER BY SUM([Sessions]) DESC, SUM([PageViews]) DESC, [SourceKey]
        """;

    /// <summary>The period's most-viewed paths by human page views, stored and live together, with product names.</summary>
    public static string TopPages() => $"""
        WITH [live] AS ({PageRows}),
        [everything] AS (
            SELECT pd.[Path], pd.[PageType], pd.[ProductId], pd.[PageViews], pd.[Sessions]
            FROM [analytics].[PageDaily] AS pd
            WHERE pd.[Date] >= @fromDay AND pd.[Date] <= @toDay AND pd.[Date] < @liveFrom
            UNION ALL
            SELECT [Path], [PageType], [ProductId], [PageViews], [Sessions] FROM [live]
        ),
        [top] AS (
            SELECT TOP (@take) [Path], MAX([PageType]) AS [PageType], MAX([ProductId]) AS [ProductId],
                   SUM([PageViews]) AS [PageViews], SUM([Sessions]) AS [Sessions]
            FROM [everything]
            GROUP BY [Path]
            ORDER BY SUM([PageViews]) DESC, SUM([Sessions]) DESC, [Path]
        )
        SELECT t.[Path], t.[PageType], t.[ProductId], p.[Name] AS [ProductName], t.[PageViews], t.[Sessions]
        FROM [top] AS t
        LEFT JOIN [catalog].[Products] AS p ON p.[Id] = t.[ProductId]
        ORDER BY t.[PageViews] DESC, t.[Sessions] DESC, t.[Path]
        """;

    /// <summary>
    /// The oldest <c>@take</c> Colombo days before <see cref="ToUtc"/> that still have raw views and no
    /// <c>TrafficDaily</c> row — the nightly catch-up's work list (issue #193).
    /// <para>
    /// <b>No lower bound, on purpose.</b> The purge never deletes a day the rollup has not built, so a never-built day
    /// keeps all of its raw rows however old it is, and this finds it wherever it is. It reads only days that have
    /// raw rows (a range of <c>IX_PageViews_Occurred</c>, which holds everything the purge has kept, i.e. roughly the
    /// raw period) and never enumerates empty calendar days; <c>TOP</c> caps one night's work.
    /// </para>
    /// </summary>
    public const string OldestUnbuiltDaysBefore = """
        SELECT TOP (@take) d.[Date] AS [Value]
        FROM (
            SELECT CONVERT(date, DATEADD(minute, 330, pv.[OccurredAtUtc])) AS [Date]
            FROM [analytics].[PageViews] AS pv
            WHERE pv.[OccurredAtUtc] < @toUtc
        ) AS d
        WHERE NOT EXISTS (SELECT 1 FROM [analytics].[TrafficDaily] AS t WHERE t.[Date] = d.[Date])
        GROUP BY d.[Date]
        ORDER BY d.[Date]
        """;

    /// <summary>
    /// Colombo days in [<see cref="FromUtc"/>, <see cref="ToUtc"/>) that have raw views and no <c>TrafficDaily</c> row:
    /// days the rollup never built (the host was down at 00:30, or for longer than three days).
    /// </summary>
    public const string UnbuiltDays = """
        SELECT DISTINCT d.[Date] AS [Value]
        FROM (
            SELECT CONVERT(date, DATEADD(minute, 330, pv.[OccurredAtUtc])) AS [Date]
            FROM [analytics].[PageViews] AS pv
            WHERE pv.[OccurredAtUtc] >= @fromUtc AND pv.[OccurredAtUtc] < @toUtc
        ) AS d
        WHERE NOT EXISTS (SELECT 1 FROM [analytics].[TrafficDaily] AS t WHERE t.[Date] = d.[Date])
        """;

    /// <summary>The window clause shared by every first-view-of-the-session-day.</summary>
    private const string FirstOfSessionDay =
        "(PARTITION BY [Date], [SessionId] ORDER BY [OccurredAtUtc], [Id] ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)";
}
