# Research: Sri Lankan delivery and returns practice, and couriers (2026-10-21)

For: PDP-05 (#33) product-page delivery and returns info, the GLB-10 `shipping` and `returns` content pages, and the `sales.Couriers` seed (ADM-22).
Status: **research and drafts only.** No code was changed. Every policy draft below is for the owner to approve. GLB-10 says these pages carry the client's words, so nothing here should go live unapproved.

All web facts were checked on 2026-10-21. Retailers' policies change without notice, so re-check before you quote any of them to the client.

---

## 1. Project constraints found in the codebase

| Item | What the code says | Where |
|---|---|---|
| Tracking placeholder token | **`{trackingNumber}`** (case-sensitive, braces included), `Courier.TrackingNumberPlaceholder` | `src/backend/SpencerDenim.Domain/Sales/Courier.cs` |
| DB check | `CK_Couriers_TrackingUrlTemplate`: `[TrackingUrlTemplate] IS NULL OR CHARINDEX('{trackingNumber}', ...) > 0` | `Infrastructure/Persistence/Configurations/Sales/CourierConfiguration.cs` |
| Column sizes | `Name` nvarchar(80), unique (`UX_Couriers_Name`). `TrackingUrlTemplate` nvarchar(500), nullable. `Phone` **varchar(20)**, printable ASCII only. `IsActive` defaults to true. `SortOrder` smallint | same |
| Validator | The template must contain `{trackingNumber}` **and must be an absolute `https://` URL**. A blank template means NULL: no online tracking, so the number is shown as text | `Application/Sales/Orders/Admin/SaveCourierCommand.cs` |
| **Recorded rule against seeding couriers** | The handler's remarks say: *"Nothing here invents a courier name or a tracking URL. Not a seeded row, not a default..."* The reason given is W3-03's two Major findings, where invented client data was shown as if real. **A courier seed reverses that decision. The owner must confirm which couriers the shop actually uses before any row is seeded** (see §6). | `SaveCourierCommand.cs` |
| PDP setting keys | `Content.PdpReturnsInfoHtml` and `Content.PdpDeliveryInfoHtml` are *deferred, AwaitingItsFeature*. Nothing reads them yet. `IHtmlSanitiser` (W2-11) now exists, so the only blocker is the PDP panel itself | `Domain/Settings/DeferredSettingKeys.cs` |
| Delivery methods | `STANDARD` "Standard delivery" (enabled), `EXPRESS` "Express delivery" (enabled), `PICKUP` "Store pickup" (**disabled**). Pickup is disabled because no `PickupLocation` is seeded ("client input, D8"). It switches on once a location exists and the method is enabled | `Infrastructure/Persistence/Seeding/SalesReferenceData.cs` |
| Zones | Colombo = Colombo district. Western = Gampaha and Kalutara. IslandWide = the other 22 districts | same |
| Zone rates (seeded placeholders, "client to confirm (D8)") | see the table below | same |
| Pickup locations | `sales.PickupLocations` exists (Name, AddressLine, City, DistrictId, Phone, OpeningHours, IsActive). **Zero rows are seeded.** Pickup uses the location's district zone (R7) | `Domain/Sales/PickupLocation.cs` |
| Content pages | System pages with slugs `returns` and `shipping` (`ContentPageKeys`) are seeded and unpublished, "waiting for the client's words" | `Domain/Content/ContentPage.cs` |
| Scope | PDP-05: "Delivery estimate + delivery methods ... returns info". GLB-10: Returns and Shipping pages. ADM-22: "couriers + tracking URL templates". **Courier API integrations are out of scope.** COD is out of scope (#55) | `docs/02-scope-baseline.md` |
| Issue #33 | Show the estimate and methods **from the delivery settings for the shopper's district**, and link returns to the real page. Moved to Sachith on 2026-10-21 | GitHub #33 |

Seeded zone rates (fee in Rs., free-delivery threshold, estimated days):

| Method | Colombo | Western (Gampaha, Kalutara) | Island-wide |
|---|---|---|---|
| Standard | 400, free over 10,000, 1–2 days | 450, free over 10,000, 2–3 days | 600, free over 15,000, 3–5 days |
| Express | 950, 1 day | 1,200, 1–2 days | not offered |
| Pickup (disabled) | 0, 1–2 days | 0, 1–2 days | 0, 1–2 days |

**Consequence for the drafts:** fees and days already live in `DeliveryMethodZoneRates`. The PDP text and the Shipping page should **render them from that table, or point to checkout, rather than hard-code numbers.** Where a draft below shows a number, it is in `[default: ...]` brackets and matches the seeded placeholder, so the page and checkout cannot contradict each other.

---

## 2. Sri Lankan apparel retailers: what their own policies say

The site was the source for every row. "n/s" means the policy page does not state it.

### 2.1 Per-retailer findings

**ODEL** (Softlogic). Sources: https://odel.lk/faq and https://odel.lk/terms-and-conditions
- Delivery fee: Rs. 360 within Colombo, Rs. 450 outside Colombo. No free-delivery threshold is stated.
- Delivery time: the pages contradict each other. "3-5 working days" appears in the T&C *Method of Delivery* section; "6-8 working days" appears in the T&C *Delivery & Shipping Information* section and in the FAQ.
- COD: yes, but only in a listed set of Colombo and suburban areas, with a Rs. 30,000 maximum.
- Returns: exchange only, **within 30 days of sale**, with the invoice. Fully resalable, with all tags (price tags, wash tags, brand labels), not used, damaged or washed.
- Refund: **no cash or cheque refunds.** A credit note is issued, valid for 14 days.
- Non-returnable: swimwear, lingerie, hosiery, accessories, pierced earrings, millinery, beauty, special orders and more.
- Faulty goods: ODEL arranges and pays for courier pickup. The delivery charge is refunded for incorrect, damaged or faulty goods. No reporting window is stated.

**Fashion Bug.** Source: https://fashionbug.lk/pages/return-policy (COD from the footer of https://fashionbug.lk/)
- Fees and times: n/s on the policy pages. There is no shipping policy page (`/policies/shipping-policy` returns 404).
- COD: the footer advertises "Cash On Delivery" and "Island Wide Delivery".
- Returns: change of mind **within 7 working days** of receipt. Original packaging, unused, resalable. Exchanges at an outlet.
- Return shipping: free courier collection for incorrect, damaged or faulty items. Change of mind is at the customer's cost.
- Refund: **to a bank account within 7 working days** of the return being accepted. A representative asks for the bank details. This is the closest match to Colombo Wardrobe's manual bank-transfer refund.
- Damage reporting: by email or WhatsApp with the order number. No time window is stated.
- Cancellation: within 24 hours.

**Cool Planet.** Sources: https://coolplanet.lk/policies/shipping-policy and https://coolplanet.lk/policies/refund-policy
- Fee: **Rs. 425 flat, island-wide.** No threshold is stated.
- Time: within **2 working days**. Weekends and holidays roll to the next working day.
- COD: yes, maximum LKR 20,000.
- Returns: request **within 7 days** of receipt. Unworn or unused, with tags, in original packaging, with proof of purchase. A return request is required first.
- Non-returnable: swimwear, lingerie, hosiery, accessories, pierced earrings, beauty, sale or discounted items, gift cards and more.
- Exchange: in store, or online by returning the item and placing a new order.
- Refund: to the original payment method **within 14 business days** of approval.
- **Damage complaints within 48 hours** of receipt.

**NOLIMIT.** Sources: http://www.nolimit.lk/information/delivery-details, http://www.nolimit.lk/information/return-policy and http://www.nolimit.lk/information/faq
- Fee: calculated at checkout.
- Time: Colombo and suburbs **2–3 days**; outside Colombo **3–5 days**. The FAQ says 2–5 business days. Sunday orders go out on Monday.
- COD: "available throughout the island" (FAQ).
- Returns: **14 days** from receipt (the FAQ says from the purchase date). Original condition and packaging, with the receipt and NOLIMIT Online tags.
- Sale and promotional items are final sale.
- Refund: **store credit**, for the value of the goods only (not shipping).
- Return shipping: the customer pays for change of mind. The wording implies NOLIMIT pays for damaged or wrong items.
- Cancellation: within 12 hours.

**Spring & Summer.** Sources: https://springandsummer.lk/pages/delivery-shipping and https://springandsummer.lk/
- Fee: Colombo suburbs LKR 350, outstation LKR 400. Colombo 1–15 free over LKR 3,500. The site banner says "Free Shipping on All Orders Above Rs. 5,000.00", which the page itself contradicts.
- Time: 2–3 working days.
- COD: n/s.
- Exchange: online **7 days**, in store **14 days**. Unused, price tag attached, with the invoice.
- **No cash refunds**: exchange value only.
- Sale and occasion items are excluded.
- Return shipping: the customer pays for a courier exchange.
- Defects or wrong items: report **within 14 days with photos** by email or phone. The shop arranges pickup.

**Kelly Felder.** Sources: https://www.kellyfelder.com/policies/refund-policy, https://www.kellyfelder.com/pages/shipping-returns, https://www.kellyfelder.com/ and https://kellyfelder.com/pages/track-your-order
- Fee: n/s. Banner: "FREE SHIPPING ON ORDERS ABOVE 12,000".
- Time and COD: n/s.
- Returns: the refund policy says 15 days; the shipping-returns page says **14 days** for store returns. Unworn, unwashed, unaltered, tags intact.
- Sale items are excluded.
- Refund: **store credit** within 7 working days of inspection.
- Return shipping: the courier (Fardar) pickup charge of **Rs. 300–400** is paid by the customer.
- Uses **Citypak** tracking (links to track.citypak.lk).

**House of Fashions.** Sources: https://houseoffashions.lk/pages/shipping-policy, https://houseoffashions.lk/pages/return-exchange-policy and https://www.houseoffashions.lk/
- Fee: by area, at checkout. Parcels over 1 kg or 30×30×6 cm cost extra.
- Time: island-wide in **3 to 5 working days**.
- Exchange: **7 days from purchase**. Replacement only if the item is defective or damaged.
- Non-returnable: lingerie and undergarments, sarees and nighties, handbags, belts and wallets, infant items, sale items and more.
- The customer pays return shipping, and it is not refundable.

**Not usable:**
- **Carnage** (carnage.lk) and **Avirate** (avirate.com): both sites served an untrusted or self-signed TLS certificate to every tool tried (web fetch, curl and a headless browser). Their policies could not be read.
- **Mimosa**: no Sri Lankan online shop with a policy page was found.

None of these three appears in the summary.

### 2.2 Common practice and range

| Point | Range seen | Most common / middle |
|---|---|---|
| Delivery fee | Rs. 350–600. Colombo Rs. 360 (ODEL); outstation Rs. 400–450 (S&S, ODEL); flat Rs. 425 (Cool Planet) | About **Rs. 350–400 Colombo and Rs. 450 outstation**. The project seed of 400 / 450 / 600 sits within this range; 600 for far districts is at the top |
| Free delivery threshold | Rs. 3,500 (S&S Colombo), Rs. 5,000 (S&S banner), Rs. 12,000 (Kelly Felder); none at ODEL or Cool Planet | Mixed. The seeded 10,000 / 15,000 is mid-to-high |
| Delivery time | 2 working days (Cool Planet) to 6–8 (ODEL); NOLIMIT 2–3 Colombo, 3–5 outstation | **Colombo 1–3 working days, outstation 3–5 working days** |
| COD | Offered by ODEL, Cool Planet, NOLIMIT and Fashion Bug | Usual in Sri Lanka. **Colombo Wardrobe does not offer it (#55)**, so the pages must say so plainly |
| Return window | 7 days (Fashion Bug, Cool Planet, S&S online, House of Fashions), 14 (NOLIMIT, S&S store, Kelly Felder), 30 (ODEL) | **7–14 days**. 14 days is the generous middle |
| Condition | Unworn or unused, unwashed, tags attached, original packaging, invoice or proof of order | Universal |
| Non-returnable | Innerwear and lingerie, swimwear, hosiery and socks, accessories, pierced earrings, cosmetics; **sale items** at 5 of 7 | Innerwear, swimwear, socks, earrings and sale items |
| Exchange vs refund | Exchange or store credit only at ODEL, NOLIMIT, S&S and Kelly Felder; money back at Fashion Bug (bank) and Cool Planet (original method) | Exchange-first is the local norm. Money-back refunds are offered by about a third |
| Return shipping | The customer pays for change of mind at every site that states it. The shop pays for faulty or wrong items | Same |
| Refund timing | 7 working days (Fashion Bug, Kelly Felder credit) to 14 business days (Cool Planet) | **About 7 working days after the item is checked** |
| Damaged or wrong item | 48 hours (Cool Planet), 14 days with photos (S&S); others give no window | **Report within 48 hours with photos** is the clearest rule |
| Size exchange | No retailer has a separate rule. It falls under the general exchange, subject to stock | Exchange for another size if in stock, within the window |

---

## 3. Couriers: verification

Method: each official site was visited. The tracking form was inspected (field names, GET or POST, form action) and a dummy number `12345678` was submitted, to see whether the number travels in the URL and whether the page reads it back. "Verified" means it was seen on the courier's own site on 2026-10-21.

| Courier | Official name (as shown on site) | Website | Customer phone | Public tracking | **Tracking URL template** | Coverage / notes |
|---|---|---|---|---|---|---|
| **Domex** | "Domex". Social links say Domestic Express Pvt Ltd (name **unverified** as the legal name) | https://www.domex.lk/ | **011 7 759 759** (24-hour hotline), verified on the home page and the Order-Details page. Pick-up desk 011 7 759 779 | Yes. A GET form posts `wbno` to `/Order-Details.php` | **`https://www.domex.lk/Order-Details.php?wbno={trackingNumber}`**. Verified: the page renders "Waybill No: 12345678" and the tracking table from the query string | "We Deliver Islandwide"; branch list covers all 25 districts. **Verified** |
| **Koombiyo Delivery** | Koombiyo Delivery Pvt Ltd | https://koombiyodelivery.lk/ | **011 7886786**, verified (footer and contact) | Yes. A GET form posts `id` (waybill) and `phone` to `/Track/track_id` | **`https://koombiyodelivery.lk/Track/track_id?id={trackingNumber}&phone=`**. Verified: the page shows "Tracking ID: 12345678" with an empty phone. Real parcel details with phone left blank could not be confirmed without a real waybill | "90+ Island Wide Branches". **Verified** |
| **Citypak** | IML Delivery Systems (Pvt) Ltd, "Citypak by Advantis" | https://www.citypak.lk/ | **+94 11 7697000**, verified on citypak.lk | Yes. A GET form posts `tracking_number` to `/track` | **`https://track.citypak.lk/track?tracking_number={trackingNumber}`**. Verified: the server processes it and answers "Invalid Tracking Number" for the dummy. Kelly Felder links its customers to this page | "Islandwide delivery solutions". **Verified** |
| **Pronto Lanka** | Pronto Lanka (Pvt) Ltd | https://www.prontolanka.lk/ | **+94 112 102 700**, verified (`tel:` link) | Yes, but **form only.** The number is sent by AJAX POST to `api/get_tracking_details.php` from the home page (`#book-your-pickup`) | **None possible.** The number cannot go in the URL. Seed **NULL** (the number is shown as text). The tracking page is `https://www.prontolanka.lk/#book-your-pickup` | "55+ Islandwide Branches". The site showed a notice that web tracking may be unavailable during a system migration. **Verified** |
| **Trans Express** | Trans Express Services Lanka (Pvt) Ltd | https://transexpress.lk/ | **011 2999 888** (also 0777 771 455), verified | Yes, but **form only.** The SPA at `https://tracking.transexpress.lk/tracking` calls an API; the page URL does not change | **None possible.** Seed **NULL** | Serves "across Sri Lanka". **Verified** |
| **Sri Lanka Post (Speed Post / SL Post Courier)** | Department of Posts, Sri Lanka | https://slpost.gov.lk/ | Speed post enquiries: **0714440440 / 0112328112**, verified on the speed-post page. Head office +94 112328301-3. The "1950" call centre appears only as an image, so it is **unverified** | Yes, but **POST with a CAPTCHA**: `https://slpmail.slpost.gov.lk/track/`. The site links http://www.slpmail... which redirects to https://slpmail... | **None possible.** Seed **NULL** | Island-wide by definition. 24 hours within the own delivery zone, 48 hours outside. Barcodes look like `BA…LK` (13 characters). **Verified** |
| **Prompt Xpress** | "Prompt Xpress" / "Prompt XP" | https://www.promptxpress.lk/ | **+94 114 422 733**, verified (header) | Yes, but **form only.** ASP.NET postback on `https://www.promptxpress.lk/TrackItem.aspx` | None possible: NULL | Claims island-wide; same day only in main towns. **Verified** |
| **Royal Express** | Royal Express Courier & Logistics (Pvt) Ltd | https://royalexpress.lk/ | **+94 112 417 417**, verified | Yes, but **form only.** SPA at `https://royalexpress.tracking.curfox.com/`; the URL is unchanged after a search and the code reads no query parameter | None possible: NULL | "Deliveries across the country". **Verified** |
| Grasshoppers | — | grasshoppers.lk | — | — | — | **The domain is now an independent courier-comparison site**, not a courier. Do not seed |
| Certis Lanka | — | certislanka.com | — | — | — | **The domain redirects to sitrekgroup.com.** Not verified as an active consumer parcel courier. Do not seed |
| DHL | DHL Express Sri Lanka | — | — | — | — | International express. Not a domestic e-commerce courier (also listed under "International Express" by grasshoppers.lk, a secondary source). Do not seed |

Others listed by grasshoppers.lk but **not verified here**: Fardar Express (named by Kelly Felder for return pickups), Mishi Express, Darnet Logistics, PickMe Flash, Quickee and Aramex. They could be added later through the admin screen.

**About form-only couriers:** the CK and the validator do not allow a template without `{trackingNumber}`. A hack such as `https://royalexpress.tracking.curfox.com/?n={trackingNumber}` would pass both, but the number would not be pre-filled. The customer would land on a blank form, which reads as a broken link. **Recommendation: leave the template NULL** (the order page shows the number as text) and, optionally, add the courier's tracking page to the courier help text later. Changing the model is out of scope for this research.

---

## 4. Recommended default policy for Colombo Wardrobe

Position: the middle of local practice, adjusted for this shop's two differences, **no COD** and **manual refunds by bank transfer**.

| Point | Recommended default (owner can change) | Why |
|---|---|---|
| Fees, times, free delivery | **Read from `DeliveryMethodZoneRates`.** Seeded placeholders: Standard Rs. 400 Colombo / 450 Gampaha and Kalutara / 600 elsewhere; free over Rs. 10,000 / 10,000 / 15,000; 1–2, 2–3 and 3–5 working days. Express Colombo Rs. 950 (1 day), Western Rs. 1,200 (1–2 days) | Already the system's figures. Avoids two sources of truth |
| Dispatch | Orders are dispatched after the bank transfer is confirmed. Delivery days count from dispatch | Bank-transfer flow (ADM-09 approval) |
| Return window | **14 days from delivery** | Middle of the 7–30 range; easy to remember |
| Condition | Unworn, unwashed, unaltered, all tags attached, original packaging | Universal |
| Non-returnable | Innerwear and lingerie, swimwear, socks and hosiery, pierced jewellery, face masks, and items marked final sale. **[default: sale items are final sale]** | Matches 5 of 7 retailers |
| Exchange or refund | The customer chooses: **size or colour exchange (if in stock), or a refund** | Money back is a trust point for a new online shop paid upfront by bank transfer |
| Refund method | **Bank transfer to the customer's account, done manually by our team, [default: within 7 working days] of the return being checked**. Original delivery fee not refunded for change of mind | Owner decision. Fashion Bug does the same |
| Return shipping | Customer pays for change of mind or size exchange. **We pay** for faulty, damaged or wrong items | Universal |
| Damaged or wrong item | **Tell us within [default: 48 hours] of delivery, with photos** | The clearest rule seen (Cool Planet) |
| Store returns | Optional, only if the owner wants it and once pickup locations exist | Pickup method exists but is disabled |

---

## 5. Drafts (original wording, for owner approval)

Markers:
- `[default: ...]`: a number the owner can change.
- `{{...}}`: a value the developer should fill from settings or the delivery-rate table, not type in by hand.
- `[if pickup enabled] ... [/if]`: show only when the `PICKUP` method is enabled and at least one `PickupLocation` is active.

### (a) Product page: delivery info (`Content.PdpDeliveryInfoHtml`)

```html
<ul>
  <li>Delivered island-wide by courier, usually in {{days for shopper's district}} working days after your payment is confirmed. [default: 1–2 Colombo, 2–3 Gampaha &amp; Kalutara, 3–5 elsewhere]</li>
  <li>Delivery costs {{fee for shopper's district}}, and is free on orders over {{free-above amount}}. [default: Rs. 400 / 450 / 600; free over Rs. 10,000, or Rs. 15,000 outside the Western Province]</li>
  <li>Express delivery is available in Colombo, Gampaha and Kalutara.</li>
  [if pickup enabled]<li>Prefer to collect? Choose store pickup at checkout. It's free.</li>[/if]
  <li>We take payment by bank transfer. Cash on delivery isn't available. <a href="/shipping">Delivery details</a></li>
</ul>
```

Developer note: #33 asks for the estimate and fee **for the shopper's district**. If no district is known yet, show the range ("from {{lowest fee}}") or "Delivery cost is shown at checkout", rather than the Colombo figure.

### (b) Product page: returns info (`Content.PdpReturnsInfoHtml`)

```html
<ul>
  <li>Changed your mind? Return or exchange within [default: 14] days of delivery.</li>
  <li>Items must be unworn and unwashed, with all tags still attached.</li>
  <li>Innerwear, swimwear, socks and sale items can't be returned. [default]</li>
  <li>Refunds are paid by bank transfer once we've checked your return.</li>
  <li>Something wrong with your order? Tell us within [default: 48 hours]. <a href="/returns">Returns &amp; exchanges</a></li>
</ul>
```

### (c) Shipping & Delivery page (slug `shipping`)

> # Shipping & Delivery
>
> We deliver to every district in Sri Lanka by trusted courier partners. Here's how it works.
>
> ## When we send your order
> Once you place an order, you'll get your bank details and order number by email. We pack your order as soon as your bank transfer is confirmed, normally within [default: 1 working day] of the payment reaching us. Orders confirmed on a weekend or public holiday go out on the next working day.
>
> If we don't receive your payment within {{Orders.BankTransferExpiryHours}} hours, the order is cancelled automatically and the items go back on sale.
>
> ## Delivery options and costs
> The cost and the expected delivery time depend on your district. You'll see the exact amount at checkout before you pay.
>
> {{Render a table from DeliveryMethodZoneRates: method × zone, showing fee, "free over" amount and days. Hide rows where IsAvailable = false.}}
>
> *[default figures, as currently seeded:]*
>
> | | Colombo | Gampaha & Kalutara | Rest of the island |
> |---|---|---|---|
> | Standard delivery | Rs. 400, free over Rs. 10,000, 1–2 working days | Rs. 450, free over Rs. 10,000, 2–3 working days | Rs. 600, free over Rs. 15,000, 3–5 working days |
> | Express delivery | Rs. 950, next working day | Rs. 1,200, 1–2 working days | Not available |
>
> Delivery times are our best estimate and count working days from dispatch. Remote areas, bad weather and public holidays can add a day or two.
>
> [if pickup enabled]
> ## Collect from our store
> Choose **Store pickup** at checkout and collect your order free of charge from:
> {{List active PickupLocations: name, address, city, opening hours, phone.}}
> We'll email you when your order is ready. Please bring your order number and a photo ID. We'll hold your order for [default: 7 days].
> [/if]
>
> ## Tracking your parcel
> When your order leaves us, we'll email you the courier's name and your tracking number. You can also find them under **My account → Orders**. Where the courier offers online tracking, the number links straight to it.
>
> ## Payment
> We accept bank transfer [and PayPal, when enabled]. **Cash on delivery is not available.**
>
> ## Receiving your parcel
> Please make sure someone can receive the parcel at the address and phone number you gave us; the courier will call before delivering. If a delivery fails after the courier's attempts and the parcel comes back to us, we'll contact you to arrange redelivery. A second delivery fee may apply.
>
> Before signing, check the parcel isn't torn or opened. If it is, note it with the courier and tell us within [default: 48 hours] (see Returns & Exchanges).
>
> ## Wrong address?
> Contact us as soon as possible. We can change the address until the order is dispatched.
>
> ## Questions
> Reach us at {{Store contact email}} or {{Store phone / WhatsApp}}.

### (d) Returns & Exchanges page (slug `returns`)

> # Returns & Exchanges
>
> We want you to love what you ordered. If something isn't right, here's what to do.
>
> ## The short version
> - Return or exchange within **[default: 14] days** of delivery.
> - Items must be unworn, unwashed and have all their tags.
> - Refunds are made **by bank transfer** to your account.
> - Damaged, faulty or wrong item? Tell us within **[default: 48 hours]** and we'll put it right at our cost.
>
> ## What can be returned
> Most clothing, footwear and bags can be returned if they are:
> - unworn, unwashed and unaltered, with no marks, makeup or scent;
> - in their original packaging, with every tag and label still attached;
> - sent back with your order number.
>
> We check every return when it arrives. If an item doesn't meet these conditions, we'll let you know and send it back to you.
>
> ## What can't be returned [default list]
> For hygiene reasons, or because they were sold at a reduced price, we can't take back:
> - innerwear, lingerie and sleepwear sold as innerwear;
> - swimwear;
> - socks and hosiery;
> - pierced jewellery;
> - items marked **Sale** or **Final sale**.
>
> This doesn't apply if the item arrived damaged, faulty or wrong; see below.
>
> ## Exchanges, including a different size
> Not the right fit? We'll swap it for another size or colour of the same item, if we have it in stock. If it's out of stock, we'll offer a refund instead. If you want a different product, return the first one for a refund and place a new order. That way you get the new item sooner.
>
> ## How to return or exchange
> 1. Contact us within [default: 14] days of delivery at {{Store contact email}} or {{WhatsApp}}. Give your order number, the item, and whether you'd like an exchange or a refund.
> 2. We'll reply with the return address and instructions. Please don't send items back before we confirm.
> 3. Pack the item securely and send it with a courier of your choice. Keep the receipt or tracking number until your return is complete.
> [if pickup enabled]You can also hand it in at our store; bring your order number.[/if]
>
> ## Who pays for return delivery
> - **Change of mind or size exchange:** you pay to send the item back. For an exchange, we send the new item at [default: no charge / the standard delivery fee].
> - **Damaged, faulty or wrong item:** we cover everything. We'll arrange collection or refund your return postage.
>
> ## Refunds
> Because we're paid by bank transfer, we refund by bank transfer too. Once your return reaches us and passes our check, we'll ask for your bank details (account name, bank, branch and account number) and send the money **within [default: 7] working days**. We'll email you when it's done.
> - You'll get back the price you paid for the item.
> - The original delivery fee is refunded only if the item was damaged, faulty or wrong, or if the whole order is returned for that reason.
> - [When PayPal is enabled: orders paid by PayPal are refunded to the same PayPal account.]
>
> ## Damaged, faulty or wrong items
> We're sorry if this happens. Please tell us **within [default: 48 hours] of delivery**. Send your order number and clear photos of the item, its tags and the parcel. We'll offer a replacement, or a full refund including delivery, and we'll cover the cost of getting it back.
>
> ## Cancelling an order
> You can cancel any time before we dispatch it. If you've already paid, we'll refund you in full by bank transfer.
>
> ## Questions
> Get in touch at {{Store contact email}} or {{Store phone / WhatsApp}}. We're happy to help.

---

## 6. Courier seed list (only if the owner approves seeding)

**Read first:** `SaveCourierCommandHandler` records a decision that no courier row is seeded, because invented client data was a Major finding in W3-03. Seed these only after the owner confirms which couriers the shop actually uses. Otherwise, hand this table to the owner to enter through **Admin → Settings → Couriers**, which needs no code change. If seeding is approved, the safest form is the table below, with **only the confirmed couriers active**.

Phones are written to fit `varchar(20)` printable ASCII. Every template contains `{trackingNumber}` and starts with `https://`, so it passes both the CK and the validator.

| SortOrder | Name | Phone | TrackingUrlTemplate | IsActive (recommended) | Verification |
|---|---|---|---|---|---|
| 1 | Domex | `011 7759 759` | `https://www.domex.lk/Order-Details.php?wbno={trackingNumber}` | **true** | Name, phone and template verified |
| 2 | Koombiyo Delivery | `011 788 6786` | `https://koombiyodelivery.lk/Track/track_id?id={trackingNumber}&phone=` | **true** | Name, phone and template verified (number read from the URL; behaviour with a real waybill and no phone is not tested) |
| 3 | Citypak | `011 769 7000` | `https://track.citypak.lk/track?tracking_number={trackingNumber}` | **true** | Name, phone and template verified |
| 4 | Pronto Lanka | `011 210 2700` | NULL (form-only tracking) | false | Name and phone verified; no URL template is possible |
| 5 | Trans Express | `011 299 9888` | NULL (form-only tracking) | false | Name and phone verified; no URL template is possible |
| 6 | Sri Lanka Post | `011 232 8112` | NULL (POST + CAPTCHA) | false | Speed-post enquiry phone verified; no URL template is possible |

Why the first three are active: their tracking links were verified end to end, and all three cover the whole island. Pronto, Trans Express and SL Post are real and widely used, but the customer would see only a number, so they are best switched on when the shop actually books with them.

Open points for the owner:
1. Which courier(s) does the shop already have an account with? Only those should be active. Kelly Felder's use of Citypak is a useful local precedent.
2. Confirm or change the 14-day window, the 48-hour damage window, the 7-working-day refund time, and the final-sale default for sale items.
3. Exchanges: is the new item sent free, or is the standard delivery fee charged?
4. Store pickup: provide at least one store address (district, hours, phone) to enable `PICKUP`. Should in-store returns be accepted?
5. Confirm the D8 delivery fees, thresholds and days in `DeliveryMethodZoneRates`. The pages render from them.

---

## 7. Sources

| # | Source | Used for |
|---|---|---|
| 1 | https://odel.lk/faq | ODEL fees, delivery days, faulty goods |
| 2 | https://odel.lk/terms-and-conditions | ODEL returns, credit note, non-returnables, COD areas and cap |
| 3 | https://fashionbug.lk/pages/return-policy | Fashion Bug returns, bank refund |
| 4 | https://fashionbug.lk/ | Fashion Bug COD and island-wide banner |
| 5 | https://coolplanet.lk/policies/shipping-policy | Cool Planet fee, time, COD |
| 6 | https://coolplanet.lk/policies/refund-policy | Cool Planet returns, 48-hour damage, refund timing |
| 7 | http://www.nolimit.lk/information/delivery-details | NOLIMIT delivery times |
| 8 | http://www.nolimit.lk/information/return-policy | NOLIMIT returns, store credit |
| 9 | http://www.nolimit.lk/information/faq | NOLIMIT COD, 2–5 days, 14-day window |
| 10 | https://springandsummer.lk/pages/delivery-shipping | S&S fees, exchange windows, no cash refunds, 14-day defects |
| 11 | https://springandsummer.lk/ | S&S Rs. 5,000 free-shipping banner |
| 12 | https://www.kellyfelder.com/policies/refund-policy | Kelly Felder conditions, store credit |
| 13 | https://www.kellyfelder.com/pages/shipping-returns | Kelly Felder 14 days, Fardar Rs. 300–400, 7-working-day credit |
| 14 | https://www.kellyfelder.com/ and https://kellyfelder.com/pages/track-your-order | Rs. 12,000 free shipping; Citypak tracking link |
| 15 | https://houseoffashions.lk/pages/shipping-policy and https://www.houseoffashions.lk/ | House of Fashions delivery rules, 3–5 days |
| 16 | https://houseoffashions.lk/pages/return-exchange-policy | House of Fashions exchange rules |
| 17 | https://www.domex.lk/ and https://www.domex.lk/Order-Details.php?wbno=12345678 | Domex phone, tracking pattern |
| 18 | https://koombiyodelivery.lk/ and https://koombiyodelivery.lk/Track/track_id?id=12345678&phone= | Koombiyo name, phone, tracking pattern |
| 19 | https://www.citypak.lk/ and https://track.citypak.lk/track?tracking_number=12345678 | Citypak name, phone, tracking pattern |
| 20 | https://www.prontolanka.lk/ and https://www.prontolanka.lk/dist/js/order-tracking.js | Pronto name, phone, AJAX-only tracking |
| 21 | https://transexpress.lk/ and https://tracking.transexpress.lk/tracking | Trans Express name, phone, SPA tracking |
| 22 | https://slpost.gov.lk/, https://slpost.gov.lk/contact-us/, https://slpost.gov.lk/services/speed-post-courier-tracking/ and https://slpmail.slpost.gov.lk/track/ | SL Post phones, speed-post times, CAPTCHA tracking |
| 23 | https://www.promptxpress.lk/ and https://www.promptxpress.lk/TrackItem.aspx | Prompt Xpress phone, postback tracking |
| 24 | https://royalexpress.lk/ and https://royalexpress.tracking.curfox.com/ | Royal Express name, phone, SPA tracking |
| 25 | https://www.grasshoppers.lk/ | The domain is a comparison site, not a courier; secondary list of couriers |
| 26 | https://www.certislanka.com/ (301 to https://www.sitrekgroup.com/) | Certis Lanka not verified as a courier |
