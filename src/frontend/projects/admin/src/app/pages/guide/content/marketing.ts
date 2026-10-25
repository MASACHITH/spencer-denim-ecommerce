import { GuideSection } from '../guide.model';

/**
 * Marketing and customers: promo codes, sale campaigns, customers, the newsletter and contact enquiries.
 * Read from the validators, the use-case handlers and the admin screens.
 */
export const MARKETING_SECTIONS: readonly GuideSection[] = [
  {
    id: 'promo-codes',
    group: 'Marketing and customers',
    title: 'Promo codes',
    summary:
      'Codes a customer types at checkout: a percentage off, an amount off, or free delivery. Nothing is set up in advance; which codes you offer is yours to decide.',
    screens: [{ label: 'Promo codes', path: '/marketing/promo-codes' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Promotions View** to see the list, **Create** to add, **Edit** to change or switch off, **Delete** to remove. The Store Manager role has them. The form and buttons are shown even without the permission; you are refused ("You do not have permission to do that.") when you press one.',
      },
      {
        kind: 'steps',
        title: 'Add a code',
        items: [
          'Open **Marketing, Promo codes**. The form is at the top, headed **Add a promo code**.',
          'Type the **Code**: letters, numbers, hyphens and underscores only, no spaces, up to 40 characters. It is stored in capitals and matched whichever way the customer types it.',
          'Choose the **Type**: "Percentage off the items", "Fixed amount off the items" or "Free delivery". Then type the **Percentage** (above 0, up to 100) or **Amount off (Rs.)** (above 0). For a percentage you may also set **Most it can take off (Rs.)** as a cap.',
          'Set **Smallest order it works on (Rs.)** if wanted (blank means no minimum).',
          'Choose **Starts** (required). **Ends (optional)** is when it stops, the exact minute: a code ending at midnight works all through the evening before. Times are the shop\'s own (Colombo) time.',
          'Optionally set **Total uses** and **Uses per customer** (blank means no limit), a **Note to yourselves (optional)** (up to 250 characters, never shown to customers) and **Skip items a sale campaign has discounted**.',
          'Leave **Shoppers can use this code** ticked and press **Add the code**. You see "(CODE) is saved."',
        ],
      },
      {
        kind: 'table',
        caption: 'How the discount is worked out',
        head: ['Type', 'What it takes off'],
        rows: [
          ['Percentage', 'That percentage of the items, after any sale prices and before delivery, rounded to 2 decimals, then limited by the cap if there is one.'],
          ['Fixed amount', 'The amount, but never more than the eligible items. A Rs. 1,000 code on Rs. 800 of items takes off Rs. 800.'],
          ['Free delivery', 'The whole Sri Lankan delivery fee. It adds nothing to the items discount. It does **not** waive an overseas fee: the code is accepted but the checkout says "Free-delivery codes cover Sri Lankan delivery only."'],
        ],
      },
      {
        kind: 'list',
        title: 'Rules to know',
        items: [
          '**One code per order.** The promo box is only on the **checkout** page, not on the bag.',
          'A code works on top of any sale campaign price. Tick **Skip items a sale campaign has discounted** to leave out lines a campaign is pricing. A product that is merely marked down (it has an original price) still counts.',
          '**Smallest order** is compared with the items after sale prices, before the code and delivery.',
          'No order can fall below **Rs. 1.00**. If a code would take it lower, the discount is cut and the customer sees "(CODE) applied - discount limited to the order total."',
          '**Uses per customer** is counted by **email address**, so the same person using another address counts separately.',
          'All amounts are in rupees. A PayPal customer is charged the final rupee total converted to dollars, so a code works the same for every payment method.',
          '**Uses per customer** cannot be larger than **Total uses**: "A customer cannot be allowed more uses than the code has in total." Two codes cannot have the same text: "A promo code with that code already exists."',
          'A free-delivery code used on an overseas order waives nothing, but it still counts as a use.',
        ],
      },
      { kind: 'heading', text: 'Read the list' },
      {
        kind: 'text',
        text: 'Codes are listed newest first, with no search or paging. A pill appears only when the code is not live: **Starts later**, **Ended** or **Switched off**. A live code has no pill. A row shows the discount ("10% off, up to Rs. 2,000"), uses ("3 of 50 used", or "3 used, no limit"), the window and the note. The window shows as raw text such as `2026-10-13T00:00:00` (Colombo time), and the smallest order as a bare number. **Uses** counts held uses: it rises when an order is placed, before payment, and falls again if that unpaid order expires or is cancelled.',
      },
      { kind: 'heading', text: 'Change, switch off, remove' },
      {
        kind: 'list',
        items: [
          '**Edit** fills the form with the code. Change it and press **Save the code**. Every box can be edited, even after use. Orders already placed keep their own copy of the code and discount.',
          'To stop a code without deleting it, untick **Shoppers can use this code**, or move **Ends** to now. Neither affects orders already placed.',
          'If you lower **Total uses** below the uses already held you are warned: no new customer can use it, but placed orders keep their use. Saving a code whose end has passed warns that no one can use it.',
          '**Remove** is two steps (**Remove**, then **Yes, remove it**; **Keep it** backs out). The code text can then be used again for a new offer. Nothing on screen restores a removed code.',
          'There is no duplicate button, no usage-count edit, no export, and no promo report. The only per-code figure is **Uses** on the list.',
          'If someone else saved the same code you see "Somebody else changed this promo code while you were editing it. Reload and re-apply your changes." **Try again** reloads only the list: press **Edit** on the refreshed row again before re-applying.',
        ],
      },
      { kind: 'heading', text: 'What the customer sees when a code is refused' },
      {
        kind: 'table',
        caption: 'Messages at checkout',
        head: ['The customer reads', 'Why'],
        rows: [
          ['"We do not recognise that code. Check it and try again, or carry on without it."', 'No such code, switched off, removed, not started yet or ended. One message on purpose, so the box cannot be used to find out which codes exist.'],
          ['"That code has been fully used. You can place your order without it."', 'Uses have reached **Total uses**.'],
          ['"That code needs an order of at least Rs. X."', 'Below **Smallest order it works on**.'],
          ['"That code does not apply to anything in your bag."', 'Every line is excluded, for example all items are on a campaign and the code skips sale items.'],
          ['"You have already used that code. You can place your order without it."', 'Per-customer limit reached. This is only discovered when they press Place order.'],
        ],
      },
      {
        kind: 'text',
        text: 'When the customer presses Place order the code is checked again. If it stopped working, the order is not placed ("Nothing has been placed.") and the bag is priced again. A code is not remembered: if the customer closes the tab, they must type it again.',
      },
    ],
  },
  {
    id: 'sale-campaigns',
    group: 'Marketing and customers',
    title: 'Sale campaigns',
    summary:
      'Take a percentage or an amount off every product in the departments, categories and products you choose, between a start and an end.',
    screens: [{ label: 'Sale campaigns', path: '/marketing/sale-campaigns' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Promotions View, Create, Edit, Delete**. To search products in the picker you also need **Products View**, and to list departments and categories **Categories View** (the Store Manager has both). To upload a banner you need **Media Create**.',
      },
      {
        kind: 'steps',
        title: 'Add a campaign',
        items: [
          'Open **Marketing, Sale campaigns** and fill the form headed **Add a sale campaign**.',
          '**Name** (required, up to 100 characters) is for your own records: customers see the price, not the name.',
          '**Type**: "Percentage off each price" or "Fixed amount off each price". Enter **Percentage (up to 90)** or **Amount off (Rs.)**.',
          '**Starts** and **Ends**. Both are required, in the shop\'s (Colombo) time, and the end is the moment the sale stops.',
          'Under **What it applies to**, choose **Add**: "A whole department", "One category" (tick **Include everything under this category** if you want its sub-categories) or "Individual products". For a department or category, choose it and then press **Add to the campaign**: one you chose but did not add is silently left out. For products, use **Find a product**: type part of a name, code or SKU, then press Enter to add the highlighted one (a product is added the moment you pick it). Add at least one target, and no more than 200.',
          'Optionally add a **Banner (optional)** (see below).',
          'Leave **Run this campaign** ticked and press **Add the campaign**. You see "(Name) is saved. N products were repriced." and any warnings.',
        ],
      },
      {
        kind: 'list',
        title: 'How the price is worked out',
        items: [
          'The discount is taken off the product\'s **list price** (the price you entered, or the size\'s own price), **not** off the crossed-out original price. It is worked out for each size separately and never goes below **Rs. 1.00**.',
          '**Campaigns never add up.** If a product is reached by two campaigns, or twice by one, it gets the **lower resulting price** once. No campaign outranks another by type, and a product target does not beat a department target.',
          'The shop shows the new price, the original price crossed out and the percentage off, which can be bigger than the campaign\'s own percentage if the product also has an original price.',
          'A [promo code](#promo-codes) works on top of the campaign price unless the code is set to skip sale items.',
          'Prices in the bag, at checkout and when an order is placed follow the campaign **from the first second to the last**. Listing pages, the Sale page and the homepage Sale row can be up to **5 minutes late** at the start and at the end.',
          'Orders already placed keep the price they were bought at. A bag that is already open shows the new price with "The price of this item has changed since you added it."',
        ],
      },
      {
        kind: 'table',
        caption: 'Messages when saving',
        head: ['Message', 'Meaning'],
        rows: [
          ['"This campaign\'s targets do not reach any product, so no price has changed."', 'You chose targets with no products in them.'],
          ['"This campaign\'s end date has already passed, so it is not discounting anything."', 'The end is in the past.'],
          ['"The banner image has no description..."', 'Add a description to the banner so screen readers can read it.'],
          ['"90% is the largest discount a campaign may take, and it leaves a tenth of each price."', 'You chose exactly 90%.'],
        ],
      },
      { kind: 'heading', text: 'Banner on the Sale page' },
      {
        kind: 'text',
        text: 'A banner shows across the top of the **Sale page** only while the campaign is live. Type **Describe the picture** (up to 250 characters), choose a PNG, JPEG or WebP picture of up to 8 MB with **Choose a picture**, press **Upload the picture**, then **Save the campaign** to keep it. It is best about three times as wide as it is tall; see [Sizes for every place](#photo-sizes). If two live campaigns both have a banner, the one that started most recently shows. Customers see only the picture, never the campaign\'s name.',
      },
      { kind: 'heading', text: 'Change, end early, remove' },
      {
        kind: 'list',
        items: [
          '**Edit** a row, change it and press **Save the campaign**. The targets are replaced with exactly what is in the list, and every product affected before or after the change is repriced in the same save.',
          'To end a sale early, move **Ends** to now or earlier, or untick **Run this campaign**. The bag and checkout return to normal prices immediately; listings follow within 5 minutes.',
          '**Remove** is two steps. The prices it was discounting are put back. Orders it priced keep their link to it. Nothing restores a removed campaign.',
          'The list shows a pill for every campaign: **Live**, **Starts later**, **Ended** or **Switched off**, with the discount, what it applies to, the window and the banner. A target whose item was removed shows "(removed from the catalogue)".',
          'Conflicts show "Somebody else changed this campaign while you were editing it. Reload and re-apply your changes."',
        ],
      },
    ],
  },
  {
    id: 'customers',
    group: 'Marketing and customers',
    title: 'Customers',
    summary:
      'Look up registered customers, see their saved addresses and order history. The screen is read-only.',
    screens: [{ label: 'Customers', path: '/customers' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permission: **Customers View** (Store Manager and Order Staff). A customer account is someone who registered. A **guest** who checked out without an account is not listed: find their orders on the [Orders](#orders) screen by email address.',
      },
      {
        kind: 'steps',
        title: 'Find a customer',
        items: [
          'Open **Customers**. Type in **Find a customer** an email address (matched from its start), a telephone number, or part of a name, and press **Search**. A telephone number is matched on its last nine digits, so `0771234567` finds `+94771234567`.',
          'Optionally choose a **Country** to narrow the list. It means the country of the customer\'s default saved address, so a customer with no saved address is in no country. The box lists Sri Lanka and every country at least one customer\'s default address is in. Choosing a country other than Sri Lanka hides the district box, because districts are Sri Lankan.',
          'Optionally choose a **Default district** to narrow the list.',
          'Open a customer by pressing their name. The list shows 25 per page, newest registration first, with telephone, district, country, order count, lifetime spend, last order and registration date.',
        ],
      },
      {
        kind: 'list',
        title: 'On a customer\'s page',
        items: [
          'Email, telephone, default district, registration date, last signed in (Colombo time), order count, **lifetime spend** (the total of their paid orders) and last order.',
          '**Saved addresses** (up to 10), with a **Default** pill on the main one.',
          '**Order history**: each order\'s number (a link), status, total and date, 10 per page. A guest order placed with the same email appears here only after the customer confirms their email address. Opening an order needs the **Orders View** permission.',
          'Warnings: "Email not confirmed" and "Disabled".',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'The Customers screen has **no buttons that change anything**: you cannot edit a customer, disable or unlock an account, reset a password, add notes, merge accounts, export the list, or delete or anonymise a customer. A customer who forgot their password uses "Forgot password" on the shop. For a request to delete someone\'s data, see [Customer data, privacy and backups](#data-privacy). Viewing a customer is not recorded in the audit log, so treat every page as confidential.',
      },
    ],
  },
  {
    id: 'newsletter',
    group: 'Marketing and customers',
    title: 'Newsletter subscribers',
    summary: 'The list of people who signed up from the homepage or the footer, and the audited export of their addresses.',
    screens: [{ label: 'Newsletter subscribers', path: '/marketing/newsletter' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Newsletter View** to see the list and **Newsletter Export** to download it (Store Manager). With View only you see "Your role can see subscribers but not export them."',
      },
      {
        kind: 'text',
        text: 'People subscribe with one step: they type an address in the homepage Newsletter block or the footer, with no confirmation email. The shop records the time (consent), where they signed up and their IP address as proof of consent. The shop sends no newsletters itself: you take the exported list to your own email tool.',
      },
      {
        kind: 'steps',
        title: 'Find and export subscribers',
        items: [
          'Open **Newsletter subscribers**. The line under the heading shows "N subscribed - N unsubscribed" for the whole list.',
          'Use **Email address contains** (up to 100 characters), **Status**, and **Consent from** / **Consent to** (Colombo days, both included), then press **Apply filters**. Rows show the email, status, consent date and where they signed up ("Homepage" or "Footer"), 25 per page.',
          'Press **Export CSV**. It downloads `newsletter-subscribers-` and the date `.csv` with subscribed addresses only, matching the search and dates you last applied. Columns: Email, Source, Consent given (Colombo time).',
        ],
      },
      {
        kind: 'list',
        items: [
          '**Every export is recorded in the audit log against your name**, with the row count and filters (not the addresses). If the record cannot be written, no file is made.',
          'The file contains personal data. Use it only in the shop\'s email tool, do not pass it on by chat or email, and delete your copy afterwards.',
          'Addresses that start with `=`, `+`, `-` or `@` get a leading apostrophe in the file so spreadsheets do not treat them as formulas.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Known gap: there is **no way to unsubscribe anyone**. No button on this screen, no unsubscribe link in any email, and nothing in the system sets the "Unsubscribed" status, so that counter normally shows 0. If someone asks to be removed, keep your own list of removals in your email tool and never email them. The sign-up form promises "Unsubscribe from any email", so tell the owner about this gap.',
      },
    ],
  },
  {
    id: 'enquiries',
    group: 'Marketing and customers',
    title: 'Contact enquiries',
    summary:
      'Messages sent through the shop\'s Contact page. Nothing notifies you when one arrives, so check this inbox every day.',
    screens: [{ label: 'Contact enquiries', path: '/enquiries' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'warning',
        text: 'This screen has **no link in the menu or on the dashboard**. Open it from here, or type its address (the admin address followed by `/enquiries`). The shop sends no email to staff or to the customer when a message arrives, and there is no unread badge anywhere.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions are the Customers ones: **Customers View** to open the inbox and read (opening a New message marks it Read), **Customers Edit** to mark Replied or Spam, **Customers Delete** to delete for good. Order Staff can read only. A Content Editor cannot open it.',
      },
      {
        kind: 'steps',
        title: 'Work the inbox',
        items: [
          'Open **Contact enquiries**. Messages are newest first. Use the **Show** buttons: All, New, Read, Replied, Spam. The line "N unread of M shown" counts every message still New.',
          'Each row shows the name, subject, status, the first 120 characters, any order number, and "(Status) by (staff email)" once handled. Press the name to open it.',
          'Opening shows the email (a link), telephone (a link, if given), when it arrived, the sender\'s IP address and the full message. A New message becomes **Read**.',
          '**Reply from your own email or phone.** There is no reply box. Then press **Mark Replied** so the team can see it is dealt with.',
          'Press **Mark Spam** for junk. It stays stored.',
        ],
      },
      {
        kind: 'list',
        items: [
          'The inbox loads only the **newest 25** messages for the filter you chose and has no next page. Move handled messages to Replied or Spam to reach older ones.',
          '"Arrived" shows the raw UTC time stored (for example `2026-10-20T04:15:00Z`), not Colombo time.',
          'An order number a visitor types is stored as typed and is not checked against real orders. Search it on the [Orders](#orders) screen.',
          'If two people set the same status, the second is ignored and the first person\'s name stays.',
        ],
      },
      { kind: 'heading', text: 'Delete a message that holds card or bank details' },
      {
        kind: 'steps',
        items: [
          'Open the message and expand **Delete this message for good**.',
          'Type **Why** (at least 3 characters, up to 500). It is recorded in the audit log; the message is not.',
          'Press **Delete for good**. You see "The message has been deleted. It cannot be recovered." This needs **Customers Delete**.',
        ],
      },
      {
        kind: 'text',
        text: 'Policy keeps enquiries for two years and clears IP addresses after 90 days, but no automatic clean-up exists yet, so messages stay until someone deletes them by hand.',
      },
    ],
  },
];
