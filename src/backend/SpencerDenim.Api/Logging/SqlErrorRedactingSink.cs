using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Api.Logging;

/// <summary>
/// Takes a database error's text out of every log event before any sink writes it (#207 review).
/// <para>
/// <b>What leaked.</b> SQL Server's duplicate-key message ends <c>The duplicate key value is (…)</c>. A checkout
/// double-submit that reached <c>UX_Orders_CheckoutIdempotencyKey</c> therefore wrote the checkout key (which
/// <c>POST /api/checkout/orders</c> answers with the whole order: link, e-mail, phone, address) to the log Docker
/// keeps, and a registration race wrote the shopper's e-mail address, both through EF Core's
/// <c>SaveChangesFailed</c> / <c>CommandError</c> events at Error. Any unhandled database error did the same
/// through <c>UseExceptionHandler</c>'s "An unhandled exception has occurred" line.
/// </para>
/// <para>
/// <b>Two layers.</b> <c>AppDbContext.DatabaseErrorEvents</c> moves EF Core's two events to Debug, which
/// production does not write. This is the net under it, for every other way a <see cref="Microsoft.Data.SqlClient.SqlException"/>
/// reaches a log: the exception handler, a background service's catch, a level a deployment lowered. For an
/// event whose exception chain holds a database error (or a message shape that quotes a value) it writes, in
/// place of the exception, one whose text is <see cref="SqlErrorRedaction.Describe(Microsoft.Data.SqlClient.SqlException)"/>
/// (the number, class, state, procedure and, for a duplicate key, the index) and whose stack trace is the
/// original's; and it replaces the same texts wherever they were copied into the message or a property (EF
/// Core puts <c>exception.ToString()</c> in its message). The value-quoting shapes are cut from every event,
/// database error or not.
/// </para>
/// <para>
/// <b>Why a sink and not an enricher.</b> A Serilog <see cref="LogEvent"/>'s exception cannot be replaced, only
/// a new event built, and only a sink can pass a new event on. <c>SerilogSetup</c> wraps every sink this
/// application configures (the console, and any registered in the container) in this one, so nothing is
/// written that did not pass through it. The path redaction (<see cref="RequestPathRedactingEnricher"/>) has
/// already run by the time an event gets here.
/// </para>
/// </summary>
internal sealed class SqlErrorRedactingSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        inner.Emit(Redact(logEvent));
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();

    /// <summary>The event as it may be written: itself when nothing in it needs cutting.</summary>
    internal static LogEvent Redact(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var replacements = SqlErrorRedaction.Replacements(logEvent.Exception);
        var changed = false;

        var exception = logEvent.Exception;

        if (SqlErrorRedaction.NeedsRedaction(exception))
        {
            exception = new RedactedDatabaseException(exception!, replacements);
            changed = true;
        }

        var template = logEvent.MessageTemplate;
        var templateText = SqlErrorRedaction.Scrub(template.Text, replacements);

        if (!string.Equals(templateText, template.Text, StringComparison.Ordinal))
        {
            template = new MessageTemplateParser().Parse(templateText);
            changed = true;
        }

        var properties = new List<LogEventProperty>(logEvent.Properties.Count);

        foreach (var (name, value) in logEvent.Properties)
        {
            var scrubbed = Scrub(value, replacements);
            changed |= !ReferenceEquals(scrubbed, value);
            properties.Add(new LogEventProperty(name, scrubbed));
        }

        return changed
            ? new LogEvent(logEvent.Timestamp, logEvent.Level, exception, template, properties, logEvent.TraceId ?? default, logEvent.SpanId ?? default)
            : logEvent;
    }

    /// <summary>The value with every database error's text cut, or the same instance when there was none.</summary>
    private static LogEventPropertyValue Scrub(LogEventPropertyValue value, IReadOnlyList<KeyValuePair<string, string>> replacements)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
            {
                var scrubbed = SqlErrorRedaction.Scrub(text, replacements);
                return string.Equals(scrubbed, text, StringComparison.Ordinal) ? value : new ScalarValue(scrubbed);
            }

            case ScalarValue { Value: Exception carried }:
                // An exception passed as a template argument (EF Core's own messages do this) is printed with
                // ToString(), stack and inner messages included: the same treatment as the event's exception.
                return SqlErrorRedaction.NeedsRedaction(carried)
                    ? new ScalarValue(new RedactedDatabaseException(carried, SqlErrorRedaction.Replacements(carried)).ToString())
                    : value;

            case ScalarValue { Value: { } other } when other is not ValueType:
            {
                // Anything else that prints through ToString() could carry a copied message.
                var text = other.ToString() ?? string.Empty;
                var scrubbed = SqlErrorRedaction.Scrub(text, replacements);
                return string.Equals(scrubbed, text, StringComparison.Ordinal) ? value : new ScalarValue(scrubbed);
            }

            case SequenceValue sequence:
            {
                var elements = sequence.Elements.Select(element => Scrub(element, replacements)).ToList();
                return elements.Where((element, index) => !ReferenceEquals(element, sequence.Elements[index])).Any()
                    ? new SequenceValue(elements)
                    : value;
            }

            case StructureValue structure:
            {
                var members = structure.Properties
                    .Select(property => new LogEventProperty(property.Name, Scrub(property.Value, replacements)))
                    .ToList();
                return members.Where((member, index) => !ReferenceEquals(member.Value, structure.Properties[index].Value)).Any()
                    ? new StructureValue(members, structure.TypeTag)
                    : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);

                foreach (var (key, element) in dictionary.Elements)
                {
                    var scrubbedKey = (ScalarValue)Scrub(key, replacements);
                    var scrubbedElement = Scrub(element, replacements);
                    changed |= !ReferenceEquals(scrubbedKey, key) || !ReferenceEquals(scrubbedElement, element);
                    entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(scrubbedKey, scrubbedElement));
                }

                return changed ? new DictionaryValue(entries) : value;
            }

            default:
                return value;
        }
    }
}

