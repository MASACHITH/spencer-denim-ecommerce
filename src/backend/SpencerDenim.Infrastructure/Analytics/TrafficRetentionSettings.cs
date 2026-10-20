using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Reads the two retention periods the nightly purge and the rollup's catch-up use: the <c>Traffic.*</c> settings
/// rows, falling back to <see cref="TrafficOptions"/> (issue #193). The rule itself is
/// <see cref="TrafficRetentionPeriods.Resolve"/>; this only fetches its inputs.
/// <para>
/// <b>Not cached.</b> It is read twice a night, so a cache would buy nothing and would make a saved change wait
/// for an eviction nobody wired. A secret row is never read: the catalogue makes both keys <c>Int</c>, and the
/// filter is here so that a row flipped to secret by hand is ignored rather than parsed as ciphertext.
/// </para>
/// </summary>
internal sealed partial class TrafficRetentionSettings(
    AppDbContext dbContext,
    TrafficOptions options,
    ILogger<TrafficRetentionSettings> logger)
{
    private static readonly string[] Keys =
    [
        SettingKeys.Traffic.RawRetentionDays,
        SettingKeys.Traffic.BotRetentionDays,
    ];

    public async Task<TrafficRetentionPeriods> ReadAsync(CancellationToken cancellationToken)
    {
        var values = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key) && !setting.IsSecret)
            .Select(setting => new { setting.Key, setting.Value })
            .ToDictionaryAsync(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var raw = values.GetValueOrDefault(SettingKeys.Traffic.RawRetentionDays);
        var bot = values.GetValueOrDefault(SettingKeys.Traffic.BotRetentionDays);

        var periods = TrafficRetentionPeriods.Resolve(raw, bot, options.RawRetentionDays, options.BotRetentionDays);

        // A row that holds something but was not used is worth one line: somebody wrote it by hand, and the purge
        // is quietly running on configuration instead. An empty row is the unseeded case and says nothing.
        if (periods.RawSource == TrafficRetentionSource.Configuration && !string.IsNullOrWhiteSpace(raw))
        {
            LogIgnored(SettingKeys.Traffic.RawRetentionDays, periods.RawDays);
        }

        if (periods.BotSource == TrafficRetentionSource.Configuration && !string.IsNullOrWhiteSpace(bot))
        {
            LogIgnored(SettingKeys.Traffic.BotRetentionDays, periods.BotDays);
        }

        return periods;
    }

    [LoggerMessage(
        EventId = 7133,
        Level = LogLevel.Warning,
        Message = "The {Key} setting holds a value that is not a whole number of days within the form's bounds, so the "
            + "traffic purge is using the configured {Days} days instead. Re-save it on the Settings screen.")]
    private partial void LogIgnored(string key, int days);
}
