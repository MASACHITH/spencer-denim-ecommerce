using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// What this seeder inserted. Separate from <see cref="SeedCounts"/>'s catalogue fields so the runner can
/// report them without this step having to know about the others.
/// </summary>
public sealed class SalesSeedCounts
{
    public int Provinces { get; set; }

    public int Districts { get; set; }

    public int DeliveryMethods { get; set; }

    public int DeliveryMethodZoneRates { get; set; }

    public int PaymentProviders { get; set; }

    /// <summary><c>sales.Couriers</c> offered by the courier seed version (issue #33).</summary>
    public int Couriers { get; set; }

    /// <summary><c>sales.DeliveryRegions</c> rows inserted (owner decision 2026-10-21).</summary>
    public int DeliveryRegions { get; set; }

    /// <summary><c>ref.Countries</c> rows inserted.</summary>
    public int Countries { get; set; }

    /// <summary>Whether this run applied the one-time bank-transfer switch-off (2026-10-21).</summary>
    public bool BankTransferSwitchedOff { get; set; }
}

/// <summary>
/// The reference data checkout reads: <c>ref.Provinces</c>, <c>ref.Districts</c>,
/// <c>sales.DeliveryMethods</c>, <c>sales.DeliveryMethodZoneRates</c> and <c>sales.PaymentProviders</c>
/// (doc 08 §9).
/// <para>
/// <b>Insert what is missing, never update what is there</b> — doc 08 §9's N13 rule, and the same shape as
/// <see cref="CatalogReferenceDataSeeder"/>. It matters more here than almost anywhere: a delivery fee and
/// a district's zone are the two things doc 08 marks "client to confirm (D8)", so the first thing the
/// client will do is change them, and a seeder that re-applied its own figures would undo that silently on
/// the next deployment.
/// </para>
/// <para>
/// <b>Why not <c>HasData</c>, which is what §9 says for provinces and districts.</b> §9's own rule for
/// <c>HasData</c> is that a released seed row is never changed afterwards — but EF re-applies a
/// <c>HasData</c> row whenever the value in the model changes, and <c>Districts.DeliveryZone</c> is
/// explicitly admin-editable (§3.1). The one column the client is expected to edit is therefore the one
/// column <c>HasData</c> would be most likely to overwrite. <see cref="CatalogReferenceDataSeeder"/> made
/// the same call for departments for the same reason and recorded it; this follows that precedent rather
/// than inventing a second convention. On an empty database the rows still land with the exact ids §9
/// fixes, because the ids are in the seed data and the keys are <c>ValueGeneratedNever</c>.
/// </para>
/// </summary>
public sealed class SalesReferenceDataSeeder(AppDbContext dbContext)
{
    public async Task<SalesSeedCounts> SeedAsync(CancellationToken cancellationToken)
    {
        var counts = new SalesSeedCounts
        {
            Provinces = await SeedProvincesAsync(cancellationToken),
        };

        // Districts after provinces, in the same call rather than a second SaveChanges later: the FK means
        // a district cannot be inserted before the province it names exists.
        counts.Districts = await SeedDistrictsAsync(cancellationToken);
        counts.DeliveryMethods = await SeedDeliveryMethodsAsync(cancellationToken);
        counts.DeliveryMethodZoneRates = await SeedZoneRatesAsync(cancellationToken);
        counts.PaymentProviders = await SeedPaymentProvidersAsync(cancellationToken);
        counts.Couriers = await SeedCouriersAsync(cancellationToken);

        // Regions before countries: a country's region is a foreign key.
        counts.DeliveryRegions = await SeedDeliveryRegionsAsync(cancellationToken);
        counts.Countries = await SeedCountriesAsync(cancellationToken);
        counts.BankTransferSwitchedOff = await ApplyPaymentProviderIncrementsAsync(cancellationToken);

        return counts;
    }

    /// <summary>The increment <see cref="ApplyPaymentProviderIncrementsAsync"/> brings a database up to.</summary>
    public const int PaymentProvidersSeedVersion = 1;

    private async Task<int> SeedDeliveryRegionsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.DeliveryRegions
            .Select(region => region.Code)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in CountryReferenceData.Regions)
        {
            if (existing.Contains(seed.Code, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            dbContext.DeliveryRegions.Add(new DeliveryRegion
            {
                Code = seed.Code,
                Name = seed.Name,

                // Placeholders for the owner (CountryReferenceData). Inserted once and never revisited, so the
                // owner's own figures survive every deployment.
                Fee = seed.Fee,
                EstimatedDaysMin = seed.EstimatedDaysMin,
                EstimatedDaysMax = seed.EstimatedDaysMax,
                IsEnabled = true,
                SortOrder = seed.SortOrder,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private async Task<int> SeedCountriesAsync(CancellationToken cancellationToken)
    {
        // Sri Lanka is normally already here: the migration inserts it so the order and address backfill has a
        // key to point at. Insert-missing makes that harmless.
        var existing = await dbContext.Countries
            .Select(country => country.Code)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in CountryReferenceData.Countries)
        {
            if (existing.Contains(seed.Code, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            dbContext.Countries.Add(new Country
            {
                Code = seed.Code,
                Name = seed.Name,
                RegionCode = seed.RegionCode,
                PostalCodeRequired = CountryReferenceData.PostalCodeRequired.Contains(seed.Code),
                RegionRequired = CountryReferenceData.RegionRequired.Contains(seed.Code),
                IsShippingEnabled = !CountryReferenceData.NoDeliverableAddresses.Contains(seed.Code),

                // Sri Lanka first in every list, the rest by name.
                SortOrder = seed.RegionCode is null ? (short)0 : (short)1,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    /// <summary>
    /// Version 1 of the payment-provider increments: <b>bank transfer is switched off once</b> (owner decision
    /// 2026-10-21, "block only for now"; PayPal is the only payment method).
    /// </summary>
    /// <remarks>
    /// <b>The rule.</b> When this database's <c>Seed.PaymentProvidersVersion</c> is below 1, the BankTransfer row
    /// is set to <c>IsEnabled = 0</c> <i>unless an admin has saved it</i> — that is, unless
    /// <c>UpdatedByUserId</c> is set, which the audited Payment methods switch stamps — and the marker is written
    /// as 1 in the same transaction. From then on this step does nothing, ever, so a Super Admin who switches bank
    /// transfer back on keeps it on through every deployment.
    /// <para>
    /// <b>Why an existing database that had it on is switched off this once.</b> Before this change there was no
    /// switch: every BankTransfer row that exists was written by the seeder with <c>IsEnabled = 1</c>, and nobody
    /// chose that value. The owner's decision is newer than every such row, so the safe reading is to apply it
    /// once. A row an admin <i>has</i> touched (<c>UpdatedByUserId</c> set) is a choice newer than the seed and
    /// is left alone. A fresh database inserts the row already switched off (<see cref="SalesReferenceData"/>),
    /// so the step only writes the marker there.
    /// </para>
    /// <para>
    /// <b>Idempotent and concurrency-safe</b> the way every reference-data step is: it runs inside
    /// <c>CatalogSeedRunner</c>'s <c>sp_getapplock</c>, and the update and the marker commit together, so an
    /// interrupted run repeats in full and a completed one never repeats.
    /// </para>
    /// </remarks>
    /// <returns>Whether this run switched bank transfer off.</returns>
    internal async Task<bool> ApplyPaymentProviderIncrementsAsync(CancellationToken cancellationToken)
    {
        var marker = await dbContext.Settings
            .FirstOrDefaultAsync(setting => setting.Key == SettingKeys.Seed.PaymentProvidersVersion, cancellationToken);

        if (ReadVersion(marker) >= PaymentProvidersSeedVersion)
        {
            return false;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var switchedOff = await dbContext.PaymentProviders
                .Where(provider => provider.Code == PaymentProviderCodes.BankTransfer
                    && provider.IsEnabled
                    && provider.UpdatedByUserId == null)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(provider => provider.IsEnabled, false),
                    cancellationToken) > 0;

            var value = PaymentProvidersSeedVersion.ToString(CultureInfo.InvariantCulture);
            if (marker is null)
            {
                dbContext.Settings.Add(new Setting
                {
                    Key = SettingKeys.Seed.PaymentProvidersVersion,
                    GroupKey = SettingKeys.Seed.Group,
                    ValueType = SettingValueType.Int,
                    Value = value,
                    RequiredPermission = Permissions.Settings.Security,
                    Description =
                        "The highest payment-provider seed increment applied to this database (2026-10-21: "
                        + "bank transfer switched off once). Written by the seeder; not editable.",
                });
            }
            else
            {
                marker.Value = value;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return switchedOff;
        });
    }

    /// <summary>The recorded increment, or 0 when there is no marker or it is not a number.</summary>
    internal static int ReadVersion(Setting? marker) =>
        marker is not null
        && int.TryParse(marker.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private async Task<int> SeedProvincesAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.Provinces
            .Select(province => province.Id)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in SalesReferenceData.Provinces)
        {
            if (existing.Contains(seed.Id))
            {
                continue;
            }

            dbContext.Provinces.Add(new Province
            {
                Id = seed.Id,
                Code = seed.Code,
                Name = seed.Name,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private async Task<int> SeedDistrictsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.Districts
            .Select(district => district.Id)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in SalesReferenceData.Districts)
        {
            if (existing.Contains(seed.Id))
            {
                continue;
            }

            dbContext.Districts.Add(new District
            {
                Id = seed.Id,
                ProvinceId = seed.ProvinceId,
                Code = seed.Code,
                Name = seed.Name,

                // The initial assignment only. D8 is open and the column is admin-editable, so this row is
                // never revisited once it exists.
                DeliveryZone = seed.DeliveryZone,
                SortOrder = seed.SortOrder,
                IsActive = true,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private async Task<int> SeedDeliveryMethodsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.DeliveryMethods
            .Select(method => method.Code)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in SalesReferenceData.DeliveryMethods)
        {
            // Ordinal: doc 08 §3.5 makes Code a varchar natural key and the three values are ASCII
            // constants in the Domain, so there is no culture in which they differ.
            if (existing.Contains(seed.Code, StringComparer.Ordinal))
            {
                continue;
            }

            dbContext.DeliveryMethods.Add(new DeliveryMethod
            {
                Code = seed.Code,
                Name = seed.Name,
                Description = seed.Description,
                IsEnabled = seed.IsEnabled,
                RequiresAddress = seed.RequiresAddress,
                SortOrder = seed.SortOrder,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private async Task<int> SeedZoneRatesAsync(CancellationToken cancellationToken)
    {
        // The methods' ids are generated, so the rates are keyed by code here and resolved against what is
        // actually in the table. A rate for a method that is not there is skipped rather than guessed at.
        var methodIds = await dbContext.DeliveryMethods
            .Select(method => new { method.Code, method.Id })
            .ToDictionaryAsync(method => method.Code, method => method.Id, StringComparer.Ordinal, cancellationToken);

        var existing = await dbContext.DeliveryMethodZoneRates
            .Select(rate => new { rate.DeliveryMethodId, rate.DeliveryZone })
            .ToListAsync(cancellationToken);

        var existingKeys = existing
            .Select(rate => (rate.DeliveryMethodId, rate.DeliveryZone))
            .ToHashSet();

        var added = 0;
        foreach (var seed in SalesReferenceData.DeliveryMethodZoneRates)
        {
            if (!methodIds.TryGetValue(seed.DeliveryMethodCode, out var methodId)
                || existingKeys.Contains((methodId, seed.DeliveryZone)))
            {
                continue;
            }

            dbContext.DeliveryMethodZoneRates.Add(new DeliveryMethodZoneRate
            {
                DeliveryMethodId = methodId,
                DeliveryZone = seed.DeliveryZone,
                IsAvailable = seed.IsAvailable,
                Fee = seed.Fee,
                FreeAboveAmount = seed.FreeAboveAmount,
                EstimatedDaysMin = seed.EstimatedDaysMin,
                EstimatedDaysMax = seed.EstimatedDaysMax,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private async Task<int> SeedPaymentProvidersAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.PaymentProviders
            .Select(provider => provider.Code)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in SalesReferenceData.PaymentProviders)
        {
            if (existing.Contains(seed.Code, StringComparer.Ordinal))
            {
                continue;
            }

            dbContext.PaymentProviders.Add(new PaymentProvider
            {
                Code = seed.Code,
                DisplayName = seed.DisplayName,
                IsEnabled = seed.IsEnabled,
                DisplayOrder = seed.DisplayOrder,
                CheckoutDescription = seed.CheckoutDescription,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    /// <summary>
    /// Issue #33: the couriers the shop starts with (<see cref="SalesReferenceData.Couriers"/>), <b>offered once by
    /// version</b> through <c>Seed.CouriersVersion</c>.
    /// <para>
    /// A courier is the kind of row an owner is expected to delete (a carrier the shop never books), and "insert
    /// what is missing" would put a deleted one back on every deployment — so only seeds introduced after the
    /// recorded version are considered, and the new version is recorded in the same save. <b>Never over an existing
    /// row:</b> a courier already holding the name (UX_Couriers_Name, case-insensitive like the collation) is left
    /// exactly as the admin set it — its template, telephone number and switch included.
    /// </para>
    /// </summary>
    private async Task<int> SeedCouriersAsync(CancellationToken cancellationToken)
    {
        var (marker, recorded) = await SeedVersionMarker.ReadAsync(
            dbContext, SettingKeys.Seed.CouriersVersion, cancellationToken);

        if (recorded >= SalesReferenceData.CourierSeedVersion)
        {
            return 0;
        }

        var taken = (await dbContext.Couriers.Select(courier => courier.Name).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var seed in SalesReferenceData.Couriers.Where(seed => seed.IntroducedIn > recorded))
        {
            if (!taken.Add(seed.Name))
            {
                continue;
            }

            dbContext.Couriers.Add(new Courier
            {
                Name = seed.Name,
                Phone = seed.Phone,
                TrackingUrlTemplate = seed.TrackingUrlTemplate,
                IsActive = seed.IsActive,
                SortOrder = seed.SortOrder,
            });
            added++;
        }

        SeedVersionMarker.Record(
            dbContext,
            marker,
            SettingKeys.Seed.CouriersVersion,
            SalesReferenceData.CourierSeedVersion,
            "The highest courier seed version applied to this database (#33). Written by the seeder; not editable.");

        await dbContext.SaveChangesAsync(cancellationToken);

        return added;
    }
}