/// <summary>
/// What a log writes in place of an exception that carries a database error: the same chain of types, each
/// message made safe by <see cref="SqlErrorRedaction"/>, and the original stack traces (frames hold no data).
/// <see cref="ToString"/> is what Serilog's formatters print, so it is shaped like <see cref="Exception.ToString"/>.
/// </summary>
internal sealed class RedactedDatabaseException : Exception
{
    private readonly string _text;
    private readonly string? _stackTrace;

    public RedactedDatabaseException()
        : this("A database error; its text is withheld.")
    {
    }

    public RedactedDatabaseException(string message)
        : base(message)
    {
        _text = message;
    }

    public RedactedDatabaseException(string message, Exception innerException)
        : base(message, innerException)
    {
        _text = message;
    }

    internal RedactedDatabaseException(Exception original, IReadOnlyList<KeyValuePair<string, string>> replacements)
        : base(SqlErrorRedaction.SafeMessage(original, replacements))
    {
        _stackTrace = original.StackTrace;
        _text = Render(original, replacements);
    }

    public override string? StackTrace => _stackTrace ?? base.StackTrace;

    public override string ToString() => _text;

    private static string Render(Exception original, IReadOnlyList<KeyValuePair<string, string>> replacements)
    {
        var chain = SqlErrorRedaction.Chain(original).ToList();
        var text = new StringBuilder();

        for (var index = 0; index < chain.Count; index++)
        {
            var current = chain[index];

            if (index > 0)
            {
                text.Append(" ---> ");
            }

            text.Append(current.GetType().FullName).Append(": ").Append(SqlErrorRedaction.SafeMessage(current, replacements));
        }

        // Innermost stack first, as Exception.ToString prints them.
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            if (chain[index].StackTrace is { Length: > 0 } stack)
            {
                text.AppendLine().Append(stack);
            }

            if (index > 0)
            {
                text.AppendLine().Append("   --- End of inner exception stack trace ---");
            }
        }

        return text.ToString();
    }
}
