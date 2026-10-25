import { GuideSection } from '../guide.model';

/**
 * Help: common problems, what the admin cannot do yet, and a glossary. "Cannot do yet" is the list to keep
 * honest: when a feature is built, delete its line here and write its section above.
 */
export const HELP_SECTIONS: readonly GuideSection[] = [
  {
    id: 'troubleshooting',
    group: 'Help',
    title: 'Common problems and how to fix them',
    summary: 'The questions that come up most, with the likely cause and what to do.',
    blocks: [
      {
        kind: 'table',
        caption: 'Problems and fixes',
        head: ['Problem', 'Likely cause', 'What to do'],
        rows: [
          ['A product is not on the shop.', 'It is still "Not published", its department or main category is hidden, or it was removed.', 'Open it in [Products](#products) and check the pill. Press **Put on the shop**; read the yellow "Before this product can go on the shop" box. Check the category on [Departments and categories](#categories).'],
          ['"Put on the shop" is greyed out.', 'No switched-on size, no Gallery photograph, or a photograph with no alt text.', 'Fix what the yellow box lists, **save**, then try again.'],
          ['The form says only "One or more fields are invalid."', 'A box without its own error broke a limit (colour name, per-size price, barcode, description or search-engine length, alt text, related products).', 'Check the limits in [Product form](#product-fields).'],
          ['A colour dot is invisible on the shop.', 'The colour has no Swatch colour.', 'Type one such as `#3a5a8c` and save.'],
          ['My description has no paragraphs.', 'The shop treats the Description as web text.', 'Put each paragraph between `<p>` and `</p>`.'],
          ['A photograph or video is refused.', 'Wrong format or too large.', 'See [When an upload is refused](#upload-problems).'],
          ['A picture looks cropped or blurry.', 'Its shape or size does not match the place.', 'See [Sizes for every place](#photo-sizes).'],
          ['A customer says they paid, but the order is Expired or Cancelled.', 'The transfer arrived after the deadline.', 'Check the bank statement, then **Accept late payment**. See [Bank transfers](#bank-transfers). PayPal money that arrives late cannot be accepted: see [Refunds](#refunds).'],
          ['A customer did not get an email.', 'The email failed, or the shop\'s email account is not set up.', 'Look for an "Email not delivered" alert ([Alerts](#admin-alerts)); phone the customer. Check the Email account panel in [Settings](#settings), and press **Send test email**.'],
          ['Customers cannot check out.', 'No payment method is switched on or ready, or PayPal\'s exchange rate is older than its limit.', 'A Super Admin checks [Settings](#settings), **Payment methods** and the PayPal readiness line.'],
          ['PayPal has disappeared from checkout.', 'The exchange rate is too old.', 'Update **Rupees per US dollar** or press "The rate is still correct".'],
          ['A promo code is refused.', 'See the customer\'s message in [Promo codes](#promo-codes). Often it has ended, is switched off, has reached its limit, or the order is below its minimum.', 'Edit the code to fix the dates, limits or switch.'],
          ['A sale price is not showing on the listing.', 'Listings and the Sale page can take up to five minutes to follow a campaign\'s start or end.', 'Wait; the bag and checkout already use the new price.'],
          ['A customer cannot get delivery to a country.', 'The country is blocked, its region is off, overseas delivery is off, or it has no fee.', 'See [Delivery](#delivery) and the reason in the country\'s row on Overseas delivery.'],
          ['I changed the homepage but the shop looks the same.', 'You did not press the block\'s own Save button, or you are looking at an old page.', 'Press **Save (block name)** and reload the shop. Product rows can take 5 minutes.'],
          ['"Somebody else changed this while you were editing it."', 'A colleague saved first.', 'Reload the page, look at their change, repeat yours.'],
          ['I can see a menu item but the page says "You do not have permission to do that."', 'Your role does not include that permission.', 'See [Roles and permissions](#staff-roles). Roles cannot be edited: ask a Super Admin to give you a role that has it.'],
          ['I am locked out.', 'Five wrong passwords or codes.', 'Wait 15 minutes. If you forgot your password or lost your phone, ask your developer.'],
          ['I cannot find Contact enquiries.', 'It has no menu link.', 'Open [Contact enquiries](#enquiries).'],
          ['The dashboard\'s revenue does not drop after a refund.', 'Refunds are not recorded in the system.', 'This is expected. See [Refunds](#refunds).'],
        ],
      },
    ],
  },
  {
    id: 'not-built',
    group: 'Help',
    title: 'What the admin cannot do yet',
    summary:
      'Things that sound as if they should exist but do not in this version, so you do not promise a customer something the shop cannot do.',
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'This list was written from the code in October 2026. Where the project\'s older design notes promise one of these, the notes are out of date. Ask your developer before telling a customer a feature exists.',
      },
      { kind: 'heading', text: 'Orders and money' },
      {
        kind: 'list',
        items: [
          '**Refunds:** no Refund button; the Refunded status cannot be reached; refunds do not appear on the dashboard or reports; nothing restocks automatically. Do it by hand and note it. See [Refunds](#refunds).',
          '**Refunds, reversals and chargebacks made inside PayPal** are received and ignored: nothing changes in the shop and no alert appears. Watch your PayPal account.',
          '**Resending an email:** there is no resend button and no screen listing failed emails. Phone or WhatsApp the customer.',
          '**Changing an order after it is placed:** address, items and delivery method cannot be changed; only the courier, the tracking number and the internal note.',
          '**Split shipments:** one courier and one tracking number per order.',
          '**An orders export** and a **promo-code report**.',
          '**Notifications to staff:** nothing tells you about a new order, a slip upload, a rejected transfer, low stock or a new enquiry, and payment alerts send no email. The alert bell is the only notice.',
          '**Store pickup:** switched off, with no way to add a pickup location.',
        ],
      },
      { kind: 'heading', text: 'People and security' },
      {
        kind: 'list',
        items: [
          '**Roles:** they cannot be created, edited or deleted.',
          '**Staff passwords:** no forgot-password link, no reset by another user, no unlock button and no reset of two-factor authentication. No invitation email is sent to a new staff user.',
          '**Customers:** the Customers screen cannot edit, disable, unlock, annotate, delete or anonymise anyone, or export the list. (The Customers report under Reports can be downloaded.) See [Customer data](#data-privacy).',
          '**Newsletter:** nobody can be unsubscribed from the admin, and no email carries an unsubscribe link.',
          '**Automatic clean-up** of the audit log, alerts, enquiries and IP addresses, and **automatic backups**, are not built yet. The technical owner takes backups by hand.',
        ],
      },
      { kind: 'heading', text: 'Catalogue and content' },
      {
        kind: 'list',
        items: [
          '**Restoring** a removed product, category, promo code, campaign or page.',
          '**A media library:** you cannot browse or delete uploaded pictures.',
          '**Pictures** for categories, department headers and colour swatches, and the shop icon.',
          '**Brand, tags, copying a product, bulk product actions, a publish date, stock upload from a spreadsheet,** and **back-in-stock alerts.**',
          '**Prices in dollars:** products are priced in rupees only; there is no multi-currency display.',
          '**A preview** of any content before it is live, and warnings when you leave with unsaved changes.',
          '**Editing Sri Lankan delivery fees,** the bank-transfer waiting time, the shop-wide low-stock level and the sign-in lockout settings from a screen.',
          '**Adding a setting.** The list is fixed.',
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'There is no help or support link inside the admin. For anything on this list, or anything that looks broken, contact your developer or technical owner and tell them the screen, what you pressed and the exact message you saw.',
      },
    ],
  },
  {
    id: 'glossary',
    group: 'Help',
    title: 'Glossary',
    summary: 'The words this admin and this guide use.',
    blocks: [
      {
        kind: 'table',
        caption: 'Terms',
        head: ['Word', 'Meaning'],
        rows: [
          ['Alt text', 'A short description of a picture, read aloud to people who cannot see it.'],
          ['Available', 'Stock you can sell: on hand, less what unpaid orders are holding.'],
          ['Badge', 'The small label on a product card: New, Sale, Online Exclusive, Best Seller or Low Stock.'],
          ['Colombo time', 'The shop\'s own time, UTC+05:30, shown in 24-hour form.'],
          ['Department', 'Women, Men or Kids. The top level of the menu.'],
          ['Gallery photograph', 'A photograph shown on the product page. The first one is the picture on listing cards.'],
          ['H.264', 'The video format the shop accepts, inside an MP4 file. Also called AVC.'],
          ['Hover photograph', 'The picture shown when a customer points at a product card.'],
          ['Internal note', 'A staff-only note on an order.'],
          ['On hand', 'How many of a size are physically in stock.'],
          ['Permission', 'One thing a person may do, such as "Orders Edit".'],
          ['Poster image', 'The still picture shown before a video plays.'],
          ['Reserved (held)', 'Stock held for an unpaid order until it is paid, cancelled or expires.'],
          ['Role', 'A fixed bundle of permissions: Super Admin, Store Manager, Order Staff or Content Editor.'],
          ['SKU', 'A unique code for one colour-and-size of a product. Never reused, even after a product is removed.'],
          ['Slug (web address)', 'The part of a web address that names a page, such as the end of `/product/linen-shirt`.'],
          ['Swatch colour', 'The code for the colour dot, such as `#3a5a8c`.'],
          ['Threshold', 'The available stock at or below which a size is "low".'],
          ['Variant', 'One colour-and-size of a product.'],
        ],
      },
    ],
  },
];
