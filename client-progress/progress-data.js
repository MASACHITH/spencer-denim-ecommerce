/*
 * Progress page content.
 *   status   : "planned" | "in-progress" | "done"
 *   progress : 0–100, used when status is "in-progress"
 *              (Done always counts as 100, Planned always counts as 0)
 *   dates    : "YYYY-MM-DD"
 *   Text values go in "double quotes"; progress is a plain number (40, not "40").
 */
const progressData = {
  project: {
    title: "Colombo Wardrobe — Website Build Progress",
    // The site is Colombo Wardrobe; the company that commissioned it is unchanged. This field
    // renders as "Prepared by TU2SL for ...", which is about the company, not the shop.
    client: "Spencer Denim Industries Pvt Ltd",
    agency: "TU2SL",
    startDate: "2026-09-15",
    endDate: "2026-10-15"
  },

  lastUpdated: "2026-10-21",

  milestones: [
    { week: "Week 0", title: "Setup & design", start: "2026-09-15", end: "2026-09-18" },
    { week: "Week 1", title: "Foundation & admin catalogue", start: "2026-09-19", end: "2026-09-25" },
    { week: "Week 2", title: "Shopping website", start: "2026-09-26", end: "2026-10-02" },
    { week: "Week 3", title: "Checkout & orders", start: "2026-10-03", end: "2026-10-09" },
    { week: "Week 4", title: "Payments, dashboard & launch", start: "2026-10-10", end: "2026-10-15" }
  ],

  groups: [
    {
      name: "Project Planning & Design",
      features: [
        { name: "Requirements & scope finalised", status: "done", progress: 100 },
        { name: "Technical plan & architecture", status: "done", progress: 100 },
        { name: "Design style guide", status: "in-progress", progress: 90 },
        { name: "Hosting, domain & security setup", status: "in-progress", progress: 25 }
      ]
    },
    {
      name: "Homepage",
      features: [
        { name: "Hero video banner", status: "in-progress", progress: 90 },
        { name: "Shop by department (Women, Men, Kids)", status: "done", progress: 100 },
        { name: "New arrivals", status: "done", progress: 100 },
        { name: "Trending items", status: "done", progress: 100 },
        { name: "Campaign banner", status: "done", progress: 100 },
        { name: "Activewear feature", status: "done", progress: 100 },
        { name: "Sale section", status: "done", progress: 100 },
        { name: "Social media gallery", status: "in-progress", progress: 85 },
        { name: "Customer benefits", status: "done", progress: 100 },
        { name: "Newsletter signup", status: "done", progress: 100 }
      ]
    },
    {
      name: "Shopping Pages",
      features: [
        { name: "Site header with announcement bar, footer, WhatsApp chat button & delivery district selector", status: "done", progress: 100 },
        { name: "Main menu (Women · Men · Kids · New Arrivals · Sale) with mega menu & mobile menu", status: "done", progress: 100 },
        { name: "Department pages", status: "done", progress: 100 },
        { name: "Category listings with filters & sorting", status: "done", progress: 100 },
        { name: "Product cards with quick view", status: "done", progress: 100 },
        { name: "Search", status: "in-progress", progress: 90 },
        { name: "Store pages: About, Contact, Returns, Shipping, Privacy & Terms", status: "in-progress", progress: 90 }
      ]
    },
    {
      name: "Product Page",
      features: [
        { name: "Image gallery, zoom & video", status: "done", progress: 100 },
        { name: "Colour & size selection with size guide", status: "done", progress: 100 },
        { name: "Product details & delivery information", status: "in-progress", progress: 60 },
        { name: "Related & recently viewed products", status: "done", progress: 100 },
        { name: "Reviews & ratings", status: "done", progress: 100 }
      ]
    },
    {
      name: "Wishlist, Bag & Checkout",
      features: [
        { name: "Wishlist", status: "done", progress: 100 },
        { name: "Shopping bag & mini bag", status: "done", progress: 100 },
        { name: "Promo codes", status: "done", progress: 100 },
        { name: "Checkout & guest checkout", status: "done", progress: 100 },
        { name: "Delivery options", status: "done", progress: 100 },
        { name: "Bank transfer payment", status: "done", progress: 100 },
        { name: "PayPal payment", status: "in-progress", progress: 75 },
        { name: "Order confirmation page", status: "done", progress: 100 }
      ]
    },
    {
      name: "Customer Account",
      features: [
        { name: "Register & login", status: "done", progress: 100 },
        { name: "Order history with tracking", status: "done", progress: 100 },
        { name: "Order & shipping emails", status: "done", progress: 100 }
      ]
    },
    {
      name: "Admin Panel",
      features: [
        { name: "Admin login with user roles & permissions", status: "in-progress", progress: 90 },
        { name: "Product management (images & video)", status: "done", progress: 100 },
        { name: "Category management", status: "done", progress: 100 },
        { name: "Inventory", status: "done", progress: 100 },
        { name: "Order management & payment approval", status: "done", progress: 100 },
        { name: "Promo codes & sale campaigns", status: "done", progress: 100 },
        { name: "Customers", status: "done", progress: 100 },
        { name: "Review moderation", status: "done", progress: 100 },
        { name: "Newsletter subscriber list", status: "done", progress: 100 },
        { name: "Homepage & content management", status: "done", progress: 100 },
        { name: "Dashboard", status: "done", progress: 100 },
        { name: "Reports & traffic", status: "done", progress: 100 },
        { name: "Settings", status: "in-progress", progress: 80 }
      ]
    },
    {
      name: "Launch",
      // Not development: excluded from the headline figure (see app.js).
      countsTowardOverall: false,
      features: [
        { name: "Preview website for your review", status: "planned", progress: 0 },
        { name: "Mobile, tablet & desktop testing", status: "planned", progress: 0 },
        { name: "Website live on the domain with SSL", status: "planned", progress: 0 },
        { name: "Admin training & handover", status: "planned", progress: 0 }
      ]
    }
  ],

  // Newest first.
  updates: [
    {
      date: "2026-10-21",
      title: "Reviews and ratings",
      text: "Signed-in customers can now rate and review products, with a Verified buyer badge for those who bought the item, and staff approve or hide reviews in the admin panel.",
    },
    {
      date: "2026-10-21",
      title: "Homepage and menu editing",
      text: "Every homepage section, the announcement bar, size guides and the menu promo cards can now be edited from the admin panel.",
    },
    {
      date: "2026-10-21",
      title: "Sale campaigns",
      text: "Sale campaigns can now include individual products, and show a banner on the Sale page while they run.",
    },
    {
      date: "2026-10-20",
      title: "Inventory",
      text: "The admin panel now has an inventory screen to see and update stock for every colour and size, with low-stock levels and a history of every change.",
    },
    {
      date: "2026-10-20",
      title: "Colourful charts",
      text: "The dashboard, website traffic and reports pages now show colourful charts, including a new chart of first-time and returning customers.",
    },
    {
      date: "2026-10-20",
      title: "Settings",
      text: "Settings now include how long website visit records are kept, and a YouTube link in the shop footer.",
    },
    {
      date: "2026-10-15",
      title: "Website traffic",
      text: "The shop now records its visitors, and the admin panel shows visits, where they came from, the most viewed pages and the devices used.",
    },
    {
      date: "2026-10-15",
      title: "PayPal payment and stronger admin sign-in",
      text: "PayPal checkout is now built into the shop, ready to switch on from the admin settings once it has been tested with your PayPal account. Admin sign-in is also stronger: accounts lock after repeated wrong passwords, and each two-factor code can be used only once.",
    },
    {
      date: "2026-10-15",
      title: "Dashboard and reports",
      text: "The admin panel now opens on a dashboard of sales, orders and stock, compared with the previous period, with sales, inventory and customer reports you can download as spreadsheets.",
    },
    {
      date: "2026-10-15",
      title: "Customers and newsletter subscribers",
      text: "The admin panel now lists your customers, with each one's addresses and order history, and your newsletter subscribers, which you can download as a spreadsheet.",
    },
    {
      date: "2026-10-14",
      title: "Customer accounts",
      text: "Customers can now create an account, sign in, reset their password, save delivery addresses, and see their past orders with the courier and tracking link.",
    },
    {
      date: "2026-10-13",
      title: "Discount codes and seasonal sales are both in, and one is already fixed",
      text: "Two things that work together. Discount codes: you create a code in the admin panel \u2014 a percentage off, a fixed amount off, or free delivery \u2014 with a minimum spend, the dates it is valid between, a total limit on how many times it can be used, and a limit per customer. A shopper types it in at checkout and sees what it took off. Separately, sale campaigns: you choose a discount and the dates, point it at a department or a category, and every product underneath goes on sale at once, on the shop pages and in the basket. If two campaigns could apply to the same product the shopper gets the cheaper price, never both discounts stacked. There is one deliberate gap: a campaign can target a whole department or category, but not a list of individual products you pick one by one. That one is a bigger job and we would rather ship the part your client actually asked for \u2014 \"20% off Women\u0027s shirts for a week\" \u2014 working properly than half of both. And one correction worth making plainly: when discount codes went in yesterday we reported them finished, and the admin screen for them was in fact broken \u2014 it could not list your codes at all, and saving one showed an error even though it had saved. We found it, fixed it, and added the first real test of those screens, which is why it will not happen again quietly.",
    },
    {
      date: "2026-10-09",
      title: "If an email fails to reach a customer, you will be told",
      text: "A small safety net, built because the alternative was silence. Emails can fail for ordinary reasons \u2014 a customer mistypes their address, or their mailbox is full. The shop retries six times over about five hours, and until today, if all six failed, it gave up and told nobody. That customer would sit waiting for payment instructions that were never going to arrive, and you would have no way of knowing. Now a warning appears at the top of every admin screen naming which email failed and which order it belonged to, and it stays there until somebody marks it as seen. The wording tells you plainly what you can do about it, which today means opening the order and telephoning or messaging the customer, because this version has no way to send the email again. We would rather the warning said something true than offer you a button that does not exist. One thing to expect: because no email account is connected yet, nothing can fail yet. The first day these warnings can appear is the day you give us the email account.",
    },
    {
      date: "2026-10-09",
      title: "Your customers will now be emailed at every step of their order",
      text: "This was the biggest thing still missing, and it is worth explaining why. Until today a customer could place an order, see their bank details and reference number on the screen, and then receive nothing at all. If they closed that page, the details they needed in order to pay you were gone. Six emails are now built and tested. When they order, they get a confirmation listing what they bought. Separately they get the payment instructions: your bank, branch, account name and account number, the exact amount, the order number they must quote as the reference, the deadline, and a link back to their order where they can upload a photograph of the transfer slip. When you approve their payment, they are told. If they do not pay in time and the order expires, they are told. If the order is cancelled, they are told. And when you post it, they get the courier name and a link that takes them straight to the courier's tracking page. One point to be clear about: none of these will actually be sent until you give us the email account the shop should send from, which comes with your hosting. Everything is queued and waiting, and nothing is lost in the meantime \u2014 the moment that account is set up, the emails start going out.",
    },
    {
      date: "2026-10-08",
      title: "An order can now be cancelled, which puts the stock straight back on sale",
      text: "A customer who changes their mind an hour after ordering is a normal part of retail, and until today there was no way to deal with it. The order simply sat there until its 48-hour payment deadline passed, and for those two days the items on it were unavailable to everybody else. Now, as long as the order has not been paid for, you open it and press Cancel, type the reason in your own words, and the items go back on sale immediately. The reason and your name are saved against the order, so in a month's time anyone can see why it was closed and who closed it. The customer can also cancel it themselves from the order link we send them, which saves them telephoning you and saves you doing anything at all. A paid order cannot be cancelled this way, because once money has arrived the right answer is to return it, and that is handled as a refund rather than a cancellation. There is one more thing worth knowing: if you cancel an order and the customer's bank transfer then turns up the next morning anyway, you can still accept it. The order comes back to life, the shop re-checks that the items are still in stock, and tells you if any of them have since sold.",
    },
    {
      date: "2026-10-08",
      title: "The Orders screen is finished \u2014 you can now run the shop day to day",
      text: "Until today you could take an order and take the payment, but there was no screen to manage what happened next. That is now built and tested. There is an Orders list with one search box: type an order number, part of one, just the digits, or the customer's email or telephone, and it finds the order. You can filter by status, and the list of orders waiting to be posted comes up oldest first so nothing sits forgotten. Opening an order shows everything on it \u2014 each item with its code, colour, size and price, the customer's details, the delivery address or the collection point, the full money breakdown, the payment and any slip they uploaded, and a dated history of every step with the name of whoever did it. From there you press Start processing, then Mark as shipped once you have chosen the courier and entered the tracking number, then Mark as delivered. Collection orders follow the same path with Ready for collection and Mark as collected instead. You can also print an invoice or packing copy.",
    },
    {
      date: "2026-10-08",
      title: "A late payment is no longer a problem we have to fix by hand",
      text: "Worth explaining because it was a real risk. An unpaid order releases its stock after the deadline so it is not held for ever \u2014 but if the customer then transferred the money a day late, that order had dropped off every screen and the only way to settle it was for us to edit the database directly. Now you search the order number the customer quotes from their bank statement, open it, and press Accept late payment. The shop re-checks that the items are still in stock and tells you exactly which ones are not if any have sold, so you are never committing to send something you no longer have.",
    },
    {
      date: "2026-10-08",
      title: "Three more things to add when you are ready, and why the shop shows nothing until you do",
      text: "Your couriers are now set up from a screen in the admin panel \u2014 the name and the tracking-link format each one uses. Until you add at least one, Mark as shipped cannot be used, and the screen says so rather than giving you a button that does nothing. The same applies to the details that go on an invoice: your registered company name, address, business registration number and VAT number. There is a screen for those too, and an invoice prints only what you have entered \u2014 no invented address, because an invoice is a legal document and a wrong one is worse than an incomplete one. With your bank details, that is three short forms standing between the shop and taking real orders, and all three are built and waiting.",
    },
    {
      date: "2026-10-07",
      title: "Your shop can now take money",
      text: "This is the milestone the whole build was heading for. A customer places their order and immediately sees your bank details, the exact amount to transfer, the order number to quote as the reference, and a deadline. They can type in their bank reference and upload a photograph or PDF of their deposit slip. On your side there is a new screen \u2014 Orders, bank transfers awaiting approval \u2014 listing every order waiting on payment, oldest first, with the amount, the reference, the slip and how long is left. You check your bank statement and press Approve, Reject, or Accept a late payment. Approving does everything in one go: the order becomes paid, the stock is committed, an invoice number is issued, and the whole decision is recorded with your name against it. Rejecting keeps the order alive so the customer can transfer again. And unpaid orders now release their stock automatically rather than holding it for ever.",
    },
    {
      date: "2026-10-07",
      title: "Your bank details are yours to enter, and to change",
      text: "We have deliberately not typed your account details into the shop. There is a screen in the admin panel where you enter the bank name, branch, account name, account number and any instructions you want the customer to see \u2014 and change them later whenever you like. Until you fill them in, the shop simply shows nothing rather than inventing a placeholder, because a wrong account number on a live page is worse than an empty one. The same applies to your address, telephone, email, opening hours and WhatsApp number: every one of them now appears only once you have set it. Nothing on the shop states a contact detail we made up.",
    },
    {
      date: "2026-10-07",
      title: "The store pages are built and waiting for your words",
      text: "About, Contact, Returns, Shipping, Privacy and Terms all exist now, each editable from the admin panel with its own page title and description for Google. Five of them are deliberately switched off until you send us the copy \u2014 and Privacy and Terms need a proper data-protection review before they go live, which is not something we should write for you. The Contact page is switched on already, because it needs no copy at all: it is the enquiry form plus whichever of your contact details you have entered. Messages from it arrive in a new inbox in the admin panel, where staff can mark them read, replied or spam. You reply by phone or email as you prefer \u2014 the shop does not send email yet.",
    },
    {
      date: "2026-10-07",
      title: "Product management and the wishlist are finished and tested",
      text: "Both passed their testing and are live in the build. You can add products with photographs and video, colours and sizes, prices and discounts, badges, material and care details, related products and the text Google shows \u2014 and put each one on the shop or take it off. A product with no photograph cannot be published, and the shop tells you exactly what is missing rather than letting a broken product through. Shoppers can save items with the heart on any card or product page, see them all on a wishlist page at today's prices, and move one into the bag choosing a size as they go.",
    },
    {
      date: "2026-10-06",
      title: "A customer can now place an order from start to finish",
      text: "This is the biggest step so far. A shopper — with no account needed — fills in their name, email, phone and delivery address, picks their district, chooses a delivery method and sees the full cost before committing: items, any savings, the delivery charge, and the total. They press Place order and land on a confirmation page with their order number. Behind the scenes the stock is held for them, the order is written down exactly as they saw it, and the bag is emptied. The bag page also finally has its Proceed to checkout button. Two things are deliberately not part of this: no money moves yet — the order is recorded as awaiting payment, and taking the bank transfer is the next piece of work — and there is no confirmation email yet, so for now the confirmation page's web address is the customer's link back to their order, which the page tells them to keep.",
    },
    {
      date: "2026-10-06",
      title: "Delivery charges are working, using the draft rates — we need your confirmation on the figures",
      text: "The delivery fees now calculate properly by district: all 25 districts, with Standard and Express options, free delivery above a threshold, and Express correctly shown as unavailable where it is not offered. Pick-up is switched off because no collection address has been set. Please note the actual figures in there are the draft ones from the proposal — for example Colombo Standard at Rs. 400, free above Rs. 10,000, and island-wide Standard at Rs. 600, free above Rs. 15,000. The machinery is built and tested; the numbers are yours to confirm or change, and changing them later is a settings change rather than new work. This is one of the items still waiting on you, along with the returns policy.",
    },
    {
      date: "2026-10-06",
      title: "Two problems found and fixed before they could reach a customer",
      text: "Worth telling you because both would have been damaging and neither was obvious. First: if a shopper double-tapped Place order on a weak connection, one of the taps could be told 'your bag is empty' even though their order had in fact gone through — so they might have ordered twice or phoned you thinking it failed. Only one order was ever created, but the message was wrong, and it is now fixed. Second: the checkout page promised 'we will email your order number', which nothing could do yet, while the confirmation page correctly said to keep the page address instead. The false promise was on the page a shopper reads before paying, so that wording has been removed until the emails are actually built.",
    },
    {
      date: "2026-10-06",
      title: "Quick View on product cards, and Add to Bag straight from a listing",
      text: "Shoppers no longer have to open a product page to buy. Hovering or tapping a product card now offers Quick View, which opens a panel over the listing with the photographs, the price and any discount, the star rating, the colours and the sizes — so a shopper can choose a colourway and a size, set a quantity and add it to the bag without leaving the page they were browsing. A link through to the full product page is always there for anyone who wants the full description and the size guide. If a product needs a size chosen, the card's Add to Bag opens Quick View rather than guessing: a card has no way to know which size somebody wants, and putting the wrong size in a bag is worse than asking.",
    },
    {
      date: "2026-10-06",
      title: "Your logo is now something you can upload and change yourself",
      text: "Rather than the logo being fixed in the design, there is now a screen in the admin panel where you upload it, see it in place, and replace it later with a new one whenever you like — the old one is removed when you do. It also takes your contact details and your social media addresses, so the links in the footer come from you rather than from us. This means the shop does not wait on the final logo artwork: we are building with a plain wordmark now, and the day you have the finished logo you load it in yourself and it appears across the site.",
    },
    {
      date: "2026-10-06",
      title: "A fix worth telling you about: the WhatsApp button was covering Add to Bag on phones",
      text: "The floating WhatsApp chat button sits in the bottom-right corner of every page. On a phone, the buttons on a product card sat in that same corner — so on some screens, tapping Add to Bag opened WhatsApp instead. On the narrowest phones this happened on the very first card a shopper sees. The card buttons have moved to the opposite corner at phone sizes, so the two no longer meet; the WhatsApp button keeps the corner it had, and nothing has been removed.",
    },
    {
      date: "2026-10-02",
      title: "The shopping bag is built",
      text: "You can now add a product to the bag from a product page, see it in a slide-out bag that opens from the header, and open a full bag page: each item with its colour and size, change the quantity, remove it, or move it to your wishlist. Prices are re-checked every time the bag is opened, so a shopper always sees today's price rather than the price when they added it — and if something sells out, is taken off sale, or you hide its category while it is in somebody's bag, the item stays visible and named but shows no price and is left out of the total, rather than quietly disappearing or being counted at a price you are no longer offering. Two things are deliberately not there yet: the promotion-code box, which needs the promo-code feature built first, and a delivery charge, because the delivery rates and zones are not set up — so the summary says 'Delivery: calculated at checkout' and totals the items only. The bag also has no 'Proceed to checkout' button yet, because the checkout itself is next; a button that looked like it worked and did nothing would be worse than none.",
    },
    {
      date: "2026-10-02",
      title: "Search engines can now find and list your shop properly",
      text: "Two files every search engine looks for are now in place: one that tells Google and others what they may read, and a site map that lists every department, category and product page. The site map is built from your live catalogue, so when you add a category or hide one in the admin screens, the list Google reads updates itself — nothing has to be maintained by hand. We also fixed something that would have cost you visitors: a mistyped or out-of-date web address used to quietly show the home page instead, which search engines treat as a broken promise; those addresses now correctly say the page does not exist. And the whole site is now checked automatically against accessibility standards on every change, so it stays usable with a keyboard and with a screen reader rather than only on the day someone checks.",
    },
    {
      date: "2026-10-02",
      title: "Search now works end to end",
      text: "Typing in the search box now suggests products and categories as you type, and there is a full results page behind it with the same filters and sorting as the category pages, plus a clear message when nothing matches. A search can be shared: send someone the link and they get the same results, and it opens properly on a phone. Searching finds a product by its name, its category, its colour, its material, its fit and its product code. The box also remembers your own recent searches on your own device, and deliberately never remembers anything that looks like an email address or a phone number. One piece is still to come: a 'popular searches' list, which needs the site to have been live long enough to have something real to show, so it is scheduled with the reporting work rather than invented now.",
    },
    {
      date: "2026-10-02",
      title: "You can now manage your own departments and categories",
      text: "The admin screens for your shop's structure are finished and tested. You can add a category, rename one, change its description, picture and search-engine text, move it under a different parent, reorder how categories appear in the menu, and hide or show one. Reordering works by Move up and Move down buttons rather than dragging, so it works the same on a phone as on a desktop and can be done entirely from the keyboard. Two things are deliberately careful: before you hide a category the screen tells you exactly what it will cost — how many products leave the shop, how many sub-categories are affected, and how many products would stay on sale with no menu route left to them — and a category that still holds products or sub-categories cannot be removed by accident. Every change is recorded against the staff member who made it. Department names and web addresses are deliberately fixed, because changing them would break every existing link to those pages.",
    },
    {
      date: "2026-10-02",
      text: "The site is now Colombo Wardrobe throughout — every page title, heading and email will carry that name. Size guides are working: choosing a size on a product page opens the measurement chart for that kind of garment, and it reads properly on a phone. The product page is now complete apart from customer reviews."
    },
    {
      date: "2026-10-02",
      text: "Your photographs are on the site. Every product in Women and Men now shows your own pictures, with a second view when you hover over the card, and the three department tiles, the campaign banner, the trending row and the social wall are all live with your images. The home page video is running too. Kids products still show marked placeholder images until those photographs are supplied. Two things would improve what is there: a wide (16:9) version of the home page video, because the one supplied is a tall phone-shaped video that has to be cropped to fit, and a photograph of activewear for the section that now reads Linen and denim to match the picture we have."
    },
    {
      date: "2026-10-01",
      text: "Staff sign-in is now protected by two-step verification using an authenticator app on the phone, and a forced password change on first sign-in. Every action an administrator takes is recorded, and there is a screen to browse and filter that history — useful if you ever need to see who changed a price or approved a payment. The photo and video library is finished and tested: it accepts your photographs and video, makes the smaller sizes each page needs automatically, and strips hidden camera data such as GPS location from every file before it is stored. Your pictures can now go in."
    },
    {
      date: "2026-09-29",
      text: "Admin sign-in security tightened: when a staff account is switched off it loses access immediately rather than staying signed in, and every change an administrator makes is now recorded against their name. The photo and video library is being built and tested — that is the part that lets you upload your own product pictures, the hero video and the campaign banners, and it is what the remaining picture-led home page sections are waiting for."
    },
    {
      date: "2026-09-24",
      text: "The home page and the product page are built. The home page now shows Shop by Department, New Arrivals, Trending, Sale, why-shop-with-us and newsletter sign-up, and every part of it is stored so you can edit it yourself later. The product page has the photo gallery with zoom and full screen, colour and size choice with prices per size, product details and related items. The picture-led blocks (hero video, campaign banner, activewear, social wall) are built and switched off until your photographs and video are uploaded."
    },
    {
      date: "2026-09-24",
      text: "Shopping pages are working. You can now open Women, Men or Kids, narrow the products by size, colour, price, material and more, sort them, and load more as you scroll. New Arrivals and Sale pages are live too. Product cards show the photo, a second photo on hover, the price with any discount, the rating and the available colours. A filtered page keeps its own web address, so you can share or bookmark it. Tested on phone, tablet and desktop, and with keyboard and screen-reader use."
    },
    {
      date: "2026-09-23",
      text: "The shop frame is built and tested: the top announcement bar, the header with your logo area, search, wishlist and bag, the main menu with drop-down categories on desktop and a slide-out menu on phones, the delivery district selector, the footer, and the WhatsApp chat button. Checked on phone, tablet and desktop sizes."
    },
    {
      date: "2026-09-22",
      text: "Secure admin sign-in is built and tested: staff accounts, roles and permissions, and an activity log that records every sign-in and sign-out. The remaining admin screens (managing staff users, viewing the activity log) come next. Work also continued on the product catalogue."
    },
    {
      date: "2026-09-18",
      text: "Development environment and project foundation completed and tested. The product catalogue is now built in the database: departments, categories, products, colours, sizes, stock and product images, with sample products loaded for testing. This is the groundwork behind the shop pages, the product page and the admin product screens, which are built next."
    },
    {
      date: "2026-09-17",
      text: "Hosting purchased. Development environment setup is under way; server security and domain configuration are next."
    },
    {
      date: "2026-09-16",
      text: "Project code repository set up and design decisions recorded for the build. A short list of items for your confirmation (logo, delivery fees and couriers, PayPal currency) will be shared with you."
    },
    {
      date: "2026-09-15",
      text: "Database design reviewed and finalised, covering products, stock, orders, payments, delivery zones, user roles and security."
    },
    {
      date: "2026-09-15",
      text: "Design style guide prepared (colours, fonts, buttons, menus, product cards, product page, shopping bag) and ready for your review. Database design drafted."
    },
    {
      date: "2026-09-15",
      text: "Project kick-off. Requirements and scope finalised from the agreed proposal. Technical plan prepared. Next: design style guide and project setup."
    }
  ]
};
