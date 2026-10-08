using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Sales;

/// <summary>
/// <see cref="OrderTrackingLink"/>: the courier's template with the tracking number substituted.
/// </summary>
/// <remarks>
/// <b>Added by W3-06's tester, because the one behaviour this type was extracted for was untested.</b>
/// W3-06 moved the substitution out of <c>AdminOrderReader</c>'s private <c>BuildTrackingUrl</c> into the
/// Domain so the admin screen and the <c>OrderShipped</c> email could share it, and gave one reason:
/// "the one thing a second copy is most likely to get differently is the escaping, which is not
/// cosmetic". Deleting <c>Uri.EscapeDataString</c> left all 1,906 unit tests green — the dispatch test
/// the task report named exercises the substitution and the template's absence, not the escaping.
/// <para>
/// Tracking numbers are validated to printable ASCII on the way in, which is not the same as safe in a
/// URL: an <c>&amp;</c> adds a parameter the courier did not expect and a <c>#</c> truncates the link at
/// the fragment. The customer's only route to their tracking number is this email (ACC-02 does not put it
/// on the order page yet), so a link that silently loses it leaves them with "Shipped" and nothing to do.
/// </para>
/// </remarks>
public sealed class OrderTrackingLinkTests
{
    private const string Template = "https://courier.example.lk/track?no={trackingNumber}";

    [Fact]
    public void The_placeholder_is_replaced_with_the_number()
    {
        Assert.Equal(
            "https://courier.example.lk/track?no=PC-99881",
            OrderTrackingLink.Build(Template, "PC-99881"));
    }

    /// <summary>
    /// The number is URL-escaped, so a character with a meaning in a URL cannot change the link.
    /// </summary>
    [Theory]
    [InlineData("PC&ref=1", "https://courier.example.lk/track?no=PC%26ref%3D1")]
    [InlineData("PC#99881", "https://courier.example.lk/track?no=PC%2399881")]
    [InlineData("PC 99881", "https://courier.example.lk/track?no=PC%2099881")]
    [InlineData("PC/99881", "https://courier.example.lk/track?no=PC%2F99881")]
    public void The_number_is_url_escaped(string trackingNumber, string expected)
    {
        Assert.Equal(expected, OrderTrackingLink.Build(Template, trackingNumber));
    }

    /// <summary>
    /// No template means no link — a courier with no online tracking, whose number is shown as text.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_courier_with_no_template_has_no_link(string? template)
    {
        Assert.Null(OrderTrackingLink.Build(template, "PC-99881"));
    }

    /// <summary>
    /// And no number means no link either.
    /// </summary>
    /// <remarks>
    /// <b>This is the one behavioural difference from the private method it replaced</b>, which W3-06
    /// called "strictly safer and unreachable": the old version guarded only the template, so a null
    /// number with a template reached <c>Uri.EscapeDataString(null)</c> and threw. Unreachable is right
    /// for the caller it had — <c>AdminOrderReader.ReadDispatchAsync</c> returns before the call when
    /// <c>TrackingNumber</c> is null — but it is a property of this method now, and the dispatch email is
    /// a second caller that passes <c>dispatch?.TrackingNumber</c>, which is null on every pickup order.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_order_with_no_tracking_number_has_no_link(string? trackingNumber)
    {
        Assert.Null(OrderTrackingLink.Build(Template, trackingNumber));
    }

    /// <summary>
    /// A template with no placeholder is returned unchanged rather than refused.
    /// </summary>
    /// <remarks>
    /// <c>SaveCourierCommand</c> is what requires the placeholder on the way in; this type does not
    /// re-check it, and a courier row written before that rule existed would otherwise lose its link
    /// entirely instead of pointing at the courier's tracking page.
    /// </remarks>
    [Fact]
    public void A_template_with_no_placeholder_is_left_alone()
    {
        Assert.Equal(
            "https://courier.example.lk/track",
            OrderTrackingLink.Build("https://courier.example.lk/track", "PC-99881"));
    }
}
