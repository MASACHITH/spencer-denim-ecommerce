using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Delivery.Admin;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Delivery;

/// <summary>
/// The Overseas delivery screen's reads and writes (owner decision 2026-10-21). Writes are optimistic: the
/// caller's token is set as the row's original <c>rowversion</c>, so EF's <c>UPDATE … WHERE RowVersion = @token</c>
/// refuses a stale screen in the same statement that would have overwritten a newer edit — no check-then-act
/// window under RCSI.
/// </summary>
internal sealed class OverseasDeliveryAdminStore(AppDbContext dbContext) : IOverseasDeliveryAdminStore
{
    public async Task<OverseasDeliveryAdminDto> ReadAsync(CancellationToken cancellationToken)
    {
        var method = await dbContext.DeliveryMethods
            .AsNoTracking()
            .Where(candidate => candidate.Code == DeliveryMethod.InternationalCode)
            .Select(candidate => new { candidate.IsEnabled, candidate.RowVersion })
            .FirstOrDefaultAsync(cancellationToken);

        var regions = await dbContext.DeliveryRegions
            .AsNoTracking()
            .OrderBy(region => region.SortOrder)
            .ThenBy(region => region.Name)
            .Select(region => new
            {
                region.Code,
                region.Name,
                region.Fee,
                region.EstimatedDaysMin,
                region.EstimatedDaysMax,
                region.IsEnabled,
                region.RowVersion,
                Countries = dbContext.Countries.Count(country => country.RegionCode == region.Code),
            })
            .ToListAsync(cancellationToken);

        var countries = await dbContext.Countries
            .AsNoTracking()
            .Where(country => country.Code != Country.SriLankaCode && country.RegionCode != null)
            .OrderBy(country => country.Name)
            .Select(country => new
            {
                country.Code,
                country.Name,
                RegionCode = country.RegionCode!,
                country.IsShippingEnabled,
                country.DeliveryFeeOverride,
                country.EstimatedDaysMinOverride,
                country.EstimatedDaysMaxOverride,
                country.PostalCodeRequired,
                country.RegionRequired,
                country.RowVersion,
            })
            .ToListAsync(cancellationToken);

        var methodEnabled = method?.IsEnabled ?? false;
        var byCode = regions.ToDictionary(region => region.Code, StringComparer.OrdinalIgnoreCase);

        return new OverseasDeliveryAdminDto(
            methodEnabled,
            method?.RowVersion is { } methodVersion ? Convert.ToBase64String(methodVersion) : null,
            regions
                .Select(region => new DeliveryRegionAdminDto(
                    region.Code,
                    region.Name,
                    region.Fee,
                    region.EstimatedDaysMin,
                    region.EstimatedDaysMax,
                    region.IsEnabled,
                    region.Countries,
                    Convert.ToBase64String(region.RowVersion ?? [])))
                .ToList()
                .AsReadOnly(),
            countries
                .Select(country =>
                {
                    byCode.TryGetValue(country.RegionCode, out var region);

                    // The same function checkout prices with, so the screen shows exactly what a shopper pays.
                    var quote = OverseasDeliveryRate.Resolve(new OverseasRateInputs(
                        methodEnabled,
                        country.IsShippingEnabled,
                        country.DeliveryFeeOverride,
                        country.EstimatedDaysMinOverride,
                        country.EstimatedDaysMaxOverride,
                        region?.IsEnabled ?? false,
                        region?.Fee,
                        region?.EstimatedDaysMin ?? 0,
                        region?.EstimatedDaysMax ?? 0));

                    return new CountryDeliveryAdminDto(
                        country.Code,
                        country.Name,
                        country.RegionCode,
                        country.IsShippingEnabled,
                        country.DeliveryFeeOverride,
                        country.EstimatedDaysMinOverride,
                        country.EstimatedDaysMaxOverride,
                        country.PostalCodeRequired,
                        country.RegionRequired,
                        quote.IsAvailable,
                        quote.IsAvailable ? quote.Fee : null,
                        quote.IsAvailable ? quote.EstimatedDaysMin : null,
                        quote.IsAvailable ? quote.EstimatedDaysMax : null,
                        quote.Refusal.ToString(),
                        Convert.ToBase64String(country.RowVersion ?? []));
                })
                .ToList()
                .AsReadOnly());
    }

    public async Task<DeliveryRegion?> FindRegionForUpdateAsync(
        string code,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var region = await dbContext.DeliveryRegions.FirstOrDefaultAsync(row => row.Code == code, cancellationToken);

        if (region is not null)
        {
            dbContext.Entry(region).Property(row => row.RowVersion).OriginalValue = expectedRowVersion;
        }

        return region;
    }

    public async Task<Country?> FindOverseasCountryForUpdateAsync(
        string code,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var country = await dbContext.Countries.FirstOrDefaultAsync(
            row => row.Code == code && row.Code != Country.SriLankaCode,
            cancellationToken);

        if (country is not null)
        {
            dbContext.Entry(country).Property(row => row.RowVersion).OriginalValue = expectedRowVersion;
        }

        return country;
    }

    public async Task<DeliveryMethod?> FindInternationalMethodForUpdateAsync(
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var method = await dbContext.DeliveryMethods.FirstOrDefaultAsync(
            row => row.Code == DeliveryMethod.InternationalCode,
            cancellationToken);

        if (method is not null)
        {
            dbContext.Entry(method).Property(row => row.RowVersion).OriginalValue = expectedRowVersion;
        }

        return method;
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }
}
