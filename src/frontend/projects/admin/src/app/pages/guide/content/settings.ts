import { GuideSection } from '../guide.model';

/**
 * Settings and records: the Settings screen panel by panel, the audit log, the alert bell, and data and
 * privacy. Read from the settings catalogue, the settings validators and the admin screens.
 */
export const SETTINGS_SECTIONS: readonly GuideSection[] = [
  {
    id: 'settings',
    group: 'Settings and records',
    title: 'Settings: every panel and box',
    summary:
      'How to change a setting, which panels you see, what each box does and its limits, and how soon a change reaches the shop.',
    screens: [
      { label: 'Settings', path: '/settings' },
      { label: 'Overseas delivery', path: '/settings/overseas-delivery' },
      { label: 'Blocked countries', path: '/settings/blocked-countries' },
    ],
    blocks: [
      { kind: 'heading', text: 'How to change a setting' },
      {
        kind: 'steps',
        items: [
          'Open **Settings** and find the panel.',
          'Change the boxes. The hint under each box says what it does and what happens if you leave it empty.',
          'Press that panel\'s own **Save** button. Each panel saves on its own: there is no single Save for the page. While saving, the button says "Saving...".',
          'There is usually **no visible "Saved" message** on this screen: confirmations are read out to screen readers only. After saving, check that your values are still in the boxes and that no red message appeared. The page reloads from what was saved.',
        ],
      },
      {
        kind: 'list',
        items: [
          '**You only see the panels you are allowed to use.** A Store Manager (Settings Edit) sees the Logo, Contact and social, Invoice and fulfilment, Traffic, Product page and Reviews panels. The bank, PayPal and payment-method panels need Settings Payments, and the Email account panel needs Settings Security, which in practice means a Super Admin. With only Settings View (no default role is like that) you see "There are no settings you can change here."',
          '**Secrets** (the PayPal client secret, the email password) show an empty box with "A secret is saved. Type a new one to replace it." Leaving the box empty keeps the saved one. The value is stored encrypted and never shown again.',
          'If someone else saved the same panel first, your save is refused ("Somebody else changed the email account while you were editing it. Reload the page to see what they changed.") and you reload and try again.',
          'Every saved change is recorded in the [Audit log](#audit-log), key by key, with secrets shown only as "[secret changed]".',
        ],
      },
      { kind: 'heading', text: 'How to add a setting' },
      {
        kind: 'callout',
        tone: 'note',
        text: 'You cannot add a setting. The Settings screen shows a fixed list of 45 settings, grouped into the panels below. A new one appears only when a new version of the software is installed. If a setting is missing in the database, saving fails with a general "Something went wrong" message: tell your developer to run the settings seeder. Things that are not on the screen (see the end of this section) are set by your developer on the server.',
      },
      { kind: 'heading', text: 'Logo' },
      {
        kind: 'table',
        caption: 'Logo panel (Settings Edit; the upload also needs Media Create)',
        head: ['Box', 'What to enter', 'Limits and notes'],
        rows: [
          ['Upload a logo / Replace the logo', 'A flat logo file with a transparent background, about 600 x 150 pixels.', 'PNG, JPEG or WebP, up to 2 MB. A JPEG shows its background as a rectangle. With no logo the header shows the shop\'s name as text, which is a perfectly good header.'],
          ['Describe the logo (alternative text)', 'A short description. It starts as the shop\'s name.', 'Up to 200 characters.'],
          ['Remove the logo', 'Takes the logo out of the header.', 'Every page shows the change at once.'],
        ],
      },
      { kind: 'heading', text: 'Contact and social' },
      {
        kind: 'table',
        caption: 'Contact and social panel (Settings Edit; saves together)',
        head: ['Box', 'What to enter', 'Limits and notes'],
        rows: [
          ['WhatsApp number', 'Digits only, in international form: `94771234567`.', '7 to 15 digits, no plus sign, no spaces, no leading zero. Empty hides the WhatsApp button.'],
          ['Telephone number', 'For example `+94 11 234 5678`.', 'Up to 25 characters: digits, spaces, brackets, hyphens and a leading plus; 7 to 15 digits. Shown on the Contact page and footer.'],
          ['Contact email address', 'The address customers can write to.', 'A valid email address. It is only shown; the shop never sends mail from it.'],
          ['Postal address', 'One line per line.', 'Plain text, up to 400 characters.'],
          ['Opening hours', 'One line per line.', 'Plain text, up to 400 characters.'],
          ['Map embed address', 'In Google Maps choose Share, Embed a map, and copy the address inside src="...".', 'https only, and only from Google Maps or OpenStreetMap; up to 2,048 characters. Empty shows no map.'],
          ['Facebook page, Instagram profile, TikTok profile, YouTube channel', 'The full address of each profile, for example `https://www.instagram.com/yourshop`.', 'A full web address starting with https://. Empty hides that icon in the footer.'],
        ],
      },
      { kind: 'heading', text: 'Payment methods, bank transfer and PayPal (Settings Payments)' },
      {
        kind: 'list',
        items: [
          '**Payment methods:** a switch each for "Bank or online transfer" and "PayPal". A new shop starts with both **off**. PayPal can only be switched on once it is ready (below). Switching off is always allowed, even the last method, but then "Checkout will refuse every order until a payment method is switched on and ready." Orders already placed are not affected. A change raises a security alert (see [Alerts](#admin-alerts)).',
          '**Bank transfer:** **Bank name**, **Account name** and **Account number** are required before customers see anything (otherwise their order page says the details are being confirmed). **Branch (optional)** up to 120 characters. The account number takes digits, spaces and dashes only, up to 40. **Extra instructions (optional)** up to 600 characters. The owner should type these in the live admin themselves, never in a message.',
        ],
      },
      {
        kind: 'table',
        caption: 'PayPal panel (Settings Payments)',
        head: ['Box', 'What it does', 'Limits and notes'],
        rows: [
          ['Environment', '"Sandbox (test money, nothing real is charged)" or "Live (real payments)".', 'Use credentials that belong to the chosen environment. Takes effect at once. Starts as sandbox.'],
          ['Client ID', 'From the PayPal developer dashboard.', 'Up to 200 characters, no spaces.'],
          ['Client secret', 'From the PayPal developer dashboard.', 'Secret. Up to 300 characters, no spaces. Empty keeps the saved one.'],
          ['Webhook ID', 'The ID of the webhook registered in PayPal for the shop\'s PayPal notification address.', 'Up to 60 characters. PayPal is not offered without it.'],
          ['Rupees per US dollar', 'Used to convert each order to dollars at checkout.', 'More than 0, up to 10,000, up to 6 decimals.'],
          ['Currency buffer (%)', 'Added on top of the converted amount.', '0 to 20. Starts at 3.'],
          ['Rate valid for (days)', 'PayPal is hidden at checkout when the rate is older than this.', '1 to 30. Starts at 7.'],
          ['The rate is still correct (button)', 'Confirms the rate without changing it, so PayPal stays on sale for another period.', 'Needs a saved rate.'],
          ['Time to pay (minutes)', 'How long a customer has to finish paying before the order expires and the items go back on sale.', '10 to 180. Starts at 60. It is on the PayPal panel, so only a Super Admin sees and changes it.'],
          ['Capture lock (hours)', 'An order being collected cannot expire for this long.', '1 to 24. Starts at 3.'],
          ['Recovery window (hours)', 'After this a "could not be confirmed" alert is raised.', '1 to 168. Starts at 24.'],
          ['Hold for pending payments (days)', 'How long items are held when PayPal reports a payment as pending.', '1 to 14. Starts at 5.'],
        ],
      },
      {
        kind: 'text',
        text: 'The panel ends with a readiness line: "Ready. The credentials and a fresh rate are in place." or "Not ready. PayPal is not offered at checkout yet. Still needed: ...". Because PayPal charges in dollars while the shop is priced in rupees, **update the rate before it is older than "Rate valid for"** (7 days to start with), or press "The rate is still correct"; when it is older than the limit PayPal disappears from checkout. Saving raises a security alert.',
      },
      { kind: 'heading', text: 'Invoice and fulfilment' },
      {
        kind: 'table',
        caption: 'Invoice and fulfilment panel (Settings Edit)',
        head: ['Box', 'What it does', 'Limits and notes'],
        rows: [
          ['Registered company name', 'The company that issues invoices. This is not the shop\'s name.', 'Up to 200 characters. Applies to new invoices.'],
          ['Registered address', 'The company\'s address on invoices (not the contact address).', 'Up to 400 characters, one line per line.'],
          ['Business registration number (optional)', 'Printed in the invoice header.', 'Up to 40 characters.'],
          ['VAT registration number (optional)', 'Printed in the invoice header. Prices already include VAT, so there is no VAT line.', 'Up to 40 characters.'],
          ['Mark delivered after', 'A daily job marks Shipped orders Delivered after this many days. Store pickup orders are never marked automatically.', 'Whole days, 1 to 90. Starts at 7. Empty stops the automatic closing, and staff confirm every delivery by hand.'],
        ],
      },
      { kind: 'heading', text: 'Traffic, product page text, reviews' },
      {
        kind: 'table',
        caption: 'Other panels (Settings Edit)',
        head: ['Box', 'What it does', 'Limits and notes'],
        rows: [
          ['Keep page views for (days)', 'How long each individual page view is kept. Daily totals on the Website traffic page are kept for ever.', '30 to 365, cannot be empty. Starts at 90. Applies from the next night.'],
          ['Keep bot page views for (days)', 'The same for visits by search-engine and other robots.', '7 to 90, never more than the page-view period. Starts at 7.'],
          ['Delivery text, Returns text', 'The two collapsible panels on every product page. See [Delivery and returns text on product pages](#product-page-text).', 'Up to 4,000 characters of HTML each.'],
          ['Approve reviews before they appear', 'On: new and edited reviews wait for you on the [Reviews](#reviews) screen. Off: they appear as soon as written. A review you hid stays hidden either way.', 'Starts on.'],
        ],
      },
      { kind: 'heading', text: 'Email account (Super Admin only)' },
      {
        kind: 'table',
        caption: 'Email account panel (Settings Security)',
        head: ['Box', 'What it does', 'Limits and notes'],
        rows: [
          ['Mail server', 'The mail server\'s host name only, with no https:// and no port.', 'Up to 253 characters. **Empty means the shop keeps using the mail account set up on the server.**'],
          ['Port', 'Usually 587.', '1 to 65535. Required when a server is set. SSL on port 465 is not supported; use 587.'],
          ['User name', 'The login. Leave empty for a relay with no login.', 'Up to 256 characters.'],
          ['Password', 'The mail password.', 'Secret. Empty keeps the saved one. Refused when a user name is set and encryption is off.'],
          ['Send from address', 'The address emails are sent from.', 'One email address; required with a server.'],
          ['Send from name', 'The name beside the address in the inbox.', 'Up to 120 characters.'],
          ['Encrypt the connection (STARTTLS)', 'Keep it on unless your provider says otherwise.', 'Counts as on if never saved.'],
        ],
      },
      {
        kind: 'text',
        text: 'Press **Send test email** to send a test to your own address before you save (it uses the boxes as typed). Press **Save email account** to save. Every change raises a security alert and emails every Super Admin, through the new account and, if there was one before, the previous account too. It takes effect at once. **If no usable email account exists, here or on the server, emails are not sent, are not marked failed, and no alert warns you**: they wait in a queue and go out when an account is saved. Always send a test email after changing the account.',
      },
      { kind: 'heading', text: 'How soon a change takes effect' },
      {
        kind: 'list',
        items: [
          'Logo, contact details and social links: the next page a shopper loads. A shopper already browsing sees it when they load another page.',
          'Bank details, PayPal settings, the email account and payment-method switches: at once, no restart.',
          'Invoice details: on invoices made from then on. Traffic retention: from the next night. Product page text: the next product page. Review approval: the next review written.',
        ],
      },
      { kind: 'heading', text: 'Things that are not on the Settings page' },
      {
        kind: 'table',
        caption: 'Where else to look',
        head: ['What', 'Where it is'],
        rows: [
          ['Low-stock warning level', 'Per colour and size on the [Inventory](#inventory) screen, **Threshold**. The shop-wide default of 5 is set by your developer.'],
          ['How long a bank transfer order waits for payment', 'Fixed at 48 hours by your developer. Not editable here. (The PayPal waiting time is on this page.)'],
          ['Sign-in lockout (5 tries, 15 minutes)', 'Set by your developer.'],
          ['Couriers', 'Sales, [Couriers](#couriers).'],
          ['Overseas delivery and blocked countries', 'Links at the top of this page; see [Delivery](#delivery).'],
          ['Delivery fees inside Sri Lanka, store pickup locations', 'No screen for them was found in this version of the admin. Ask your developer.'],
          ['The shop\'s name, favicon and share picture', 'Built into the software. Not editable.'],
          ['Currency', 'Prices are in rupees everywhere. PayPal orders are charged in dollars at the rate above. There is no currency switch.'],
          ['Maintenance mode', 'There is none.'],
        ],
      },
    ],
  },
  {
    id: 'audit-log',
    group: 'Settings and records',
    title: 'Audit log: who changed what',
    summary: 'A read-only record of changes and sign-ins. Use it to answer "who did this?".',
    screens: [{ label: 'Audit log', path: '/audit-log' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Needs the **Audit log View** permission (Super Admin and Store Manager). Without it: "You do not have permission to view the audit log." Anyone who is not a Super Admin does not see rows about, or made by, a Super Admin: so a Store Manager does not see the changes a Super Admin makes to bank, PayPal or email settings.',
      },
      {
        kind: 'text',
        text: 'Each row says when, who, what action, on what, and from which IP address. Press **View details** to see the **Before** and **After** values as stored, the user agent and a correlation ID. There is no list of changed fields: compare Before and After. Passwords, tokens and secret values are never stored. There is no export button.',
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'The Audit log shows times in **UTC**, not Colombo time (Colombo is UTC+05:30, so add five and a half hours). The Staff users "Last sign-in" column is UTC too. Orders, customers, the dashboard, inventory history, reviews and alerts show Colombo time.',
      },
      {
        kind: 'list',
        title: 'Filters',
        items: [
          '**Entity type** (for example `Product`, `Order`, `Setting`, `HomeSection`, `User`), **Entity ID**, **User ID** and **Action**.',
          '**From (UTC)** and **To (UTC)**. A "To" date alone covers that whole day. From cannot be later than To.',
          'Press **Apply filters**; **Clear filters** resets. Newest first, 25 per page.',
        ],
      },
      {
        kind: 'list',
        title: 'What is recorded',
        items: [
          'Creates, updates, status changes (put on or take off the shop, hide or show), removals, approvals and exports.',
          'Every sign-in, failed sign-in and lockout, password changes, two-factor changes and recovery-code use, staff deactivation and role changes, setting changes and alert acknowledgements.',
          'Changes to a product are all under the entity "Product"; to settings, "Setting"; to the homepage, "HomeSection" and "HomeSectionItem".',
          'Page bodies, size-guide text and enquiry messages are recorded by size and shape only, never their words.',
        ],
      },
      {
        kind: 'text',
        text: 'Policy keeps audit rows for three years. No automatic clean-up exists in this version, so they are kept until a developer removes them.',
      },
    ],
  },
  {
    id: 'admin-alerts',
    group: 'Settings and records',
    title: 'Alerts: the bell at the top of every screen',
    summary:
      'Red and amber notices that need a person to look. What each one means and what to do. Check them first thing every morning.',
    blocks: [
      {
        kind: 'text',
        text: 'The **bell** sits in the dark bar at the top right of **every** admin screen, with a red number on it when something needs a person. The number refreshes each time you open a page. Press the bell and a panel opens under it. It lists the 10 newest open alerts, each with a severity (Critical, Warning or Information), a title, a line of advice and a link to the order when there is one. Press **Escape**, press anywhere else, or open another page to close the panel. Press **I have seen this** to acknowledge. That only records that you saw it; it does not fix the problem and does not send, retry or change anything. Nothing clears itself, and when one person presses the button the alert disappears for everyone at their next page load.',
      },
      {
        kind: 'text',
        text: '**Super Admins see every alert.** People with **Orders Approve** or **Orders Refund** also see the two payment alerts. Everyone else sees no bell at all. A Super Admin with nothing to deal with sees the bell with no number, and the panel says "Nothing needs your attention right now."',
      },
      {
        kind: 'table',
        caption: 'Alert types and what to do',
        head: ['Alert', 'Severity', 'What it means', 'What to do'],
        rows: [
          ['Email not delivered: (template) for order (number)', 'Warning', 'An email to a customer failed every retry. Nothing will try again, and there is no resend button.', 'Open the order from the alert, then **telephone or WhatsApp the customer** the same morning, because they may be waiting for bank details or an order link. Fix the cause (a wrong address cannot be corrected on a placed order; a mail problem may need the Email account panel). For a password-reset or confirmation email, ask the customer to request a new link. Then press **I have seen this**.'],
          ['(Area) were changed (N settings)', 'Critical', 'Bank details, PayPal settings, a payment method switch or the email account was changed.', 'Check the change was expected: ask the person named in the [Audit log](#audit-log). If not, treat it as a security incident: revert it and review the log. Every Super Admin sees this alert; only an Email account change also sends an email.'],
          ['Payment received but not applied to order (number)', 'Warning', 'A PayPal payment arrived that could not be applied: the order had expired, was cancelled or already paid, or the amount did not match.', 'Open the order the same day. There is no refund screen in this version, so a refund is made in the PayPal dashboard. See [Orders](#orders).'],
          ['PayPal payment for order (number) could not be confirmed', 'Warning', 'A PayPal payment could not be confirmed within the recovery window (24 hours).', 'Check that payment in PayPal by hand, the same day.'],
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Our rule of thumb from the project\'s operating notes: an alert nobody has acknowledged for 24 hours is passed to a Super Admin by phone or WhatsApp, not email (a mail problem may be the alert). A payment alert is dealt with the **same day**. Nothing is "solved" by pressing the button.',
      },
    ],
  },
  {
    id: 'data-privacy',
    group: 'Settings and records',
    title: 'Customer data, privacy and backups',
    summary:
      'What staff must protect, which data rules are in force and which are only policy so far, and what to do when a customer asks for their data to be deleted.',
    blocks: [
      {
        kind: 'text',
        text: 'The shop holds customers\' names, addresses, phone numbers, emails and order history. Sri Lanka\'s Personal Data Protection Act (PDPA) applies. Some protections are built into the software; others are policy that people carry out.',
      },
      {
        kind: 'list',
        title: 'Always',
        items: [
          'Use your own account. Never share a password.',
          'When a customer phones, confirm who they are by the order date, item and amount before saying anything. Never read the address out first.',
          'Never ask for or write down card numbers, security codes, passwords or PayPal credentials. Never ask for bank account details unless a refund has been agreed.',
          'Add an **internal note** on the order after every call: who called, what was asked and what was promised.',
          'Pass anything involving money going out (a refund, a disputed amount, a payment alert) to a Store Manager or Super Admin.',
          'If a contact enquiry contains card or bank details, delete it for good on the [Contact enquiries](#enquiries) screen; the audit log keeps the reason but not the message.',
          'Our advice: do not copy customer lists or exports to personal email or phones. Exports (reports, newsletter subscribers) are recorded in the audit log with who made them.',
        ],
      },
      { kind: 'heading', text: 'What the software does and does not do yet' },
      {
        kind: 'table',
        caption: 'Data rules: built, and policy only',
        head: ['Data', 'Policy', 'In this version'],
        rows: [
          ['Individual page views', '90 days (bots 7 days)', 'Built: purged each night. The periods are editable in Settings, Traffic.'],
          ['Daily traffic totals', 'Kept', 'Built.'],
          ['Audit log', '3 years', 'Not purged automatically yet.'],
          ['Alerts', 'Acknowledged ones 1 year', 'Not purged automatically yet.'],
          ['Contact enquiries, order and enquiry IP addresses', '2 years; IP addresses 90 days', 'Policy only; no clean-up job was found.'],
          ['Orders, payments, invoices', '7 years or more', 'Kept; never deleted.'],
          ['Deleted, unused pictures', 'Removed after 30 days', 'Hidden at once; the clean-up job does not exist yet, so files stay on disk.'],
          ['Backups', 'Nightly full backups with a log backup every 15 minutes, copied off the server', 'Not built yet. After go-live the technical owner takes backups by hand and copies them off the server.'],
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: '**Deleting or anonymising a customer is not built.** The customer pages are read-only: no edit, delete or anonymise. If a customer asks for their data to be deleted, confirm the request comes from the registered email address, note it, and give it to your developer. Tell the customer their data may stay in backups for a while (the policy is up to 35 days once automatic backups are running). There is also no tool to export a customer\'s data.',
      },
    ],
  },
];
