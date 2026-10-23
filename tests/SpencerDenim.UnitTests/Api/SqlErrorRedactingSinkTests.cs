using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Api.Logging;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// #207 review: the sink every configured sink sits behind takes a database error's quoted value out of the
/// exception, the message and every property. (The real <c>SqlException</c> path, through EF Core and the
/// exception handler, is <c>DatabaseErrorLogRedactionTests</c>; a <c>SqlException</c> cannot be built here.)
/// </summary>
public sealed class SqlErrorRedactingSinkTests
{
    private const string Key = "7D1E5C0A-4B3F-4A8E-9C2D-1F6B8A3E5D90";

    private static readonly string Quoted =
        $"Cannot insert duplicate key row in object 'sales.Orders' with unique index 'UX_A'. The duplicate key value is ({Key}).";

    [Fact]
    public void The_exception_text_is_cut_and_its_type_and_stack_are_kept()
    {
        Exception thrown;

        try
        {
            throw new InvalidOperationException("Saving failed", new InvalidOperationException(Quoted));
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        var logged = Write(logger => logger.Error(thrown, "An unhandled exception has occurred while executing the request."));
        var text = logged.Exception!.ToString();

        Assert.DoesNotContain(Key, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.InvalidOperationException: Saving failed ---> System.InvalidOperationException: Cannot insert duplicate key row", text, StringComparison.Ordinal);
        Assert.Contains(nameof(The_exception_text_is_cut_and_its_type_and_stack_are_kept), text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_holding_the_text_is_cut_and_so_is_the_rendered_message()
    {
        var logged = Write(logger => logger.Error("Saving changes failed.{NewLine}{Error}", Environment.NewLine, "Microsoft.Data.SqlClient.SqlException (0x80131904): " + Quoted));

        Assert.DoesNotContain(Key, logged.RenderMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UX_A", logged.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_passed_as_a_property_is_cut()
    {
        var logged = Write(logger => logger.Warning("Retrying: {Error}", new InvalidOperationException(Quoted)));

        Assert.DoesNotContain(Key, logged.RenderMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nested_values_are_cut()
    {
        var logged = Write(logger => logger.Information("{@Detail} {Items}", new { Reason = Quoted }, new[] { "ok", Quoted }));

        Assert.DoesNotContain(Key, logged.RenderMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_message_template_built_from_the_text_is_cut()
    {
#pragma warning disable CA2254 // The point: a template somebody built by concatenation.
        var logged = Write(logger => logger.Error("Failed: " + Quoted));
#pragma warning restore CA2254

        Assert.DoesNotContain(Key, logged.RenderMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_event_with_nothing_to_cut_is_passed_on_as_it_is()
    {
        var original = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            new InvalidOperationException("The bag is empty."),
            new Serilog.Parsing.MessageTemplateParser().Parse("Order {Number} failed"),
            [new LogEventProperty("Number", new ScalarValue("CW-1001"))]);

        Assert.Same(original, SqlErrorRedactingSink.Redact(original));
    }

    private static LogEvent Write(Action<ILogger> act)
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new SqlErrorRedactingSink(sink))
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
