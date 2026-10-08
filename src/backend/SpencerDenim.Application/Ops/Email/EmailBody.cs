using System.Globalization;
using System.Net;
using System.Text;
using SpencerDenim.Domain.Branding;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// The shared shell every order email is built in: one HTML layout, one plain-text layout, and the
/// formatting rules both of them share.
/// </summary>
/// <remarks>
/// <b>Both parts are written, never one derived from the other.</b> A plain-text part stripped from HTML
/// reads like stripped HTML, and it is the part that reaches a text-only client, a screen reader set to
/// plain text, and every spam filter that scores a message with no text alternative.
/// <para>
/// <b>Inline styles, one column, no images and no web fonts.</b> A mail client is not a browser: there is
/// no stylesheet, <c>&lt;style&gt;</c> is stripped by several of them, and a remote image is blocked by
/// most until the reader asks for it — so a message whose meaning depended on one would arrive blank. The
/// shop's logo is a <c>Store.LogoAssetId</c> setting the client has not filled in (doc 08 §3.8), and
/// there is no absolute media origin configured to serve it from, so there is nothing to embed and
/// nothing is invented. The layout is a 600-pixel container that collapses to the viewport, which is what
/// makes it readable on a telephone.
/// </para>
/// <para>
/// <b>Every interpolated value is HTML-escaped.</b> Product names, a staff member's cancellation reason
/// and the shop's own <c>Bank.Instructions</c> are all free text somebody typed into an admin screen or a
/// catalogue, and <c>Bank.Instructions</c> is documented as "plain text, never markup"
/// (<c>PaymentInstructions.Notes</c>). An email body is stored in the database and rendered by a mail
/// client, so an unescaped apostrophe in "Women's" is a cosmetic bug and an unescaped angle bracket is
/// worse.
/// </para>
/// </remarks>
public static class EmailBody
{
    /// <summary>
    /// The shop's name, read from the one constant that holds it (<see cref="StoreBrand"/>). Every
    /// customer-facing string in these emails is the shop's name and never the client company's —
    /// they are two different things (<c>CLAUDE.md</c>, docs/02 §I2), and
    /// <c>BrandNameConventionTests</c> fails the build if either is written out here.
    /// </summary>
    public static string ShopName => StoreBrand.Name;

    /// <summary>"Rs. 15,596.60", doc 11's own format, from the one helper that produces it.</summary>
    public static string Money(decimal amount) => CatalogPricing.FormatLkr(amount);

    /// <summary>
    /// A UTC instant as a Colombo date and time — "Thursday 9 October 2026 at 2:03 pm".
    /// </summary>
    /// <remarks>
    /// <b>Colombo, not UTC, and the offset is named in the string.</b> Doc 06 stores every timestamp in
    /// UTC and displays it in Colombo; a bank-transfer deadline is the one figure in these emails a
    /// customer will act on with a clock in front of them, and a time five and a half hours out would
    /// cost them their order. The weekday is spelled out because "09/10/2026" means two different days
    /// depending on where the reader learned to write dates.
    /// </remarks>
    public static string ColomboDateTime(DateTime instantUtc)
    {
        var local = ColomboTime.ToLocal(new DateTimeOffset(instantUtc, TimeSpan.Zero));

        // The meridiem is written out rather than taken from the format string: `tt` under the invariant
        // culture gives "PM", and a shop's email saying "2:03 PM" reads as machine output. Hard-coded
        // English is correct here — the storefront is English-only (doc 02), and these bodies are English
        // throughout.
        var meridiem = local.Hour < 12 ? "am" : "pm";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{local:dddd d MMMM yyyy} at {local:h:mm} {meridiem} (Sri Lanka time)");
    }

