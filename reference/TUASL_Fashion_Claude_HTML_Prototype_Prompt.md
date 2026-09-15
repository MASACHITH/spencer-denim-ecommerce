# Claude Code Master Prompt
## TU@SL Fashion — Max Fashion-Inspired Static HTML Client Prototype

You are a senior frontend developer, e-commerce UI/UX designer and quality-assurance engineer.

Create a complete, polished, client-ready fashion e-commerce website prototype for:

- **Company:** TU@SL
- **Store name:** TU@SL Fashion
- **Tagline:** Fashion for Everyone
- **Currency:** LKR

The primary reference website is:

- https://www.maxfashion.com/
- https://www.maxfashion.in/in/en/

Before writing code, carefully review the reference website and study its homepage, announcement bar, delivery-location area, search, account access, favourites, basket, department navigation, mega menus, campaigns, category pages, product grids, filters, sorting, product detail pages, mobile behaviour and footer.

The goal is to produce a static prototype that gives the client a shopping experience and content structure comparable to a large family-fashion retailer.

## 1. Critical Reference Rules

Use Max Fashion as the main functional, structural and visual-quality reference.

Study and recreate the general ideas behind:

- Announcement and benefit bars
- Large search-led header
- Women, Men, Kids and Baby departments
- Desktop mega menus
- Mobile slide-out navigation
- Promotional campaign blocks
- Category and department landing pages
- Dense but readable product grids
- Product badges, pricing and discounts
- Filters and sorting
- Wishlist and shopping-bag flows
- Product details and recommendations
- Footer organisation
- Responsive behaviour

Do not:

- Use the Max Fashion logo or name in the prototype
- Copy its source code
- Reproduce its site pixel for pixel
- Copy its exact colour palette or typography
- Copy its product names, descriptions, prices or catalogue data
- Scrape or mirror the site
- Display the site in an iframe
- Reuse Max Fashion image or video URLs
- Download or embed its protected photos, videos, banners or icons

Create original TU@SL branding, dummy catalogue content and a distinct visual identity.

## 2. Technology

Use only:

- HTML5
- CSS3
- Vanilla JavaScript
- Google Fonts through CDN
- Lucide Icons or Font Awesome through CDN

Do not use:

- React
- Next.js
- Vue
- Angular
- PHP
- Node.js
- npm
- A database
- A backend
- Build tools

The website must work by opening `index.html` locally and by uploading the files directly to Hostinger under `public_html/`.

## 3. Folder Structure

```text
tuasl-fashion/
├── index.html
├── women.html
├── men.html
├── kids.html
├── baby.html
├── new-arrivals.html
├── sale.html
├── category.html
├── product.html
├── search.html
├── wishlist.html
├── cart.html
├── checkout.html
├── success.html
├── about.html
├── contact.html
├── css/
│   ├── style.css
│   ├── responsive.css
│   └── animations.css
├── js/
│   ├── config.js
│   ├── products.js
│   ├── app.js
│   ├── filters.js
│   ├── product-page.js
│   ├── cart.js
│   └── wishlist.js
├── assets/
│   ├── images/
│   │   ├── hero/
│   │   ├── campaigns/
│   │   ├── categories/
│   │   ├── products/
│   │   └── placeholders/
│   ├── videos/
│   └── icons/
├── README.md
├── MEDIA_SOURCES.md
└── HOSTINGER_UPLOAD_GUIDE.md
```

All page links, assets and scripts must work with ordinary static relative paths.

## 4. Brand Identity

Use the temporary brand identity:

- **TU@SL Fashion**
- **Fashion for Everyone**

Create an original text or simple SVG logo. Do not imitate an existing fashion logo.

Suggested CSS variables:

```css
:root {
  --primary: #8b1e3f;
  --primary-dark: #5f1029;
  --accent: #e8c3b5;
  --background: #f8f6f3;
  --surface: #ffffff;
  --text: #222222;
  --muted: #6f6f6f;
  --border: #e5e5e5;
  --sale: #c62828;
  --success: #287a45;
}
```

