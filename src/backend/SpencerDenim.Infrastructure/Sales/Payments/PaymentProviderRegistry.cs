using SpencerDenim.Application.Sales.Payments.Abstractions;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// <see cref="IPaymentProviderRegistry"/> over whatever <see cref="IPaymentProvider"/> implementations DI
/// has been given (PAY-03).
/// <para>
/// Registering a provider is therefore the whole act of adding one: one <c>AddScoped</c> line beside
/// <c>BankTransferProvider</c>'s and checkout finds it. Nothing here names a provider, so this class does
/// not change when PayPal arrives.
/// </para>
/// </summary>
internal sealed class PaymentProviderRegistry : IPaymentProviderRegistry
{
    private readonly Dictionary<string, IPaymentProvider> _byCode;

    public PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _byCode = new Dictionary<string, IPaymentProvider>(StringComparer.OrdinalIgnoreCase);
        var codes = new List<string>();

        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Code))
            {
                throw new InvalidOperationException(
                    $"{provider.GetType().Name} has no payment provider code. It must be a seeded "
                    + "sales.PaymentProviders.Code.");
            }

            // Loudly, at the first resolution rather than at the first order. Two providers claiming one
            // code is a question about which of them takes money, and "the last one registered" is not an
            // answer anybody would choose on purpose.
            if (!_byCode.TryAdd(provider.Code, provider))
            {
                throw new InvalidOperationException(
                    $"Two payment providers are registered for the code '{provider.Code}': "
                    + $"{_byCode[provider.Code].GetType().Name} and {provider.GetType().Name}.");
            }

            codes.Add(provider.Code);
        }

        ImplementedCodes = codes;
    }

    public IReadOnlyList<string> ImplementedCodes { get; }

    /// <summary>
    /// Case-insensitively, to agree with the database. <c>sales.PaymentProviders.Code</c> is
    /// <c>varchar(30)</c> in a <c>Latin1_General_100_CI_AS_SC</c> database, so a row written as
    /// <c>banktransfer</c> is the same row to SQL Server and the same provider here — where an ordinal
    /// comparison would find nothing and refuse the checkout with "that payment method cannot be used".
    /// </summary>
    public IPaymentProvider? Find(string? code) =>
        code is not null && _byCode.TryGetValue(code.Trim(), out var provider) ? provider : null;
}
