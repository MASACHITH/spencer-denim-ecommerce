import { GuideSection } from '../guide.model';

/**
 * Start here: the map of the admin, the setup checklist and the daily routine. The routine follows the
 * project's operating notes, corrected for what the screens can actually do.
 */
export const START_SECTIONS: readonly GuideSection[] = [
  {
    id: 'start-here',
    group: 'Start here',
    title: 'Welcome: how the admin is organised and how to use this guide',
    summary:
      'A map of the menu, the habits that apply on every screen, and the things most people get caught out by.',
    blocks: [
      {
        kind: 'text',
        text: 'This admin panel runs the whole shop: products, orders, delivery, promotions, the shop\'s pages, staff and settings. This guide covers every screen, how to do each job, and what the pictures and video you upload must look like. Use the **search box** at the top to find a job, the **contents list** to jump to a section, and **Print or save as PDF** to make a copy for training. Words in **bold** are buttons, boxes and labels exactly as they appear on screen.',
      },
      { kind: 'heading', text: 'The menu' },
      {
        kind: 'table',
        caption: 'The left-hand menu',
        head: ['Group', 'What is in it', 'Where to read about it'],
        rows: [
          ['Overview', 'Dashboard, Reports, Website traffic.', '[Dashboard](#dashboard), [Reports](#reports), [Traffic](#traffic)'],
          ['Sales', 'Orders, Bank transfers, Couriers, Customers.', '[Orders](#orders), [Bank transfers](#bank-transfers), [Couriers](#couriers), [Customers](#customers)'],
          ['Catalogue', 'Products, Inventory, Departments and categories, Reviews.', '[Products](#products), [Inventory](#inventory), [Categories](#categories), [Reviews](#reviews)'],
          ['Marketing', 'Promo codes, Sale campaigns, Newsletter subscribers.', '[Promo codes](#promo-codes), [Sale campaigns](#sale-campaigns), [Newsletter](#newsletter)'],
          ['Content', 'Homepage, Announcement bar, Mega menu cards, Size guides, Content pages.', '[Homepage](#homepage), [Announcement bar](#announcements), [Mega menu](#mega-menu), [Size guides](#size-guides), [Content pages](#content-pages)'],
          ['Administration', 'Settings, Audit log, Staff users, Roles.', '[Settings](#settings), [Audit log](#audit-log), [Staff users](#staff-users), [Roles](#staff-roles)'],
          ['Help', 'This guide.', 'You are here.'],
        ],
      },
      {
        kind: 'text',
        text: 'Not in the menu: [Contact enquiries](#enquiries), [Overseas delivery](#delivery) and [Blocked countries](#delivery) (links at the top of Settings), [Change password](#sign-in) and [Account security](#two-factor). This guide links to each. On a phone or small screen, open the menu with the button at the top left.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'You only see links, cards and buttons your **role** allows in a few places; most links are shown to everyone, and pressing one you may not use shows "You do not have permission to do that." See [Roles and permissions](#staff-roles).',
      },
      { kind: 'heading', text: 'Habits that apply everywhere' },
      {
        kind: 'list',
        items: [
          '**Money** is in rupees, written "Rs. 15,596.60". PayPal customers are charged in US dollars, worked out from the rupee total.',
          '**Dates and times** are Colombo time (UTC+05:30, 24-hour), with three exceptions that show UTC: the Audit log, the Staff users "Last sign-in" column and the "Arrived" line of a contact enquiry.',
          '**Save buttons:** most screens save with their own button. Several screens show no visible "Saved" message: look for your change on the page, or for a red message if it failed.',
          '**There is no preview and no "unsaved changes" warning** on content screens. Save before you leave, then look at the shop in another browser tab.',
          '**Two people editing the same thing:** the second to save is refused and told to reload and try again.',
          '**Moving things up and down** uses Move up and Move down buttons, never dragging.',
          '**Removing** something usually asks twice: Remove, then "Yes, remove it". Removed items generally cannot be brought back from the screen.',
          'Everything important you change is recorded in the [Audit log](#audit-log).',
        ],
      },
      { kind: 'heading', text: 'Ten things worth knowing first' },
      {
        kind: 'steps',
        items: [
          'Both payment methods start **switched off**. Customers cannot check out until a Super Admin switches one on in [Settings](#settings). See [How customers pay](#payments).',
          '**Refunds are done by hand** (bank or PayPal) and noted on the order; the dashboard will not show them. See [Refunds](#refunds).',
          'Photographs must be JPEG, PNG or WebP, and video MP4 in H.264 under 50 MB. iPhone HEIC photos and HEVC video are refused. See [Photos and video](#photos-and-video).',
          'The **Description** box on a product needs `<p>` tags for paragraphs. See [Product form](#product-fields).',
          'Always type a **Swatch colour** for every colour, or the colour dot is invisible.',
          'A product is created **not published**. Press **Put on the shop** when it has at least one size switched on, a gallery photograph and alt text on every photograph (and add its stock on Inventory so it can be bought).',
          'Check the [alert bell](#admin-alerts) and the [Bank transfers](#bank-transfers) queue every morning. [Contact enquiries](#enquiries) send no notification.',
          'Roles cannot be edited, there is no staff password reset, and staff accounts cannot yet be created on screen. Ask your developer to make sure there are **two** Super Admins, and keep your recovery codes. See [Staff users](#staff-users).',
          'Overseas delivery fees begin as placeholders. Check every region before taking an overseas order. See [Delivery](#delivery).',
          'Look at [What the admin cannot do yet](#not-built) before promising a customer something.',
        ],
      },
    ],
  },
  {
    id: 'setup-checklist',
    group: 'Start here',
    title: 'Setting up the shop: first-time checklist',
    summary:
      'The order to do things in before the shop takes real orders. Most items need a Super Admin.',
    blocks: [
      {
        kind: 'steps',
        title: 'Accounts and security',
        items: [
          'Sign in with the temporary password your developer gave you, choose a new one, and (as a Super Admin) set up two-factor authentication. Save the recovery codes. See [Signing in](#sign-in) and [Two-factor authentication](#two-factor).',
          'Ask your developer to create a second Super Admin, so one can act when the other is away, and accounts for the people who will help: the New staff user screen cannot do this yet. Then give each person their roles on the [Staff users](#staff-users) screen.',
        ],
      },
      {
        kind: 'steps',
        title: 'Settings (Super Admin for the money items)',
        items: [
          '**Logo, Contact and social:** upload the logo; fill WhatsApp, telephone, email, address, opening hours, map and social links. See [Settings](#settings).',
          '**Invoice and fulfilment:** the registered company name and address and the registration numbers, or invoices print without them.',
          '**Email account:** enter the mail server details and press **Send test email**. Until this works, no order email is sent and nothing warns you.',
          '**Bank transfer:** only if you will switch it on (the owner decided that PayPal alone is offered for now): bank name, account name and account number, typed by you and checked twice.',
          '**PayPal:** start in Sandbox with test credentials, webhook ID and the exchange rate; check "Ready"; test; then move to Live with live credentials.',
          '**Payment methods:** switch on the methods you will accept.',
          '**Product page text:** the Delivery text and Returns text. **Reviews:** keep approval on.',
        ],
      },
      {
        kind: 'steps',
        title: 'Delivery and couriers',
        items: [
          'Overseas delivery starts **on** with placeholder fees, so untick **Accept orders to addresses outside Sri Lanka** on [Overseas delivery](/settings/overseas-delivery) until you have checked every region\'s **Standard fee** and days, added **Express** fees where you offer it, and blocked any countries you will not ship to on [Blocked countries](/settings/blocked-countries).',
          'Sri Lankan delivery fees cannot be changed in the admin: ask your developer to confirm them.',
          'Check the tracking addresses on [Couriers](/orders/couriers), and switch on the couriers you use.',
        ],
      },
      {
        kind: 'steps',
        title: 'Catalogue',
        items: [
          'Check the [categories](#categories). Create the size guides you need ([Size guides](#size-guides)).',
          'Add products ([Products](#products)) with good photographs ([Photos and video](#photos-and-video)). Enter the stock for every size on [Inventory](#inventory).',
        ],
      },
      {
        kind: 'steps',
        title: 'Shop content',
        items: [
          'Write the About, Returns and Shipping pages and your own Privacy policy and Terms (reviewed against the PDPA), and put them on the shop. See [Content pages](#content-pages).',
          'Fill the [Homepage](#homepage) blocks (hero video, banners, social gallery), the [Announcement bar](#announcements) and the [Mega menu cards](#mega-menu), replacing the placeholder wording.',
        ],
      },
      {
        kind: 'steps',
        title: 'Before you open',
        items: [
          'Place a test order through each payment method you will use, from the shop in another browser tab. Check the emails arrive, process the order here through to shipped, and print the invoice.',
          'Check the homepage and a product page on a phone as well as a computer.',
        ],
      },
    ],
  },
  {
    id: 'daily-routine',
    group: 'Start here',
    title: 'Your daily, weekly and monthly routine',
    summary:
      'What to check and when. Times are suggestions. The person who usually does each job is in brackets.',
    blocks: [
      { kind: 'heading', text: 'Every morning (by 10:00)' },
      {
        kind: 'steps',
        items: [
          '**Alerts** (Super Admin; Order Staff and Store Manager for payment alerts): press the bell at the top right of the screen and open every alert. See [Alerts](#admin-alerts). A settings-change alert: confirm it was expected. A payment alert: deal with it today.',
          '**Bank transfers** (Order Staff, while bank transfer is switched on): check the bank statement and approve the matches; reject, with a note, where no credit has arrived. Look first at orders due to expire today. See [Bank transfers](#bank-transfers).',
          '**Expired orders from the last 24 hours** (Order Staff): compare with the bank statement. If money arrived, use **Accept late payment**.',
          '**Paid orders** (Order Staff): press **Start processing** on each, oldest first.',
          '**Contact enquiries** (Store Manager): open [Contact enquiries](#enquiries), reply from your own email or phone, and mark each Replied. Nothing notifies you.',
          '**Failed emails** (Super Admin): for each "Email not delivered" alert, open the order and telephone or WhatsApp the customer, then press **I have seen this**.',
        ],
      },
      { kind: 'heading', text: 'During the day' },
      {
        kind: 'steps',
        items: [
          'Pack and book couriers, then mark each order **shipped** with the courier and tracking number before the courier\'s cut-off. See [Processing an order](#order-fulfilment).',
          'Check bank transfers again in the early afternoon.',
        ],
      },
      { kind: 'heading', text: 'End of the day' },
      {
        kind: 'steps',
        items: [
          'A final bank-transfer check, especially orders due to expire overnight.',
          'Mark orders **delivered** where the courier has confirmed.',
          'Make agreed refunds by hand and record each in the order\'s internal note.',
        ],
      },
      { kind: 'heading', text: 'Every week' },
      {
        kind: 'list',
        items: [
          '**PayPal rate** (Super Admin): update **Rupees per US dollar**, or press "The rate is still correct", at least every 7 days, or PayPal disappears from checkout.',
          '**Low stock** (Store Manager): look at the dashboard\'s low-stock card and the [Inventory](#inventory) screen; record deliveries and stock-takes with a reason.',
          '**Reviews** (Content Editor): moderate the waiting reviews. See [Reviews](#reviews).',
          '**Promotions** (Store Manager): check upcoming campaign dates and code limits; switch off finished codes.',
          '**Content** (Content Editor): check the announcement messages, homepage blocks and Trending items links for the coming week.',
          '**Stuck orders** (Store Manager): orders marked Delivered automatically in the last week (confirm with the courier), and orders in Processing for more than two days.',
          '**Sales report** (Store Manager): this week against last week. Remember refunds are not in it.',
        ],
      },
      { kind: 'heading', text: 'Every month' },
      {
        kind: 'list',
        items: [
          '**Staff access** (Super Admin): deactivate anyone who has left, check roles, and confirm two Super Admins can sign in with two-factor.',
          '**Audit log spot check** (Super Admin): settings changes, exports, deactivations. See [Audit log](#audit-log).',
          '**Newsletter export** (Store Manager) for your email tool. See [Newsletter](#newsletter).',
          '**Delivery settings** (Store Manager): fees, regions, blocked countries and couriers still correct.',
          '**Backups** (technical owner): confirm a recent backup exists off the server.',
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Escalation: an alert nobody has acknowledged for 24 hours goes to a Super Admin by phone or WhatsApp, not email (a mail problem may be the alert). A payment alert is dealt with the same day. If the only Super Admin is away, the second one acts; if both are away, tell the technical owner. Pressing "I have seen this" never solves anything. Write the names and numbers of your Super Admins, Store Manager and technical owner on your handover sheet and keep it up to date.',
      },
    ],
  },
];
