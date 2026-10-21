using System.Globalization;
using System.Text;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Application.Sales.Payments.Contracts;

namespace SpencerDenim.Application.Ops.Email;

/// <summary>A composed message: what goes in the three columns of an outbox row.</summary>
public sealed record ComposedEmail(string Subject, string Html, string Text);

/// <summary>
/// Turns the facts of an order into six customer emails: doc 11 §4.1's five, and
/// <see cref="OrderCancelled"/>, which is <b>not</b> one of §4.1's — §4.4 lists "order cancelled" among
/// the events with no notification in v1, and that method's own remark says so. (This line read "the six
/// customer emails of doc 11 §4.1". Corrected by W3-06's tester.)
/// </summary>
/// <remarks>
/// <b>It is pure, and that is what makes it testable without a mailbox.</b> Nothing here reads a
/// database, a clock or a request; every value arrives on the facts record or on
/// <see cref="EmailOptions"/>. So the unit suite asserts the bodies themselves — that the bank
/// instructions carry all eight fields doc 11 P7 step 1 names, that a missing storefront origin costs the
/// links and nothing else, that no body contains the client company's name.
/// <para>
/// <b>Three rules every body here obeys.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Nothing is invented.</b> A value the shop has not configured is omitted, never replaced with a
/// plausible one. That is W3-04's invoice rule ("prints nothing for unset legal details") applied to an
/// email, and the reason this project has it: a task once shipped invented contact details into a footer.
/// So there is no telephone number, no postal address and no support mailbox in any of these messages —
/// those are <c>Contact.*</c> settings the client has not filled in (doc 08 §3.8), and a customer asked
/// to ring a number nobody answers is worse off than one who is asked to get in touch.
/// </description></item>
/// <item><description>
/// <b>Nothing is promised that this build cannot do.</b> No message says another message is coming unless
/// the transaction that composes it has also queued that message. (A promise made <i>inside</i> a sent
/// email is safe in one direction only: if the reader is holding it, the transport works, so "we will
/// email you when it is dispatched" is honest — <c>OrderShipped</c> is built and is queued by the
/// dispatch transaction. A promise about an email the shop has no template for would not be.)
/// </description></item>
/// <item><description>
/// <b>The shop names itself</b> from <see cref="EmailBody.ShopName"/>, and
/// <c>BrandNameConventionTests</c> fails the build if any string here spells that name out — including
/// this sentence, which is why it does not. The client company's name belongs on an invoice, not in a
/// shopper's inbox.
/// </description></item>
/// </list>
/// </remarks>
public sealed class OrderEmailComposer(EmailOptions options)
{
    /// <summary>
    /// The storefront link to one order, or <see langword="null"/> when no origin is configured.
    /// </summary>
    /// <remarks>
    /// <c>/order/{token}</c> is the storefront's own route (<c>app.routes.ts</c>), and the token is the
    /// order's <c>PublicId</c> — the unguessable value doc 08 §3.5 makes a guest's only credential. The
    /// GUID is formatted <c>"D"</c> under the invariant culture, which is the form the route parses and
    /// the form <c>ops.AuditLogs.EntityId</c> already stores.
    /// </remarks>
    public string? OrderUrl(Guid publicId) =>
        options.NormalisedStorefrontBaseUrl is { } origin
            ? $"{origin}/order/{publicId.ToString("D", CultureInfo.InvariantCulture)}"
            : null;

    /// <summary>Doc 11 §4.1 <c>OrderPlaced</c>.</summary>
    /// <remarks>
    /// <b>It does not say how to pay, and that is deliberate for a bank transfer.</b> The account details
    /// live in <c>BankTransferInstructions</c>, which the same transaction queues — but only when the
    /// shop's bank details are set (see <see cref="PaymentInstructions"/>). If this message announced a
    /// second one, an order placed before the client sends their bank details would promise an email that
    /// was never queued. So it names the deadline, points at the order, and leaves the "how" to the
    /// message that can actually answer it.
    /// </remarks>
    public ComposedEmail OrderPlaced(PlacedOrderEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var header = facts.Header;
        var title = $"We have your order {header.OrderNumber}";
        var url = OrderUrl(header.PublicId);

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(header.To.Greeting));
        html.AppendLine(EmailBody.Paragraph(
            "Thank you for shopping with us. Your order is saved and the items are held for you while we "
            + "wait for payment."));

