using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Diagnostics.Ping;

namespace SpencerDenim.Application;

public static class DependencyInjection
{
    /// <summary>Registers application use-case handlers.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQueryHandler<PingQuery, PingResponse>, PingQueryHandler>();

        return services;
    }
}
