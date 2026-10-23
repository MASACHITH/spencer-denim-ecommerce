using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reads and records one of the seeder's own <c>Seed.*</c> version rows (the #152 rule, doc 08 §9 N13): content
/// offered in a version at or below the recorded one has been offered already, so an admin's edit or deletion of
/// it is final. Issue #33's courier and policy-page steps use it; the older markers keep their own copies of the
/// same few lines, which this deliberately does not touch.
/// </summary>
internal static class SeedVersionMarker
{
    /// <summary>The recorded version, or 0 when the row is missing or unreadable.</summary>
    public static async Task<(Setting? Row, int Version)> ReadAsync(
        AppDbContext dbContext,
        string key,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.Settings.FirstOrDefaultAsync(setting => setting.Key == key, cancellationToken);

        var version = row is not null
            && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        return (row, version);
    }

    /// <summary>
    /// Stages <paramref name="version"/> into the marker, adding the row when it is missing. Not saved here, so the
    /// caller commits it in the same save as the rows it offered.
    /// </summary>
    public static void Record(AppDbContext dbContext, Setting? row, string key, int version, string description)
    {
        var value = version.ToString(CultureInfo.InvariantCulture);

        if (row is null)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = key,
                GroupKey = SettingKeys.Seed.Group,
                ValueType = SettingValueType.Int,
                Value = value,

                // As Seed.Version: no screen lists this key, and CK_Settings_RequiredPermission needs a value.
                RequiredPermission = Permissions.Settings.Security,
                Description = description,
            });
        }
        else
        {
            row.Value = value;
        }
    }
}
