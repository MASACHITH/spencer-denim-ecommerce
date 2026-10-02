using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates the <c>ops.Settings</c> rows of <see cref="SettingCatalog"/> (ADM-22, doc 08 §9 N13).
/// <para>
/// <b>Idempotent, and asymmetric on purpose.</b> A key with no row gets one, with a <see langword="null"/>
/// value. A key that already has a row keeps its <b>value</b> untouched for ever — that value is the
/// client's — while its <see cref="Setting.GroupKey"/>, <see cref="Setting.ValueType"/>,
/// <see cref="Setting.IsSecret"/>, <see cref="Setting.RequiredPermission"/> and
/// <see cref="Setting.Description"/> are brought back into line with the catalogue.
/// </para>
/// <para>
/// <b>Why the metadata is re-applied and the value is not.</b> Those five columns describe the key and are
/// this codebase's to decide; the value is data and is the client's. The asymmetry is also the repair path
/// for the one failure that would otherwise be unfixable without SQL: if a key's
/// <c>RequiredPermission</c> were ever written wrong — a typo, a migration, a tightening of doc 08 §3.8's
/// DB2 — the row would be readable or writable by the wrong people and no screen could correct it, because
/// no screen exposes that column. Running the seeder fixes it. Re-applying the value instead would quietly
/// delete the client's social links on every deployment, which is the W1-12 defect wearing a different hat.
/// </para>
/// <para>
/// Rows for keys the catalogue no longer lists are <b>left alone</b>, not deleted: a key withdrawn from the
/// catalogue in a future build still holds a value somebody typed, and a seeder that deleted it would be a
/// data loss triggered by a code change. Cleaning those up is a migration's job, where it is deliberate and
/// reviewable.
/// </para>
/// </summary>
public sealed class SettingsSeeder(AppDbContext dbContext)
{
    /// <summary>Inserts missing rows and realigns existing rows' metadata. Returns how many rows it inserted.</summary>
    public async Task<SettingsSeedCounts> SeedAsync(CancellationToken cancellationToken)
    {
        var keys = SettingCatalog.All.Select(definition => definition.Key).ToList();

        var existing = await dbContext.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var inserted = 0;
        var realigned = 0;

        foreach (var definition in SettingCatalog.All)
        {
            if (!existing.TryGetValue(definition.Key, out var row))
            {
                dbContext.Settings.Add(new Setting
                {
                    Key = definition.Key,
                    GroupKey = definition.GroupKey,
                    ValueType = definition.ValueType,

                    // Null, always. The client has not uploaded a logo and has not sent their profile
                    // addresses, and a seeded placeholder would be the exact thing this task exists to
                    // remove: a social icon on a live shop pointing somewhere that is not the shop.
                    Value = null,
                    IsSecret = definition.IsSecret,
                    RequiredPermission = definition.RequiredPermission,
                    Description = definition.Description,
                });

                inserted++;
                continue;
            }

            if (Realign(row, definition))
            {
                realigned++;
            }
        }

        if (inserted > 0 || realigned > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new SettingsSeedCounts(inserted, realigned);
    }

    /// <summary>
    /// Brings one row's metadata back into line with the catalogue, answering whether anything moved.
    /// <para>
    /// Written as a diff rather than five unconditional assignments so that an already-correct row produces
    /// no UPDATE: without that, every start-up with seeding on would move every settings row's
    /// <c>rowversion</c> and invalidate every open settings form for no reason.
    /// </para>
    /// </summary>
    private static bool Realign(Setting row, SettingDefinition definition)
    {
        var moved = false;

        if (!string.Equals(row.GroupKey, definition.GroupKey, StringComparison.Ordinal))
        {
            row.GroupKey = definition.GroupKey;
            moved = true;
        }

        if (row.ValueType != definition.ValueType)
        {
            row.ValueType = definition.ValueType;
            moved = true;
        }

        if (row.IsSecret != definition.IsSecret)
        {
            row.IsSecret = definition.IsSecret;
            moved = true;
        }

        if (!string.Equals(row.RequiredPermission, definition.RequiredPermission, StringComparison.Ordinal))
        {
            row.RequiredPermission = definition.RequiredPermission;
            moved = true;
        }

        if (!string.Equals(row.Description, definition.Description, StringComparison.Ordinal))
        {
            row.Description = definition.Description;
            moved = true;
        }

        return moved;
    }
}

/// <summary>What <see cref="SettingsSeeder"/> did.</summary>
/// <param name="Inserted">Rows created because the key had none.</param>
/// <param name="Realigned">Existing rows whose metadata was brought back into line. Values are never touched.</param>
public sealed record SettingsSeedCounts(int Inserted, int Realigned);
