using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Sales;

/// <summary>Doc 08 §3.5's number series keys and the strings they produce.</summary>
public sealed class DocumentNumberTests
{
    [Fact]
    public void An_order_number_reads_the_way_doc_08_writes_it()
    {
        Assert.Equal("SD-2026-000123", DocumentNumber.Format(DocumentNumber.OrderNumberPrefix, 2026, 123));
    }

    [Fact]
    public void An_invoice_number_reads_the_way_doc_08_writes_it()
    {
        Assert.Equal("INV-2026-000045", DocumentNumber.Format(DocumentNumber.InvoiceNumberPrefix, 2026, 45));
    }

    [Fact]
    public void The_series_keys_are_the_ones_doc_08_names()
    {
        Assert.Equal("ORDER-2026", DocumentNumber.OrderSeriesKey(2026));
        Assert.Equal("INVOICE-2026", DocumentNumber.InvoiceSeriesKey(2026));
    }

    [Fact]
    public void The_sequence_widens_rather_than_wrapping_past_six_digits()
    {
        // Six digits is a minimum width, not a cap. A shop that places a millionth order in one year must
        // get a longer number, not a number that collides with an earlier one.
        Assert.Equal("SD-2026-1000000", DocumentNumber.Format(DocumentNumber.OrderNumberPrefix, 2026, 1_000_000));
    }

    [Fact]
    public void A_number_too_long_for_the_column_is_refused_rather_than_truncated()
    {
        // varchar(20) with a unique index: a truncated number would not look wrong, it would collide.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentNumber.Format("VERYLONGPREFIX", 2026, 1_234_567_890));
    }

    [Fact]
    public void A_sequence_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentNumber.Format(DocumentNumber.OrderNumberPrefix, 2026, 0));
    }

    [Fact]
    public void The_series_key_follows_the_Colombo_year_and_not_the_UTC_one()
    {
        // 20:00 UTC on 31 December 2026 is 01:30 on 1 January 2027 in Colombo. Doc 08 §3.5 says the year is
        // "the Colombo year", so this order belongs to the 2027 series — and getting it wrong would mean
        // one order numbered SD-2026-… arriving after SD-2027-… had started, in the client's own calendar.
        var instant = new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(2027, ColomboTime.YearOf(instant));
        Assert.Equal("ORDER-2027", DocumentNumber.OrderSeriesKeyFor(instant));
    }

    [Fact]
    public void An_instant_earlier_in_the_same_day_stays_in_the_old_year()
    {
        var instant = new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero);

        Assert.Equal(2026, ColomboTime.YearOf(instant));
        Assert.Equal("ORDER-2026", DocumentNumber.OrderSeriesKeyFor(instant));
    }

    [Fact]
    public void Colombo_is_five_and_a_half_hours_ahead_of_UTC()
    {
        Assert.Equal(TimeSpan.FromMinutes(330), ColomboTime.Offset);

        var local = ColomboTime.ToLocal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(5, local.Hour);
        Assert.Equal(30, local.Minute);
        Assert.Equal(5, local.Day);
    }
}
