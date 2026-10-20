using System.Globalization;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Domain.Analytics;

/// <summary>Where a retention period the purge uses came from.</summary>
public enum TrafficRetentionSource
{
    /// <summary>The <c>ops.Settings</c> row, as the Traffic settings form saved it.</summary>
    Settings = 1,

    /// <summary><c>Traffic__RawRetentionDays</c> / <c>Traffic__BotRetentionDays</c>, or their code defaults.</summary>
    Configuration = 2,
}

/// <summary>
/// The two retention periods the nightly purge and the rollup's catch-up use, and where each came from
/// (doc 08 §3.8, §8.4; issue #193).
/// </summary>
/// <param name="RawDays">Colombo days of page views kept, today included.</param>
/// <param name="BotDays">Colombo days of bot page views kept, today included. Never more than <paramref name="RawDays"/>.</param>
/// <param name="RawSource">Where <paramref name="RawDays"/> came from.</param>
/// <param name="BotSource">Where <paramref name="BotDays"/> came from.</param>
public sealed record TrafficRetentionPeriods(
    int RawDays,
    int BotDays,
    TrafficRetentionSource RawSource,
    TrafficRetentionSource BotSource)
{
    /// <summary>
    /// <b>The precedence rule, in one place: a valid settings row wins, configuration is the fallback.</b>
    /// <para>
    /// A row is used when it holds whole invariant digits inside the form's bounds
    /// (<see cref="SettingKeys.Traffic"/>). Anything else — no row, an empty row, text, a number outside the
    /// bounds because somebody wrote it with SQL — falls back to the configured figure, which
    /// <c>TrafficOptions</c>' start-up validation has already held to 4–3,650 with bots no longer than people.
    /// Falling back rather than clamping a bad row, because a clamped value is a number nobody chose, while the
    /// configured one is the deployment's own stated default.
    /// </para>
    /// <para>
    /// <b>Why the row wins.</b> It is the one a staff user can see and change, so it is the one they will believe
    /// is in force; an environment variable that silently overrode the screen would make the form lie. The
    /// fallback still matters: before the seeder has run, and if a row is ever damaged, the purge keeps doing
    /// exactly what it did before the form existed.
    /// </para>
    /// <para>
    /// <b>Bots are never kept longer than people.</b> If the resolved pair breaks that — a bot row of 60 beside a
    /// raw figure of 30 that fell back to configuration, say — the bot period is cut to the raw one. The form
    /// refuses the combination; this only covers a pair it never saw.
    /// </para>
    /// </summary>
    /// <param name="rawSetting">The <c>Traffic.RawRetentionDays</c> row's value, or <see langword="null"/>.</param>
    /// <param name="botSetting">The <c>Traffic.BotRetentionDays</c> row's value, or <see langword="null"/>.</param>
    /// <param name="configuredRawDays">The configured raw period, already validated at start-up.</param>
    /// <param name="configuredBotDays">The configured bot period, already validated at start-up.</param>
    public static TrafficRetentionPeriods Resolve(
        string? rawSetting,
        string? botSetting,
        int configuredRawDays,
        int configuredBotDays)
    {
        var raw = TryReadDays(rawSetting, SettingKeys.Traffic.RawRetentionDaysMin, SettingKeys.Traffic.RawRetentionDaysMax);
        var bot = TryReadDays(botSetting, SettingKeys.Traffic.BotRetentionDaysMin, SettingKeys.Traffic.BotRetentionDaysMax);

        var rawDays = raw ?? configuredRawDays;
        var botDays = bot ?? configuredBotDays;

        return new TrafficRetentionPeriods(
            rawDays,
            Math.Min(botDays, rawDays),
            raw is null ? TrafficRetentionSource.Configuration : TrafficRetentionSource.Settings,
            bot is null ? TrafficRetentionSource.Configuration : TrafficRetentionSource.Settings);
    }

    /// <summary>
    /// A stored value as a whole number of days inside <paramref name="min"/>–<paramref name="max"/>, or
    /// <see langword="null"/>. Invariant digits only, the way the form writes them (doc 08 §3.8: values are
    /// stored in invariant culture); a sign, a space or a decimal point is not a number of days.
    /// </summary>
    public static int? TryReadDays(string? value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
        && days >= min
        && days <= max
            ? days
            : null;
}