Suggested typography:

- Headings: Playfair Display or Cormorant Garamond
- Body: Inter, Manrope or Poppins

Keep the design modern, affordable, family-friendly, clean, spacious and highly visual.

## 5. Central Configuration

Create `js/config.js` with one easy-to-edit configuration object:

```js
const siteConfig = {
  companyName: "TU@SL",
  storeName: "TU@SL Fashion",
  tagline: "Fashion for Everyone",
  currency: "LKR",
  currencySymbol: "Rs.",
  phone: "+94 77 123 4567",
  whatsapp: "+94 77 123 4567",
  email: "hello@tuaslfashion.com",
  address: "Colombo, Sri Lanka",
  facebook: "#",
  instagram: "#",
  tiktok: "#",
  youtube: "#",
  freeDeliveryMinimum: 10000,
  standardDeliveryFee: 450,
  announcementMessages: [
    "Islandwide delivery",
    "Free delivery above Rs. 10,000",
    "Easy returns within 14 days",
    "New collection now available"
  ]
};
```

Use these shared values throughout the website where practical.

## 6. Media and Copyright Requirements

Do not use Max Fashion photos, videos, product images, banners or CDN links.

Use temporary licensed or royalty-free fashion media from:

- Pexels
- Unsplash
- Pixabay

Use the Max Fashion media only to understand:

- Image aspect ratios
- Campaign composition
- Model positioning
- Product-image backgrounds
- Desktop and mobile cropping
- Text-overlay positioning
- Video placement

For the prototype, use direct royalty-free URLs or download approved files into the local `assets` folders.

Do not use Google Images result URLs or third-party retailer media.

Avoid photos containing clearly visible third-party logos or trademarks.

Do not state or imply that stock-photo models endorse TU@SL Fashion.

Every external asset must be documented in `MEDIA_SOURCES.md` using this table:

```markdown
| Asset | Source Platform | Creator | Original Page URL | Local File or External URL | Used On | Licence Note |
|---|---|---|---|---|---|---|
```

Each image must include:

- Meaningful alt text
- `loading="lazy"` when below the fold
- Width and height or stable aspect ratio
- A local fallback placeholder

Example:

```html
<img
  src="ROYALTY_FREE_IMAGE_URL"
  alt="Woman wearing a modern casual outfit"
  width="720"
  height="960"
  loading="lazy"
  onerror="this.src='assets/images/placeholders/product-placeholder.svg'"
>
```

## 7. Homepage Video

Use one royalty-free fashion or lifestyle video for the hero where suitable.

The video must:

- Autoplay
- Be muted
- Loop
- Use `playsinline`
- Include a poster image
- Have a dark overlay
- Include a static-image fallback
- Respect reduced-motion settings
- Not block the page load
- Be replaced with a lightweight image on small devices when appropriate

Do not use a Max Fashion video or video URL.

## 8. Header and Navigation

Create a responsive header inspired by a large fashion-retail site.

### Announcement bar

Rotate configurable messages such as delivery, returns and new collections.

### Main header

Include:

- TU@SL Fashion logo
- Delivery-location placeholder
- Large search field
- Sign In
- Wishlist with count
- Shopping Bag with count
- Mobile-menu button

### Main navigation

Include:

- Women
- Men
- Kids
- Baby
- Activewear
- Footwear
- Accessories
- New Arrivals
- Sale

The header should become sticky while scrolling.

## 9. Mega Menu

Create a large desktop mega menu and a mobile accordion menu.

### Women

- New Arrivals
- Dresses
- Tops and T-Shirts
- Shirts and Blouses
- Jumpsuits
- Jeans
- Trousers
- Leggings
- Shorts
- Skirts
- Co-ords
- Jackets
- Knitwear
- Activewear
- Sleepwear
- Loungewear
- Innerwear
- Occasionwear
- Traditional Wear
- Plus Size
- Maternity
- Footwear
- Bags
- Jewellery
- Accessories

