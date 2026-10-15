using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using SpencerDenim.Api.Health;

namespace SpencerDenim.Api.Logging;

internal static class SerilogSetup
{
    /// <summary>
    /// Structured logging to the console. Format: <c>Logging__ConsoleFormat</c> = <c>text</c> or <c>json</c>
    /// (default: text in Development, compact JSON elsewhere). Levels come from the <c>Serilog</c> config section.
    /// </summary>
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        var useJson = builder.Configuration["Logging:ConsoleFormat"] is { } format
            ? string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            : !builder.Environment.IsDevelopment();

        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", builder.Environment.ApplicationName);

            if (useJson)
            {
                configuration.WriteTo.Console(new RenderedCompactJsonFormatter());
            }
            else
            {
                configuration.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            }
        });

        return builder;
    }

    /// <summary>Request logging; successful health probes are logged at Verbose to keep the logs quiet.</summary>
    public static IApplicationBuilder UseSerilogRequestLoggingWithHealthFilter(this IApplicationBuilder app)
    {
        return app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (httpContext, _, exception) =>
            {
                if (exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Request.Path.StartsWithSegments(HealthEndpoints.LivenessPath, StringComparison.OrdinalIgnoreCase))
                {
                    return LogEventLevel.Verbose;
                }

                // The page-view beacon is one request per storefront page view (W4-03). At Information it
                // would put a line per page view in the log for ever — a second, unretained copy of the
                // traffic table's volume — so a successful one is Debug, like a health probe is Verbose.
                return httpContext.Request.Path.StartsWithSegments(
                    Endpoints.Analytics.TrafficEndpoints.PageViewsRoute,
                    StringComparison.OrdinalIgnoreCase)
                    ? LogEventLevel.Debug
                    : LogEventLevel.Information;
            };
        });
    }
}
