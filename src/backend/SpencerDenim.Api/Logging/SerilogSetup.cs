using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using SpencerDenim.Api.Health;
using SpencerDenim.Application.Privacy;

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
                .Enrich.WithProperty("Application", builder.Environment.ApplicationName)

                // ASP.NET Core's hosting scope puts the raw request path on EVERY event written during a
                // request, and a guest's order token is a path segment (issue #201). Last, so it sees the
                // properties every other enricher and the scope added.
                .Enrich.With<RequestPathRedactingEnricher>()

                // And below Warning, ASP.NET Core's own categories are not written at all, whatever level the
                // configuration asks for: see IsAspNetCoreBelowWarning.
                .Filter.ByExcluding(IsAspNetCoreBelowWarning);

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

    /// <summary>
    /// The four properties of Serilog's default request-completion template (<c>HTTP {RequestMethod}
    /// {RequestPath} responded {StatusCode} in {Elapsed} ms</c>), under the same names, with the one that can hold
    /// a credential redacted. <see cref="RequestPathRedactingEnricher"/> would redact it as well; this keeps the
    /// request line safe on its own.
    /// </summary>
    internal static IEnumerable<LogEventProperty> RequestMessageTemplateProperties(
        HttpContext httpContext, string requestPath, double elapsedMilliseconds, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return
        [
            new LogEventProperty("RequestMethod", new ScalarValue(httpContext.Request.Method)),
            new LogEventProperty(RequestPathRedactingEnricher.PropertyName, new ScalarValue(RequestPathRedaction.ForLog(requestPath))),
            new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
            new LogEventProperty("Elapsed", new ScalarValue(elapsedMilliseconds)),
        ];
    }

    /// <summary>
    /// True for an event below Warning from a <c>Microsoft.AspNetCore</c> category (issue #201).
    /// <para>
    /// <b>Why not leave it to the configured level.</b> <c>appsettings.json</c> already sets
    /// <c>Microsoft.AspNetCore</c> to Warning, which is what keeps the lines out today. But below Warning those
    /// categories print the request target as a matter of course: <c>Hosting.Diagnostics</c>' "Request starting
    /// HTTP/1.1 GET https://host/api/orders/{token}?…" and "Request finished", the routing matcher's "candidate(s)
    /// found for the request path '/api/orders/{token}'", Kestrel's bad-request lines that quote the target they
    /// refused. A deployment that sets <c>Serilog__MinimumLevel__Override__Microsoft.AspNetCore</c> to chase a
    /// routing problem would write every guest's order token to the log, which Docker keeps. So the level is a
    /// floor in code: raising it is a configuration choice, lowering it is not available. Warning and above (an
    /// unhandled exception, an antiforgery failure) are written as before; the path on them is redacted by
    /// <see cref="RequestPathRedactingEnricher"/>.
    /// </para>
    /// </summary>
    internal static bool IsAspNetCoreBelowWarning(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        return logEvent.Level < LogEventLevel.Warning
            && logEvent.Properties.TryGetValue(Serilog.Core.Constants.SourceContextPropertyName, out var source)
            && source is ScalarValue { Value: string category }
            && category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Request logging; successful health probes are logged at Verbose to keep the logs quiet.
    /// <para>
    /// <b>The path that is written is <see cref="RequestPathRedaction.ForLog"/> of the request's, never the
    /// request's own (issue #201).</b> A guest's order token is a path segment (<c>/api/orders/{token}</c>, the
    /// payment-slip and PayPal routes under it, <c>/api/admin/orders/…</c>), and this line is written for every
    /// request, so before this the token of every order page view was in the container's log, which Docker keeps.
    /// The property keeps its name, <c>RequestPath</c>, so a consumer of the log sees the same shape with a
    /// different value. The query string is not in it (<c>IncludeQueryInRequestPath</c> is off, and the redaction
    /// cuts one anyway); no address is, since none is a property of this event.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseSerilogRequestLoggingWithHealthFilter(this IApplicationBuilder app)
    {
        return app.UseSerilogRequestLogging(options =>
        {
            options.IncludeQueryInRequestPath = false;

            options.GetMessageTemplateProperties = RequestMessageTemplateProperties;

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
