using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// A SQL Server error, described without the data it can quote (#207 review).
/// <para>
/// <b>Why a database error's text is not safe to write down.</b> Several of SQL Server's messages quote the row
/// that failed: a duplicate key (2601, 2627) ends <c>The duplicate key value is (…)</c>, a truncation (2628)
/// ends <c>Truncated value: '…'</c>, a failed conversion (245) names the value it could not convert, and a
/// <c>THROW</c>/<c>RAISERROR</c> (50000) says whatever its author put in it. On this shop those values are a
/// checkout key (which <c>POST /api/checkout/orders</c> answers with the whole order), a shopper's e-mail
/// address, a phone number. A log line or a stored <c>LastError</c> that copies the message copies them.
/// </para>
/// <para>
/// <b>What is kept.</b> Every error's number, class, state, procedure and line, which is what an operator
/// searches for. For a duplicate key, the index or constraint name and the table, read from the part of the
/// message <i>before</i> the value. For a short list of errors whose message names only schema objects
/// (<see cref="SchemaOnlyErrors"/>), the message. Everything else: the message is withheld. An allow-list, so
/// an error nobody thought about is withheld by default.
/// </para>
/// <para>
/// <see cref="Scrub"/> is the net for text that already left the exception (a property somebody formatted, an
/// inner message a wrapper copied): it cuts the value out of the three message shapes known to quote one.
/// </para>
/// </summary>
public static class SqlErrorRedaction
{
    /// <summary>Unique index (2601) and unique / primary key constraint (2627) violations.</summary>
    private const int DuplicateKeyRow = 2601;

    private const int DuplicateKeyConstraint = 2627;

    /// <summary>What replaces a withheld value inside a message.</summary>
    internal const string Withheld = "(withheld)";

    /// <summary>
    /// Errors whose message names schema objects, settings or the connection, and never a row's value, so it
    /// is kept: timeouts and network failures, invalid object or column, NULL into a NOT NULL column (names the
    /// column, not the value), a foreign-key or check conflict (names the constraint, table and column), a
    /// deadlock or lock timeout, a snapshot update conflict, "the statement has been terminated", an object
    /// that already exists, a database that cannot be opened.
    /// </summary>
    private static readonly HashSet<int> SchemaOnlyErrors =
    [
        -2, 53, 207, 208, 233, 515, 547, 1205, 1222, 2714, 3621, 3960, 4060, 10053, 10054, 10060, 40613,
    ];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The index name in a 2601 message: "… with unique index 'UX_…'. The duplicate key value is (…)."</summary>
    private static readonly Regex UniqueIndexName = new(
        @"with unique index '(?<name>[^'\r\n]{1,256})'", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The constraint name in a 2627 message: "Violation of UNIQUE KEY constraint 'UQ_…'. …".</summary>
    private static readonly Regex ConstraintName = new(
        @"constraint '(?<name>[^'\r\n]{1,256})'", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The table in either duplicate-key message: "… in object 'sales.Orders' …".</summary>
    private static readonly Regex ObjectName = new(
        @"in object '(?<name>[^'\r\n]{1,256})'", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// The message shapes that quote a value, each cut from the value to the end of its line (a value can hold
    /// a parenthesis or a quote, so nothing narrower is safe).
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] ValueQuotingShapes =
    [
        (new Regex(@"The duplicate key value is \([^\r\n]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout),
            "The duplicate key value is " + Withheld + "."),
        (new Regex(@"Truncated value: '[^\r\n]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout),
            "Truncated value: " + Withheld + "."),
        (new Regex(@"Conversion failed when converting (?<what>[^\r\n']{0,64}?)value '[^\r\n]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout),
            "Conversion failed when converting ${what}value " + Withheld + "."),
    ];

    /// <summary>Every <see cref="SqlException"/> in the exception, its inner exceptions and any aggregate's.</summary>
    public static IReadOnlyList<SqlException> FindSqlExceptions(Exception? exception)
    {
        var found = new List<SqlException>();
        Collect(exception, found, depth: 0);
        return found;
    }

    /// <summary>
    /// Whether anything in the exception chain is a database error or carries a message shape that quotes a
    /// value, i.e. whether its text must not be written as it is.
    /// </summary>
    public static bool NeedsRedaction(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (FindSqlExceptions(exception).Count > 0)
        {
            return true;
        }

        foreach (var current in Chain(exception))
        {
            if (Scrub(current.Message) != current.Message)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One SQL Server exception as one line: each error's number, class, state and procedure, the index for a
    /// duplicate key, the message only for <see cref="SchemaOnlyErrors"/>.
    /// </summary>
    public static string Describe(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var errors = exception.Errors.Cast<SqlError>().ToList();

        if (errors.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"SQL Server error {exception.Number}: message withheld.");
        }

        return string.Join("; ", errors.Select(Describe));
    }

    /// <summary>One <see cref="SqlError"/>, as <see cref="Describe(SqlException)"/> describes each.</summary>
    public static string Describe(SqlError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return Describe(error.Number, error.Class, error.State, error.Procedure, error.LineNumber, error.Message);
    }

    /// <summary>The description of one error from its parts (the parts a <see cref="SqlError"/> has).</summary>
    internal static string Describe(int number, byte errorClass, byte state, string? procedure, int lineNumber, string? message)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"SQL Server error {number} (class {errorClass}, state {state}");

        if (!string.IsNullOrEmpty(procedure))
        {
            text.Append(CultureInfo.InvariantCulture, $", procedure {procedure}");
        }

        if (lineNumber > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", line {lineNumber}");
        }

        text.Append(')');
        message ??= string.Empty;

        if (number is DuplicateKeyRow or DuplicateKeyConstraint)
        {
            // The names come before the value in both messages, so only that part is searched.
            var cut = message.IndexOf("The duplicate key value", StringComparison.OrdinalIgnoreCase);
            var head = cut >= 0 ? message[..cut] : message;
            var name = Match(number == DuplicateKeyRow ? UniqueIndexName : ConstraintName, head);
            var table = Match(ObjectName, head);

            text.Append(": duplicate key");

            if (name is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" in {(number == DuplicateKeyRow ? "unique index" : "constraint")} '{name}'");
            }

            if (table is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" on '{table}'");
            }

            text.Append(" (the value is withheld).");
        }
        else if (SchemaOnlyErrors.Contains(number) && Scrub(message) == message)
        {
            text.Append(": ").Append(message);
        }
        else
        {
            text.Append(": message withheld (it can quote the data).");
        }

        return text.ToString();
    }

    /// <summary>
    /// An exception's message, safe to store or write: a <see cref="SqlException"/>'s is
    /// <see cref="Describe(SqlException)"/>; any other's is its own with every database error's text (of the
    /// chain it belongs to) and every value-quoting shape cut out. For <c>LastError</c> columns and health
    /// reports, which keep a message where the log keeps the exception.
    /// </summary>
    public static string SafeMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return SafeMessage(exception, Replacements(exception));
    }

