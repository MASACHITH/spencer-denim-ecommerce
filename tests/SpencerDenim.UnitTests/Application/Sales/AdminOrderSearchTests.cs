using SpencerDenim.Application.Sales.Orders;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// How the order list's one search box decides what the staff member typed (ADM-09, doc 11 §6.6).
/// <para>
/// <b>Why this is a unit test and not left to the reader.</b> The classification decides which column is
/// matched and therefore which index can be used, and a reader that disagreed with it would be a search
/// that quietly stopped finding orders. Issue #126 is the other half of the argument: Playwright never
/// starts the .NET API, so a browser test cannot see this at all.
/// </para>
/// </summary>
public sealed class AdminOrderSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Nothing_to_search_is_no_term(string? raw)
    {
        Assert.Null(AdminOrderSearch.Classify(raw));
    }

    /// <summary>
    /// A whole order number, which is what a customer reads off their confirmation.
    /// </summary>
    [Theory]
    [InlineData("SD-2026-000123")]
    [InlineData("  SD-2026-000123  ")]
    [InlineData("SD-2026-0001")]
    [InlineData("INV-2026-000045")]
    public void A_document_number_is_matched_as_a_number(string raw)
    {
        var term = AdminOrderSearch.Classify(raw);

        Assert.NotNull(term);
        Assert.Equal(AdminOrderSearchKind.NumberOrName, term.Kind);
        Assert.Equal(raw.Trim(), term.Value);

        // No sequence suffix: the term has letters in it, so it is a prefix of a whole number and the
        // reader can seek UX_Orders_OrderNumber. Padding it would be nonsense.
        Assert.Null(term.SequenceSuffix);
    }

    /// <summary>
    /// <b>The case this screen exists for.</b> A staff member reading an order number off a screen says
    /// the part that changes — "one two three" — and the number is <c>SD-2026-000123</c>.
    /// </summary>
    [Theory]
    [InlineData("123", "000123")]
    [InlineData("1", "000001")]
    [InlineData("45", "000045")]
    [InlineData("000123", "000123")]
    [InlineData("999999", "999999")]
    public void Digits_alone_are_padded_to_the_series_width(string raw, string expectedSuffix)
    {
        var term = AdminOrderSearch.Classify(raw);

        Assert.NotNull(term);
        Assert.Equal(AdminOrderSearchKind.NumberOrName, term.Kind);
        Assert.Equal(expectedSuffix, term.SequenceSuffix);
    }

    /// <summary>
    /// Seven digits or more is a telephone number, not a sequence number — and the boundary is not
    /// arbitrary.
    /// </summary>
    /// <remarks>
    /// An order number's sequence part is six digits wide, and Sri Lankan telephone numbers are nine or
    /// ten. Seven is the first length at which the two cannot be confused, which is why it is the
    /// threshold rather than a round number.
    /// </remarks>
    [Theory]
    [InlineData("0771234567", "0771234567")]
    [InlineData("+94 77 123 4567", "94771234567")]
    [InlineData("077-123-4567", "0771234567")]
    [InlineData("1234567", "1234567")]
    public void Seven_digits_or_more_is_a_telephone_number(string raw, string expectedDigits)
    {
        var term = AdminOrderSearch.Classify(raw);

        Assert.NotNull(term);
        Assert.Equal(AdminOrderSearchKind.Phone, term.Kind);
        Assert.Equal(expectedDigits, term.Value);
        Assert.Null(term.SequenceSuffix);
    }

    /// <summary>
    /// Six digits is still an order number, which is the other side of the same boundary.
    /// </summary>
    /// <remarks>
    /// <b>Asserted explicitly, because this is the pair of cases a mutation would swap.</b> Changing the
    /// threshold from 7 to 6 makes <c>000123</c> a telephone number and the order unfindable, and nothing
    /// else in the suite would notice.
    /// </remarks>
    [Fact]
    public void Six_digits_is_an_order_number_and_seven_is_a_telephone_number()
    {
        Assert.Equal(AdminOrderSearchKind.NumberOrName, AdminOrderSearch.Classify("123456")!.Kind);
        Assert.Equal(AdminOrderSearchKind.Phone, AdminOrderSearch.Classify("1234567")!.Kind);
    }

    [Theory]
    [InlineData("someone@example.com", "SOMEONE@EXAMPLE.COM")]
    [InlineData("  Someone@Example.com ", "SOMEONE@EXAMPLE.COM")]
    [InlineData("someone@", "SOMEONE@")]
    public void An_address_is_matched_against_the_normalised_column(string raw, string expected)
    {
        var term = AdminOrderSearch.Classify(raw);

        Assert.NotNull(term);
        Assert.Equal(AdminOrderSearchKind.Email, term.Kind);

        // Upper-cased, because `Orders.EmailNormalized` is — doc 08 §3.5 keeps that column precisely so
        // the database collation is not what decides whether two addresses are the same.
        Assert.Equal(expected, term.Value);
    }

    [Theory]
    [InlineData("Perera")]
    [InlineData("nimal perera")]
    public void A_name_falls_through_to_the_number_or_name_predicate(string raw)
    {
        var term = AdminOrderSearch.Classify(raw);

        Assert.NotNull(term);
        Assert.Equal(AdminOrderSearchKind.NumberOrName, term.Kind);
        Assert.Null(term.SequenceSuffix);
    }

    /// <summary>
    /// A paste of half a bank statement is truncated, not refused.
    /// </summary>
    /// <remarks>
    /// The box is a search, not a form field: a 400 for a long value would be an error page where a
    /// staff member expected "nothing found".
    /// </remarks>
    [Fact]
    public void An_over_long_term_is_truncated_rather_than_refused()
    {
        var term = AdminOrderSearch.Classify(new string('x', AdminOrderSearch.MaxLength + 50));

        Assert.NotNull(term);
        Assert.Equal(AdminOrderSearch.MaxLength, term.Value.Length);
    }

    /// <summary>
    /// <b><c>LIKE</c>'s own wildcards are escaped, and this is a correctness and a performance matter at
    /// once.</b>
    /// </summary>
    /// <remarks>
    /// EF translates <c>EndsWith</c> to <c>LIKE '%' + @p</c> and does <i>not</i> escape the parameter, so
    /// a <c>%</c> typed into the search box would otherwise be a free table scan that also matched rows
    /// the typist did not mean. Backslash is escaped first, because it is the escape character.
    /// </remarks>
    [Theory]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a[b", "a\\[b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("%_[\\", "\\%\\_\\[\\\\")]
    [InlineData("SD-2026-000123", "SD-2026-000123")]
    public void Like_wildcards_are_escaped(string raw, string expected)
    {
        var term = AdminOrderSearch.Classify(raw);

        Assert.NotNull(term);
        Assert.Equal(expected, term.LikeEscaped());
    }

    /// <summary>
    /// The padded suffix is escaped too, although it can only be digits.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because "this value can never contain a wildcard" is exactly the kind of claim that
    /// stops being true without anybody noticing</b> — and a wrong comment is worse than none, because it
    /// stops the next reader checking.
    /// </remarks>
    [Fact]
    public void The_sequence_suffix_is_escaped_and_is_null_when_there_is_none()
    {
        Assert.Equal("000123", AdminOrderSearch.Classify("123")!.SequenceSuffixEscaped());
        Assert.Null(AdminOrderSearch.Classify("Perera")!.SequenceSuffixEscaped());
    }

    /// <summary>
    /// The order-number prefix comes from the Domain's own constant, not a copy of it.
    /// </summary>
    [Fact]
    public void The_order_number_prefix_is_the_domains_own()
    {
        Assert.Equal(SpencerDenim.Domain.Sales.DocumentNumber.OrderNumberPrefix, AdminOrderSearch.OrderNumberPrefix);
    }

    /// <summary>
    /// <c>ToString</c> is for a log line and names the kind as well as the value.
    /// </summary>
    [Fact]
    public void A_term_describes_itself_for_a_log_line()
    {
        Assert.Equal("Email:SOMEONE@EXAMPLE.COM", AdminOrderSearch.Classify("someone@example.com")!.ToString());
    }
}
