using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// Everything PayHere needs from Infrastructure other than the provider class itself, registered from one
/// place so the shared <c>AddInfrastructure</c> gains one line for it. The provider's own registration stays
/// in <c>AddInfrastructure</c> beside the bank transfer's, because <c>PaymentProviderContractTests</c> reads
/// that list to prove every provider class is on it.
/// </summary>
public static class PayHereServiceCollectionExtensions
{
    /// <summary>Registers the settings reader, the stores and the retry service.</summary>
    public static IServiceCollection AddPayHereSupport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IPayHereSettingsReader, PayHereSettingsReader>();
        services.AddScoped<IPayHereProviderSwitch, PayHereProviderSwitch>();
        services.AddScoped<IPayHerePaymentStore, PayHerePaymentStore>();
        services.AddScoped<IPayHereEventRetryStore, PayHereEventRetryStore>();

        // PayHere is told 200 as soon as a verified notification is stored, so the sweep is what finishes
        // one whose first delivery died (doc 11 P8b step 5).
        services.AddHostedService<RetryPayHereEventsService>();

        return services;
    }
}
