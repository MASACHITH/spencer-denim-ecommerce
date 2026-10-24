using SpencerDenim.Domain.Branding;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The starting copy of five of the six system pages: <c>shipping</c> and <c>returns</c> (GLB-10, issue #33) and,
/// since version 2, <c>about</c>, <c>privacy</c> and <c>terms</c> (issue #252).
/// <para>
/// <b>Why this repository writes five of the six pages, when it wrote none.</b> The owner asked on 2026-10-21
/// for the shop's delivery and returns terms to be based on the best Sri Lankan apparel sites, in our own words,
/// published, and editable in admin; and the same day decided the shop delivers worldwide and takes payment
/// online through PayPal only. The research and sources are in
/// <c>docs/research/2026-10-21-sri-lanka-delivery-returns-couriers.md</c>; the defaults the owner may change are
/// listed in the #33 journal entry.
/// </para>
/// <para>
/// <b>About, Privacy and Terms are drafts, written on the owner's instruction of 2026-10-24 (#252).</b> Until then
/// they waited for the client's words and a PDPA review. They are now seeded so that UAT, and a first deployment,
/// have complete pages that fit a shop delivering in Sri Lanka and overseas. They are <b>plain-language drafts for the
/// owner and a lawyer to review before the shop goes live</b>, and the owner edits them on the content page screen.
/// Every factual sentence in them is backed by the code or a document; the table that says which is in the #252
/// journal entry. Where a sentence is a promise about how the shop behaves rather than a fact about the system, that
/// journal entry lists it for the owner to confirm. The shop's name comes from <see cref="StoreBrand.Name"/>, and
/// nothing here spells it out.
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
    public const int SeedVersion = 2;

    /// <summary>
    /// The version in which each page's copy was first offered, by system key. <b>A page is offered once, at that
    /// version</b>: <see cref="HomeContentSeeder"/> considers it only while the database's recorded version is below
    /// this number. That is what stops a bump of <see cref="SeedVersion"/> from re-offering an earlier page: an admin
    /// who emptied Shipping or Returns after version 1 would otherwise have it filled and published again by version 2.
    /// </summary>
    public static IReadOnlyDictionary<string, int> IntroducedIn { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [ContentPageKeys.Shipping] = 1,
            [ContentPageKeys.Returns] = 1,
            [ContentPageKeys.About] = 2,
            [ContentPageKeys.Privacy] = 2,
            [ContentPageKeys.Terms] = 2,
        };

    /// <summary>
    /// The company behind the shop, named once so the three pages cannot disagree about it. It is the client named
    /// in docs/01 ("Spencer Denim Industries Pvt Ltd. Sri Lankan family fashion retailer"). <b>The exact registered
    /// name and address are unconfirmed</b> (the <c>Store.LegalName</c> setting is unset), which is why this is the
    /// first item the owner is asked to check.
    /// </summary>
    private const string CompanyName = "Spencer Denim Industries Pvt Ltd";

    /// <summary>
    /// A paragraph holding only this text marks where the storefront draws the live Sri Lankan fee list on the
    /// Shipping page (under "Delivery within Sri Lanka"). The owner can move or delete the line; without it the
    /// list is drawn after the copy. It never reaches a shopper as text: the storefront replaces it.
    /// </summary>
    public const string DeliveryFeesMarker = "{{delivery fees}}";

    /// <summary>The Shipping &amp; Delivery page.</summary>
    public const string ShippingBody =
        "<p>We deliver to every district in Sri Lanka and to customers overseas. Here is how it works.</p>"
        + "<h2>When we send your order</h2>"
        + "<p>We pack your order once your payment is confirmed, normally by the next working day. Orders "
        + "confirmed on a weekend or a public holiday go out on the next working day.</p>"
        + "<p>If an order is still waiting for payment when the deadline in your order email passes, it is "
        + "cancelled automatically and the items go back on sale.</p>"
        + "<h2>Delivery within Sri Lanka</h2>"
        + "<p>We deliver to every district through trusted local couriers. The fee and the expected delivery time "
        + "depend on your district; checkout shows the exact fee for your district before you pay.</p>"
        + "<p>Delivery times are counted in working days from dispatch and are our best estimate. Remote areas, "
        + "bad weather and public holidays can add a day or two.</p>"
        + "<p>" + DeliveryFeesMarker + "</p>"
        + "<h2>Delivery worldwide</h2>"
        + "<p>We also ship overseas with a tracked international service. The delivery fee for your country is "
        + "shown at checkout before you pay. Delivery usually takes between 5 and 15 working days after dispatch, "
        + "depending on the destination and on customs.</p>"
        + "<p>Customs duties, import taxes and any other charges your country makes on arrival are not included "
        + "in our prices or the delivery fee. They are the customer's responsibility and are paid when the "
        + "parcel arrives.</p>"
        + "<h2>Store pickup</h2>"
        + "<p>When store pickup is available, it appears as a delivery option at checkout with the store's "
        + "address and opening hours. We will contact you about collecting your order; please bring your order "
        + "number.</p>"
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
        + "courier and let us know within 48 hours of delivery (see Returns and exchanges).</p>"
        + "<h2>Wrong address?</h2>"
        + "<p>Contact us as soon as possible. We can change the delivery address until the order has been "
        + "dispatched.</p>"
        + "<h2>Questions</h2>"
        + "<p>Contact us through the Contact page, linked at the foot of every page, and we will be glad to "
        + "help.</p>";

    /// <summary>The Returns &amp; Exchanges page.</summary>
    public const string ReturnsBody =
        "<p>We want you to love what you ordered. If something is not right, here is what to do.</p>"
        + "<h2>The short version</h2>"
        + "<ul>"
        + "<li>Return or exchange within <strong>14 days</strong> of delivery.</li>"
        + "<li>Items must be unworn and unwashed, with all their tags still attached.</li>"
        + "<li>Refunds go back to your original payment method.</li>"
        + "<li>Damaged in transit or the wrong item? Tell us within <strong>48 hours</strong> of delivery. A "
        + "fault found later? Tell us as soon as you notice it. Either way we put it right at our cost.</li>"
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
        + "<li>sale items.</li>"
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
        + "<li>Contact us within 14 days of delivery through the Contact page, linked at the foot of every page, "
        + "with your order number, the item, and whether you would like an exchange or a refund.</li>"
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
        + "within 7 working days and contact you to confirm it. Your bank or payment provider may take a few more "
        + "days to show it.</p>"
        + "<ul>"
        + "<li>You get back the price you paid for the item.</li>"
        + "<li>The original delivery fee is refunded only when the item was damaged, faulty or wrong.</li>"
        + "</ul>"
        + "<h2>Damaged, faulty or wrong items</h2>"
        + "<p>We are sorry if this happens. If an item arrives damaged in transit, or is not what you ordered, "
        + "please tell us within 48 hours of delivery. If you find a fault later, tell us as soon as you notice "
        + "it. Send your order number with clear photos of the item, its tags and the parcel. We will send a "
        + "replacement or give you a full refund including delivery, and cover the cost of getting the item "
        + "back.</p>"
        + "<h2>Cancelling an order</h2>"
        + "<p>You can cancel any time before we dispatch your order. If you have already paid, we refund you in "
        + "full to your original payment method.</p>"
        + "<h2>Questions</h2>"
        + "<p>Contact us through the Contact page, linked at the foot of every page. We are happy to help.</p>";

    /// <summary>The About us page (issue #252). The #252 journal entry traces every sentence, rows A1 to A12.</summary>
    public const string AboutBody =
        $"<p>{StoreBrand.Name} is the online shop of {CompanyName}, a Sri Lankan fashion retailer for women, men and "
        + "kids. We sell clothing, footwear and accessories, and we deliver across Sri Lanka and to customers "
        + "overseas.</p>"
        + "<h2>What you will find here</h2>"
        + "<p>The shop is organised into Women, Men and Kids, with New Arrivals and Sale pages to show you what has "
        + "just come in and what is reduced. Each product page shows photographs, the colours and sizes with any that "
        + "are sold out marked and, where we have written one, a size guide. Where customers have reviewed a product "
        + "you can read what they said, and a "
        + "Verified buyer mark means the review comes from someone whose order we delivered.</p>"
        + "<h2>Shopping with us</h2>"
        + "<ul>"
        + "<li>Save your favourites to a wishlist without signing in. The list is kept in your own browser.</li>"
        + "<li>Check out as a guest, or create an account to keep your addresses and follow your orders.</li>"
        + "<li>Pay securely online with PayPal.</li>"
        + "<li>Follow your parcel with the courier's tracking number, which appears on your order page once your "
        + "order has been sent.</li>"
        + "</ul>"
        + "<h2>Delivery and returns</h2>"
        + "<p>We deliver to every district in Sri Lanka and ship overseas with a tracked international service. If "
        + "something is not right, you can return or exchange most items. The details are on our Delivery and "
        + "shipping page and our Returns and exchanges page, both linked at the foot of every page.</p>"
        + "<h2>Get in touch</h2>"
        + "<p>Questions about an order, a size or anything else? Write to us through the Contact page, also linked "
        + "at the foot of every page, and our team will reply.</p>";

    /// <summary>
    /// The Privacy policy page (issue #252). Written for the Sri Lankan Personal Data Protection Act, No. 9 of 2022
    /// (named at the owner's instruction: a lawyer confirms it), and for overseas shoppers by saying that the same
    /// practices apply to everyone. The #252 journal entry traces every sentence, rows P1 to P34, and lists the
    /// sentences that are promises about how the shop works for the owner to confirm.
    /// </summary>
    public const string PrivacyBody =
        $"<p>{StoreBrand.Name} is the online shop of {CompanyName} (we and us). This policy explains what personal "
        + "data we collect, why, who receives it, how long we keep it and what choices you have. We handle your "
        + "personal data in line with Sri Lanka's Personal Data Protection Act, "
        + "No. 9 of 2022, and we apply the same practices to every customer, whether you live in Sri Lanka or "
        + "overseas.</p>"
        + "<h2>What we collect and why</h2>"
        + "<ul>"
        + "<li><strong>Orders.</strong> Your name, email address, telephone number and delivery address (for an "
        + "overseas order also your country and, where your country uses one, your postcode), the items you bought, "
        + "the delivery method, any note you add, whether the order has been paid and the IP address the order was "
        + "placed from. If you upload a bank transfer payment slip, we keep that too. We use this to take "
        + "payment, deliver your order, send you updates and deal with returns and questions.</li>"
        + "<li><strong>Your account (optional).</strong> Your name, email address and password (stored in scrambled "
        + "form, a hash, never as readable text), the addresses you save and your order history. We use it to let you "
        + "sign in, check out faster and follow your orders. If an account is locked after repeated failed sign-ins, "
        + "we record that with the IP address and browser details.</li>"
        + "<li><strong>Newsletter.</strong> Your email address, the time you signed up and the IP address you signed "
        + "up from. We keep the time and the IP address as proof that you agreed.</li>"
        + "<li><strong>Questions you send us.</strong> The name, email address, optional telephone number, subject, "
        + "message and order number that you give us, and the IP address it was sent from, so that we can answer "
        + "you.</li>"
        + "<li><strong>Reviews.</strong> A review is shown with your first name and the first letter of your last "
        + "name (for example Nimal P.), and with a Verified buyer mark when we have delivered an order to you. We "
        + "never show your email address.</li>"
        + "<li><strong>Visits to our website.</strong> We count page views to see what is popular. A record holds the "
        + "page address (with anything private removed), the time, the kind of device, how you arrived and a random "
        + "visit number, which is replaced after 30 minutes without activity. It holds no IP address, name or email "
        + "address, so we cannot follow you from one visit to the next.</li>"
        + "</ul>"
        + "<h2>Cookies and your browser</h2>"
        + "<p>We use a few cookies, all set by us and none for advertising, and we load no third-party analytics or "
        + "tracking scripts on our pages.</p>"
        + "<ul>"
        + "<li><strong>sd_cart</strong> keeps your shopping bag if you are not signed in. It holds a random code and "
        + "lasts 30 days from your last visit.</li>"
        + "<li><strong>sd_customer_auth</strong> keeps you signed in if you have an account, for up to 30 days.</li>"
        + "<li><strong>sd_vs</strong> holds the random visit number described above and expires after 30 minutes "
        + "without activity.</li>"
        + "</ul>"
        + "<p>Your wishlist, recently viewed products, recent searches and chosen delivery country and district are "
        + "saved in your own browser, and you can clear them in your browser settings. A map on our Contact page, if "
        + "shown, comes from Google Maps or OpenStreetMap, which may receive your IP address. PayPal sets its own "
        + "cookies on its own pages.</p>"
        + "<h2>Who receives your data</h2>"
        + "<ul>"
        + "<li><strong>PayPal.</strong> When you pay with PayPal we send it your order total and the recipient's "
        + "name and delivery address so that it can process the payment. We never see or store your card details. "
        + "PayPal's own privacy policy applies to what it holds, and it may process data outside Sri Lanka.</li>"
        + "<li><strong>Couriers.</strong> We give the courier that delivers your parcel your name, delivery address "
        + "and telephone number.</li>"
        + "<li><strong>Our email service.</strong> It sends the order and account emails we address to you.</li>"
        + "<li><strong>Our staff.</strong> They use separate sign-ins and see your data only to do their work.</li>"
        + "</ul>"
        + "<p>We do not sell your personal data. We may disclose it where the law requires us to.</p>"
        + "<h2>How long we keep your data</h2>"
        + "<ul>"
        + "<li>Visit records are removed automatically after a set period (90 days as standard).</li>"
        + "<li>Order and invoice records are kept for as long as we need them for accounting, tax and legal "
        + "reasons.</li>"
        + "</ul>"
        + "<p>For everything else we keep personal data only as long as the purpose it was collected for needs, and "
        + "we deal with each request to delete or anonymise it individually. Deleted data may remain in our backups "
        + "for up to 35 days.</p>"
        + "<h2>Keeping your data safe</h2>"
        + "<p>Our website uses an encrypted (HTTPS) connection. No system is completely secure, so if a breach "
        + "affects you we will tell you, and the authorities, as the law requires.</p>"
        + "<h2>Your rights</h2>"
        + "<p>Under the Personal Data Protection Act you may ask what personal data we hold about you and receive a "
        + "copy, ask us to correct data that is wrong, ask us to delete or stop using your data in the cases the Act "
        + "provides for, withdraw a consent you gave (for example for the newsletter) at any time, and complain to "
        + "the Data Protection Authority of Sri Lanka if you think we have handled your data wrongly.</p>"
        + "<p>To use any of these rights, or to ask anything about this policy, write to us through the Contact page "
        + "(linked at the foot of every page) with your name, the email address on your order or account and what you "
        + "would like us to do. We will reply as soon as we reasonably can, and within the time the Act requires. We "
        + "may ask you to confirm who you are first.</p>"
        + "<p>If you live outside Sri Lanka, the same rights apply to you, and you use them in the same way.</p>"
        + "<h2>Changes to this policy</h2>"
        + "<p>We may update this policy. The date it was last changed is shown at the top of this page.</p>";

    /// <summary>
    /// The Terms and conditions page (issue #252). It states bank transfer as a payment for Sri Lankan deliveries
    /// only, which is the rule of issue #253. The #252 journal entry traces every sentence, rows T1 to T36, and lists
    /// the sentences that are promises for the owner to confirm.
    /// </summary>
    public const string TermsBody =
        $"<p>These terms apply to every order you place on {StoreBrand.Name}, the online shop of {CompanyName} (in "
        + "these terms, we and us). By placing an order you agree to them. They work together with our Delivery and "
        + "shipping page, our Returns and exchanges page and our Privacy policy, all linked at the foot of every "
        + "page.</p>"
        + "<h2>Placing an order</h2>"
        + "<p>When you place an order we hold your items while we wait for your payment. Your order is confirmed "
        + "when your payment has been received. If payment does not arrive by the deadline shown on your order "
        + "page, the order is cancelled automatically and the items go back on sale. You can also cancel an unpaid "
        + "order yourself from your order page.</p>"
        + "<p>If we cannot supply an item after you have paid for it, we will tell you and refund what you paid for "
        + "that item.</p>"
        + "<h2>Prices</h2>"
        + "<p>Prices are shown in Sri Lankan rupees (LKR) and are the prices on the website when you place your "
        + "order. The delivery fee is shown at checkout before you pay. We may change prices at any time, but a "
        + "change does not affect an order you have already placed. Customs duties and import taxes that an overseas "
        + "delivery attracts are not included in our prices and are your responsibility.</p>"
        + "<h2>Payment</h2>"
        + "<p>PayPal is available for every order and is the way to pay for a delivery outside Sri Lanka. For a "
        + "delivery within Sri Lanka we may also offer payment by bank transfer. The payment methods open to you are "
        + "shown at checkout.</p>"
        + "<p>PayPal takes payments in US dollars. When you pay with PayPal we convert your total from rupees to US "
        + "dollars at an exchange rate that we set, which includes a small allowance for currency movement, and "
        + "PayPal shows you the amount in US dollars before you approve the payment. Your bank or card issuer may add "
        + "its own currency conversion charges. We never see or store your card details.</p>"
        + "<p>If you pay by bank transfer, follow the instructions on your order page and pay by the deadline shown "
        + "there. We confirm your order once we have checked your payment.</p>"
        + "<h2>Delivery</h2>"
        + "<p>We deliver across Sri Lanka and to customers overseas. Delivery fees, estimated delivery times and the "
        + "countries we deliver to are shown at checkout and on our Delivery and shipping page. If we cannot deliver "
        + "to your country, checkout tells you before you pay. Delivery times are our best estimate and not a "
        + "promise, and we are not responsible for delays caused by couriers, customs or events outside our "
        + "control.</p>"
        + "<h2>Returns and refunds</h2>"
        + "<p>You can return or exchange most items within the time set out on our Returns and exchanges page, and "
        + "we replace or refund anything that arrives damaged, faulty or wrong. That page gives the full rules, "
        + "including what cannot be returned. Nothing in these terms takes away your legal rights as a "
        + "consumer.</p>"
        + "<h2>Your account</h2>"
        + "<p>You do not need an account to buy from us. If you create one, keep your password private and tell us "
        + "straight away if you think someone else is using your account. You are responsible for what happens "
        + "under your account until you tell us. We may suspend an account that is used to abuse the shop.</p>"
        + "<h2>Product information</h2>"
        + "<p>We take care to describe our products, colours and sizes accurately. Colours can look different on "
        + "different screens and sizes can vary between styles, so please check the size guide on the product "
        + "page where there is one.</p>"
        + "<h2>Reviews</h2>"
        + "<p>A review is the opinion of the customer who wrote it. By submitting one you confirm that it is honest "
        + "and your own, and you allow us to show it on the shop with your first name and the first letter of your "
        + "last name. We may remove a review that is offensive, is not about the product or includes personal "
        + "details.</p>"
        + "<h2>Our content</h2>"
        + "<p>The photographs, text, logo and designs on this website belong to us or are used with permission. "
        + "Please do not copy or reuse them without our written permission.</p>"
        + "<h2>Our responsibility to you</h2>"
        + "<p>Nothing in these terms limits any liability that the law does not allow us to limit. Otherwise, as far "
        + "as the law allows, we are not liable for indirect or consequential loss, or for loss caused by events "
        + "outside our reasonable control.</p>"
        + "<h2>Changes and governing law</h2>"
        + "<p>We may update these terms from time to time. The version on this page when you place an order applies "
        + "to that order. These terms are governed by the laws of Sri Lanka, and the courts of Sri Lanka have "
        + "jurisdiction over any dispute about them.</p>"
        + "<h2>Contact</h2>"
        + "<p>If you have a question about these terms or about an order, write to us through the Contact page, "
        + "linked at the foot of every page.</p>";

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
            [ContentPageKeys.About] = [],
            [ContentPageKeys.Privacy] = [],
            [ContentPageKeys.Terms] = [],
        };

    /// <summary>The current body for each page this seeds, by system key.</summary>
    public static IReadOnlyDictionary<string, string> Bodies { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ContentPageKeys.Shipping] = ShippingBody,
            [ContentPageKeys.Returns] = ReturnsBody,
            [ContentPageKeys.About] = AboutBody,
            [ContentPageKeys.Privacy] = PrivacyBody,
            [ContentPageKeys.Terms] = TermsBody,
        };
}
