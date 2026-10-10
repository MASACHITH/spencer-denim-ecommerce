using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The use cases PayHere adds (doc 11 P8b), registered from one place so that the shared
/// <c>AddApplication</c> gains a single line for them rather than five.
/// </summary>
public static class PayHereServiceCollectionExtensions
{
    /// <summary>Registers the notify, settings and status handlers, the processor and the retry sweep.</summary>
    public static IServiceCollection AddPayHereUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICommandHandler<PayHereNotifyCommand, PayHereNotifyResult>, PayHereNotifyCommandHandler>();
        services.AddScoped<ICommandHandler<UpdatePayHereSettingsCommand, SettingsWriteResult>, UpdatePayHereSettingsCommandHandler>();
        services.AddScoped<IQueryHandler<GetPayHereStatusQuery, PayHereStatusDto>, GetPayHereStatusQueryHandler>();

        // Shared by the first delivery and the sweep, so the two cannot apply a notification differently.
        services.AddScoped<PayHereNotificationProcessor>();
        services.AddScoped<PayHereRetrySweep>();

        return services;
    }
}
