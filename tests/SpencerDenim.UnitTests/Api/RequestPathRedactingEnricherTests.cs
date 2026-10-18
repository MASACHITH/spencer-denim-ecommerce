using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Api.Logging;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// Issue <b>#201</b>: ASP.NET Core's hosting scope puts the raw <c>RequestPath</c> on every event written while a
/// request runs, so the enricher has to redact the property wherever it is, not only on the request-completion
/// line.
/// </summary>
public sealed class RequestPathRedactingEnricherTests
{
    private const string OrderToken = "7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90";

    [Fact]
    public void A_request_path_property_is_redacted_on_any_event()
    {
        var logged = Write(logger => logger.ForContext("RequestPath", $"/api/orders/{OrderToken}").Warning("EF Core said something"));

        Assert.Equal("/api/orders/*", Path(logged));
    }

    [Fact]
    public void The_redaction_reaches_the_rendered_message_of_the_request_completion_event()
    {
        var logged = Write(logger => logger.Information(
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode}", "GET", $"/api/orders/{OrderToken}", 200));

        Assert.Equal("HTTP \"GET\" \"/api/orders/*\" responded 200", logged.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_path_object_is_redacted_the_way_its_text_would_be()
    {
        var logged = Write(logger => logger.ForContext("RequestPath", new PathString($"/order/{OrderToken}"), destructureObjects: false).Information("x"));

        Assert.Equal("/order/*", Path(logged));
    }

    [Fact]
    public void A_structured_value_under_that_name_is_replaced_not_trusted()
    {
        var logged = Write(logger => logger.ForContext("RequestPath", new { Raw = $"/api/orders/{OrderToken}" }, destructureObjects: true).Information("x"));

        Assert.Equal("*", Path(logged));
        Assert.DoesNotContain(OrderToken, logged.Properties["RequestPath"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_without_the_property_are_left_alone()
    {
        var logged = Write(logger => logger.ForContext("OrderNumber", "CW-1001").Information("x"));

        Assert.False(logged.Properties.ContainsKey("RequestPath"));
        Assert.Equal("CW-1001", ((ScalarValue)logged.Properties["OrderNumber"]).Value);
    }

    [Fact]
    public void It_is_idempotent()
    {
        var logged = Write(logger => logger.ForContext("RequestPath", "/api/admin/orders/bank-transfers/*").Information("x"));

        Assert.Equal("/api/admin/orders/bank-transfers/*", Path(logged));
    }

    /// <summary>
    /// The request-completion line is safe on its own, without the enricher: same four property names as Serilog's
    /// default template (what any consumer of the log keys on), the path redacted, nothing else added.
    /// </summary>
    [Fact]
    public void The_request_line_keeps_serilogs_property_names_and_redacts_only_the_path()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?token=qs-Zk93TmXp41");

        var properties = SerilogSetup.RequestMessageTemplateProperties(
            context, $"/api/orders/{OrderToken}/payment-slip", 12.5, 404).ToList();

        Assert.Equal(["RequestMethod", "RequestPath", "StatusCode", "Elapsed"], properties.Select(property => property.Name));
        Assert.Equal("GET", ((ScalarValue)properties[0].Value).Value);
        Assert.Equal("/api/orders/*", ((ScalarValue)properties[1].Value).Value);
        Assert.Equal(404, ((ScalarValue)properties[2].Value).Value);
        Assert.Equal(12.5, ((ScalarValue)properties[3].Value).Value);
        Assert.DoesNotContain("qs-Zk93TmXp41", string.Join(' ', properties.Select(property => property.Value.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public void The_request_line_never_carries_the_query_even_if_the_path_handed_over_has_one()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";

        var properties = SerilogSetup.RequestMessageTemplateProperties(
            context, "/account/reset-password?token=CfDJ8Abc", 1, 200).ToList();

        Assert.Equal("/account/reset-password", ((ScalarValue)properties[1].Value).Value);
    }

    private static string? Path(LogEvent logged) => ((ScalarValue)logged.Properties["RequestPath"]).Value as string;

    private static LogEvent Write(Action<ILogger> act)
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new RequestPathRedactingEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        act(logger);

        return Assert.Single(sink.Events);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
