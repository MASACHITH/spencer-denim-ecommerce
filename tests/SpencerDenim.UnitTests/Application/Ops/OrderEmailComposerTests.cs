using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Branding;

namespace SpencerDenim.UnitTests.Application.Ops;

/// <summary>
/// The six customer emails of doc 11 §4.1, asserted on their own text.
/// <para>
/// <b>These are the tests that could most easily have been written so that they cannot fail</b>, which is
/// the defect this project has shipped seven times in ten days. So none of them asserts that a body is
/// non-empty, or that it contains a word the composer would print whatever happened. Each one names a
/// value doc 11 requires and checks that <i>that value</i> is in the message — and each one was proved
/// against a mutation of the composer that removes it. The mutations are listed in the task report.
/// </para>
/// </summary>
public sealed class OrderEmailComposerTests
{
    private const string Origin = "https://shop.example.com";

    private static readonly Guid PublicId = Guid.Parse("7f3a1c90-2b44-4d6e-9f11-0c5e8a6d3b22");

    private static OrderEmailComposer Composer(string? origin = Origin) =>
        new(new EmailOptions { StorefrontBaseUrl = origin });

    private static OrderEmailHeader Header(string? firstName = "Nimal", string? lastName = "Perera") =>
        new(
            "SD-2026-000123",
            PublicId,
            new OrderEmailAddressee("buyer@example.com", firstName, lastName),

            // 2026-10-09 08:33 UTC is 14:03 on the same day in Colombo (+05:30). The two differ in the
            // hour *and* the minute, so a body that printed UTC could not pass by coincidence.
            new DateTime(2026, 10, 9, 8, 33, 0, DateTimeKind.Utc));

    private static PlacedOrderEmailFacts Placed(
        OrderEmailDelivery? delivery = null,
        string productName = "Relaxed Linen Blend Shirt",
        decimal promoDiscount = 0m,
        decimal deliveryFee = 450m) =>
        new(
            Header(),
            [new OrderEmailLine(productName, "Beige", "M", 2, 11_980m)],
            new OrderEmailTotals(11_980m, promoDiscount, deliveryFee, 0m, 11_980m - promoDiscount + deliveryFee),
            delivery ?? new OrderEmailDelivery(
                "Standard delivery",
                IsPickup: false,
                PickupLocationName: null,
                ["Nimal Perera", "14 Galle Road", "Kollupitiya"],
                "Colombo"),
            "Bank / online transfer",
            new DateTime(2026, 10, 11, 8, 33, 0, DateTimeKind.Utc));

    private static PaymentInstructions Instructions(string? notes = "Cash-machine deposits can take a day.") =>
        new(
            "BankTransfer",
            "SD-2026-000123",
            12_430m,
            new DateTime(2026, 10, 11, 8, 33, 0, DateTimeKind.Utc),
            [
                new PaymentInstructionField("Bank", "Example Bank PLC"),
                new PaymentInstructionField("Branch", "Kollupitiya"),
                new PaymentInstructionField("Account name", "Example Shop (Pvt) Ltd"),
                new PaymentInstructionField("Account number", "0012 3456 7890", Copyable: true),
            ],
            notes,
            RedirectUrl: null,
            AcceptsProof: true);

