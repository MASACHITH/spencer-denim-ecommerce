import { GuideSection } from '../guide.model';

/**
 * Shop content: the homepage, announcement bar, mega menu cards, size guides, content pages and the product
 * page delivery and returns text. Read from the admin screens and the validators behind them.
 */
export const SHOP_CONTENT_SECTIONS: readonly GuideSection[] = [
  {
    id: 'content-rules',
    group: 'Shop content',
    title: 'Rules every content screen shares',
    summary:
      'Links, plain text, dates, order, on and off, saving and how soon the shop changes. These apply to the Homepage, Announcement bar, Mega menu cards and Size guides screens (Content pages differ a little: see the last point).',
    screens: [
      { label: 'Homepage', path: '/content/home' },
      { label: 'Announcement bar', path: '/content/announcements' },
      { label: 'Mega menu cards', path: '/content/mega-menu' },
      { label: 'Size guides', path: '/content/size-guides' },
      { label: 'Content pages', path: '/content/pages' },
    ],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: the Content permissions. **View** opens a screen, **Create** adds an item, **Edit** saves changes, **Delete** removes. Uploading a picture or video also needs **Media Create**. The buttons are not hidden if you lack a permission; the action fails with "Your role does not allow this. Ask an administrator for the Content permission it needs." (On **Content pages** the same refusal reads "You do not have permission to do that.") The Store Manager and Content Editor roles have all of these.',
      },
      { kind: 'heading', text: 'Text boxes are plain, single-line text' },
      {
        kind: 'list',
        items: [
          'Titles, subtitles, labels, messages and the **Text** box are plain text. They are shown exactly as typed, never as formatting. Sinhala and Tamil text is fine.',
          '**No line breaks.** Even the larger **Text** box refuses one: "This is a single line of plain text: remove line breaks and tabs." Write a single paragraph.',
          'A box over its limit says "Keep this to N characters or fewer." A required box left empty says "This is required."',
        ],
      },
      { kind: 'heading', text: 'Links' },
      {
        kind: 'list',
        items: [
          'A link is normally **a page on this shop**, starting with exactly one `/`, such as `/women/dresses`, `/new-arrivals`, `/sale` or `/women/jeans?sort=newest`. Up to 500 characters, no spaces.',
          'There are no pickers for products, categories or collections. You type the path. A "collection" is just a listing path with filters after a `?`, for example `/women?material=linen&material=linen-blend`.',
          'A full `https://` address is accepted **only** in Social gallery items, Announcement bar messages and Mega menu cards.',
          'Refused: spaces, backslashes, `//` addresses, `javascript:`, `mailto:`, `tel:` and any scheme but http and https. The message is "Use a path on this site starting with \'/\', such as /women/dresses." or, where addresses are allowed, "...or a full https:// address."',
        ],
      },
      { kind: 'heading', text: 'Position, on and off, dates' },
      {
        kind: 'list',
        items: [
          '**Position** (also called Order) is a whole number from 0 to 999. Lowest comes first; a tie goes to whichever was created first. There are no up and down arrows and no dragging on these screens: to reorder, type new numbers and save.',
          'The tick box ("Show this block on the homepage", "Show this item", "Show this message", "Show this card") switches something on or off.',
          '**Show from (optional)** and **Show until (optional)** set a window. From is inclusive, until is exclusive, and blank means no limit. Until must be after from: "The end must be after the start." Times are the shop\'s own time (Colombo, UTC+05:30), to the minute.',
          'A pill shows the state: **On the shop now**, **Scheduled** (switched on but not in its window), or **Off**.',
          '**Remove** is two steps: the first press turns the button into "Yes, remove it" with "Keep it" beside it. (Removing a content page is the exception: one press.)',
        ],
      },
      { kind: 'heading', text: 'Saving, conflicts and how soon the shop changes' },
      {
        kind: 'list',
        items: [
          'There is **no preview** and no autosave. Save, then open the shop in another tab to check. There is no warning if you leave with unsaved changes.',
          'If someone else changed the same thing, your save is refused: "Someone else changed this ... while you were editing it. Reload to see their change, then make yours again." Press **Reload**, then repeat your change.',
          'Words, pictures and on/off changes appear on the homepage straight away. Product rows (New arrivals, Sale, automatic Trending) can take up to **5 minutes** to follow a change to prices, stock or the catalogue. Scheduled start and end times are exact.',
          'The announcement bar and mega menu cards are not cached on the server, but a visitor who already has the page open sees the change when they load another page.',
          'Every change is written to the [Audit log](#audit-log).',
          '**Content pages** differ in three ways: there is no **Reload** button after a conflict (reload the browser page), **Remove** is one press, and refusals read "You do not have permission to do that."',
        ],
      },
    ],
  },
  {
    id: 'homepage',
    group: 'Shop content',
    title: 'Homepage: how the editor works',
    summary:
      'The shop\'s front page is ten fixed blocks. You can switch them on or off, reorder them, change their words and pictures, and add items to some of them.',
    screens: [{ label: 'Homepage', path: '/content/home' }],
    blocks: [
      {
        kind: 'text',
        text: 'Open **Content, Homepage**. You see the ten blocks top to bottom. You cannot add, delete or duplicate a block. **Each block saves on its own.** Each block has **Block settings** and, for most, a list of **Items (n of up to y)**.',
      },
      {
        kind: 'steps',
        title: 'Change a block',
        items: [
          'Find the block and change its **Block settings**: **Title** (up to 150 characters), **Subtitle** (up to 300), **Position on the page**, **Show from / Show until** and **Show this block on the homepage**. Some blocks have more (see the table).',
          'Press **Save** followed by the block\'s name, for example **Save Hero video**. You see "(Block) is saved. The homepage shows it now."',
          'To change an item, press **Edit** on it. To add one, press **Add an item**. Fill the boxes, add pictures with **Upload image** (describe each picture) and press **Add the item** or **Save the item**.',
          'To reorder items, change each item\'s **Position** and save each one.',
          'To remove an item press **Remove**, then **Yes, remove it**.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Save the block\'s **Block settings** before you add, change or remove an item in the same block. Saving an item reloads the block, and unsaved edits to its settings may be replaced by the saved values.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'A block that is switched on but has nothing it can show (for example a banner whose item has no picture) shows nothing at all, with no empty gap. If every block is off, visitors see a plain page headed "Shop" followed by the shop\'s name, with buttons for each department, New Arrivals and Sale. A banner item cannot be saved with **Show this item** ticked until it has a picture: untick it, upload the picture, then tick it.',
      },
      {
        kind: 'text',
        text: 'Where pictures and video are involved, the shapes and sizes are in [Sizes for every place](#photo-sizes). The rules for links, dates and saving are in [Rules every content screen shares](#content-rules).',
      },
    ],
  },
  {
    id: 'homepage-blocks',
    group: 'Shop content',
    title: 'Homepage: the ten blocks, one by one',
    summary:
      'What each block is, what you put in it and what to be careful of. Starting state is how a brand-new shop is set up; the live shop may differ.',
    screens: [{ label: 'Homepage', path: '/content/home' }],
    blocks: [
      {
        kind: 'table',
        caption: 'The ten homepage blocks',
        head: ['Block', 'What visitors see', 'Items', 'What to put in it'],
        rows: [
          ['1. Hero video', 'The big banner at the very top, with your words over it.', 'Up to 1', '**Change the words on the item** (press **Edit** on it): the item\'s **Title** is the headline and its **Subtitle** a line under it. The **Title** and **Subtitle** in Block settings are used only if the item\'s are empty. The button appears only if the item has a **Link**; its words are **Button label** (or "Shop now"). Pictures: **Still image**, optional **Phone image**, optional **Video** with its poster. **Shade over the picture**: leave it on **Dark shade, light text** (also used when it is not chosen) or choose **No shade**; avoid **Light shade, dark text**, which still draws a dark fade under dark words. Starts switched off.'],
          ['2. Shop by department', 'Tiles for Women, Men and Kids.', 'Up to 6', 'Each item needs a **Department**. **Title** (else the department name), **Button label** (else "Shop" and the name) and **Link** (else the department page) are optional. A picture is optional. A tile for a hidden department is not drawn unless you gave it its own **Title** and **Link**. Starts on, with three tiles.'],
          ['3. New arrivals', 'A row of product cards.', 'None', '**Title**, **How many products** (1 to 24) and **"View all" link**, which says which listing the row shows (for example `/new-arrivals`). The row is that listing\'s first products. Add a **"View all" label** and the link appears beside the heading (it needs both). Starts on.'],
          ['4. Trending items', 'Picture tiles, or a product row.', 'Up to 12', '**Which products**: "Chosen automatically from the link below" (works like New arrivals) or "Hand-picked, as the items below". For hand-picked tiles each item needs a **Title**, and has **Subtitle**, **Link**, **Product** (the end of the product\'s web address, for example `linen-shirt`) and a picture. A tile opens its Link, or else the named product; with neither it is not shown. **How many products** is still required in hand-picked mode. Starts off.'],
          ['5. Campaign banner', 'A full-width picture banner with words over it.', 'Up to 1', 'On the item: **Title**, **Subtitle**, **Text** (one line), **Button label**, **Link**, **Image** and **Phone image**. The item\'s **Title** and **Subtitle** win over the ones in Block settings; with neither, the heading reads "Featured". Same **Shade over the picture** advice as the hero. The item cannot be switched on until it has a picture. Starts off.'],
          ['6. Activewear feature', 'Another full-width banner. The shop\'s decision is that it is the "Linen and denim" banner, not activewear.', 'Up to 1', 'Same boxes as the Campaign banner. Starts off.'],
          ['7. Sale section', 'A row of product cards.', 'None', 'Same boxes as New arrivals, for example **"View all" link** `/sale`. Starts on.'],
          ['8. Social gallery', 'A square picture grid linking to your social accounts.', 'Up to 12', 'Each item needs an **Image**. **Title** is a hidden label for screen readers. **Link** can be an `https://` address (it opens in a new tab) or a shop path. There is no automatic Instagram feed: add each picture by hand. A new shop starts off, with placeholder tiles pointing at dummy addresses: replace them before switching the block on.'],
          ['9. Benefits', 'A row of small icon-and-words boxes.', 'Up to 6', 'Each item needs an **Icon** (delivery, shipping, returns, exchanges, quality, secure, payment, support or contact), plus a **Title** (the bold line) and **Text** (one sentence). An item with neither Title nor Text is not drawn. Starts on, with three placeholder items to replace.'],
          ['10. Newsletter', 'The email sign-up box with a "Sign up" button.', 'None', '**Title** (else "Newsletter") and **Subtitle**. The consent line is fixed. Subscribers are listed under [Newsletter subscribers](#newsletter). Starts on.'],
        ],
      },
      { kind: 'heading', text: 'Item boxes and their limits' },
      {
        kind: 'table',
        caption: 'Homepage item boxes',
        head: ['Box', 'Limit', 'Notes'],
        rows: [
          ['Title', '150 characters', 'Shown as "(required)" next to the label when the block needs it.'],
          ['Subtitle', '300 characters', 'Optional.'],
          ['Text', '1,000 characters', 'One line only, even though the box is large.'],
          ['Button label', '40 characters', 'The button appears only if the item has a Link.'],
          ['Link', '500 characters', 'A shop path starting with `/`. Social gallery items may also use `https://`.'],
          ['Product', '200 characters', 'Trending items only. Error: "No product has that address."'],
          ['Position', '0 to 999', 'New items start at the number of items already there.'],
        ],
      },
      { kind: 'heading', text: 'What to write' },
      {
        kind: 'list',
        items: [
          'Keep it short: one headline, one supporting line, and a button that names the destination, for example "Shop new arrivals" linking to `/new-arrivals`.',
          'The Hero video, Campaign banner, Activewear feature and Trending items blocks start with example words and links already typed into their items, switched off. Read and change them before you add a picture and switch them on.',
          'Do not promise what the shop cannot back up: no free-delivery threshold or returns window is in the starting content, because none is set in the system.',
          'Check **Trending items** links after the catalogue changes. A filter with no products shows "No products found".',
          'Check the homepage once a week: the announcement messages, the blocks, and the Trending links for the coming week.',
        ],
      },
    ],
  },
  {
    id: 'announcements',
    group: 'Shop content',
    title: 'Announcement bar',
    summary: 'The thin strip of short messages above the header on every page of the shop.',
    screens: [{ label: 'Announcement bar', path: '/content/announcements' }],
    blocks: [
      {
        kind: 'text',
        text: 'Visitors see one message at a time, centred. If it has a link, the whole sentence is the link. With two or more live messages the bar changes message every **6 seconds**, and shows Previous, Next and Pause buttons. It stops while the pointer is over it, and does not rotate at all for visitors who ask their device for reduced motion. There is **no close button**. With no live message the bar is not drawn.',
      },
      {
        kind: 'steps',
        title: 'Add a message',
        items: [
          'Open **Content, Announcement bar**.',
          'Type the **Message** (required, up to 200 characters, one line). A counter shows "n of 200 characters". Over 60 characters you are told it will run to several lines on phones.',
          'Optionally add a **Link (optional)**, such as `/sale`.',
          'Set **Order** (0 to 999, lowest first), and optionally **Show from** and **Show until**. Leave **Show this message** ticked.',
          'Press **Add the message**. To change one, press **Edit**, then **Save the message**. To remove one press **Remove**, then **Yes, remove it**.',
        ],
      },
      {
        kind: 'list',
        items: [
          'You can keep **20 messages** in all, on or off. The 21st is refused: "The strip already holds 20 messages. Remove one you no longer need first."',
          'On the shop **only links starting with `/` work**. An `https://` address is accepted and saved, but the message is then shown as plain text with no link. Use a shop path.',
          'Messages are plain text. Keep each one short enough to read at a glance.',
          'Do not promise a free-delivery limit or a returns period unless the shop really offers it.',
          'A new shop starts with three messages: "Delivery across Sri Lanka and worldwide", "New season linen has arrived" and "Questions? Message us on WhatsApp". Replace them with your own.',
          'If the shop cannot load the messages within two seconds, visitors see those three built-in lines instead.',
        ],
      },
    ],
  },
  {
    id: 'mega-menu',
    group: 'Shop content',
    title: 'Mega menu cards',
    summary: 'The picture card at the right-hand end of each department\'s drop-down menu.',
    screens: [{ label: 'Mega menu cards', path: '/content/mega-menu' }],
    blocks: [
      {
        kind: 'text',
        text: 'Hovering over Women, Men or Kids on a computer screen opens a menu with a tall picture card at the end: a picture, a heading, an optional line under it, and a button-like link. **It appears only from 1024 pixels wide**; the phone and tablet menu has no card. If a department has no card showing, the menu shows "New in" the department with its own description.',
      },
      {
        kind: 'steps',
        title: 'Add a card',
        items: [
          'Open **Content, Mega menu cards**. Each department has its own panel. Press **Add a card for** the department.',
          'Fill in **Department**, **Heading** (required, up to 100 characters), **Line under the heading (optional)** (up to 200), **Button words** (required, up to 40, starts as "Shop now") and **Link** (required, starts as `/new-arrivals`).',
          'Upload the **Picture** and describe it. A card needs a picture before it can be switched on; until then save it switched off.',
          'Set **Order**, and optionally **Show from / Show until**. Leave **Show this card** ticked.',
          'Press **Add the card**.',
        ],
      },
      {
        kind: 'list',
        items: [
          'A department holds **6 cards** at most, on or off. When it is full the add button is replaced by "holds 6 cards, the most it can. Remove one to add another."',
          '**Only one card is shown per department at a time:** the first, by Order, that is switched on, inside its dates and has a ready picture. Use dates to line up a sale card to start on a day and let the normal card come back afterwards.',
          'Pills: **On the menu now**, **Waiting its turn** (live but an earlier card is shown), **Picture unavailable**, **Ended**, **Scheduled** and **Off**.',
          'Links may be shop paths or full `https://` addresses; an outside address opens in a new tab.',
          'Removing a card is permanent. Picture shape and size are in [Sizes for every place](#photo-sizes).',
        ],
      },
    ],
  },
  {
    id: 'size-guides',
    group: 'Shop content',
    title: 'Size guides',
    summary:
      'The measurement tables customers open from a product page. Make a guide here, then choose it on each product.',
    screens: [
      { label: 'Size guides', path: '/content/size-guides' },
      { label: 'Add a size guide', path: '/content/size-guides/new' },
    ],
    blocks: [
      {
        kind: 'text',
        text: 'On a product page a link under the size buttons reads "(Guide name) size guide" and opens a pop-up showing, in order and only for parts you filled in: an introduction, the size table, a picture, and "How to measure" tips. **Do not put the words "size guide" in the Name**: the shop adds them.',
      },
      {
        kind: 'steps',
        title: 'Make a size guide',
        items: [
          'Open **Content, Size guides** and press **Add a size guide**.',
          'Type the **Name**, up to 100 characters, for example "Women\'s apparel".',
          '**Introduction (HTML, optional)**: for example "Measurements are in centimetres and describe the body, not the garment." Up to 20,000 characters.',
          'Build the **Size table** in the grid. The **first row is the column headings** and none can be blank (for example Size, Bust, Waist, Hip). Use **Add a column**, **Add a row** and **Remove**.',
          '**Measuring tips (HTML, optional)**, and optionally an **Image, e.g. how to measure**, with a description.',
          'Press **Add the size guide**. Then open each product and choose the guide in its **Size guide** box.',
        ],
      },
      {
        kind: 'table',
        caption: 'Size table limits',
        head: ['Limit', 'Value'],
        rows: [
          ['Columns', 'Up to 20'],
          ['Rows', 'Up to 100'],
          ['Cells in all', 'Up to 1,000 (for example 20 by 50, or 10 by 100, never 20 by 100)'],
          ['Characters in a cell or heading', 'Up to 60, plain text or a number, no line breaks'],
          ['Introduction and tips', 'Up to 20,000 characters each, after cleaning'],
        ],
      },
      {
        kind: 'list',
        items: [
          'There is **no unit setting**. Write the unit in the introduction or the headings. The starting guides use centimetres and ranges such as "88-93".',
          'The Introduction and Tips boxes take simple HTML. Kept: paragraphs, line breaks, bold, italic, underline, strike-through, small headings (h2 to h4), bullet and numbered lists, quotes, links (web, email or shop paths) and pictures (which need alt text). Removed: scripts, styling, classes, iframes. When you save, the screen lists what it removed: "Saved, but the shop removed some HTML it does not allow".',
          '**Remove** is refused while any product or category uses the guide: "(Name) cannot be removed: it is still used by..." Choose another guide for them first.',
          'The **Size guide** box on a category is saved but does not reach the shop. A product shows a size guide only when you choose one **on the product**.',
          'The shop picks up a guide the next time a product page loads. There is no preview.',
        ],
      },
    ],
  },
  {
    id: 'content-pages',
    group: 'Shop content',
    title: 'Content pages: About, Contact, Shipping, Returns, Privacy, Terms and your own',
    summary:
      'The shop\'s written pages. Write them, put them on the shop, and they appear in the footer automatically.',
    screens: [
      { label: 'Content pages', path: '/content/pages' },
      { label: 'Add a page', path: '/content/pages/new' },
    ],
    blocks: [
      {
        kind: 'text',
        text: 'Each page lives at one address on the shop, such as `/returns`, shows its **Heading** as the page title, a line "Last updated (date)", then its body. A page that is on the shop is linked from the footer and listed in the sitemap. A page that is not on the shop answers "We could not find that page".',
      },
      {
        kind: 'table',
        caption: 'The six built-in pages',
        head: ['Page', 'Starting address', 'On a new shop', 'Notes'],
        rows: [
          ['About us', '/about', 'Not on the shop, empty', 'You write it.'],
          ['Contact us', '/contact', 'On the shop, empty body', 'Needs no writing: its content is the enquiry form and the details in [Settings](#settings). Anything you write appears above them.'],
          ['Returns and exchanges', '/returns', 'On the shop, starting policy text', 'The starting text says 14 days, unworn with tags, 48 hours to report damage, refunds within 7 working days. Check it matches your real policy.'],
          ['Delivery and shipping', '/shipping', 'On the shop, starting policy text', 'The paragraph that holds only `{{delivery fees}}` is replaced by the live table of Sri Lankan delivery fees. Keep it on a line of its own (you may move it); if you delete it the table appears after your text. Do not type fees in the body.'],
          ['Privacy policy', '/privacy', 'Not on the shop, empty', 'Carries legal weight: use your own wording, reviewed against Sri Lanka\'s Personal Data Protection Act, before publishing.'],
          ['Terms and conditions', '/terms', 'Not on the shop, empty', 'Same legal warning as Privacy.'],
        ],
      },
      {
        kind: 'steps',
        title: 'Write or add a page',
        items: [
          'Open **Content, Content pages**. Click the name of a page in the **Page** column to open it, or press **Add a page**.',
          'Fill **Heading** (required, up to 150 characters) and **Address** (required, up to 100 characters: lower-case letters, digits and single hyphens, no slashes or spaces, for example `size-guide`).',
          'Write **The page itself** in the big box as HTML (see below). Optionally fill **Search-engine title** (up to 160; empty uses the heading and the shop name) and **Search-engine description** (up to 320).',
          'Press **Save**. A new page is always created **not on the shop**.',
          'Back on the list, press **Put on the shop**. The footer links it at once. **Take off the shop** reverses it.',
        ],
      },
      {
        kind: 'list',
        title: 'The page body is HTML',
        items: [
          'There is no toolbar. Type or paste HTML. Kept: `<h2>`, `<h3>`, `<h4>`, `<p>`, `<br>`, `<strong>`, `<em>`, `<u>`, lists, quotes, `<hr>`, links, tables, `<img src="..." alt="...">` and figures.',
          'Removed: `<h1>` (the page Heading is the only h1), `<div>`, `<span>` and `<form>`, keeping the text inside them; the `class`, `id` and `style` settings; and scripts, styles, iframes, video and drop-down lists, which are deleted together with what is inside them. After saving, the screen lists what it removed.',
          'A starting layout that works: short `<h2>` headings with a short paragraph or bullet list under each.',
          'A body can hold up to 200,000 characters. Pictures are pasted by address: there is no upload button inside a page.',
        ],
      },
      {
        kind: 'list',
        items: [
          'A page cannot be put on the shop while any picture in it has no alt text: "This page holds an image with no alt text, so it cannot go on the shop yet." Editing a live page so a picture loses its alt text is refused too. The list marks such pages "(n) image(s) need alt text".',
          'The address can be changed, even on the built-in pages, but it breaks every link already saved to the old one. Some addresses are taken by the shop and refused, such as `product`, `search`, `bag`, `checkout`, `account`, `sale`, `new-arrivals` and the department names `women`, `men`, `kids`.',
          'Only your own pages can be removed. **Remove** has no confirmation and deletes at once; its address can then be reused. The built-in six cannot be removed: take them off the shop instead.',
          'There is no preview. Put the page on the shop to see it, or write it carefully and check it after.',
          'If someone else changed the same page you are told to reload the browser page and repeat your changes. There is no Reload button on this screen.',
        ],
      },
    ],
  },
  {
    id: 'product-page-text',
    group: 'Shop content',
    title: 'Delivery and returns text on product pages',
    summary:
      'The two short collapsible panels, "Delivery" and "Returns and exchanges", that appear on every product page. They are edited in Settings, not here.',
    screens: [{ label: 'Settings', path: '/settings' }],
    blocks: [
      {
        kind: 'text',
        text: 'Open **Settings** and find the panel **Product page: delivery and returns**. It needs the **Settings Edit** permission, so the Content Editor role cannot change it. It has two boxes, **Delivery text** and **Returns text**, and the button **Save product page texts**.',
      },
      {
        kind: 'list',
        items: [
          'The same two texts appear on **every** product. You cannot write different text for one product.',
          'The delivery **fees and days are drawn from your delivery rates**, so do not type them. Use the box for a few lines: how you deliver in Sri Lanka and overseas, and how customers pay.',
          'Each box takes up to **4,000 characters** of HTML. Kept: paragraphs, line breaks, headings, lists, bold, italic, underline, strike-through, sub and superscript, quotes, rules, links and tables. Removed: pictures and anything else. Empty shows no text.',
          'After saving you see "Product page texts saved. They show on the next product page." and, if something was removed, what.',
          'The panels link to the Shipping and Returns pages when those pages are on the shop. Keep the longer detail on those pages.',
        ],
      },
    ],
  },
];
