using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Analytics;
using SpencerDenim.Application.Analytics.Reporting;
using SpencerDenim.Application.Analytics.Search;

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
            .Validate(
                options => options.RawRetentionDays is >= TrafficOptions.MinimumRetentionDays and <= 3650,
                "Traffic:RawRetentionDays must be between 4 and 3650: the nightly rollup rebuilds the last three "
                + "days from the raw rows, so they must still be there.")
            .Validate(
                options => options.BotRetentionDays >= TrafficOptions.MinimumRetentionDays
                    && options.BotRetentionDays <= options.RawRetentionDays,
                "Traffic:BotRetentionDays must be at least 4 and no more than Traffic:RawRetentionDays.")
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

        // Doc 08 §8.3's traffic rollup (00:30 Colombo) and §8.4's raw-traffic purge (01:30). Both registered as
        // themselves too, so a test can run one pass as of a chosen day without starting the schedule; both
        // stand down when Traffic:JobsEnabled is false, which every test host sets.
        services.AddScoped<TrafficRollup>();
        services.AddScoped<TrafficRetention>();
        services.AddScoped<TrafficRetentionSettings>();
        services.AddSingleton<TrafficRollupService>();
        services.AddHostedService(provider => provider.GetRequiredService<TrafficRollupService>());
        services.AddSingleton<TrafficRetentionService>();
        services.AddHostedService(provider => provider.GetRequiredService<TrafficRetentionService>());

        // The Traffic page and the dashboard widget (ADM-20, ADM-06).
        services.AddScoped<ITrafficReader, TrafficReader>();

        return services.AddSearchStats();
    }

    /// <summary>
    /// Search statistics (doc 08 DB25, §7.6, §8.4; SRCH-01 popular searches; issue #78): the buffer, its 60-second
    /// flush, the popular-list reads, the staff hide list, and the 13-month purge (run by
    /// <see cref="TrafficRetentionService"/>, which is why this is registered beside traffic).
    /// </summary>
    public static IServiceCollection AddSearchStats(this IServiceCollection services)
    {
        services.AddOptions<SearchStatsOptions>()
            .BindConfiguration(SearchStatsOptions.SectionName)
            .Validate(
                options => options.FlushSeconds is >= 1 and <= 600,
                "SearchStats:FlushSeconds must be between 1 and 600.")
            .Validate(
                options => options.MaxBufferedTerms is >= 1 and <= 100_000,
                "SearchStats:MaxBufferedTerms must be between 1 and 100,000: the buffer is held in memory.")
            .Validate(
                options => options.PopularMinimumSearches is >= 1 and <= 100_000,
                "SearchStats:PopularMinimumSearches must be at least 1.")
            .ValidateOnStart();

        services.AddSingleton(provider => provider.GetRequiredService<IOptions<SearchStatsOptions>>().Value);

        // One buffer for the process; the recorder port and the writer resolve the same instance.
        services.AddSingleton<SearchQueryBuffer>();
        services.AddSingleton<ISearchQueryRecorder>(provider => provider.GetRequiredService<SearchQueryBuffer>());
        services.AddScoped<SearchQueryStore>();

        // Registered as itself and as the hosted service, so a test can drain it with FlushAsync.
        services.AddSingleton<SearchQueryWriterService>();
        services.AddHostedService(provider => provider.GetRequiredService<SearchQueryWriterService>());

        services.AddSingleton<PopularSearchCache>();
        services.AddScoped<IPopularSearchReader, PopularSearchReader>();
        services.AddScoped<IHiddenSearchTermStore, HiddenSearchTermStore>();
        services.AddScoped<SearchQueryRetention>();

        return services;
    }
}