    /// <summary>
    /// The texts of every database error in the chain and what each is replaced with, longest first so that an
    /// exception's whole message is replaced before an error that is part of it.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Replacements(Exception? exception)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sql in FindSqlExceptions(exception))
        {
            var description = Describe(sql);

            foreach (var error in sql.Errors.Cast<SqlError>())
            {
                if (!string.IsNullOrEmpty(error.Message))
                {
                    replacements.TryAdd(error.Message, Describe(error));
                }
            }

            if (!string.IsNullOrEmpty(sql.Message))
            {
                replacements.TryAdd(sql.Message, description);
            }
        }

        return replacements
            .OrderByDescending(pair => pair.Key.Length)
            .ToList();
    }

    /// <summary>
    /// The text with each database error's message replaced by its description, then every value-quoting shape
    /// cut. Ordinal: the messages are copied, not reworded.
    /// </summary>
    public static string Scrub(string? text, IReadOnlyList<KeyValuePair<string, string>> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);

        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        foreach (var (secret, replacement) in replacements)
        {
            text = text.Replace(secret, replacement, StringComparison.Ordinal);
        }

        return Scrub(text);
    }

    /// <summary>The text with every value-quoting message shape cut from the value to the end of its line.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        foreach (var (pattern, replacement) in ValueQuotingShapes)
        {
            try
            {
                text = pattern.Replace(text, replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological input: withholding all of it is the safe answer.
                return Withheld;
            }
        }

        return text;
    }

    /// <summary><see cref="SafeMessage(Exception)"/> with the chain's <see cref="Replacements"/> already worked out.</summary>
    public static string SafeMessage(Exception exception, IReadOnlyList<KeyValuePair<string, string>> replacements) =>
        exception is SqlException sql ? Describe(sql) : Scrub(exception.Message, replacements);

    /// <summary>The exception and its inner exceptions, outermost first (an aggregate's first inner only).</summary>
    public static IEnumerable<Exception> Chain(Exception exception)
    {
        var depth = 0;

        for (var current = exception; current is not null && depth <= 32; current = current.InnerException, depth++)
        {
            yield return current;
        }
    }

    private static void Collect(Exception? exception, List<SqlException> found, int depth)
    {
        // The depth bound keeps a cyclic or absurd chain from looping; real chains are three deep.
        if (exception is null || depth > 32)
        {
            return;
        }

        if (exception is SqlException sql)
        {
            found.Add(sql);
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                Collect(inner, found, depth + 1);
            }

            return;
        }

        Collect(exception.InnerException, found, depth + 1);
    }

    private static string? Match(Regex pattern, string text)
    {
        try
        {
            var match = pattern.Match(text);
            return match.Success ? match.Groups["name"].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