### Men

- New Arrivals
- T-Shirts
- Polo Shirts
- Casual Shirts
- Formal Shirts
- Jeans
- Trousers
- Shorts
- Joggers
- Hoodies
- Jackets
- Activewear
- Loungewear
- Innerwear
- Traditional Wear
- Footwear
- Bags
- Belts
- Wallets
- Accessories

### Kids

Separate Boys and Girls and include:

- 0–2 Years
- 2–4 Years
- 4–6 Years
- 6–10 Years
- 10–14 Years
- 14–16 Years
- Tops
- T-Shirts
- Dresses
- Shirts
- Jeans
- Trousers
- Leggings
- Shorts
- Skirts
- Clothing Sets
- Sleepwear
- Partywear
- Schoolwear
- Footwear
- Accessories

### Baby

- Newborn
- Baby Girls
- Baby Boys
- Bodysuits
- Sleepsuits
- Clothing Sets
- Dresses
- Tops
- Bottoms
- Essentials
- Footwear
- Accessories

Add one royalty-free promotional image card to each major mega menu.

## 10. Required Pages

Create:

- `index.html`
- `women.html`
- `men.html`
- `kids.html`
- `baby.html`
- `new-arrivals.html`
- `sale.html`
- `category.html`
- `product.html`
- `search.html`
- `wishlist.html`
- `cart.html`
- `checkout.html`
- `success.html`
- `about.html`
- `contact.html`

Do not stop after completing only the homepage.

## 11. Homepage Sections

Create a complete homepage with:

1. Announcement bar
2. Sticky header
3. Desktop mega menu and mobile navigation
4. Full-width hero image or video
5. Shop Women, Shop Men and Shop Kids actions
6. Shop by Department cards
7. New Arrivals
8. Promotional split banners
9. Trending Categories
10. Full-width campaign banner
11. Best Sellers
12. Kids campaign
13. Family Fashion section
14. Activewear campaign
15. Sale section
16. Social-style gallery using approved media
17. Customer benefits
18. Newsletter signup
19. Large multi-column footer

Suggested campaign copy:

```text
NEW SEASON 2026
Style for Every Story
Explore fresh fashion for women, men and kids.
```

Other campaign ideas:

- Everyday Essentials
- Weekend Collection
- Workwear Refresh
- Kids' Favourites
- Active Collection
- Occasion Edit
- Summer Sale

## 12. Dummy Product Catalogue

Create at least 80 original dummy products inside `js/products.js`.

Distribution:

- At least 20 Women products
- At least 20 Men products
- At least 15 Girls products
- At least 15 Boys products
- At least 10 Baby products
- Include activewear, footwear and accessories

Example object:

```js
{
  id: 1,
  name: "Relaxed Linen Blend Shirt",
  slug: "relaxed-linen-blend-shirt",
  department: "Women",
  category: "Shirts and Blouses",
  price: 5490,
  originalPrice: 6990,
  discount: 21,
  rating: 4.6,
  reviewCount: 38,
  colours: ["White", "Beige", "Sage"],
  sizes: ["XS", "S", "M", "L", "XL"],
  material: "Linen blend",
  fit: "Relaxed",
  occasion: "Casual",
  image: "ROYALTY_FREE_IMAGE_URL",
  hoverImage: "ROYALTY_FREE_IMAGE_URL",
  gallery: [
    "ROYALTY_FREE_IMAGE_URL",
    "ROYALTY_FREE_IMAGE_URL",
    "ROYALTY_FREE_IMAGE_URL"
  ],
  badge: "New",
  stock: 20,
  description: "An original demonstration product description for the client prototype."
}
```

Use realistic dummy Sri Lankan prices.

Never copy Max Fashion product names or descriptions.

## 13. Product Cards

Each product card must include:

- Main image
- Secondary hover image
- Wishlist icon
- Product badge
- Product name
- Category
- Current price
- Original price
- Discount percentage
- Rating
- Colour indicators
- Quick View
- Add to Bag

