namespace SpencerDenim.Domain.Sales;

/// <summary>
/// Builds a courier's tracking URL from its template and an order's tracking number (doc 08 §3.5
/// <c>Couriers</c>: "tracking links are rendered from the current template").
/// </summary>
/// <remarks>
/// <b>One implementation, read by two callers, and that is the whole reason it is here.</b> The admin
/// order detail puts the link on the screen and the <c>OrderShipped</c> email puts it in an inbox. The
/// substitution was a private method on <c>AdminOrderReader</c>; a second copy beside it would be free to
/// drift, and the one thing a second copy is most likely to get differently is the escaping below — which
/// is not cosmetic.
/// <para>
/// <b>The number is URL-escaped before it goes in.</b> The template is a URL and the number lands in its
/// query string or path; a number containing <c>&amp;</c> or <c>#</c> would otherwise silently truncate
/// the link or add a parameter the courier did not expect. Tracking numbers are validated to printable
/// ASCII on the way in, which is not the same as "safe in a URL".
/// </para>
/// </remarks>
public static class OrderTrackingLink
{
    /// <summary>
    /// The template with <see cref="Courier.TrackingNumberPlaceholder"/> replaced, or
    /// <see langword="null"/> when there is no template — a courier with no online tracking, whose number
    /// is shown as text instead.
    /// </summary>
    public static string? Build(string? template, string? trackingNumber) =>
        string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(trackingNumber)
            ? null
            : template.Replace(
                Courier.TrackingNumberPlaceholder,
                Uri.EscapeDataString(trackingNumber),
                StringComparison.Ordinal);
}
