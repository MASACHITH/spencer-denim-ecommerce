using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// <c>sales.PaymentProviders.IsEnabled</c> for the Payment methods switch (owner decision 2026-10-21). Optimistic,
/// like every admin write here: the caller's token is the row's original <c>rowversion</c>.
/// </summary>
internal sealed class PaymentProviderAdminStore(AppDbContext dbContext) : IPaymentProviderAdminStore
{
    public async Task<IReadOnlyList<PaymentProviderRow>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.PaymentProviders
            .AsNoTracking()
            .OrderBy(provider => provider.DisplayOrder)
            .ThenBy(provider => provider.Code)
            .Select(provider => new PaymentProviderRow(
                provider.Code,
                provider.DisplayName,
                provider.IsEnabled,
                provider.RowVersion ?? Array.Empty<byte>()))
            .ToListAsync(cancellationToken);

    public async Task<PaymentProvider?> FindForUpdateAsync(
        string code,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var provider = await dbContext.PaymentProviders.FirstOrDefaultAsync(
            row => row.Code == code,
            cancellationToken);

        if (provider is not null)
        {
            dbContext.Entry(provider).Property(row => row.RowVersion).OriginalValue = expectedRowVersion;
        }

        return provider;
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