Use badges such as:

- New
- Sale
- Best Seller
- Online Exclusive
- Low Stock

Clicking a card must open:

```text
product.html?id=PRODUCT_ID
```

## 14. Product Listing and Category Pages

Create category pages that support query parameters such as:

```text
category.html?department=women
category.html?category=dresses
category.html?sale=true
```

Include:

- Breadcrumb
- Page heading
- Description
- Promotional banner
- Product count
- Sort dropdown
- Desktop filter sidebar
- Mobile filter drawer
- Product grid
- Clear Filters
- Load More
- Empty state

Required filters:

- Department
- Category
- Age group
- Price range
- Size
- Colour
- Material
- Fit
- Style
- Occasion
- Rating
- Availability
- Discount
- Promotion

Required sorting:

- Recommended
- Newest
- Best Selling
- Price Low to High
- Price High to Low
- Highest Rated
- Biggest Discount

All filtering and sorting must work in JavaScript.

## 15. Product Details Page

Create `product.html` and load the selected product using its URL `id`.

Include:

- Large image gallery
- Thumbnail navigation
- Hover or click zoom
- Full-screen image modal
- Optional video
- Product name and code
- Rating and review count
- Current and original prices
- Discount
- Colour selector
- Size selector
- Size guide modal
- Quantity selector
- Stock status
- Add to Bag
- Buy Now
- Add to Wishlist
- Delivery estimate
- Delivery methods
- Product description
- Material
- Care instructions
- Fit information
- Returns information
- Related products
- Recently viewed products

Disable Add to Bag until required size and colour selections are made.

## 16. Search

Create a working search overlay and `search.html` page.

Search through:

- Product name
- Category
- Department
- Colour
- Material
- Tags

Display:

- Suggested products
- Suggested categories
- Popular searches
- Recent searches
- No-results state

Support keyboard navigation and debounced input.

## 17. Wishlist

Use `localStorage`.

Support:

- Add product
- Remove product
- Wishlist count
- Move to cart
- Clear wishlist
- Empty state

## 18. Shopping Cart

Use `localStorage`.

Support:

- Product image and name
- Selected size and colour
- Quantity controls
- Remove
- Move to wishlist
- Subtotal
- Discount
- Delivery fee
- Final total
- Promo codes

Demo promo codes:

- `TUASL10` — 10% discount
- `FREESHIP` — removes delivery fee

## 19. Checkout Prototype

Create a frontend-only checkout with:

### Customer details

- Full name
- Email
- Phone

### Delivery address

- Address line 1
- Address line 2
- City
- District
- Postal code

### Delivery method

- Standard delivery
- Express delivery
- Store pickup

### Payment method

- Cash on delivery
- Card payment demonstration
- Bank transfer demonstration

Display clearly:

```text
This is a demonstration checkout. No real payment will be processed.
```

Do not collect real card details.

After successful validation:

1. Generate a dummy order number
2. Save a temporary order in `localStorage`
3. Clear the cart
4. Redirect to `success.html`

Example:

```text
TUASL-2026-001245
```

## 20. About and Contact Pages

### About page

Include editable dummy sections for:

- Our Story
- Our Vision
- Our Values
- Quality
- Customer Experience
- Islandwide Delivery

Clearly identify unsupported statistics or claims as prototype content.

Do not invent certifications or awards.

### Contact page

Include:

- Company name
- Address
- Phone
- WhatsApp
- Email
- Business hours
- Contact form
- Map placeholder
- Social links
- Floating WhatsApp button

## 21. Responsive Behaviour

Optimise and test at:

- 320px
- 375px
- 390px
- 768px
- 1024px
- 1280px
- 1440px
- 1920px

Use:

- Two product columns on mobile
- Three columns on tablets
- Four columns on common desktops
- Five columns on large displays where appropriate
- Mobile filter drawer
- Mobile slide-out menu
- Touch-friendly controls
- Sticky mobile actions where helpful

