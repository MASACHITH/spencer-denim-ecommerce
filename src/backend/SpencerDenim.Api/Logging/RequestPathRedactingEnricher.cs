using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Application.Privacy;

namespace SpencerDenim.Api.Logging;

/// <summary>
/// Redacts the <c>RequestPath</c> property wherever it appears on a log event (issue #201).
/// <para>
/// <b>Why the request-logging middleware's own property is not enough.</b> ASP.NET Core's hosting layer opens a
/// logging <i>scope</i> for every request that carries <c>RequestId</c>, <c>ConnectionId</c> and the raw
/// <c>RequestPath</c>, and Serilog's <c>ILogger</c> bridge copies a scope's properties onto <b>every event written
/// while the request runs</b>: an EF Core warning, the exception handler's "unhandled exception" error, a throttle
/// warning. The JSON console formatter, which is what a deployment prints, writes all of them. Each of those events
/// therefore carried <c>/api/orders/{token}</c> as a property even after the request-completion line was fixed.
/// </para>
/// <para>
/// This runs for every event, before any sink, so it is the one place that makes "no event carries a raw request
/// path" true rather than true of the events somebody remembered. It is idempotent: it keeps the redaction of the
/// request-completion event, which <c>UseSerilogRequestLoggingWithHealthFilter</c> already produced. The rule is
/// <see cref="RequestPathRedaction"/>, the same one the gateway's access log and the storefront's failure log
/// follow.
/// </para>
/// </summary>
internal sealed class RequestPathRedactingEnricher : ILogEventEnricher
{
    /// <summary>The property ASP.NET Core's hosting scope and Serilog's request logging both call it.</summary>
    internal const string PropertyName = "RequestPath";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (!logEvent.Properties.TryGetValue(PropertyName, out var value))
        {
            return;
        }

        // A scalar is the hosting scope's string (or a PathString, which prints as the path). Anything
        // structured under this name is not something this application writes, so it is not trusted either.
        var redacted = value is ScalarValue scalar
            ? RequestPathRedaction.ForLog(scalar.Value?.ToString())
            : RequestPathRedaction.Redacted;

        logEvent.AddOrUpdateProperty(new LogEventProperty(PropertyName, new ScalarValue(redacted)));
    }
}
