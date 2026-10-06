using System.Globalization;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// The two number series of doc 08 §3.5 and the shape of the strings they produce:
/// <c>SD-2026-000123</c> for an order and <c>INV-2026-000045</c> for an invoice.
/// <para>
/// <b>Why formatting lives in the Domain and not next to the SQL.</b> The series key, the prefix and the
/// rendered string are one decision in three parts — <c>ORDER-2026</c> allocates the counter that becomes
/// <c>SD-2026-000123</c> — and splitting them across an allocator, a use case and a view model is how a
/// shop ends up with an order number that does not match the series it came from. Keeping them together
/// also lets the format be tested without a database.
/// </para>
/// </summary>
public static class DocumentNumber
{
    /// <summary>Doc 08 §3.5: <c>varchar(20)</c> on both <c>OrderNumber</c> and <c>InvoiceNumber</c>.</summary>
    public const int MaxLength = 20;

    /// <summary>
    /// Six digits, which is what doc 08's examples show. It is a minimum width, not a cap: a seventh
    /// order in a year beyond 999,999 widens the string rather than wrapping or truncating, and
    /// <c>SD-2026-1000000</c> is still 15 characters, well inside <see cref="MaxLength"/>.
    /// </summary>
    public const int SequenceDigits = 6;

    private const string OrderSeriesPrefix = "ORDER";
    private const string InvoiceSeriesPrefix = "INVOICE";

    /// <summary>The order series' prefix, <c>SD</c> — Spencer Denim, the client company (doc 02 §I2).</summary>
    public const string OrderNumberPrefix = "SD";

    /// <summary>The invoice series' prefix, <c>INV</c>.</summary>
    public const string InvoiceNumberPrefix = "INV";

    /// <summary>
    /// <c>ORDER-2026</c>: the <c>sales.NumberSeries</c> primary key for orders placed in a Colombo year.
    /// </summary>
    public static string OrderSeriesKey(int colomboYear) => SeriesKey(OrderSeriesPrefix, colomboYear);

    /// <summary>
    /// <c>INVOICE-2026</c>. Nothing in this task allocates from it — invoice numbers are issued inside the
    /// Paid transaction (doc 08 §3.5, §4.1), which is W3-03's step 3 — but the key belongs with its
    /// sibling so that the two can never drift apart in shape.
    /// </summary>
    public static string InvoiceSeriesKey(int colomboYear) => SeriesKey(InvoiceSeriesPrefix, colomboYear);

    /// <summary>The series key for the Colombo year of <paramref name="placedAt"/>.</summary>
    public static string OrderSeriesKeyFor(DateTimeOffset placedAt) =>
        OrderSeriesKey(ColomboTime.YearOf(placedAt));

    /// <summary>
    /// <c>SD-2026-000123</c> from the prefix, the year and the value the series handed out.
    /// </summary>
    public static string Format(string prefix, int colomboYear, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        var number = string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}-{colomboYear:D4}-{sequence.ToString(CultureInfo.InvariantCulture).PadLeft(SequenceDigits, '0')}");

        // A loud failure rather than a silently truncated number: the column is varchar(20) with a unique
        // index, and a truncated number would collide with a real one rather than simply look wrong.
        return number.Length <= MaxLength
            ? number
            : throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                $"'{number}' is longer than the {MaxLength} characters doc 08 §3.5 allows for a document number.");
    }

    private static string SeriesKey(string seriesPrefix, int colomboYear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(colomboYear, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{seriesPrefix}-{colomboYear:D4}");
    }
}