        text.AppendLine(header.To.Greeting);
        text.AppendLine();
        text.AppendLine(
            "Thank you for shopping with us. Your order is saved and the items are held for you while we "
            + "wait for payment.");
        text.AppendLine();

        var rows = new List<(string Label, string Value)>
        {
            ("Order number", header.OrderNumber),
            ("Placed", EmailBody.ColomboDateTime(header.NowUtc)),
            ("Payment method", facts.PaymentMethodName),
            ("Pay before", EmailBody.ColomboDateTime(facts.PaymentDueAtUtc)),
            ("Total", EmailBody.Money(facts.Totals.GrandTotal)),
        };

        html.AppendLine(EmailBody.DetailBlock(rows.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
        AppendTextRows(text, rows);

        AppendItems(html, text, facts.Lines, facts.Totals);
        AppendDelivery(html, text, facts.Delivery);

        html.AppendLine(EmailBody.Paragraph(
            "If payment does not reach us by the time above, the items go back on sale and the order "
            + "closes."));

        text.AppendLine(
            "If payment does not reach us by the time above, the items go back on sale and the order "
            + "closes.");
        text.AppendLine();

        AppendOrderLink(html, text, url, "View your order");

        return Compose(title, $"Order {header.OrderNumber} — we have it and the items are held for you.", html, text);
    }

    /// <summary>
    /// Doc 11 P7 step 1 / §4.1 <c>BankTransferInstructions</c>, field for field: bank name, branch,
    /// account name, account number, the exact amount, the order number to quote as the reference, the
    /// due date and time, and the slip-upload link.
    /// </summary>
    /// <remarks>
    /// <b>The bank fields are the provider's, not this class's.</b> They arrive as
    /// <see cref="PaymentInstructions.Fields"/> — label and value pairs the provider built from the
    /// <c>Bank.*</c> settings, omitting any it has no value for — so this message cannot print a field the
    /// shop has not filled in, and cannot invent a label for one either. A provider that cannot describe
    /// itself answers null and no message is queued at all; that decision belongs to the caller and is
    /// recorded on <c>IOrderEmailQueue.EnlistPaymentInstructions</c>.
    /// <para>
    /// <b>The slip-upload link is the order link, and that is a fact about the storefront rather than a
    /// shortcut.</b> There is one order page (<c>/order/:token</c>) and the upload control lives on it —
    /// so a separate "upload" address would be an address that does not exist. The label says what the
    /// link is for.
    /// </para>
    /// </remarks>
    public ComposedEmail BankTransferInstructions(OrderEmailHeader header, PaymentInstructions instructions)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(instructions);

        var title = $"How to pay for order {header.OrderNumber}";
        var url = OrderUrl(header.PublicId);

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(header.To.Greeting));
        html.AppendLine(EmailBody.Paragraph(
            "Here is everything you need to pay for your order by bank or online transfer. Please transfer "
            + "the exact amount and quote the order number as the reference, so we can match your payment."));

        text.AppendLine(header.To.Greeting);
        text.AppendLine();
        text.AppendLine(
            "Here is everything you need to pay for your order by bank or online transfer. Please transfer "
            + "the exact amount and quote the order number as the reference, so we can match your payment.");
        text.AppendLine();

        var rows = new List<(string Label, string Value)>
        {
            ("Amount to transfer", EmailBody.Money(instructions.AmountLkr)),

            // Doc 11 P7 step 1 and step 5: the reference is the order number, and it is the only thing
            // tying an anonymous credit on a bank statement to an order. The provider supplies it rather
            // than this class assuming it, because a gateway that identifies its own payments sets it null.
            ("Reference to quote", instructions.Reference ?? header.OrderNumber),
        };

