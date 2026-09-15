# TU@SL Fashion — Static HTML Prototype

A complete client-ready fashion e-commerce prototype for **TU@SL Fashion** ("Fashion for Everyone"), a Sri Lankan family fashion retailer.

## Technology

- HTML5, CSS3, Vanilla JavaScript
- Google Fonts (Playfair Display, Inter) via CDN
- No frameworks, no npm, no build tools, no backend

## Folder Structure

```
tuasl-fashion/
├── index.html            Homepage
├── women.html            Women's department
├── men.html              Men's department
├── kids.html             Kids' department
├── baby.html             Baby department
├── new-arrivals.html     New Arrivals listing
├── sale.html             Sale listing
├── category.html         Filterable product listing (accepts URL params)
├── product.html          Product detail page (?id=NUMBER)
├── search.html           Search page (?q=QUERY)
├── wishlist.html         Saved wishlist
├── cart.html             Shopping bag
├── checkout.html         Demo checkout
├── success.html          Order confirmation
├── about.html            About us
├── contact.html          Contact page
├── css/
│   ├── style.css         Main styles + CSS variables
│   ├── responsive.css    All breakpoints (320–1920px)
│   └── animations.css    Scroll reveals, hover effects
├── js/
│   ├── config.js         Site configuration (edit here)
│   ├── products.js       Product catalogue (80+ products)
│   ├── app.js            Shared: header, footer, nav, toasts
│   ├── filters.js        Filtering & sorting logic
│   ├── product-page.js   Product detail page logic
│   ├── cart.js           Shopping cart (localStorage)
│   └── wishlist.js       Wishlist (localStorage)
└── assets/
    └── images/placeholders/product-placeholder.svg
```

## Local Usage

1. Open `index.html` directly in a browser — no server needed.
2. For best results (avoids CORS issues), use VS Code Live Server or Python's `http.server`.

```bash
# Python quick server
python -m http.server 8000
# Then open http://localhost:8000
```

## How to Edit Business Information

Edit `js/config.js`:

```js
const siteConfig = {
  companyName: "TU@SL",
  storeName: "TU@SL Fashion",
  phone: "+94 77 123 4567",
  email: "hello@tuaslfashion.com",
  address: "Colombo, Sri Lanka",
  freeDeliveryMinimum: 10000,
  standardDeliveryFee: 450,
  announcementMessages: [ /* edit messages here */ ]
};
```

## How to Edit Products

Edit `js/products.js`. Each product object:

```js
{
  id: 1,                          // Unique integer
  name: "Product Name",
  slug: "product-name",           // URL-friendly
  department: "Women",            // Women | Men | Girls | Boys | Baby
  category: "Dresses",
  price: 5490,                    // LKR, integer
  originalPrice: 6990,
  discount: 21,                   // Percentage (0 if no discount)
  rating: 4.6,
  reviewCount: 38,
  colours: ["White", "Beige"],
  sizes: ["XS","S","M","L","XL"],
  material: "Linen blend",
  fit: "Relaxed",
  occasion: "Casual",
  image: "URL_TO_IMAGE",
  hoverImage: "URL_TO_HOVER_IMAGE",
  gallery: ["URL1", "URL2"],
  badge: "New",                   // New | Best Seller | Sale | Online Exclusive | Low Stock | null
  stock: 20,
  description: "Product description.",
  tags: ["casual","summer"]
}
```

## How to Replace Media

All images use Pexels CDN URLs with an `onerror` fallback to `assets/images/placeholders/product-placeholder.svg`.

To replace with local images:
1. Place images in `assets/images/products/`
2. Update `image`, `hoverImage` and `gallery` in `products.js` to relative paths like `assets/images/products/my-image.jpg`
3. Update hero images and campaign images in each HTML file

## Wishlist & Cart Storage

Both use `localStorage`:
- Cart key: `tuasl_cart`
- Wishlist key: `tuasl_wishlist`
- Active promo: `tuasl_promo`
- Last order: `tuasl_last_order`
- Recently viewed: `tuasl_recently_viewed`

Data persists in the user's browser only.

## Demo Promo Codes

- `TUASL10` — 10% discount on order subtotal
- `FREESHIP` — Removes delivery fee

## Prototype Limitations

- **No backend** — all data is static JavaScript arrays
- **No database** — products cannot be added/removed from a CMS
- **No real authentication** — sign-in links are placeholders
- **No real payment processing** — checkout is for demonstration only
- **No live stock synchronisation** — stock numbers are static
- **Orders exist only in the current browser session** via localStorage
- **Remote media may need replacement** before production — Pexels URLs are for prototype use; verify licence requirements for production
