using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Analytics;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>Traffic recording, aggregation and retention (doc 08 §3.8, §8.3, §8.4; ADM-06, ADM-20).</summary>
internal static class TrafficServiceCollectionExtensions
{
    public static IServiceCollection AddTrafficRecording(this IServiceCollection services)
    {
        services.AddOptions<TrafficOptions>()
            .BindConfiguration(TrafficOptions.SectionName)
            .Validate(
                options => options.QueueCapacity is >= 1 and <= 1_000_000,
                "Traffic:QueueCapacity must be between 1 and 1,000,000: the queue is held in memory.")
            .Validate(
                options => options.BatchSize is >= 1 and <= 1000,
                "Traffic:BatchSize must be between 1 and 1000.")
            .Validate(
                options => options.FlushSeconds is >= 1 and <= 300,
                "Traffic:FlushSeconds must be between 1 and 300.")
            .ValidateOnStart();

        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TrafficOptions>>().Value);

        // One queue for the process: a queue per request queues nothing. The recorder port and the writer
        // resolve the same instance.
        services.AddSingleton<PageViewQueue>();
        services.AddSingleton<IPageViewRecorder>(provider => provider.GetRequiredService<PageViewQueue>());

        services.AddScoped<PageViewStore>();

        // Registered as itself and then as the hosted service, so a test (and nothing else) can resolve the
        // running writer and drain it with FlushAsync.
        services.AddSingleton<PageViewWriterService>();
        services.AddHostedService(provider => provider.GetRequiredService<PageViewWriterService>());

        return services;
    }
}
