using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The starting copy of the <c>shipping</c> and <c>returns</c> system pages (GLB-10, issue #33).
/// <para>
/// <b>Why this repository now writes two of the six pages, when it wrote none.</b> The owner asked on 2026-10-21
/// for the shop's delivery and returns terms to be based on the best Sri Lankan apparel sites, in our own words,
/// published, and editable in admin; and the same day decided the shop delivers worldwide and takes payment
/// online through PayPal only. The research and sources are in
/// <c>docs/research/2026-10-21-sri-lanka-delivery-returns-couriers.md</c>; the defaults the owner may change are
/// listed in the #33 journal entry. About, Privacy and Terms stay empty: they wait for the client's words and a
/// PDPA review.
/// </para>
/// <para>
/// <b>No delivery fee and no Sri Lankan delivery day is written here.</b> Those live in
/// <c>sales.DeliveryMethodZoneRates</c>; the product page and checkout draw them from there, and the storefront
/// adds the live table under this page. Copy that quoted "Rs. 400" would contradict checkout the first time the
/// owner changed a rate. The figures that do appear — 14 days, 48 hours, 7 working days, the overseas range — are
/// policy rather than rates, and the owner edits them on the content page screen.
/// </para>
/// <para>
/// <b>Already in the sanitiser's output form</b>: only allow-listed elements, no attributes but <c>href</c>, and a
/// unit test sanitises each body and asserts it comes back unchanged — so the "is this still exactly the seed?"
/// comparison <see cref="HomeContentSeeder"/> makes cannot be thrown by a re-serialisation.
/// </para>
/// </summary>
public static class PolicyPageContent
{
    /// <summary>The copy version this build ships, recorded in <c>Seed.PolicyPagesVersion</c>.</summary>
    public const int SeedVersion = 1;

    /// <summary>The Shipping &amp; Delivery page.</summary>
    public const string ShippingBody =
        "<p>We deliver to every district in Sri Lanka and to customers overseas. Here is how it works.</p>"
        + "<h2>When we send your order</h2>"
        + "<p>We pack your order once your payment is confirmed, normally by the next working day. Orders "
        + "confirmed on a weekend or a public holiday go out on the next working day.</p>"
        + "<p>If an order is still waiting for payment when the deadline in your order email passes, it is "
        + "cancelled automatically and the items go back on sale.</p>"
        + "<h2>Delivery within Sri Lanka</h2>"
        + "<p>We deliver island-wide through trusted local couriers. The fee and the expected delivery time "
        + "depend on your district. The current fees, free-delivery amounts and delivery times for each area are "
        + "shown on every product page, and checkout shows the exact fee for your district before you pay.</p>"
        + "<p>Delivery times are counted in working days from dispatch and are our best estimate. Remote areas, "
        + "bad weather and public holidays can add a day or two.</p>"
        + "<h2>Delivery worldwide</h2>"
        + "<p>We also ship overseas with a tracked international service. The delivery fee for your country is "
        + "shown at checkout before you pay. Delivery usually takes between 5 and 15 working days after dispatch, "
        + "depending on the destination and on customs.</p>"
        + "<p>Customs duties, import taxes and any other charges your country makes on arrival are not included "
        + "in our prices or the delivery fee. They are the customer's responsibility and are paid when the "
        + "parcel arrives.</p>"
        + "<h2>Store pickup</h2>"
        + "<p>When store pickup is available, it appears as a delivery option at checkout with the store's "
        + "address and opening hours. We will let you know when your order is ready to collect; please bring "
        + "your order number.</p>"
        + "<h2>Tracking your parcel</h2>"
        + "<p>When your order leaves us, we email you the courier's name and your tracking number. You can also "
        + "find them on your order page. Where the courier offers online tracking, the number links straight to "
        + "it.</p>"
        + "<h2>Payment</h2>"
        + "<p>Prices are shown in Sri Lankan rupees (LKR). You pay securely online with the payment methods shown "
        + "at checkout; if your payment is taken in another currency, the amount is shown before you confirm. "
        + "Cash on delivery is not available.</p>"
        + "<h2>Receiving your parcel</h2>"
        + "<p>Please make sure someone can receive the parcel at the address and telephone number you gave us; "
        + "couriers usually call before they deliver. If a parcel comes back to us after failed delivery "
        + "attempts, we will contact you to arrange delivery again, and a second delivery fee may apply.</p>"
        + "<p>Before you accept a parcel, check that it has not been opened or damaged. If it has, tell the "
        + "courier and let us know within 48 hours (see Returns and exchanges).</p>"
        + "<h2>Wrong address?</h2>"
        + "<p>Contact us as soon as possible. We can change the delivery address until the order has been "
        + "dispatched.</p>"
        + "<h2>Questions</h2>"
        + "<p>Get in touch through our <a href=\"/contact\">Contact page</a> and we will be glad to help.</p>";

    /// <summary>The Returns &amp; Exchanges page.</summary>
    public const string ReturnsBody =
        "<p>We want you to love what you ordered. If something is not right, here is what to do.</p>"
        + "<h2>The short version</h2>"
        + "<ul>"
        + "<li>Return or exchange within <strong>14 days</strong> of delivery.</li>"
        + "<li>Items must be unworn and unwashed, with all their tags still attached.</li>"
        + "<li>Refunds go back to your original payment method.</li>"
        + "<li>Damaged, faulty or wrong item? Tell us within <strong>48 hours</strong> of delivery and we will "
        + "put it right at our cost.</li>"
        + "</ul>"
        + "<h2>What can be returned</h2>"
        + "<p>Most clothing, footwear and bags can be returned if they are:</p>"
        + "<ul>"
        + "<li>unworn, unwashed and unaltered, with no marks, make-up or scent;</li>"
        + "<li>in their original packaging, with every tag and label still attached;</li>"
        + "<li>sent back with your order number.</li>"
        + "</ul>"
        + "<p>We check every return when it arrives. If an item does not meet these conditions, we will let you "
        + "know and send it back to you.</p>"
        + "<h2>What cannot be returned</h2>"
        + "<p>For hygiene reasons, or because they were sold at a reduced price, we cannot take back:</p>"
        + "<ul>"
        + "<li>innerwear and lingerie;</li>"
        + "<li>swimwear;</li>"
        + "<li>socks and hosiery;</li>"
        + "<li>pierced jewellery;</li>"
        + "<li>items marked Sale or Final sale.</li>"
        + "</ul>"
        + "<p>This does not apply if the item arrived damaged, faulty or wrong; see below.</p>"
        + "<h2>Exchanges, including a different size</h2>"
        + "<p>Not the right fit? We will exchange it for another size or colour of the same item if we have it "
        + "in stock; if we do not, we will refund it instead. If you would like a different product, return the "
        + "first one for a refund and place a new order.</p>"
        + "<p>If the item was faulty, we send the replacement free of charge. For a size or change-of-mind "
        + "exchange, the replacement is sent at the standard delivery fee for your address.</p>"
        + "<h2>How to return or exchange</h2>"
        + "<ol>"
        + "<li>Contact us within 14 days of delivery through our <a href=\"/contact\">Contact page</a>, with "
        + "your order number, the item, and whether you would like an exchange or a refund.</li>"
        + "<li>We will reply with the return address and instructions. Please do not send anything back before "
        + "we confirm.</li>"
        + "<li>Pack the item securely and send it with a tracked service of your choice. Keep the receipt or "
        + "tracking number until your return is complete.</li>"
        + "</ol>"
        + "<h2>Who pays for return postage</h2>"
        + "<ul>"
        + "<li><strong>Change of mind or size exchange:</strong> you pay to send the item back, from within Sri "
        + "Lanka or from overseas.</li>"
        + "<li><strong>Damaged, faulty or wrong item:</strong> we cover it. We will arrange collection or refund "
        + "your return postage.</li>"
        + "</ul>"
        + "<p>For a return from overseas, please mark the parcel as returned goods. Customs duties and taxes, on "
        + "the original delivery or on the return, are the customer's responsibility and are not refunded.</p>"
        + "<h2>Refunds</h2>"
        + "<p>Once your return reaches us and passes our check, we refund you to your original payment method "
        + "within 7 working days and email you when it is done. Your bank or payment provider may take a few more "
        + "days to show it.</p>"
        + "<ul>"
        + "<li>You get back the price you paid for the item.</li>"
        + "<li>The original delivery fee is refunded only when the item was damaged, faulty or wrong.</li>"
        + "</ul>"
        + "<h2>Damaged, faulty or wrong items</h2>"
        + "<p>We are sorry if this happens. Please tell us within 48 hours of delivery and send your order number "
        + "with clear photos of the item, its tags and the parcel. We will send a replacement or give you a full "
        + "refund including delivery, and cover the cost of getting the item back.</p>"
        + "<h2>Cancelling an order</h2>"
        + "<p>You can cancel any time before we dispatch your order. If you have already paid, we refund you in "
        + "full to your original payment method.</p>"
        + "<h2>Questions</h2>"
        + "<p>Get in touch through our <a href=\"/contact\">Contact page</a>. We are happy to help.</p>";

    /// <summary>
    /// What each page may hold and still count as untouched, by system key: empty, or exactly an earlier seed's
    /// body. Version 1 has no earlier seed; a version 2 adds the version 1 body here, so a page nobody edited
    /// moves to the new copy and a page somebody did edit keeps theirs.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PreviousSeeds { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ContentPageKeys.Shipping] = [],
            [ContentPageKeys.Returns] = [],
        };

    /// <summary>The current body for each page this seeds, by system key.</summary>
    public static IReadOnlyDictionary<string, string> Bodies { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ContentPageKeys.Shipping] = ShippingBody,
            [ContentPageKeys.Returns] = ReturnsBody,
        };
}