    /// <summary>
    /// Doc 11 P7 step 1's list, in full and one item at a time: "bank name, branch, account name, account
    /// number, exact amount, <b>order number to quote as the reference</b>, due date and time, and the
    /// link to upload a payment slip".
    /// </summary>
    /// <remarks>
    /// <b>This is the one email where a missing field costs a customer money</b>, so it is asserted field
    /// by field in both parts rather than by a single "contains the bank name" check. The amount is
    /// checked in its formatted form, because <c>12430.00</c> in a body a customer reads into their
    /// banking app is a different number from <c>Rs. 12,430.00</c>.
    /// </remarks>
    [Theory]
    [InlineData("Example Bank PLC")]
    [InlineData("Kollupitiya")]
    [InlineData("Example Shop (Pvt) Ltd")]
    [InlineData("0012 3456 7890")]
    [InlineData("Rs. 12,430.00")]
    [InlineData("SD-2026-000123")]
    [InlineData("Sunday 11 October 2026 at 2:03 pm")]
    [InlineData(Origin + "/order/7f3a1c90-2b44-4d6e-9f11-0c5e8a6d3b22")]
    public void The_bank_instructions_carry_every_field_P7_step_1_names(string expected)
    {
        var email = Composer().BankTransferInstructions(Header(), Instructions());

        Assert.Contains(expected, email.Html, StringComparison.Ordinal);
        Assert.Contains(expected, email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deadline is printed in <b>Colombo</b> time, which is the only figure in these messages a
    /// customer acts on with a clock in front of them.
    /// </summary>
    /// <remarks>
    /// The instant under test is 08:33 UTC. A body that printed the UTC time would say "8:33 am" and this
    /// assertion would fail on the hour as well as the minute.
    /// </remarks>
    [Fact]
    public void Times_are_Colombo_times_and_say_so()
    {
        var email = Composer().BankTransferInstructions(Header(), Instructions());

        Assert.Contains("2:03 pm (Sri Lanka time)", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("8:33 am", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shop's own instructions note (<c>Bank.Instructions</c>) is carried, and omitted when unset.
    /// </summary>
    [Fact]
    public void The_shops_own_payment_note_is_carried_when_it_has_one()
    {
        Assert.Contains(
            "Cash-machine deposits can take a day.",
            Composer().BankTransferInstructions(Header(), Instructions()).Text,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Cash-machine",
            Composer().BankTransferInstructions(Header(), Instructions(notes: null)).Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A field the provider omitted — an unset branch — is simply absent, with no empty label left behind.
    /// </summary>
    /// <remarks>
    /// <b>This is the "nothing is invented" rule from the other end.</b>
    /// <c>BankTransferProvider.DescribeAsync</c> omits a field it has no value for, and the composer must
    /// not print a label for it: "Branch:" with nothing after it reads as a shop that has lost its own
    /// details.
    /// </remarks>
    [Fact]
    public void A_field_the_provider_omitted_leaves_no_empty_label()
    {
        var withoutBranch = Instructions() with
        {
            Fields =
            [
                new PaymentInstructionField("Bank", "Example Bank PLC"),
                new PaymentInstructionField("Account name", "Example Shop (Pvt) Ltd"),
                new PaymentInstructionField("Account number", "0012 3456 7890"),
            ],
        };

        var email = Composer().BankTransferInstructions(Header(), withoutBranch);

        Assert.DoesNotContain("Branch", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Branch", email.Html, StringComparison.Ordinal);
        Assert.Contains("Account number: 0012 3456 7890", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no storefront origin configured the messages carry <b>no links at all</b> — and lose nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <b>Both halves are the assertion.</b> It would be easy to write this test so that it only proved
    /// the links were gone, which a composer that returned an empty body would also pass. So it also
    /// checks that the account number, the amount and the order number are still there: an unset base URL
    /// costs the convenience and not the content.
    /// </remarks>
    [Fact]
    public void With_no_configured_origin_there_are_no_links_and_nothing_else_is_lost()
    {
        var email = Composer(origin: null).BankTransferInstructions(Header(), Instructions());

        Assert.DoesNotContain("http", email.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload your payment slip", email.Text, StringComparison.Ordinal);

        Assert.Contains("0012 3456 7890", email.Text, StringComparison.Ordinal);
        Assert.Contains("Rs. 12,430.00", email.Text, StringComparison.Ordinal);
        Assert.Contains("SD-2026-000123", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing slash on the configured origin does not produce a double slash in the link.
    /// </summary>
    [Fact]
    public void The_origins_trailing_slash_is_not_doubled()
    {
        var email = Composer(origin: Origin + "/").OrderPlaced(Placed());

        Assert.Contains($"{Origin}/order/", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("//order/", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 11 §4.1's <c>OrderPlaced</c> content: order number, items with colour, size and quantity,
    /// prices, delivery method and address, total, payment method.
    /// </summary>
    [Theory]
    [InlineData("SD-2026-000123")]
    [InlineData("2 × Relaxed Linen Blend Shirt, Beige / M")]
    [InlineData("Rs. 11,980.00")]
    [InlineData("Standard delivery")]
    [InlineData("14 Galle Road")]
    [InlineData("Colombo")]
    [InlineData("Bank / online transfer")]
    [InlineData("Rs. 12,430.00")]
    public void The_placement_email_carries_what_doc_11_lists(string expected)
    {
        var email = Composer().OrderPlaced(Placed());

        Assert.Contains(expected, email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The placement email must not announce a second one</b>, because the second one is not always
    /// queued: with the shop's bank details unset there are no instructions to send, and a message
    /// promising an email that never arrives is the defect this project has already paid for twice.
    /// </summary>
    [Fact]
    public void The_placement_email_promises_no_other_email()
    {
        var text = Composer().OrderPlaced(Placed()).Text;

        Assert.DoesNotContain("another email", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("separate email", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("we have emailed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("we have sent you", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A discount line appears only when there is a discount, and free delivery says "Free" rather than
    /// "Rs. 0.00".
    /// </summary>
    [Fact]
    public void Money_lines_that_would_read_as_zero_are_worded_or_omitted()
    {
        var plain = Composer().OrderPlaced(Placed()).Text;
        Assert.DoesNotContain("Discount", plain, StringComparison.Ordinal);
        Assert.Contains("Delivery: Rs. 450.00", plain, StringComparison.Ordinal);

        var discounted = Composer().OrderPlaced(Placed(promoDiscount: 500m, deliveryFee: 0m)).Text;
        Assert.Contains("Discount: -Rs. 500.00", discounted, StringComparison.Ordinal);
        Assert.Contains("Delivery: Free", discounted, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pickup order's placement email names the store and no delivery address, because a pickup order
    /// has none (<c>CK_Orders_ShipAddress</c>).
    /// </summary>
    [Fact]
    public void A_pickup_placement_names_the_store_and_no_address()
    {
        var email = Composer().OrderPlaced(Placed(
            new OrderEmailDelivery("Store pickup", IsPickup: true, "Colombo 03 store", [], "Colombo")));

        Assert.Contains("Colombo 03 store", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Galle Road", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 11 §4.1 <c>PaymentConfirmed</c>: the amount and the invoice number.
    /// </summary>
    [Fact]
    public void The_confirmation_carries_the_amount_and_the_invoice_number()
    {
        var email = Composer().PaymentConfirmed(
            new PaymentConfirmedEmailFacts(Header(), 12_430m, "INV-2026-000045", WasLatePayment: false));

        Assert.Contains("Rs. 12,430.00", email.Text, StringComparison.Ordinal);
        Assert.Contains("INV-2026-000045", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no invoice number there is no invoice line, rather than a label with nothing after it.
    /// </summary>
    [Fact]
    public void A_confirmation_with_no_invoice_number_omits_the_line()
    {
        var email = Composer().PaymentConfirmed(
            new PaymentConfirmedEmailFacts(Header(), 12_430m, null, WasLatePayment: false));

        Assert.DoesNotContain("Invoice", email.Text, StringComparison.Ordinal);
        Assert.Contains("Rs. 12,430.00", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A late acceptance (doc 11 P7 step 8) says something different, because the customer's previous
    /// message from us said the order had closed.
    /// </summary>
    [Fact]
    public void A_late_acceptance_acknowledges_that_the_order_had_closed()
    {
        var late = Composer()
            .PaymentConfirmed(new PaymentConfirmedEmailFacts(Header(), 12_430m, "INV-1", WasLatePayment: true))
            .Text;

        var normal = Composer()
            .PaymentConfirmed(new PaymentConfirmedEmailFacts(Header(), 12_430m, "INV-1", WasLatePayment: false))
            .Text;

        Assert.Contains("reopened your order", late, StringComparison.Ordinal);
        Assert.DoesNotContain("reopened", normal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 11 P9 step 5: the courier, the tracking number and the tracking link.
    /// </summary>
    [Fact]
    public void The_dispatch_email_carries_the_courier_the_number_and_the_link()
    {
        var email = Composer().OrderShipped(new ShippedOrderEmailFacts(
            Header(),
            IsPickup: false,
            "Pronto Couriers",
            "PC-99881",
            "https://courier.example.lk/track?no=PC-99881",
            null,
            null,
            null));

        Assert.Contains("Pronto Couriers", email.Text, StringComparison.Ordinal);
        Assert.Contains("PC-99881", email.Text, StringComparison.Ordinal);
        Assert.Contains("https://courier.example.lk/track?no=PC-99881", email.Text, StringComparison.Ordinal);
        Assert.Contains("on its way", email.Subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// A courier with no tracking URL template (doc 08 §3.5) gets the number as text and no invented
    /// link.
    /// </summary>
    [Fact]
    public void A_courier_with_no_tracking_site_gets_no_invented_link()
    {
        var email = Composer().OrderShipped(new ShippedOrderEmailFacts(
            Header(),
            IsPickup: false,
            "Village Van",
            "VV-7",
            TrackingUrl: null,
            null,
            null,
            null));

        Assert.Contains("VV-7", email.Text, StringComparison.Ordinal);
        Assert.Contains("does not offer online tracking", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Track your parcel", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 11 §7 G-35: the pickup variant says "ready to collect" with the location and its hours, and
    /// carries no courier wording.
    /// </summary>
    [Fact]
    public void The_pickup_dispatch_is_a_different_message()
    {
        var email = Composer().OrderShipped(new ShippedOrderEmailFacts(
            Header(),
            IsPickup: true,
            CourierName: null,
            TrackingNumber: null,
            TrackingUrl: null,
            "Colombo 03 store",
            "41 Duplication Road, Colombo 03",
            "Mon–Sat 10:00–19:00"));

        Assert.Contains("ready to collect", email.Subject, StringComparison.Ordinal);
        Assert.Contains("Colombo 03 store", email.Text, StringComparison.Ordinal);
        Assert.Contains("41 Duplication Road, Colombo 03", email.Text, StringComparison.Ordinal);
        Assert.Contains("Mon–Sat 10:00–19:00", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("courier", email.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A pickup location whose address and hours the shop has not filled in costs those two lines and
    /// nothing else — the customer still gets the order number and the wording that says it is ready.
    /// </summary>
    [Fact]
    public void A_pickup_with_no_address_on_file_still_tells_the_customer_it_is_ready()
    {
        var email = Composer().OrderShipped(new ShippedOrderEmailFacts(
            Header(),
            IsPickup: true,
            null,
            null,
            null,
            "Colombo 03 store",
            PickupAddress: null,
            PickupOpeningHours: null));

        Assert.Contains("ready to collect", email.Subject, StringComparison.Ordinal);
        Assert.Contains("SD-2026-000123", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening hours", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Address", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 11 §4.1 <c>OrderExpired</c>: not paid in time, items released, how to order again, and what to
    /// do if the money has already gone.
    /// </summary>
    [Fact]
    public void The_expiry_email_covers_the_already_paid_case()
    {
        var email = Composer().OrderExpired(Header());

        Assert.Contains("back on sale", email.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing has been charged", email.Text, StringComparison.Ordinal);
        Assert.Contains("already transferred the money", email.Text, StringComparison.Ordinal);
        Assert.Contains("SD-2026-000123", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cancellation email says which of the two things happened (doc 11 P10 step 3's source).
    /// </summary>
    [Fact]
    public void A_cancellation_says_who_cancelled_it()
    {
        var byCustomer = Composer()
            .OrderCancelled(new CancelledOrderEmailFacts(Header(), CancelledByCustomer: true)).Text;

        var byShop = Composer()
            .OrderCancelled(new CancelledOrderEmailFacts(Header(), CancelledByCustomer: false)).Text;

        Assert.Contains("cancelled as you asked", byCustomer, StringComparison.Ordinal);
        Assert.Contains("We have had to cancel", byShop, StringComparison.Ordinal);
        Assert.DoesNotContain("as you asked", byShop, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every one of the six says <b>Colombo Wardrobe</b> and none of them says the client company's name.
    /// </summary>
    /// <remarks>
    /// <c>CLAUDE.md</c>: the two names are not interchangeable. The shop a customer sees is Colombo
    /// Wardrobe; Spencer Denim Industries is the company that commissioned it, and belongs on an invoice.
    /// <c>BrandNameConventionTests</c> stops the name being written out in production source;
    /// this stops it being <i>missing</i> from the messages that most need it.
    /// </remarks>
    [Fact]
    public void Every_message_is_from_the_shop_and_never_from_the_client_company()
    {
        foreach (var email in AllSix())
        {
            Assert.Contains(StoreBrand.Name, email.Html, StringComparison.Ordinal);
            Assert.Contains(StoreBrand.Name, email.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Spencer", email.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Spencer", email.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// None of the six invents a telephone number, a postal address or a support mailbox.
    /// </summary>
    /// <remarks>
    /// The shop's <c>Contact.*</c> settings are unset and W3-04's invoice rule applies: print nothing
    /// rather than something plausible. An earlier task shipped invented contact details into a footer and
    /// had to have them removed, and an email is harder to take back than a page.
    /// </remarks>
    [Fact]
    public void No_message_invents_a_way_to_contact_the_shop()
    {
        foreach (var email in AllSix())
        {
            Assert.DoesNotContain("+94", email.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("mailto:", email.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@colombo", email.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tel:", email.Html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Every message has both parts, and the HTML one is a whole document.
    /// </summary>
    /// <remarks>
    /// A message with no text part is scored as spam by several filters and arrives empty in a text-only
    /// client; an HTML fragment with no <c>&lt;html&gt;</c> is rendered inconsistently. Neither is
    /// something a shop finds out about from its own inbox.
    /// </remarks>
    [Fact]
    public void Every_message_has_a_text_part_and_a_whole_html_document()
    {
        foreach (var email in AllSix())
        {
            Assert.StartsWith("<!doctype html>", email.Html, StringComparison.Ordinal);
            Assert.Contains("</html>", email.Html, StringComparison.Ordinal);
            Assert.Contains("viewport", email.Html, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(email.Text));
            Assert.DoesNotContain("<", email.Text.Replace("<!--", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A subject fits <c>ops.EmailOutbox.Subject</c>'s <c>nvarchar(200)</c>, and is one line.
    /// </summary>
    [Fact]
    public void Every_subject_fits_the_column()
    {
        foreach (var email in AllSix())
        {
            Assert.InRange(email.Subject.Length, 1, 200);
            Assert.DoesNotContain('\n', email.Subject);
        }
    }

    /// <summary>
    /// A product name holding markup is escaped, not rendered.
    /// </summary>
    /// <remarks>
    /// <b>A product name is free text a staff member typed into the catalogue</b>, and this body is stored
    /// in the database and rendered by a mail client. The apostrophe case is in here with it because it is
    /// the one that actually happens — "Women's" — and because a composer that escaped angle brackets by
    /// hand would get it wrong.
    /// </remarks>
    [Fact]
    public void A_product_name_holding_markup_is_escaped()
    {
        var email = Composer().OrderPlaced(Placed(productName: "<script>alert('x')</script> Shirt"));

        Assert.DoesNotContain("<script>", email.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", email.Html, StringComparison.Ordinal);

        // The text part is not HTML and must not be escaped: a shopper reading it should see the name as
        // it is, not "&lt;script&gt;".
        Assert.Contains("<script>", email.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An order with no first name greets the customer without inventing one.
    /// </summary>
    [Fact]
    public void A_nameless_order_is_greeted_without_a_made_up_name()
    {
        var email = Composer().OrderExpired(Header(firstName: null, lastName: null));

        Assert.Contains("Hello,", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello ,", email.Text, StringComparison.Ordinal);
    }

    private static IEnumerable<ComposedEmail> AllSix()
    {
        var composer = Composer();

        yield return composer.OrderPlaced(Placed());
        yield return composer.BankTransferInstructions(Header(), Instructions());
        yield return composer.PaymentConfirmed(
            new PaymentConfirmedEmailFacts(Header(), 12_430m, "INV-2026-000045", false));
        yield return composer.OrderShipped(new ShippedOrderEmailFacts(
            Header(), false, "Pronto Couriers", "PC-99881", "https://courier.example.lk/t?no=PC-99881",
            null, null, null));
        yield return composer.OrderExpired(Header());
        yield return composer.OrderCancelled(new CancelledOrderEmailFacts(Header(), true));
    }
}