    /// <summary>Escapes a value for an HTML body. Null becomes an empty string, never "null".</summary>
    public static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Wraps a body in the HTML shell: a full document, the shop's name as a heading, and one footer line.
    /// </summary>
    /// <param name="preheader">
    /// The one line a mail client shows beside the subject in the inbox list. Hidden in the body itself,
    /// because printing it twice is what makes a message look machine-made.
    /// </param>
    public static string Html(string title, string preheader, string inner) =>
        $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{Escape(title)}</title>
        </head>
        <body style="margin:0;padding:0;background:#f4f2ef;">
        <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{Escape(preheader)}</div>
        <div style="max-width:600px;margin:0 auto;padding:24px 16px;font-family:Georgia,'Times New Roman',serif;font-size:16px;line-height:1.55;color:#1c1c1c;">
          <p style="margin:0 0 24px;font-family:Arial,Helvetica,sans-serif;font-size:14px;letter-spacing:0.18em;text-transform:uppercase;color:#1c1c1c;">{Escape(ShopName)}</p>
          <h1 style="margin:0 0 16px;font-size:22px;line-height:1.3;font-weight:normal;">{Escape(title)}</h1>
        {inner}
          <p style="margin:32px 0 0;padding-top:16px;border-top:1px solid #ddd8d0;font-family:Arial,Helvetica,sans-serif;font-size:13px;color:#5a5550;">
            {Escape(ShopName)}
          </p>
        </div>
        </body>
        </html>
        """;

    /// <summary>The plain-text shell: the title, a rule, the body, and the shop's name at the end.</summary>
    public static string Text(string title, string inner)
    {
        var builder = new StringBuilder();

        builder.AppendLine(title);
        builder.AppendLine(new string('=', title.Length));
        builder.AppendLine();
        builder.AppendLine(inner.TrimEnd());
        builder.AppendLine();
        builder.AppendLine("--");
        builder.AppendLine(ShopName);

        return builder.ToString();
    }

    /// <summary>
    /// An HTML paragraph of escaped text.
    /// </summary>
    public static string Paragraph(string text) =>
        $"""  <p style="margin:0 0 16px;">{Escape(text)}</p>""";

    /// <summary>
    /// A labelled row of a details block: "Order number" / "SD-2026-000123".
    /// </summary>
    /// <remarks>
    /// <b>A two-column table, which is the one layout primitive every mail client renders the same.</b>
    /// Flexbox and grid are unreliable in Outlook's Word rendering engine, and a client that ignores the
    /// inline styling altogether still shows the label beside its value rather than running the pairs
    /// together. <c>role="presentation"</c> on the wrapper is what stops a screen reader announcing
    /// "table with two columns" before reading a four-row details block.
    /// </remarks>
    public static string DetailRow(string label, string value) =>
        $"""
            <tr>
              <td style="padding:4px 12px 4px 0;font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#5a5550;vertical-align:top;">{Escape(label)}</td>
              <td style="padding:4px 0;font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#1c1c1c;vertical-align:top;"><strong>{Escape(value)}</strong></td>
            </tr>
        """;

    /// <summary>Wraps <see cref="DetailRow"/>s in the block that holds them.</summary>
    public static string DetailBlock(IEnumerable<string> rows) =>
        $"""
          <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 16px;border-collapse:collapse;">
            <tbody>
        {string.Join(Environment.NewLine, rows)}
            </tbody>
          </table>
        """;

    /// <summary>
    /// A link, as a button-shaped anchor.
    /// </summary>
    /// <remarks>
    /// <b>The URL is escaped like any other value and is never user input.</b> Every link in these emails
    /// is built from the configured storefront origin plus an order's own GUID, so there is no path by
    /// which a shopper's text reaches an <c>href</c> — which is why this takes a plain string and does
    /// not try to validate a URL it is handed.
    /// </remarks>
    public static string Button(string url, string text) =>
        $"""
          <p style="margin:0 0 20px;">
            <a href="{Escape(url)}" style="display:inline-block;padding:12px 20px;background:#1c1c1c;color:#ffffff;font-family:Arial,Helvetica,sans-serif;font-size:14px;letter-spacing:0.06em;text-decoration:none;">{Escape(text)}</a>
          </p>
        """;
}
