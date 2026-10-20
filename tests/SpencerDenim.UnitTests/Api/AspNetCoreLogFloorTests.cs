using Serilog;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Api.Logging;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// Issue <b>#201</b>: below Warning, ASP.NET Core's own categories print the request target (Hosting.Diagnostics'
/// "Request starting HTTP/1.1 GET https://host/api/orders/{token}?…", the routing matcher's "candidate(s) found for
/// the request path", Kestrel's bad-request lines). <c>appsettings.json</c> keeps them off with a level; a level is
/// configuration, so this is the floor in code that an environment variable cannot lower.
/// </summary>
public sealed class AspNetCoreLogFloorTests
{
    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Information, true)]
    [InlineData("Microsoft.AspNetCore.Routing.Matching.DfaMatcher", LogEventLevel.Debug, true)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogEventLevel.Information, true)]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogEventLevel.Verbose, true)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning, false)]
    [InlineData("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogEventLevel.Error, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogEventLevel.Information, false)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Information, false)]
    [InlineData("Serilog.AspNetCore.RequestLoggingMiddleware", LogEventLevel.Information, false)]
    [InlineData("SpencerDenim.Api.Http.NewsletterSignupThrottle", LogEventLevel.Debug, false)]
    [InlineData("Microsoft.AspNetCoreX", LogEventLevel.Information, false)]
    public void Only_events_below_warning_from_aspnetcore_categories_are_dropped(string category, LogEventLevel level, bool dropped)
    {
        var logged = Write(category, level);

        Assert.Equal(dropped, SerilogSetup.IsAspNetCoreBelowWarning(logged));
    }

    [Fact]
    public void An_event_with_no_category_is_kept()
    {
        var logged = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, MessageTemplate.Empty, []);

        Assert.False(SerilogSetup.IsAspNetCoreBelowWarning(logged));
    }

    private static LogEvent Write(string category, LogEventLevel level)
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.ForContext(Constants.SourceContextPropertyName, category).Write(level, "x");

        return Assert.Single(sink.Events);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
