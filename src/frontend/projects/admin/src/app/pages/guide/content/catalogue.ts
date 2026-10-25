import { GuideSection } from '../guide.model';

/**
 * Catalogue: products, departments and categories, inventory and reviews. Every limit and message here was
 * read from the code (the validators, the form components and the API handlers) when the guide was written;
 * when one of those changes, change the sentence beside it.
 */
export const CATALOGUE_SECTIONS: readonly GuideSection[] = [
  {
    id: 'products',
    group: 'Catalogue',
    title: 'Products: add, publish, hide and remove',
    summary:
      'Everything a customer sees on a product page is set here. Read this first, then use the field-by-field table in the next section.',
    screens: [
      { label: 'Products', path: '/products' },
      { label: 'Add a product', path: '/products/new' },
    ],
    blocks: [
      {
        kind: 'text',
        text: 'A product is one style, such as a shirt, with its **colours**, **sizes**, photographs and wording. Each colour-and-size combination is counted and sold on its own. How many you have of each is kept on the [Inventory](/inventory) screen, not on the product form.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Products View** to look, **Create** to add, **Edit** to change and to put on or take off the shop, **Delete** to remove. Uploading a photograph or video also needs the **Media Create** permission. The buttons are not hidden if you lack a permission; pressing one shows "You do not have permission to do that." See [Roles and permissions](#staff-roles).',
      },
      { kind: 'heading', text: 'Add a product' },
      {
        kind: 'steps',
        items: [
          'Open **Products** and press **Add a product**.',
          'Under **The product**, fill in **Product code**, **Name**, **Web address** (it is suggested from the name), **Department** and **Main category**. For the Kids department also choose an **Age group**.',
          'Enter **Price (Rs.)**. If the product is reduced, enter the higher old price in **Original price (Rs., optional)**. The shop crosses it out and works out the percentage off.',
          'Under **Colours**, press **Add a colour** for each colour: its **Name**, **Colour family** and **Swatch colour** (see the warning below).',
          'Choose the **Size scale** and tick the **Sizes offered**. A card appears for every colour and size, each with a suggested SKU. Check them.',
          'Under **Photographs and video**, add at least one gallery photograph and type **Alt text** for every photograph. Sizes and quality are in [Photos and video](#photos-and-video).',
          'Fill in **Details** (short description, description, care, material, fit, occasion) and the two **Search engines** boxes.',
          'Press **Create product**. It is created **not published**, and you land on its edit page.',
          'Add stock for each size on the [Inventory](/inventory) screen. New sizes start at 0.',
          'Press **Put on the shop**.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Always type a **Swatch colour** for every colour, for example `#3a5a8c` (a hash and six letters or digits). The shop draws the little colour dot from it alone, and a colour with no swatch shows an invisible dot.',
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Most mistakes are not shown beside the box that caused them. If the form says only "One or more fields are invalid.", check the limits in [Product form, field by field](#product-fields): colour names, per-size prices, barcodes, description and search-engine lengths, alt text and related products all fail this way.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'The form shows no visible "Saved" message. After **Save changes** the form refreshes with your changes in it, and a red message appears if something was refused. After **Create product** the page title changes to "Edit product" and the address changes to `/products/` and a number. There is also no warning when you leave with unsaved edits, so save before you navigate away.',
      },
      { kind: 'heading', text: 'Put on the shop and take off the shop' },
      {
        kind: 'text',
        text: '**Put on the shop** only works when three things are true: at least one size is switched on, at least one **Gallery** photograph is attached, and every Gallery or Hover photograph has alt text. If something is missing, a yellow box headed "Before this product can go on the shop:" lists it above the Save button and the button is greyed out.',
      },
      {
        kind: 'text',
        text: '**Take off the shop** is not held up by the publish checklist. The only thing that can stop it is someone else having saved the product since you opened it: reload and press it again. The product disappears from the shop at once, and its stock, orders and photographs stay exactly as they were. Both buttons act immediately and **throw away any edits you have not saved**, so press **Save changes** first. Both are also on the product list, as one click with no confirmation.',
      },
      {
        kind: 'text',
        text: 'A live product cannot be edited into a state it could not be published in (for example removing its last photograph or switching off every size). The save is refused with a message telling you to take it off the shop first.',
      },
      { kind: 'heading', text: 'Remove a product' },
      {
        kind: 'text',
        text: 'Open the product and press **Remove this product** (this needs the **Products Delete** permission, and the button is only on the edit page). You are asked "Remove (the product\'s name)? It leaves the shop at once. Its sizes, photographs and order history are kept, but nothing brings it back from this screen." Choose **Yes, remove it** or **Keep it**.',
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Nothing on screen brings a removed product back. Its sizes, photographs and order history are kept, but only a developer can restore it. If you may want it again, use **Take off the shop** instead. A product that is in a customer\'s bag is removed anyway: the customer sees "This item is no longer available. Remove it to carry on." and cannot check out until they do.',
      },
      {
        kind: 'text',
        text: 'A removed product\'s code and web address can be used again. Its SKUs cannot: a SKU is unique across every size the shop has ever had, removed products included.',
      },
      { kind: 'heading', text: 'Find a product in the list' },
      {
        kind: 'list',
        items: [
          '**Search** matches the name, product code, web address and any SKU, anywhere in the text.',
          'Filter by **Department**, and by **On the shop**: published and not, published only, or not published only.',
          '**Order** by recently edited (the default), name, product code, or price low to high or high to low.',
          'Each row shows the photo, name, code, department and category, price, and how many colours and sizes are available, with pills for **On the shop** or **Not published** and for any badge you set. There are 24 products per page. The list has no bulk actions, no copy-a-product button and no category filter.',
        ],
      },
      { kind: 'heading', text: 'Two people editing, and when changes appear' },
      {
        kind: 'text',
        text: 'If someone else saved the same product while you were editing, your save is refused with "Somebody else changed this while you were editing it. Reload the page to see their change, then re-apply yours." Reload the browser page, then repeat your change.',
      },
      {
        kind: 'text',
        text: 'The shop reads products live. A change is on the shop the next time a customer loads the page; someone already looking at the page sees it when they refresh. The homepage rows (New arrivals, Sale, automatic Trending) can be up to 5 minutes behind a price, stock or publish change. Every change is recorded in the [Audit log](#audit-log) under the entity "Product".',
      },
    ],
  },
  {
    id: 'product-fields',
    group: 'Catalogue',
    title: 'Product form, field by field',
    summary:
      'Every box on the product form, what to type, and the limits. Limits are enforced; a box that breaks one is refused when you save.',
    screens: [{ label: 'Add a product', path: '/products/new' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Prices are in rupees only. There is no dollar price and no tax box: PayPal customers pay a dollar amount the shop works out from the rupee total at the rate, plus a small buffer, set in [Settings](#settings).',
      },
      { kind: 'heading', text: 'The product' },
      {
        kind: 'table',
        caption: 'The product box',
        head: ['Box', 'What to type', 'Limits'],
        rows: [
          ['Product code', 'Your own style code, for example `SD-W-0001`.', 'Required. Up to 40 characters, unaccented letters, digits and punctuation only. Must not be used by another product.'],
          ['Name', 'The title customers see, for example "Relaxed Linen Blend Shirt". Leave out the code and any brand.', 'Required. Up to 200 characters.'],
          ['Web address', "The last part of the product's address on the shop: a product with the web address `linen-shirt` is at `/product/linen-shirt`. (The form shows `/p/` in front of the box; ignore that, it is a labelling mistake.) It is suggested from the name until you change it by hand.", 'Required. Lower-case letters, digits and single hyphens. Up to 220 characters. Must be unique.'],
          ['Department', 'Women, Men or Kids. Changing it clears the category, extra categories and age group.', 'Required.'],
          ['Main category', 'Where the product lives. It must belong to the chosen department. A hidden category is shown as "(switched off)".', 'Required.'],
          ['Also list it in', 'Tick extra categories of the same department the product should also appear in.', 'Up to 20. The main category is always included.'],
          ['Age group', 'Only for Kids: Baby, Toddler, Kids or Juniors. The shop\'s age filters are built from it.', 'Required for Kids.'],
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Changing the **Web address** of a product that has ever been on the shop breaks every old link to it. There are no redirects. The form asks you to confirm with "Yes, change the address" or "Keep the old address" before it does.',
      },
      { kind: 'heading', text: 'Price' },
      {
        kind: 'table',
        caption: 'The price boxes',
        head: ['Box', 'What to type', 'Limits'],
        rows: [
          ['Price (Rs.)', 'The price the customer pays today.', 'Required. More than 0, up to 10,000,000.00, at most two decimal places.'],
          ['Original price (Rs., optional)', 'The higher, old price, only if this is a markdown. The form shows "That is N% off."', 'Must be higher than the price. Leave empty if not reduced.'],
        ],
      },
      { kind: 'heading', text: 'Details' },
      {
        kind: 'table',
        caption: 'The details boxes',
        head: ['Box', 'What to type', 'Limits'],
        rows: [
          ['Short description', 'One plain sentence. It is used as the page text if the description is empty.', 'Plain text, up to 500 characters.'],
          ['Description', 'The full description. See the note below about paragraphs.', 'Up to 20,000 characters.'],
          ['Care instructions', 'Short plain sentences, for example "Wash inside out, cold, with like colours."', 'Plain text, up to 1,000 characters.'],
          ['Material', 'Choose one: Cotton, 100% Cotton, Cotton blend, Linen, Linen blend, Denim, Viscose or Polyester blend.', 'Optional.'],
          ['Fit', 'Regular, Slim, Relaxed, Wide Leg or Oversized.', 'Optional.'],
          ['Occasion', 'Casual, Smart Casual, Formal, Party, Traditional or Active.', 'Optional.'],
          ['Badge', 'Leave on "Work it out automatically" unless you have a reason. Or choose New, Sale, Online Exclusive or Best Seller. One badge is shown on a card.', 'Low Stock cannot be chosen: it is worked out from stock.'],
          ['Size guide', 'Choose the guide customers see from "Size guide" on the product page. Guides are made under [Size guides](#size-guides).', 'Optional. It is NOT copied from the category: choose it on every product, or none shows.'],
          ['Recommended rank (optional)', 'A whole number that pins the product high in the "Recommended" order. Lowest number first.', '0 or more. Empty means not pinned.'],
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'The **Description** box looks like plain text, but the shop shows it as web text, so pressing Enter does **not** make a new paragraph there. Put each paragraph between `<p>` and `</p>`, for example `<p>First paragraph.</p><p>Second paragraph.</p>`. Bold (`<strong>` ... `</strong>`) and bullet lists (`<ul><li>` ... `</li></ul>`) also work. Scripts and styling are removed. The short description, care instructions and search-engine boxes are plain text: do not put tags in them.',
      },
      { kind: 'heading', text: 'Related products' },
      {
        kind: 'table',
        caption: 'Related products',
        head: ['Box', 'What to do', 'Limits'],
        rows: [
          ['Find a product', 'Type at least two letters, then press **Add** next to a match. Use Move up and Move down to order them.', 'Up to 12 picks. A product cannot be related to itself.'],
        ],
      },
      {
        kind: 'text',
        text: 'Your picks appear first in the related products on the product page; the shop fills the rest from the same category. It shows four.',
      },
      { kind: 'heading', text: 'Search engines' },
      {
        kind: 'table',
        caption: 'Search-engine boxes',
        head: ['Box', 'What to type', 'Limits'],
        rows: [
          ['Page title', "The title search engines show for this product's page.", 'Optional. Up to 160 characters.'],
          ['Page description', 'One or two plain sentences search engines show under the title.', 'Optional. Up to 320 characters.'],
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Moving things up and down on this form (colours, size cards, photographs, related products) uses **Move up** and **Move down** buttons. There is no dragging.',
      },
    ],
  },
  {
    id: 'product-variants',
    group: 'Catalogue',
    title: 'Colours, sizes and SKUs',
    summary:
      'How the product form turns colours and sizes into the things customers buy, and what to do when you do not make a combination.',
    screens: [{ label: 'Add a product', path: '/products/new' }],
    blocks: [
      {
        kind: 'steps',
        items: [
          'Under **Colours**, add one row per colour. Give it a **Name** (customers see this, up to 60 characters, unique within the product), a **Colour family** (White, Cream, Beige, Brown, Black, Grey, Navy, Blue, Green, Red, Pink, Purple, Yellow or Orange; the shop\'s colour filter groups by it) and a **Swatch colour**.',
          'Choose one **Size scale**: Alpha (XS to XXXL), Women waist (24 to 36), Men waist (28 to 40), Kids age (2-3Y to 13-14Y), Baby months (0-3M to 18-24M), Footwear EU (35 to 46) or One size. Changing the scale clears the ticked sizes.',
          'Tick the **Sizes offered**. The page builds one card for every colour and size, for example "Indigo Rinse, 30", with a suggested **SKU** such as `SD-W-0009-INDIGO-RINSE-30`. Cards you have already edited keep what you typed.',
          'Check each card. **SKU** is required: up to 64 characters, no spaces, and it must be different from every other SKU the shop has ever used, including removed products. **Barcode (optional)** is up to 32 characters with no spaces and also unique.',
          '**Price for this size (optional)** and **Original price for this size (optional)** replace the product prices for that one size, for example a higher price for the largest sizes.',
          'If you do not make a colour in a size, untick **Available to buy** on that card. There is no delete button on a card.',
        ],
      },
      {
        kind: 'text',
        text: 'Each card shows "In stock: N" and tells you to change the number on the Inventory screen. Pills **Ordered** and **In stock** mark sizes that have orders or stock. Those can never be deleted: unticking the size keeps its card, switched off. If a save would delete one anyway, you are told which SKUs to switch off instead.',
      },
      {
        kind: 'text',
        text: 'Removing a colour also removes its sizes and any photographs linked only to that colour, in the form. Nothing is final until you press **Save changes**. A product can have up to 40 colours and 1,600 colour-and-size combinations.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'The order of sizes on the shop follows the size scale (XS before S before M), not the order of the cards on screen.',
      },
    ],
  },
  {
    id: 'product-pricing',
    group: 'Catalogue',
    title: 'Sale prices, badges and "New"',
    summary:
      'What the shop does with your prices, and how a product ends up on the Sale and New Arrivals pages without you doing anything.',
    blocks: [
      {
        kind: 'list',
        items: [
          'When the original price is higher than the price, the shop shows the old price crossed out, a **Sale** badge (unless you chose a different **Badge** on the product) and the percentage off (rounded down). The **Sale** page lists every product with a discount.',
          'The same applies to one size when you give it its own price and original price. The original price must be higher than the price that applies, or the save is refused.',
          '**New Arrivals** lists products for 30 days after they were first put on the shop. You do not put products into a "New" or "Sale" category.',
          'The badge on a card is the one you chose, or, if you left it on automatic: Sale, then Low Stock, then Best Seller (the ten best sellers in the department over the last 30 days), then New.',
          'A [sale campaign](#sale-campaigns) can lower prices for a period. If you save a product while one is running, the campaign price is applied again afterwards.',
        ],
      },
    ],
  },
  {
    id: 'categories',
    group: 'Catalogue',
    title: 'Departments and categories',
    summary:
      'The shop menu: three fixed departments and the categories under them. What you can change, what hiding does, and what does nothing yet.',
    screens: [{ label: 'Departments and categories', path: '/catalog' }],
    blocks: [
      {
        kind: 'text',
        text: 'The shop has three departments: **Women**, **Men** and **Kids**. The main menu shows them in the order you set, followed by the fixed **New Arrivals** and **Sale**. Each department has up to three levels of categories, such as Women, Jeans. Customers reach products through these.',
      },
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Categories View**, **Create**, **Edit** and **Delete**. Departments are changed with **Categories Edit**.',
      },
      { kind: 'heading', text: 'Departments' },
      {
        kind: 'list',
        items: [
          'You can change a department\'s **Description**, **SEO title** and **SEO description** (up to 1,000, 160 and 320 characters), move it earlier or later, and hide or show it.',
          'You cannot add, remove or rename a department, or change its web address. The page\'s hint says you can rename them; that is wrong.',
          'Moving a department stages the new order. A yellow box says "The navigation order is not saved yet." Press **Save order** or **Discard**.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: '**Hide** on a department takes effect at once with no confirmation and no warning. Every product in that department leaves the shop, its listings and the menu. Use it with care.',
      },
      { kind: 'heading', text: 'Add, change and move a category' },
      {
        kind: 'steps',
        items: [
          'Choose the department with its **Categories** button, then press **Add a top-level category**, or **Add child** on a category to make one under it. (Level 3 categories cannot have children.)',
          'Fill **Name** (required, up to 80 characters) and **URL slug** (required, lower-case letters, digits and single hyphens, up to 100 characters, unique among categories in the same place).',
          'Optionally set **Parent category**, **Mega-menu column (1-4)** (blank spreads evenly), **Description** (up to 2,000 characters, shown on the shop under the category heading), **SEO title**, **SEO description** and **Show in the mega menu**.',
          'Press **Create category**. It is live at once and joins the end of its list.',
          'To change a category, press **Edit**. To move it, change **Parent category**: it and everything under it move together. It joins the end of its new parent\'s list. You cannot move it into another department, into itself or under its own child, or deeper than three levels.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Changing a category\'s URL slug changes its page address. Old links to the category page stop working and there are no redirects. Product pages are not affected.',
      },
      { kind: 'heading', text: 'Order, hide and remove' },
      {
        kind: 'list',
        items: [
          '**Move up** and **Move down** (the arrows) arrange categories among their neighbours. Moves are staged. A yellow box says how many lists have a new order. Press **Save order** to save them all. If the list reloads before you save, the order you arranged is discarded with a notice, and nothing else is lost.',
          '**Hide** asks first and tells you the cost: how many products will disappear from the shop, how many categories underneath drop out of the menu, and how many products stay on sale underneath with no menu route to them. Press **Hide it** or **Cancel**. **Show** brings it back.',
          '**Remove** (needs **Categories Delete**) is refused while the category has any product (even an unpublished one) or any child category (even a hidden one). Move or remove those first. A removed category cannot be brought back from the screen.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'A product disappears from the shop when its **main** category or its department is hidden. Its extra categories do not keep it visible.',
      },
      { kind: 'heading', text: 'Boxes that do nothing yet' },
      {
        kind: 'list',
        items: [
          '**Size guide** and **Age group** on a category are saved but the shop does not read them. Choose the size guide on each product, and the age group on each Kids product.',
          '**Banner link** is saved but not shown anywhere. The category\'s tile image, banner image and a department\'s hero image cannot be set from the admin at all.',
          'Some hints on this screen mention internal document numbers. Ignore them.',
        ],
      },
      {
        kind: 'text',
        text: 'If someone else changed the same row, you see "Somebody else changed this while you were editing it. Reload the page to see their change, then re-apply yours." with a **Reload the tree** button.',
      },
    ],
  },
  {
    id: 'inventory',
    group: 'Catalogue',
    title: 'Inventory and stock',
    summary:
      'See what you have of every colour and size, record deliveries and stock-takes, set low-stock warnings and read the history.',
    screens: [{ label: 'Inventory', path: '/inventory' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Inventory View** to see the screen (the link is hidden without it) and **Inventory Edit** to change anything. With View only you see "Your role can see stock but not change it."',
      },
      {
        kind: 'text',
        text: 'Each colour-and-size has **On hand** (what is on the shelf), **Reserved** (held by unpaid orders) and **Available** (on hand less reserved). **Out of stock** means available is 0. **Low stock** means available is between 1 and the size\'s threshold. The shop default threshold is 5 and is set by the developers, not on a screen. Only switched-on sizes of products that have not been removed are listed, including unpublished products.',
      },
      { kind: 'heading', text: 'Find stock' },
      {
        kind: 'text',
        text: 'Use **Find variants**: search by product name, SKU or code, then filter by **Stock** (every, in stock, low, out of stock), **Department** and **Category**, and choose the **Order** (including fewest available first). The dashboard\'s low-stock link opens the low list. There are 25 rows per page.',
      },
      { kind: 'heading', text: 'Change stock' },
      {
        kind: 'steps',
        items: [
          'Press **Stock** on the row (the blue button; **Threshold** is teal and **History** is grey). The status beside it is green for In stock, amber for Low stock and red for Out of stock.',
          'Under **What happened**, choose one: **Received from a supplier or production**, **Add units (found, returned)**, **Remove units (damaged, lost)** or **Set the count after a stock-take**.',
          'Enter the number. Received, add and remove take 1 to 100,000. A count takes 0 to 1,000,000.',
          'Type a **Reason** (required, up to 250 characters), for example "Delivery from the factory" or "Damaged in store". The preview line shows what will be on hand after saving.',
          'Press **Save**.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'On hand can never go below what unpaid orders have reserved. You are told "N units are reserved for unpaid orders, so on hand cannot go below N. Nothing was changed." And a stock-take count is only applied if the on-hand number has not moved since you opened the screen; otherwise you are told the new number and nothing is changed. Check the count and save again.',
      },
      { kind: 'heading', text: 'Change several at once' },
      {
        kind: 'text',
        text: 'Tick the rows (only on the current page) and press **Change stock for selected**. You apply the same kind of change, with one reason, to every ticked row. A row that cannot take the change is left as it was and listed in the result, for example "3 saved, 1 not changed". There is no spreadsheet upload on this screen.',
      },
      { kind: 'heading', text: 'Low-stock threshold and history' },
      {
        kind: 'list',
        items: [
          '**Threshold** sets, per colour and size, either **Use the shop default (5)** or **Its own threshold** (0 to 10,000: "low at this many available or fewer"). 0 means the size is never shown as low.',
          '**History** lists every movement, newest first: when (Colombo time), what, on hand, reserved, the reason and who. Entries from orders read "Held by an order", "Released by an order", "Sold" or "Restocked after a refund" with the order number. Your own changes read "Received", "Adjusted in", "Adjusted out" or "Stock-take".',
        ],
      },
      { kind: 'heading', text: 'On the shop' },
      {
        kind: 'text',
        text: 'A size with none available stays on the page but cannot be added to the bag ("That size is out of stock. Choose another size."). When every size is out the page says "This product is out of stock." and the product is **not** hidden from listings. The shop has no back-in-stock alert and no waiting list. A low product reads "Only N left". There is no low-stock email: watch the dashboard\'s low-stock panel.',
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Refunds are made by hand outside the admin and never put stock back by themselves. When a refunded item comes back and can be sold, add it yourself: press **Stock** on its row, choose **Add units (found, returned)**, type the number and a **Reason** such as "Returned, order SD-2026-000123", and press **Save**. See [Refunds](#refunds).',
      },
    ],
  },
  {
    id: 'reviews',
    group: 'Catalogue',
    title: 'Customer reviews',
    summary: 'Approve or hide the star ratings and comments customers write about products.',
    screens: [{ label: 'Reviews', path: '/reviews' }],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Reviews View** to see the screen (the link is hidden without it), **Approve** to approve or show again, **Edit** to hide.',
      },
      {
        kind: 'text',
        text: 'Only signed-in customers can write a review, one per product, and the product must be on the shop. Anyone signed in can review, whether or not they bought it. A **Verified buyer** badge appears when that account has a Delivered order containing the product. Guests cannot write reviews.',
      },
      {
        kind: 'text',
        text: 'Reviews wait for your approval by default. That is the setting **Approve reviews before they appear** in [Settings](#settings) (it needs **Settings Edit**). If it is switched off, reviews appear as soon as they are written. A review you hide stays hidden either way. While that setting is on, editing an approved review sends it back to waiting. Editing a hidden one leaves it hidden.',
      },
      {
        kind: 'steps',
        title: 'Moderate reviews',
        items: [
          'Open **Reviews**. It starts on **Waiting for approval**, oldest first. Change **Show** to Shown on the site, Hidden or Every review, and **Buyer** to Verified buyers or Not verified.',
          'Read the card: product, stars, title, text, the reviewer\'s name as "First L." and whether they are a verified buyer.',
          'Optionally type a **Note for staff (optional)** (up to 500 characters). The customer never sees it.',
          'Press **Approve** (on a hidden review the same button reads **Show again**) or **Hide**.',
        ],
      },
      {
        kind: 'list',
        items: [
          'Only approved reviews are shown on the product page and counted in the average. Approving or hiding recalculates the average rating at once.',
          'Hide reviews with offensive wording or personal details. There is no way to edit a review\'s text, delete it, or reply to it, and there is no spam or bad-word filter: moderation is the control.',
          'If another moderator or the author changed the review since your page loaded, you see "Somebody changed this review since the page loaded ... Nothing was changed. Check it again and decide."',
        ],
      },
    ],
  },
];