        rows.AddRange(instructions.Fields.Select(field => (field.Label, field.Value)));
        rows.Add(("Pay before", EmailBody.ColomboDateTime(instructions.DueAtUtc)));

        html.AppendLine(EmailBody.DetailBlock(rows.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
        AppendTextRows(text, rows);

        if (instructions.Notes is { Length: > 0 } notes)
        {
            html.AppendLine(EmailBody.Paragraph(notes));
            text.AppendLine(notes);
            text.AppendLine();
        }

        html.AppendLine(EmailBody.Paragraph(
            "Once you have transferred the money, open your order to send us the bank reference and a "
            + "photograph or PDF of the slip. It helps us confirm your payment faster, and it is optional."));

        text.AppendLine(
            "Once you have transferred the money, open your order to send us the bank reference and a "
            + "photograph or PDF of the slip. It helps us confirm your payment faster, and it is optional.");
        text.AppendLine();

        html.AppendLine(EmailBody.Paragraph(
            "If payment does not reach us by the time above, the items go back on sale and the order "
            + "closes."));

        text.AppendLine(
            "If payment does not reach us by the time above, the items go back on sale and the order "
            + "closes.");
        text.AppendLine();

        AppendOrderLink(html, text, url, "Upload your payment slip");

        return Compose(
            title,
            $"Transfer {EmailBody.Money(instructions.AmountLkr)} and quote {instructions.Reference ?? header.OrderNumber}.",
            html,
            text);
    }

    /// <summary>Doc 11 §4.1 <c>PaymentConfirmed</c>: "Payment received, amount, invoice number, what happens next".</summary>
    public ComposedEmail PaymentConfirmed(PaymentConfirmedEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var header = facts.Header;
        var title = $"Payment received for order {header.OrderNumber}";

        var html = new StringBuilder();
        var text = new StringBuilder();

        var opening = facts.WasLatePayment

            // Doc 11 P7 step 8: the order had already closed and staff chose to honour the transfer. The
            // customer's last message from us said the order had expired, so saying nothing about that
            // would leave two messages that contradict each other.
            ? "Thank you — we have found your payment and reopened your order, and the items are yours."
            : "Thank you — your payment has reached us and your order is confirmed.";

        html.AppendLine(EmailBody.Paragraph(header.To.Greeting));
        html.AppendLine(EmailBody.Paragraph(opening));

        text.AppendLine(header.To.Greeting);
        text.AppendLine();
        text.AppendLine(opening);
        text.AppendLine();

        var rows = new List<(string Label, string Value)>
        {
            ("Order number", header.OrderNumber),
            ("Amount received", EmailBody.Money(facts.AmountLkr)),
        };

        if (facts.InvoiceNumber is { Length: > 0 } invoiceNumber)
        {
            rows.Add(("Invoice number", invoiceNumber));
        }

        html.AppendLine(EmailBody.DetailBlock(rows.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
        AppendTextRows(text, rows);

        const string next =
            "We are getting your order ready now. You will hear from us again when it leaves us, with the "
            + "courier and a tracking number — or, for a store collection, when it is ready to pick up.";

        html.AppendLine(EmailBody.Paragraph(next));
        text.AppendLine(next);
        text.AppendLine();

        AppendOrderLink(html, text, OrderUrl(header.PublicId), "View your order");

        return Compose(title, $"Order {header.OrderNumber} is paid and we are getting it ready.", html, text);
    }

    /// <summary>
    /// Doc 11 §4.1 <c>OrderShipped</c>, in both of doc 11 P9's shapes — a courier dispatch and a store
    /// collection. This is §7 G-35's "template variant by delivery method".
    /// </summary>
    public ComposedEmail OrderShipped(ShippedOrderEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var header = facts.Header;

        var title = facts.IsPickup
            ? $"Order {header.OrderNumber} is ready to collect"
            : $"Order {header.OrderNumber} is on its way";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(header.To.Greeting));
        text.AppendLine(header.To.Greeting);
        text.AppendLine();

        var rows = new List<(string Label, string Value)> { ("Order number", header.OrderNumber) };

        if (facts.IsPickup)
        {
            const string opening = "Your order is packed and waiting for you at our store.";

            html.AppendLine(EmailBody.Paragraph(opening));
            text.AppendLine(opening);
            text.AppendLine();

            // Each of the three is added only if it has a value: the location's name is snapshotted on
            // the order, and its address and opening hours are read from the pickup location, which a
            // shop may not have filled in.
            AddIfPresent(rows, "Collect from", facts.PickupLocationName);
            AddIfPresent(rows, "Address", facts.PickupAddress);
            AddIfPresent(rows, "Opening hours", facts.PickupOpeningHours);

            html.AppendLine(EmailBody.DetailBlock(rows.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
            AppendTextRows(text, rows);

            const string bring = "Please bring your order number with you.";

            html.AppendLine(EmailBody.Paragraph(bring));
            text.AppendLine(bring);
            text.AppendLine();
        }
        else
        {
            const string opening = "Your order has left us and is with the courier.";

            html.AppendLine(EmailBody.Paragraph(opening));
            text.AppendLine(opening);
            text.AppendLine();

            AddIfPresent(rows, "Courier", facts.CourierName);
            AddIfPresent(rows, "Tracking number", facts.TrackingNumber);

            html.AppendLine(EmailBody.DetailBlock(rows.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
            AppendTextRows(text, rows);

            if (facts.TrackingUrl is { Length: > 0 } trackingUrl)
            {
                html.AppendLine(EmailBody.Button(trackingUrl, "Track your parcel"));
                text.AppendLine(CultureInfo.InvariantCulture, $"Track your parcel: {trackingUrl}");
                text.AppendLine();
            }
            else if (facts.TrackingNumber is { Length: > 0 })
            {
                // Doc 08 §3.5: a courier may have no tracking URL template, in which case the number is
                // shown as text. Nothing here builds a link out of a courier's home page.
                const string noLink =
                    "Your courier does not offer online tracking, so please quote the tracking number "
                    + "above if you contact them.";

                html.AppendLine(EmailBody.Paragraph(noLink));
                text.AppendLine(noLink);
                text.AppendLine();
            }
        }

        AppendOrderLink(html, text, OrderUrl(header.PublicId), "View your order");

        return Compose(
            title,
            facts.IsPickup
                ? $"Order {header.OrderNumber} is packed and ready to collect."
                : $"Order {header.OrderNumber} has been dispatched.",
            html,
            text);
    }

    /// <summary>
    /// Doc 11 §4.1 <c>OrderExpired</c>: "Order not paid in time, items released, how to order again; what
    /// to do if already paid".
    /// </summary>
    public ComposedEmail OrderExpired(OrderEmailHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        var title = $"Order {header.OrderNumber} has closed";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(header.To.Greeting));
        text.AppendLine(header.To.Greeting);
        text.AppendLine();

        var lines = new[]
        {
            $"We did not receive payment for order {header.OrderNumber} in time, so it has closed and the "
            + "items are back on sale. Nothing has been charged.",

            "If you still want them, you are very welcome to order again — and do it soon, because they "
            + "are available to everybody now.",

            // Doc 11 P7's exception: once an order is Expired the customer's upload route is closed, so
            // staff take the reference by hand and settle it with Accept late payment. The customer
            // cannot be told to upload anything, so they are told to get in touch, quoting the number.
            $"If you have already transferred the money, please get in touch straight away and quote "
            + $"order {header.OrderNumber}. We can usually confirm the order if the items are still "
            + "available, and return your payment if they are not.",
        };

        foreach (var line in lines)
        {
            html.AppendLine(EmailBody.Paragraph(line));
            text.AppendLine(line);
            text.AppendLine();
        }

        AppendOrderLink(html, text, OrderUrl(header.PublicId), "View your order");

        return Compose(title, $"Order {header.OrderNumber} closed unpaid and the items are back on sale.", html, text);
    }

    /// <summary>
    /// A cancelled unpaid order. <b>Not a v1 commitment of doc 08 §3.8 or doc 11 §4</b> — see
    /// <c>EmailTemplateCodes.OrderCancelled</c>.
    /// </summary>
    public ComposedEmail OrderCancelled(CancelledOrderEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var header = facts.Header;
        var title = $"Order {header.OrderNumber} has been cancelled";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(header.To.Greeting));
        text.AppendLine(header.To.Greeting);
        text.AppendLine();

        // Doc 11 P10 step 3 records who closed the order, and the two are not the same message to get.
        // The shop's reason is deliberately *not* repeated here: it is a staff member's internal note
        // (`Orders.CancelReason`), readable by `Orders.View`, and may say anything at all.
        var lines = new[]
        {
            facts.CancelledByCustomer
                ? $"Order {header.OrderNumber} has been cancelled as you asked, and the items are back on "
                + "sale. Nothing has been charged."
                : $"We have had to cancel order {header.OrderNumber}, and the items are back on sale. "
                + "Nothing has been charged.",

            "You are very welcome to order again whenever you like.",

            $"If you have already transferred the money for this order, please get in touch straight away "
            + $"and quote order {header.OrderNumber}, and we will sort it out.",
        };

        foreach (var line in lines)
        {
            html.AppendLine(EmailBody.Paragraph(line));
            text.AppendLine(line);
            text.AppendLine();
        }

        AppendOrderLink(html, text, OrderUrl(header.PublicId), "View your order");

        return Compose(title, $"Order {header.OrderNumber} is cancelled and nothing has been charged.", html, text);
    }

    private static void AddIfPresent(List<(string Label, string Value)> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add((label, value.Trim()));
        }
    }

    /// <summary>
    /// The link to the order, or — with no configured storefront origin — the sentence that replaces it.
    /// </summary>
    /// <remarks>
    /// <b>The replacement is not an apology and does not mention configuration.</b> A shopper reading this
    /// message is not interested in which environment variable is unset; what they need is the order
    /// number, which every one of these messages prints in its own details block. So the link simply is
    /// not there. The operator is told, once per process, by <c>SendEmails</c>.
    /// </remarks>
    private static void AppendOrderLink(StringBuilder html, StringBuilder text, string? url, string label)
    {
        if (url is null)
        {
            return;
        }

        html.AppendLine(EmailBody.Button(url, label));
        text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {url}");
        text.AppendLine();
    }

    /// <summary>
    /// The same labelled pairs in the plain-text part, one per line.
    /// </summary>
    /// <remarks>
    /// <b>A row with no label is indented, not given a bare colon.</b> The second and later lines of a
    /// delivery address carry no label — the first says "Delivering to" and the rest continue it — and
    /// writing them as <c>": 14 Galle Road"</c> is how a text part comes to look machine-made. The HTML
    /// part has the same shape and renders an empty cell, which is simply blank.
    /// </remarks>
    private static void AppendTextRows(StringBuilder text, IEnumerable<(string Label, string Value)> rows)
    {
        foreach (var (label, value) in rows)
        {
            // Built first, then appended: a conditional of two interpolated strings is a `string`, and
            // `AppendLine(IFormatProvider, …)` takes an interpolated-string *handler* rather than one.
            var line = label.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"  {value}")
                : string.Create(CultureInfo.InvariantCulture, $"{label}: {value}");

            text.AppendLine(line);
        }

        text.AppendLine();
    }

    /// <summary>
    /// The items, with colour, size and quantity (doc 11 §4.1's <c>OrderPlaced</c> content), and the
    /// money underneath them.
    /// </summary>
    /// <remarks>
    /// <b>A discount line appears only when there is one.</b> "Discount: Rs. 0.00" is noise, and a
    /// delivery line reading "Rs. 0.00" where delivery is genuinely free says so in words instead.
    /// </remarks>
    private static void AppendItems(
        StringBuilder html,
        StringBuilder text,
        IReadOnlyList<OrderEmailLine> lines,
        OrderEmailTotals totals)
    {
        html.AppendLine("""  <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="margin:0 0 16px;border-collapse:collapse;">""");
        html.AppendLine("    <tbody>");

        text.AppendLine("Your items");
        text.AppendLine("----------");

        foreach (var line in lines)
        {
            var description = Describe(line);

            html.AppendLine(
                CultureInfo.InvariantCulture,
                $"""
                      <tr>
                        <td style="padding:8px 12px 8px 0;border-bottom:1px solid #eae6e0;font-family:Arial,Helvetica,sans-serif;font-size:14px;">{EmailBody.Escape(description)}</td>
                        <td style="padding:8px 0;border-bottom:1px solid #eae6e0;font-family:Arial,Helvetica,sans-serif;font-size:14px;text-align:right;white-space:nowrap;">{EmailBody.Escape(EmailBody.Money(line.LineTotal))}</td>
                      </tr>
                """);

            text.AppendLine(CultureInfo.InvariantCulture, $"{description} — {EmailBody.Money(line.LineTotal)}");
        }

        html.AppendLine("    </tbody>");
        html.AppendLine("  </table>");
        text.AppendLine();

        var money = new List<(string Label, string Value)> { ("Items", EmailBody.Money(totals.ItemsSubtotal)) };

        if (totals.PromoDiscountAmount > 0m)
        {
            money.Add(("Discount", $"-{EmailBody.Money(totals.PromoDiscountAmount)}"));
        }

        money.Add((
            "Delivery",
            totals.DeliveryFeeAmount - totals.DeliveryDiscountAmount <= 0m
                ? "Free"
                : EmailBody.Money(totals.DeliveryFeeAmount - totals.DeliveryDiscountAmount)));

        money.Add(("Total", EmailBody.Money(totals.GrandTotal)));

        html.AppendLine(EmailBody.DetailBlock(money.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
        AppendTextRows(text, money);
    }

    /// <summary>"2 × Relaxed Linen Blend Shirt, Beige / M" — doc 11 §4.1's "items (colour, size, qty)".</summary>
    private static string Describe(OrderEmailLine line) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{line.Quantity} × {line.ProductName}, {line.ColourName} / {line.SizeLabel}");

    private static void AppendDelivery(StringBuilder html, StringBuilder text, OrderEmailDelivery delivery)
    {
        var rows = new List<(string Label, string Value)> { ("Delivery method", delivery.MethodName) };

        if (delivery.IsPickup)
        {
            AddIfPresent(rows, "Collect from", delivery.PickupLocationName);
            AddIfPresent(rows, "District", delivery.DistrictName);
        }
        else
        {
            foreach (var line in delivery.AddressLines)
            {
                AddIfPresent(rows, rows.Count == 1 ? "Delivering to" : string.Empty, line);
            }

            // Owner decision 2026-10-21: an overseas address names its country (its region and postcode are
            // already among the address lines); a Sri Lankan one keeps its district.
            if (delivery.IsOverseas)
            {
                rows.Add(("Country", delivery.CountryName));
            }
            else
            {
                AddIfPresent(rows, "District", delivery.DistrictName);
            }
        }

        html.AppendLine(EmailBody.DetailBlock(rows.Select(row => EmailBody.DetailRow(row.Label, row.Value))));
        AppendTextRows(text, rows);
    }

    private static ComposedEmail Compose(
        string title,
        string preheader,
        StringBuilder html,
        StringBuilder text) =>
        new(
            title,
            EmailBody.Html(title, preheader, html.ToString().TrimEnd()),
            EmailBody.Text(title, text.ToString()));
}