Do not allow horizontal scrolling, clipped text, overlapping controls or stretched images.

## 22. Accessibility

Implement:

- Semantic HTML
- Skip-to-content link
- Keyboard navigation
- Visible focus states
- Accessible mega-menu controls
- `aria-expanded` states
- Accessible dialogs
- Form labels
- Clear validation errors
- Descriptive alt text
- Sufficient colour contrast
- Reduced-motion support

## 23. Animation

Use subtle animation only:

- Hero content entrance
- Product-card hover
- Image zoom
- Mega-menu transition
- Mobile drawer transition
- Section reveal
- Wishlist and cart notifications
- Smooth sliders

Respect:

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    scroll-behavior: auto !important;
  }
}
```

## 24. Performance

Because the prototype may use remote media:

- Lazy-load below-the-fold images
- Use suitable image dimensions
- Include stable aspect ratios
- Load only one homepage video
- Use a poster image
- Pause video when not visible where practical
- Avoid excessive third-party scripts
- Avoid base64 images
- Avoid huge uncompressed local videos
- Use local fallbacks for failed media URLs

## 25. Documentation

Create `README.md` containing:

- Project purpose
- Technology
- Folder structure
- Local usage
- How to edit business information
- How to edit products
- How to replace media
- How wishlist and cart storage work
- Prototype limitations

Prototype limitations must state:

- No backend
- No database
- No real authentication
- No real payment processing
- No live stock synchronisation
- Orders exist only in the current browser
- Remote media may need replacement before production

Create `HOSTINGER_UPLOAD_GUIDE.md` explaining:

1. Zip the project
2. Open Hostinger hPanel
3. Open File Manager
4. Open `public_html`
5. Remove the default placeholder if needed
6. Upload and extract the ZIP
7. Ensure `index.html` is directly inside `public_html`
8. Connect the domain
9. Enable SSL
10. Test all pages and clear caches

## 26. Quality Checklist

Before completion:

1. Open every HTML page
2. Check all navigation links
3. Check desktop mega menus
4. Check the mobile menu
5. Check search
6. Check filters and sorting
7. Check product selection
8. Check wishlist
9. Check cart and promo codes
10. Check checkout validation
11. Check success page
12. Check responsive layouts
13. Check browser-console errors
14. Check broken-image fallbacks
15. Check media-source documentation
16. Check that no Max Fashion branding or protected content remains
17. Check that all customer-facing content uses TU@SL Fashion branding

Do not claim a feature works unless it has been tested.

## 27. First Claude Instruction

Start with this exact workflow:

1. Open and study `https://www.maxfashion.com/` as the primary UX and structure reference.
2. Review the home page, navigation, mega menus, product grids, filters, product pages, cart, mobile layout and footer.
3. Do not copy or reuse protected Max Fashion content.
4. Inspect the current project folder.
5. Create the complete static folder structure.
6. Build the shared header, mega menu and footer first.
7. Build the complete homepage based on the reference shopping flow.
8. Find suitable royalty-free fashion images and video.
9. Record every media source in `MEDIA_SOURCES.md`.
10. Create the complete dummy product catalogue.
11. Build every category, product, wishlist, cart, checkout, about and contact page.
12. Test desktop and mobile behaviour.
13. Fix broken links, media failures and console errors.
14. Prepare a Hostinger-ready project folder.

Do not ask for approval between stages. Use sensible dummy information wherever details are missing, keep everything easy to replace, and continue until the complete client prototype is ready.

## 28. Verified External Reference Notes

The Max Fashion terms state that its text, images, links, trademarks and other website materials are owned or licensed by Max and may not be copied or reused commercially without permission. Therefore, use the site as a design and interaction reference only.

For prototype media, use assets under the relevant source licence and review each asset for identifiable people, logos, trademarks and other third-party rights. Unsplash permits broad commercial and non-commercial use under its licence, while restricting the resale of unmodified images and compilation into a competing image service. Always preserve asset-source records in `MEDIA_SOURCES.md`.
