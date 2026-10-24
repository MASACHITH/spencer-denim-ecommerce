import { GuideSection } from '../guide.model';

/**
 * Orders and delivery: finding and reading orders, fulfilment, how customers pay, bank transfers, refunds,
 * couriers and delivery. Read from the order handlers, the order screens and the delivery rules. Where an
 * on-screen sentence or a design document disagrees with what the code does, this describes the code.
 */
export const ORDER_SECTIONS: readonly GuideSection[] = [
  {
    id: 'orders',
    group: 'Orders and delivery',
    title: 'Orders: find an order, read it, and what the statuses mean',
    summary:
      'How to find the order a customer is asking about, what each part of the order page shows, and what each status means.',
    screens: [
      { label: 'Orders', path: '/orders' },
      { label: 'Bank transfers', path: '/orders/bank-transfers' },
      { label: 'Couriers', path: '/orders/couriers' },
    ],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Orders View** to look, **Orders Edit** to move an order along, **Orders Approve** for bank transfers. Order Staff and Store Manager have all three. The action buttons are drawn according to the order\'s state, not your permission: a role without the permission sees "Start processing" and is refused with "You do not have permission to do that." when it presses it.',
      },
      { kind: 'heading', text: 'Find an order' },
      {
        kind: 'steps',
        items: [
          'Open **Orders**. Type in **Find an order** and press **Search** (or Enter). It does not search as you type.',
          'Read the match rules below, then use the **Show** buttons to narrow by status: **All**, **Paid**, **Processing**, **Shipped**, **Delivered**, **Awaiting payment**, **Expired**, **Cancelled**, **Refunded**. Each has a count (over the whole list, ignoring your search).',
          'Sort with **Oldest first** or **Newest first**. Newest first is the default, except on the **Paid** tab, which starts oldest first so the order waiting longest is at the top.',
          'Press the order number on a card to open it. Pages hold 25 orders.',
        ],
      },
      {
        kind: 'table',
        caption: 'What the search box understands',
        head: ['You type', 'It is treated as'],
        rows: [
          ['Anything with an `@`', 'An email address, matched from its start. A piece from the middle of an address finds nothing.'],
          ['`SD-2026-000123`, `2026-000123` or `SD 2026 000123`', 'An order or invoice number. With letters it matches the start; without letters, the end.'],
          ['Up to 6 digits, such as `123`', 'The end of an order or invoice number: `SD-2026-000123`.'],
          ['7 or more digits', 'A telephone number, compared with the end of the number exactly as the customer typed it at checkout. It finds only numbers typed without spaces or dashes, so if nothing turns up, try the email or the name. (The Customers screen is more forgiving: it ignores spaces and matches the last nine digits.)'],
          ['Anything else', 'The start of an order number, or part of the customer\'s name (first and last name together work).'],
        ],
      },
      {
        kind: 'text',
        text: 'An **order number** looks like `SD-2026-000123` (the Colombo year, then a six-digit number). It is what a customer quotes on the phone, and what a bank-transfer customer is told to use as the payment reference. An **invoice number**, `INV-2026-000123`, exists only once the order is paid.',
      },
      { kind: 'heading', text: 'The order page, top to bottom' },
      {
        kind: 'list',
        items: [
          '**Top:** the order number, its status, the dates (Placed, Paid, Shipped, Delivered), the invoice number, and the courier and tracking link once shipped. An **Invoice / packing copy** button appears once the order is paid.',
          '**What to do next:** only the buttons that fit the order\'s state (next section).',
          '**What was bought:** every line with product, SKU, colour, size, quantity, unit price and line total, then the money box: Items, Markdown, Sale campaign, Promo code, Delivery, Delivery discount, **Total**. Everything is a copy of what was true when the order was placed, not today\'s prices.',
          '**Customer:** name, email and telephone (links), whether they were signed in or a Guest, the delivery address, the district (Sri Lanka) or country (with an **Overseas** pill), the "Estimate given" days, and **What the customer asked for** if they typed a note.',
          '**Payment:** one block per attempt with the method, status, amount, **Their reference**, the **Slip** download, who reviewed it and the outcome. For PayPal it shows **Charged by the provider**, the dollar amount. The winning attempt is marked **Settled this order**. Statuses appear as stored: Pending, Succeeded, Failed, Expired or Cancelled. A rejected transfer shows "Failed", with the note that was typed when it was rejected.',
          '**History:** every change, oldest first, with who made it (a staff email, "by the system", "by the payment provider" or "by the customer").',
          '**Internal note:** staff only, up to 2,000 characters. Customers never see it.',
        ],
      },
      { kind: 'heading', text: 'The statuses' },
      {
        kind: 'table',
        caption: 'Order statuses',
        head: ['Status', 'Meaning', 'Stock'],
        rows: [
          ['Awaiting payment', 'Placed, but no money confirmed. The items are held for the customer until the deadline (bank transfer 48 hours; PayPal 60 minutes by default).', 'Held (reserved)'],
          ['Paid', 'Money confirmed, invoice number issued. Nobody has started on it.', 'Taken off the shelf stock'],
          ['Processing', 'Being picked and packed.', 'Taken'],
          ['Shipped', 'With the courier. The customer has been emailed. (Shown as "Ready for collection" for store pickup.)', 'Taken'],
          ['Delivered', 'Delivered, confirmed by staff or automatically after 7 days. The customer can now write a "Verified buyer" review. (Shown as "Collected" for pickup.)', 'Taken'],
          ['Cancelled', 'Someone closed an unpaid order.', 'Released'],
          ['Expired', 'Nobody paid before the deadline; the system closed it.', 'Released'],
          ['Refunded', 'Money returned in full. **Nothing in this version can set this status**, so the tab shows 0.', 'Not applicable'],
        ],
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'Orders never move backwards. A mistake is corrected with the dispatch correction or recorded in the internal note, not undone. A paid order can never be cancelled.',
      },
    ],
  },
  {
    id: 'order-fulfilment',
    group: 'Orders and delivery',
    title: 'Processing an order: process, ship, deliver, cancel and print',
    summary:
      'The steps for every order from paid to delivered, the emails customers receive at each step, and what to do when something goes wrong.',
    screens: [{ label: 'Orders', path: '/orders' }],
    blocks: [
      {
        kind: 'steps',
        title: 'The normal path',
        items: [
          'On **Orders** press **Paid** in the **Show** buttons: the oldest orders are at the top. For each order press **Start processing**. The status becomes Processing. No email is sent.',
          'Press **Invoice / packing copy**, then **Print** (use your browser\'s print window; choose "Save as PDF" if you want a file). One page serves as the invoice and the packing list, with SKU, colour, size and quantity for every line.',
          'Pick and pack. There is no "Packed" status: use the internal note if you need to record it.',
          'Book the courier. Then, on the order, choose the **Courier** and type the **Tracking number** (up to 60 characters, plain letters, digits and punctuation) and press **Mark as shipped**. Both are required.',
          'The customer is emailed "Order SD-... is on its way" with the courier, tracking number and a **Track your parcel** button. Their order page shows the tracking link too.',
          'When the courier confirms delivery, press **Mark as delivered**. If nobody does, the shop checks once a day and marks a shipped order Delivered by itself after 7 days (the number is in [Settings](#settings), **Mark delivered after**; store pickup orders are never marked automatically).',
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Do the shipped step before the courier\'s pick-up cut-off, and close delivered orders at the end of the day. Once a week, look at orders that were marked Delivered automatically and confirm with the courier that they really arrived, because nobody pressed the button.',
      },
      { kind: 'heading', text: 'If the courier or tracking number was wrong' },
      {
        kind: 'text',
        text: 'After shipping, the same form is titled **Correct the dispatch**, with the button **Save the correction**. It works while the order is Shipped or Delivered and does not change the status. **No new email is sent**, so the customer still holds the old tracking link. Telephone or WhatsApp them the same day and record it in the internal note.',
      },
      { kind: 'heading', text: 'Cancel an unpaid order' },
      {
        kind: 'steps',
        items: [
          'The **Cancel this order** form appears only on an order that is awaiting payment with nothing in progress.',
          'Type **Why (required)**, up to 500 characters. It is kept on the order as "Why it was cancelled" and is never emailed.',
          'Press **Cancel the order**. There is no second "are you sure" step. The items go back on sale at once, the open payment is closed and any promo code use is returned.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'The form says "The customer is not emailed - tell them yourself." That is wrong: the shop **does** email the customer "Order (number) has been cancelled" (without your reason). Still tell them yourself if they may have paid. If the customer did pay by bank transfer, use **Accept late payment** (see [Bank transfers](#bank-transfers)). A paid order cannot be cancelled at all; it is refunded instead (see [Refunds](#refunds)).',
      },
      {
        kind: 'text',
        text: 'A customer can also cancel their own unpaid order from their order page ("Changed your mind?"). In the History you see "by the customer", with no reason, and they are emailed that the order "has been cancelled as you asked".',
      },
      { kind: 'heading', text: 'The internal note' },
      {
        kind: 'text',
        text: 'Use **Internal note** on any order, in any status, for anything the next person should know: who phoned and what was promised, a refund you made, a correction. Type it and press **Save the note**. Saving an empty box clears the note, but **the order\'s History keeps a line showing what it said** and cannot be edited, so never type anything there that should not be kept. The words are not copied into the audit log.',
      },
      { kind: 'heading', text: 'Print the invoice' },
      {
        kind: 'list',
        items: [
          '**Invoice / packing copy** appears once the order is paid. It prints the registered company name and address, and the business and VAT registration numbers **only if filled in** in [Settings](#settings), **Invoice and fulfilment**. If the company name and address are empty a yellow box warns you: fill them in before sending an invoice to a customer.',
          'It shows the items, any promo code, delivery, the total and "All prices include VAT." There is no VAT amount line. An unpaid order has no invoice.',
        ],
      },
      { kind: 'heading', text: 'What the customer is emailed' },
      {
        kind: 'table',
        caption: 'Order emails',
        head: ['When', 'Email'],
        rows: [
          ['Order placed', '"We have your order (number)". For bank transfer also "How to pay for order (number)", but only if the bank name, account name and account number are filled in.'],
          ['Paid (approved, PayPal confirmed, or late payment accepted)', '"Payment received for order (number)" with the invoice number.'],
          ['Shipped', '"Order (number) is on its way", with courier and tracking link.'],
          ['Closed because nobody paid', '"Order (number) has closed".'],
          ['Cancelled', '"Order (number) has been cancelled".'],
        ],
      },
      {
        kind: 'text',
        text: '**No email** is sent for Processing, Delivered, a corrected dispatch, a rejected bank transfer, a refund, a slip upload, or when a new order arrives for staff. If the shop\'s email account is not set up, nothing is sent and nothing warns you; see [Settings](#settings).',
      },
      { kind: 'heading', text: 'When an action is refused' },
      {
        kind: 'table',
        caption: 'Messages on order actions',
        head: ['Message', 'What happened'],
        rows: [
          ['"This order is now: (status)." with **Reload the order**', 'A colleague, or the system, acted first. Reload and look at the current state.'],
          ['"Somebody has already started processing this order." / "This order has already been marked as shipped." / "This order is already recorded as delivered..."', 'The same.'],
          ['"Choose the courier carrying this order. If the list is empty, add a courier in Settings first."', 'No courier chosen or none exists. Add one under Sales, [Couriers](#couriers) (not Settings). Adding needs Settings Edit, which Order Staff lack: ask a Store Manager.'],
          ['"Enter the tracking number the courier gave you..."', 'The tracking box is empty.'],
          ['"Use ordinary letters, digits and punctuation..."', 'The tracking number holds a hidden character picked up when copying (for example a non-breaking space from an email). Clear the box and type it again by hand.'],
          ['"Say why this order is being cancelled."', 'The cancel reason is empty.'],
          ['"That courier is no longer available. Reload the page and choose another."', 'The courier was switched off. When you correct a dispatch the form still lists the old courier, but saving is refused. Ask a Store Manager to tick **Available for new dispatches** on that courier again (see [Couriers](#couriers)), make the correction, then untick it.'],
          ['"This order has been paid, so it cannot be cancelled. A paid order is refunded instead..."', 'You cannot cancel a paid order.'],
          ['"A PayPal payment on this order is still being confirmed, so it cannot be cancelled here..."', 'Wait for PayPal.'],
        ],
      },
    ],
  },
  {
    id: 'payments',
    group: 'Orders and delivery',
    title: 'How customers pay: bank transfer and PayPal',
    summary:
      'The two payment methods, what is different for staff, and the switch that decides whether checkout offers them.',
    screens: [{ label: 'Settings', path: '/settings' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'warning',
        text: 'A new shop starts with **both** payment methods switched **off**, and the owner has decided that PayPal is the only method offered for now. With none on, checkout refuses every order ("No payment method is available at the moment..."). A Super Admin switches them on in [Settings](#settings), **Payment methods**. Check this before you open the shop.',
      },
      {
        kind: 'table',
        caption: 'Bank transfer compared with PayPal',
        head: ['Topic', 'Bank or online transfer', 'PayPal'],
        rows: [
          ['Offered at checkout', 'Only when switched on by a Super Admin.', 'Only when switched on **and ready**: client ID, secret, webhook ID and an exchange rate that is not too old.'],
          ['Time to pay', '48 hours from placing the order. Set by your developer; not editable on a screen.', '60 minutes by default (10 to 180), set in Settings, PayPal.'],
          ['Who marks it Paid', 'A staff member with Orders Approve, after checking the bank statement.', 'The system, automatically, when PayPal confirms. Staff do nothing.'],
          ['What the customer sends', 'Optionally a reference and a photo or PDF of the slip.', 'Nothing.'],
          ['Currency', 'Rupees.', 'Charged in US dollars: the rupee total, delivery included, converted at the shop\'s rate plus a buffer. Staff see it as "Charged by the provider".'],
          ['Money arrives late', 'Use **Accept late payment**.', 'Set aside and flagged with an alert. Refund it in the PayPal dashboard by hand.'],
          ['Refund', 'By hand, from the shop\'s bank.', 'By hand, in the PayPal dashboard.'],
        ],
      },
      {
        kind: 'list',
        items: [
          'The bank details customers see are set in [Settings](#settings), **Bank transfer** (Super Admin only). Check the account number twice: every customer who chooses bank transfer sees it. If you change the details, orders already placed keep the old details in the email they were sent, so keep the old account open for 48 hours.',
          'PayPal needs an up-to-date rate. Update **Rupees per US dollar**, or press "The rate is still correct", at least every 7 days, or PayPal disappears from checkout.',
          'Switching a method off never affects orders already placed.',
        ],
      },
    ],
  },
  {
    id: 'bank-transfers',
    group: 'Orders and delivery',
    title: 'Bank transfers: approve, reject and accept a late payment',
    summary:
      'Check the bank statement, then approve or reject each waiting bank transfer. Do it at least morning and afternoon.',
    screens: [{ label: 'Bank transfers', path: '/orders/bank-transfers' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Needs **Orders View** to see the queue and slips, and **Orders Approve** to approve, reject or accept a late payment (Order Staff and Store Manager have both). PayPal orders never appear here.',
      },
      {
        kind: 'text',
        text: 'The customer chooses bank transfer, places the order and is told to transfer the exact amount quoting the **order number** as the reference, within 48 hours. They may also type their bank\'s reference and upload a photo or PDF of the slip (up to 5 MB). **Nothing tells you when a slip arrives**, and an order appears in the queue the moment it is placed, even if the customer has sent nothing.',
      },
      {
        kind: 'steps',
        title: 'Work the queue',
        items: [
          'Open **Bank transfers** (heading "Bank transfers awaiting approval"). Orders are listed oldest first, 20 per page. Each shows a time pill ("5 h 12 min left", yellow under 6 hours, or red "Overdue by..."), the **Customer**, **Email**, **Amount**, **Their reference** and the **Slip** ("Download the slip", which opens as a download, never inside the admin).',
          'Look at the shop\'s **bank statement** for a credit that matches the amount **and** the reference. If there is no reference, match by amount, name and date, and approve only when you are certain.',
          'If the full amount has arrived, press **Approve**. The order becomes Paid, an invoice number is issued and the stock is taken. The card reads "(order) is paid. Invoice (number)." and stays until you press **Refresh**.',
          'If no credit is found, either leave it for the next check or type a **Note** (required when rejecting) and press **Reject**. The card reads "Transfer for (order) rejected. The order stays open until its deadline."',
          'Check again in the early afternoon, and finally at the end of the day, looking first at orders due to expire overnight.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Rejecting tells the customer **nothing**: no email, and their order page says nothing. After rejecting, telephone or WhatsApp them. The order leaves the queue; if the customer sends a new reference or slip it returns, otherwise it expires at the deadline. Approve only the **full** amount: there is no part-payment handling. A short or over payment is rejected with a note, or approved with the difference settled outside the system.',
      },
      { kind: 'heading', text: 'Accept a late payment' },
      {
        kind: 'text',
        text: 'If the order **expired** or was **cancelled** and the customer\'s transfer arrives afterwards, find the order on [Orders](#orders) (search its number, or press **Expired** or **Cancelled** in the **Show** buttons: it is no longer in the Bank transfers list) and press **Accept late payment**. If you press Approve in the queue on an order that has only just expired, the button turns into this. You may type **Why you are accepting it (optional)**. The shop checks every item is still in stock. If all are, the order becomes Paid with today\'s date, a new invoice number, and the customer is emailed "we have found your payment and reopened your order". If any item has sold since, **nothing changes** and you are told which items and how many are short: refund the money instead (see [Refunds](#refunds)). It works only for bank-transfer orders. If the only bank payment was rejected, there is no button: that order cannot be reopened, so refund the money by hand and ask the customer to order again.',
      },
      {
        kind: 'table',
        caption: 'Messages you may see',
        head: ['Message', 'What to do'],
        rows: [
          ['"This order has already been paid. Somebody else approved it, or the payment was confirmed another way."', 'Nothing. A colleague got there first.'],
          ['"This order expired before the transfer was approved... use Accept late payment..."', 'Press Accept late payment.'],
          ['"This order is not expired or cancelled, so there is no late payment to accept..."', 'Press Approve instead.'],
          ['"There is no bank transfer awaiting review on this order. It may already have been approved or rejected."', 'Reload the page.'],
          ['"Say why the transfer is being rejected..."', 'Type a Note, then Reject.'],
          ['"Items no longer available:" with a list', 'Refund the money received.'],
        ],
      },
      {
        kind: 'list',
        items: [
          'If two people press at once, exactly one succeeds; the other sees one of the messages above.',
          'Every approval and rejection is recorded in the [Audit log](#audit-log) and in the order\'s history, with your name.',
          'A duplicate transfer after approval is returned outside the system: record both bank references in the internal note.',
        ],
      },
    ],
  },
  {
    id: 'refunds',
    group: 'Orders and delivery',
    title: 'Refunds and payment problems (done by hand)',
    summary:
      'There is no Refund button in this version of the admin. How to refund a customer, and what to do about payment alerts.',
    screens: [{ label: 'Orders', path: '/orders' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'warning',
        text: '**Refunds are manual.** No screen refunds an order, nothing in the admin ever sets the "Refunded" status, and the "Orders Refund" permission only lets its holder see the two payment alerts. When **Accept late payment** is refused because stock has sold, the order page itself says: "Refunding from the system is not built yet... Record what you did in the internal note below so the next person reading this order knows." A refund therefore changes no figure on the dashboard or in reports, and puts no stock back.',
      },
      {
        kind: 'steps',
        title: 'Refund a customer',
        items: [
          'Agree the refund with a Store Manager or Super Admin. Money going out is never decided by the person who took the call.',
          '**Bank transfer order:** send the money back from the shop\'s bank account. Take the customer\'s bank details by phone or WhatsApp for that one transfer; never store them in the system. **PayPal order:** refund it in the PayPal dashboard.',
          'Open the order and write in the **Internal note**: the date, the amount, the reference and why. Press **Save the note**.',
          'If the goods came back and can be sold, add them back on the [Inventory](#inventory) screen with a reason such as "Returned, order SD-...". Nothing restocks automatically.',
          'Keep your own record of refunds, because the dashboard will not show them.',
        ],
      },
      { kind: 'heading', text: 'Payment alerts' },
      {
        kind: 'list',
        items: [
          '**"Payment received but not applied to order (number)"** means a PayPal payment arrived for an order that had expired, been cancelled, was already paid or did not match. Accept late payment is not available for PayPal. Open the order, refund the customer in the PayPal dashboard, note it on the order, then press **I have seen this**. If the reason is "the provider\'s reference could not be stored", check the payment in PayPal first.',
          '**"PayPal payment for order (number) could not be confirmed"**: look in PayPal by hand, the same day. A later notice from PayPal may still apply the payment.',
          'A refund, reversal or chargeback made inside PayPal is received and ignored: no alert appears and nothing changes in the shop. Watch your PayPal account.',
          'See [Alerts](#admin-alerts) for who sees these and for the full list.',
        ],
      },
      { kind: 'heading', text: 'When a customer phones about an order' },
      {
        kind: 'steps',
        items: [
          'Find the order (see [Orders](#orders)). Confirm who they are by the order date, the item and the amount before saying anything. Never read the address out first.',
          'Answer from the status: **Awaiting payment** (say when it is due and offer the bank details again), **Paid** or **Processing** (being packed), **Shipped** (read the courier and tracking number), **Delivered**, **Expired** (nobody paid by the deadline) or **Cancelled** (someone cancelled it: the History says who). If they say they paid by bank transfer, take the reference and use Accept late payment.',
          'Never ask for card numbers, security codes, passwords or PayPal credentials.',
          'Add an **Internal note**: who called, what was asked and what was promised.',
        ],
      },
    ],
  },
  {
    id: 'couriers',
    group: 'Orders and delivery',
    title: 'Couriers',
    summary: 'The delivery companies you can choose when you mark an order as shipped, and the tracking links customers get.',
    screens: [{ label: 'Couriers', path: '/orders/couriers' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Anyone with **Orders View** can see the list. Adding or changing a courier needs **Settings Edit**: Order Staff see the form but are refused when they save. Some messages say "Settings"; Couriers is under **Sales** in the menu.',
      },
      {
        kind: 'steps',
        title: 'Add or change a courier',
        items: [
          'Open **Sales, Couriers**. The form is headed **Add a courier** (or **Edit this courier** after you press **Edit** on a row).',
          'Fill **Name** (required, up to 80 characters, must be different from every other courier), **Telephone** (optional, up to 20) and **Order in the list** (0 to 999; lower comes first).',
          '**Tracking address** (optional): the courier\'s tracking web address with `{trackingNumber}` where the number goes, for example `https://www.domex.lk/Order-Details.php?wbno={trackingNumber}`. It must start with `https://`. Leave it empty if the courier has no online tracking; the number is then shown as plain text.',
          'Leave **Available for new dispatches** ticked, and press **Add the courier** or **Save the courier**.',
        ],
      },
      {
        kind: 'list',
        items: [
          'There is **no delete**. Untick **Available for new dispatches** instead: the courier leaves the shipping drop-down but old tracking links keep working. The row shows a **Not in use** pill and the count of orders sent with them.',
          'The tracking link on an order is built from the courier\'s **current** tracking address. Changing the address changes the link on older orders in the admin, but emails already sent keep the link they were sent with.',
          'Eight couriers are set up to start with (Domex, Koombiyo Delivery and Citypak are on; Pronto Lanka, Trans Express, Sri Lanka Post (Speed Post), Sri Lanka Post EMS and DHL Express are off). The tracking addresses for three were checked in October 2026. Check yours before relying on them.',
          'The courier\'s name is copied onto each order when it ships, so renaming a courier later does not rewrite old orders.',
        ],
      },
    ],
  },
  {
    id: 'delivery',
    group: 'Orders and delivery',
    title: 'Delivery: Sri Lanka, overseas and blocked countries',
    summary:
      'How delivery is priced, what staff can change, and how to stop a country at checkout.',
    screens: [
      { label: 'Overseas delivery', path: '/settings/overseas-delivery' },
      { label: 'Blocked countries', path: '/settings/blocked-countries' },
    ],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Both screens are reached from two links at the top of [Settings](#settings) (they are not in the menu, though the dashboard\'s list also links to Overseas delivery). **Settings View** lets you read them and **Settings Edit** lets you change them. Prices are in rupees only.',
      },
      { kind: 'heading', text: 'Inside Sri Lanka' },
      {
        kind: 'text',
        text: 'Delivery is priced by the customer\'s district, which belongs to a zone (Colombo, Western, or the rest of the island). **Standard** is offered everywhere; to start with, **Express** is offered only in the Colombo and Western zones. Some Standard rates are free above an order amount. The starting figures were placeholders for the owner to confirm. **No admin screen edits Sri Lankan delivery fees, free-delivery limits, delivery days or store-pickup locations in this version.** Ask your developer to change them. Store pickup is switched off and cannot be set up from the admin.',
      },
      { kind: 'heading', text: 'Overseas delivery' },
      {
        kind: 'callout',
        tone: 'warning',
        text: '**On a new shop overseas delivery is already switched ON, for almost every country, with placeholder fees** (from Rs. 6,500 to Rs. 12,500 a parcel). The page itself says "check every region before taking overseas orders." Until every region is priced and checked, untick **Accept orders to addresses outside Sri Lanka**. A fresh install offers **Standard only** everywhere until you type an **Express fee**.',
      },
      {
        kind: 'steps',
        items: [
          'Open [Overseas delivery](/settings/overseas-delivery). The tick box **Accept orders to addresses outside Sri Lanka** switches overseas delivery on or off. It saves at once. While it is off, checkout refuses every country but Sri Lanka.',
          'Under **Regions** there are six: Asia, Middle East, Europe, North America, Oceania and Rest of world. For each, set **Standard fee (Rs.)** (blank means not priced, so its countries cannot be ordered to) and **Standard estimated days** (fewest and most, whole days 1 to 90; both required). Optionally set **Express fee (Rs. - blank = not offered)** and **Express estimated days** (required if you set an Express fee). Press **Save (region)**.',
          'Under **Countries**, use **Find a country** and the **Region** drop-down. Every country uses its region\'s fee and days unless it has its own. Press **Change (country)** to tick or untick **Deliver to (country)** (unticking it blocks the country, exactly as on the Blocked countries page), or to set its own Standard and Express fee and days (leave blank to use the region\'s). Days must be given as both numbers or neither. Press **Save (country)**.',
        ],
      },
      {
        kind: 'list',
        items: [
          'Fees go up to Rs. 1,000,000 with at most two decimals. The customer sees "Estimated N-M working days".',
          'Switching a region off switches off every country in it.',
          'The country rows summarise **Standard only**. A country that has only an Express fee shows "Not delivered to - No standard fee set..." here, although checkout would take the order.',
          'There is **no free-delivery threshold overseas**. A free-delivery promo code does not waive an overseas fee.',
          'The order records "International delivery", the fee and the estimated days, **not** whether the customer chose Standard or Express.',
          'Orders already placed keep the fee and days they were bought at.',
          'Sanctioned countries are **not** blocked to start with. Which ones to block is the owner\'s decision.',
        ],
      },
      { kind: 'heading', text: 'Blocked countries' },
      {
        kind: 'steps',
        items: [
          'Open [Blocked countries](/settings/blocked-countries). All countries are open unless you block them.',
          'Find the country with **Search countries** (a name fragment or its two-letter code).',
          'Tick **Block (country)** to refuse it at checkout, or untick it to open it again. Each tick saves that one country at once; there is no Save button and no "block a whole region".',
        ],
      },
      {
        kind: 'list',
        items: [
          'The summary line reads "Currently blocked:" followed by two-letter codes. There is no reason or note box: only the [Audit log](#audit-log) records who changed it.',
          'A blocked country stays in the shop\'s country lists. The customer is told "We cannot deliver to (country) at the moment. Choose another country." and cannot place the order. Bags and the header say the same.',
          'A country can also be undeliverable because its region is switched off, overseas delivery is off, or it has no fee. The Blocked countries page does not show those; the Overseas delivery page shows the reason in the country\'s row.',
          'On a new shop seven territories with no street addresses (such as Antarctica) are already blocked, so the summary line lists seven codes. That is expected.',
          'A customer can still save an address in a blocked country; checkout refuses it.',
          'Changes reach the shop\'s country lists within about a minute.',
        ],
      },
    ],
  },
];
