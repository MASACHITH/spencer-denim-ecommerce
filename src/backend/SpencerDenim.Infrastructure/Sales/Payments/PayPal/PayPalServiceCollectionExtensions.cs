using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// Everything PayPal adds to the container except the provider itself, whose one registration line is in
/// <c>AddInfrastructure</c> beside the bank transfer's (<c>PaymentProviderContractTests</c> reads that file to
/// check that every provider class is on the list).
/// </summary>
internal static class PayPalServiceCollectionExtensions
{
    public static IServiceCollection AddPayPalPayments(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Settings and links. Not cached (see PayPalSettingsReader); links are pure over the bound options.
        services.AddScoped<IPayPalSettingsReader, PayPalSettingsReader>();
        services.AddSingleton<PayPalLinks>();

        // The gateway: a typed HttpClient, so tests replace IPayPalGateway with a fake and nothing in this
        // repository ever touches the network. The token cache is a singleton so a busy minute is not a token
        // request per call. A single timeout for every call (the buyer is waiting on the capture); no retry
        // handler, on purpose — see PayPalHttpGateway.
        services.AddSingleton<PayPalTokenCache>();
        services.AddHttpClient<IPayPalGateway, PayPalHttpGateway>(client =>
        {
            client.Timeout = PayPalHttpGateway.RequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ColomboWardrobe-PayPal/1.0");
        });

        // The lifecycle: persistence, the one apply, claim-capture-apply, the customer endpoints' logic, and
        // the webhook (which is also the retry job's processor, so the two are the same code path).
        services.AddScoped<IPayPalPaymentStore, PayPalPaymentStore>();
        services.AddScoped<PayPalEventLog>();
        services.AddScoped<PayPalCaptureApplier>();
        services.AddScoped<PayPalCaptureProcessor>();
        services.AddScoped<IPayPalCheckoutFlow, PayPalCheckoutFlow>();
        services.AddScoped<PayPalWebhookProcessor>();
        services.AddScoped<IPayPalWebhookHandler>(provider => provider.GetRequiredService<PayPalWebhookProcessor>());
        services.AddScoped<IPaymentEventProcessor>(provider => provider.GetRequiredService<PayPalWebhookProcessor>());

        // RecoverPayPalCaptures (doc 08 §4.2 step 5).
        services.AddScoped<PayPalCaptureRecovery>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RecoverPayPalCapturesService>());

        // RetryPaymentEvents (doc 08 §3.8) is provider-neutral: it sweeps whichever providers have registered
        // an IPaymentEventProcessor. The TryAdd forms mean a second gateway that registers the same pieces
        // does not run the job twice.
        services.TryAddScoped<IDuePaymentEvents, DuePaymentEvents>();
        services.TryAddScoped<PaymentEventRetryRunner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RetryPaymentEventsService>());

        return services;
    }
}
