namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.NumberSeries</c> (doc 08 §3.5): one row per allocator, e.g. <c>ORDER-2026</c>,
/// <c>INVOICE-2026</c>.
/// <para>
/// <b>Why not a SQL <c>SEQUENCE</c>.</b> Doc 08 §3.5 gives the reason: a sequence cannot be reset per
/// year safely, and it never rolls back — which is exactly wrong for an invoice number, where doc 08
/// requires the series to be <i>gapless</i>. The row is instead bumped with
/// <c>UPDATE … SET NextValue = NextValue + 1 OUTPUT deleted.NextValue</c>, inside the Paid transaction
/// for invoices (gapless, because a rollback gives the number back) and in a short separate transaction
/// for order numbers (gaps allowed, so checkouts never serialise on the row).
/// </para>
/// <para>
/// <b>Nothing in this task allocates a number.</b> The allocator belongs with placement (step 3 and
/// CHK-01); this is the table it will update.
/// </para>
/// </summary>
public sealed class NumberSeries
{
    /// <summary>
    /// <c>varchar(30)</c> PK, e.g. <c>ORDER-2026</c>. The year is the <b>Colombo</b> year (doc 08 §3.5
    /// and §1.4: Sri Lanka is a fixed UTC+05:30, so the conversion is <c>DATEADD(minute, 330, x)</c>) —
    /// a UTC year would roll the series over five and a half hours early.
    /// </summary>
    public string SeriesKey { get; set; } = string.Empty;

    /// <summary>The literal that starts a formatted number: <c>SD</c>, <c>INV</c>.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>The number the next allocation returns. Bumped by a conditional UPDATE, never read-then-written.</summary>
    public int NextValue { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
