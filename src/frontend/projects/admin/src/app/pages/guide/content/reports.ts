import { GuideSection } from '../guide.model';

/**
 * Reports: the dashboard, the three reports and the traffic screen. Read from the reporting queries and the
 * screens that draw them. All money is in rupees and all days are Colombo days.
 */
export const REPORT_SECTIONS: readonly GuideSection[] = [
  {
    id: 'dashboard',
    group: 'Reports',
    title: 'The dashboard: what every figure means',
    summary:
      'The home screen. Today\'s sales, the things that need attention, and charts for any period you choose.',
    screens: [{ label: 'Dashboard', path: '/' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'The figures need the **Dashboard View** permission (Store Manager and Order Staff). Without it you see "Your role does not include the dashboard figures. The sections you can open are listed below." Some cards also need another permission: Visits and Traffic sources need **Traffic View**, Recent orders and "paid orders not yet dispatched" need **Orders View**, and the low-stock links need **Inventory View** or **Reports View**. A card you cannot use is simply not drawn.',
      },
      {
        kind: 'text',
        text: 'Figures are read when the page opens and again when you change the period. **They do not refresh by themselves**: reload the page for the latest. All money is in **rupees**, and every "day" is a **Colombo** day, both ends included.',
      },
      { kind: 'heading', text: 'The period' },
      {
        kind: 'text',
        text: 'Under **Period** choose **Last 7 days**, **Last 30 days** (the starting choice), **This month**, **Last month**, or **Choose dates** (then **From**, **To** and **Show**; at most 366 days). A line under the form states the dates and the earlier period of the same length that figures are compared with.',
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Refunds are not recorded anywhere in this version of the admin. **Net revenue always equals Gross revenue and Refunds completed is always zero.** A refund you make by hand, in your bank or in the PayPal dashboard, does not change any figure and does not appear as an alert. Keep your own record, and write it in the order\'s internal note.',
      },
      {
        kind: 'table',
        caption: 'The dashboard, top to bottom',
        head: ['Card', 'What it shows'],
        rows: [
          ['Today', 'Today\'s revenue (orders paid today), orders placed today (paid or not) and, with Traffic View, visits today. Read once when the page opens; the period does not change it.'],
          ['Attention line', 'A line of links near the top, with no heading of its own, that appears only when there is something. "N paid orders not yet dispatched" counts orders in status Paid plus Processing across all time. "N variants low on stock" counts colour-and-size rows that are low.'],
          ['Key figures', 'Six cards, each with the previous period and the change: **Gross revenue** (what paid orders came to, delivery included, after discounts), **Net revenue** (gross less refunds, see the warning), **Refunds completed**, **Orders** (placed, any status), **Paid orders**, **Average order value** (gross divided by paid orders).'],
          ['Revenue and orders', 'A line chart of gross revenue and paid orders. Choose **Monthly** (12 months) or **Weekly** (52 Monday-to-Sunday weeks). It always shows 12 months or 52 weeks ending at the period\'s end date, whatever length of period you chose.'],
          ['Sales by department', 'Share of item sales by department, after promo codes and without delivery, so it adds up to less than gross revenue.'],
          ['Order status', 'Orders placed in the period, by where they are now: Awaiting payment, Paid, Processing, Shipped or ready for collection, Delivered or collected, Cancelled, Expired, Refunded.'],
          ['Traffic sources', 'Visits, paid orders and **Conversion** (paid orders divided by visits), and where visits came from. Needs Traffic View.'],
          ['Payment methods', 'Paid orders and revenue by "Bank or online transfer" and "PayPal". Both are always listed.'],
          ['Top categories by sales', 'The 10 best categories by item sales.'],
          ['Recent orders', 'The 10 newest orders, paid or not, with a search box ("Order number, email or telephone") and a **View all orders** link.'],
          ['Top products', 'The 10 best products by item sales, with units, sales and the trend against the previous period.'],
          ['Low stock', 'The 10 colour-and-size rows with the fewest available, ignoring the period. Low means 1 up to its threshold available (the shop default is 5). Out-of-stock rows are not listed here.'],
          ['Customer overview', 'Buyers (distinct email addresses that paid in the period) and paid orders, with buyers by delivery district. Overseas buyers are grouped by country with an "Overseas" tag.'],
          ['Admin sections', 'Links to most screens (not Contact enquiries or Blocked countries), and **Sign out**. It is shown even to a role that cannot see the figures.'],
        ],
      },
      {
        kind: 'list',
        title: 'Which orders count',
        items: [
          'A **paid** order is any order with a paid date, whatever its status now: Paid, Processing, Shipped, Delivered and Refunded all count. Pending, Cancelled and Expired never count, unless an expired or cancelled bank transfer was later accepted as a late payment.',
          'Revenue is counted on the day an order was **paid**; orders on the day they were **placed**.',
          'Every chart has a text summary and a "Show the figures as a table" button.',
        ],
      },
    ],
  },
  {
    id: 'reports',
    group: 'Reports',
    title: 'Reports: Sales, Inventory and Customers',
    summary: 'Three reports with filters and a CSV download.',
    screens: [{ label: 'Reports', path: '/reports' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Reports View** (the link is hidden without it) and **Reports Export** for the download. With View only you see "Your role can see reports but not export them." and no button. Store Manager has both; Order Staff and Content Editor have neither.',
      },
      {
        kind: 'text',
        text: 'Open **Reports** and choose a tab: **Sales**, **Inventory** or **Customers**. Press **Show** after changing any filter. Periods are Colombo days, both ends included, at most 366 days; the starting period is the last 30 days.',
      },
      { kind: 'heading', text: 'Sales' },
      {
        kind: 'text',
        text: 'Choose **From**, **To** and **One row per** Day, Week (Monday to Sunday) or Month. You get a bar chart and a table with a **Total** row.',
      },
      {
        kind: 'table',
        caption: 'Sales report columns',
        head: ['Column', 'Meaning'],
        rows: [
          ['Orders placed', 'Orders placed in that day, week or month, any status.'],
          ['Paid orders', 'Orders paid in it.'],
          ['Gross revenue', 'Total of those paid orders, delivery included, after all discounts. Later-refunded orders still count.'],
          ['Items after promo', 'The same orders\' item lines after sale prices and promo codes, without delivery.'],
          ['Delivery', 'Delivery charged, less any delivery discount.'],
          ['Refunds completed', 'Always 0 in this version.'],
          ['Net revenue', 'Gross revenue less refunds, so equal to gross revenue today.'],
        ],
      },
      { kind: 'text', text: 'Items plus Delivery equals Gross revenue. The Sales tab, the dashboard chart and the key figures use one calculation, so they agree.' },
      { kind: 'heading', text: 'Inventory' },
      {
        kind: 'text',
        text: 'Stock **now**, with no dates. Filter by **Stock** (every variant, in stock, low stock, out of stock) and **Product name, SKU or code**. A chart shows "N in stock - N low - N out of stock". Columns: Product, SKU, Colour / size, Department, On hand, Reserved (held by unpaid orders), Available (on hand less reserved), Threshold ("(default)" when the shop default applies) and Status. Only switched-on sizes of products not removed are listed. 25 rows per page.',
      },
      { kind: 'heading', text: 'Customers' },
      {
        kind: 'text',
        text: 'Everyone who paid for an order in the period, **guests included**, highest spend first. Filter by **Paid from**, **Paid to** and **Email address starts with**. A buyer is an email address with at least one paid order in the days shown. **First-time** means their first paid order in the shop ever falls in those days; **Returning** means they had paid here before those days began. Columns: Email, Name, Delivery district, Account (Yes or Guest), Paid orders, Gross revenue, First paid, Last paid. Name and district come from their latest paid order in the period.',
      },
      { kind: 'heading', text: 'Export CSV' },
      {
        kind: 'list',
        items: [
          'Press **Export CSV**. It exports exactly what the last **Show** applied, with no row limit. Files are named `sales-report-` and the dates, `inventory-report-` and today\'s date, `customers-report-` and the dates.',
          '**Every export is recorded in the audit log against your name** (the shape of the request, never the data). The Customers file holds names and email addresses: treat it as confidential.',
          'The files open correctly in Excel, including Sinhala and Tamil. Cells beginning with `=`, `+`, `-` or `@` get a leading apostrophe so they are not read as formulas.',
          'There is no promo-code report. Use **Uses** on the [Promo codes](#promo-codes) screen.',
        ],
      },
    ],
  },
  {
    id: 'traffic',
    group: 'Reports',
    title: 'Website traffic',
    summary: 'How many people visit the shop, where they come from and what they look at.',
    screens: [{ label: 'Website traffic', path: '/traffic' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Needs **Traffic View** (the link is hidden without it; Store Manager has it). Without it: "Your role does not include traffic figures. Ask a Super Admin for the Traffic permission if you need them."',
      },
      {
        kind: 'text',
        text: 'Choose a **Period** as on the dashboard. The page shows **Visits**, **Page views** and **Crawler page views**, then "Visits by day", "Where visitors came from", "Devices", "Top referring sites and campaigns" and "Top pages" (the top 10 of each).',
      },
      {
        kind: 'table',
        caption: 'What the words mean',
        head: ['Term', 'Meaning'],
        rows: [
          ['Page view', 'One shop page shown. Changing only filters or "Load more" on a listing is not a new view. Error pages and the admin itself are not counted.'],
          ['Visit', 'One person\'s pages on one Colombo day, credited to where their first page that day came from. A visit ends after 30 idle minutes. There is no "unique visitors" figure.'],
          ['Crawler page views', 'Search engines, link previews and automated browsers. They are counted only here and in no other figure.'],
          ['Direct', 'Typed the address, used a bookmark, or came from somewhere that did not say.'],
          ['Organic', 'Came from a search engine result that was not an ad.'],
          ['Social', 'Came from a social network, or a link tagged as social.'],
          ['Referral', 'Followed a link on another website.'],
          ['Email', 'Followed a link in an email or newsletter.'],
          ['Paid', 'Clicked a paid advert.'],
          ['Other', 'Followed a tagged campaign link that fits none of the above.'],
          ['Devices', 'Desktop, Mobile, Tablet or Other, guessed from the browser.'],
        ],
      },
      {
        kind: 'list',
        items: [
          'To see campaign names, tag your links with `utm_source`, `utm_medium` and `utm_campaign`.',
          'The shop uses one first-party cookie (a random code, 30 minutes) and records no IP address, user or email. No cookie-consent banner was found in this version.',
          'The last four days are counted live and earlier days from nightly totals. Individual page views are deleted after 90 days (bots 7 days); the periods can be changed in [Settings](#settings). The daily totals are kept for ever.',
          'Staff visits to the shop are not separated from customers\' visits.',
        ],
      },
    ],
  },
];
