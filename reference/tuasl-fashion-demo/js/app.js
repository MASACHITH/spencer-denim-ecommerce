/* =====================================================
   TU@SL Fashion — app.js (shared)
   ===================================================== */

/* ── Per-template theme injection ────────────────────────
   TWO-PHASE approach to avoid FOUC:
   1. Inject CSS variables INLINE immediately (synchronous)
      so buildHeader/buildFooter always sees correct colours.
   2. Load full theme CSS file (async) for detailed overrides.
──────────────────────────────────────────────────────── */
(function(){
  var tmpl = localStorage.getItem('tuasl_tmpl') || 'classic';
  document.documentElement.classList.add('tmpl-' + tmpl);
  if(tmpl === 'classic') return;

  /* Phase 1 — inline <style> applied instantly */
  var VARS = {
    urban:
      ':root{--primary:#e63946;--primary-dark:#c62333;--accent:#ff6b6b;--background:#080808;--surface:#111111;--text:#f0f0f0;--muted:#999;--border:#2a2a2a;--sale:#ff4444}'+
      'body{background:#080808!important;color:#f0f0f0!important}'+
      '.announcement-bar{background:#0a0a0a!important}'+
      '.site-header{background:#111111!important;border-bottom:2px solid #e63946!important}',
    nolimit:
      ':root{--primary:#8b1e3f;--primary-dark:#5f1029;--background:#f9f5f6;--surface:#fff;--text:#1a0a10;--muted:#7a5a65;--border:#e8d5da;--sale:#c62828}'+
      'body{background:#f9f5f6!important;color:#1a0a10!important}'+
      '.announcement-bar{background:#1a0a10!important}'+
      '.site-header{border-bottom:3px solid #8b1e3f!important}',
    luxe:
      ':root{--primary:#c9a84c;--primary-dark:#9a7d38;--accent:#e8d5a3;--background:#0a0a0a;--surface:#141414;--text:#f5f0e8;--muted:#9a8a70;--border:#2a2520;--sale:#e63946}'+
      'body{background:#0a0a0a!important;color:#f5f0e8!important}'+
      '.announcement-bar{background:#050505!important}'+
      '.site-header{background:#0d0d0d!important;border-bottom:2px solid #c9a84c!important}',
    minimal:
      ':root{--primary:#e63c26;--primary-dark:#c0321e;--background:#fff;--surface:#fff;--text:#111;--muted:#666;--border:#e5e5e5;--sale:#e63c26}'+
      'body{background:#fff!important;color:#111!important}'+
      '.announcement-bar{background:#111!important}',
    linen:
      ':root{--primary:#7c5533;--primary-dark:#5a3d24;--accent:#c4a882;--background:#f4f1ec;--surface:#fff;--text:#2a1f13;--muted:#7c6952;--border:#ddd3c5;--sale:#9b2226}'+
      'body{background:#f4f1ec!important;color:#2a1f13!important}'+
      '.announcement-bar{background:#2a1f13!important}'+
      '.site-header{border-bottom:2px solid #ddd3c5!important}',
    denim:
      ':root{--primary:#2e6fad;--primary-dark:#192d47;--accent:#a8cfe8;--background:#eef3f9;--surface:#fff;--text:#101c2e;--muted:#5a6e84;--border:#c8dae8;--sale:#c0392b}'+
      'body{background:#eef3f9!important;color:#101c2e!important}'+
      '.announcement-bar{background:#192d47!important}'+
      '.site-header{border-bottom:2px solid #c8dae8!important}',
    street:
      ':root{--primary:#E8002D;--primary-dark:#c0001f;--accent:#ff4d6b;--background:#0a0a0a;--surface:#111111;--text:#ffffff;--muted:#888;--border:rgba(255,255,255,0.1);--sale:#E8002D}'+
      'body{background:#0a0a0a!important;color:#fff!important}'+
      '.announcement-bar{background:#E8002D!important}'+
      '.site-header{background:rgba(10,10,10,.96)!important;border-bottom:1px solid rgba(255,255,255,.08)!important}',
    edit:
      ':root{--primary:#111111;--primary-dark:#000000;--accent:#c4a882;--background:#ffffff;--surface:#ffffff;--text:#111111;--muted:#888888;--border:#e0dbd4;--sale:#c62828}'+
      'body{background:#fff!important;color:#111!important}'+
      '.announcement-bar{background:#111!important}'+
      '.site-header{background:rgba(255,255,255,.97)!important;border-bottom:1px solid #e0dbd4!important}'
  };
  if(VARS[tmpl]){
    var st=document.createElement('style'); st.textContent=VARS[tmpl];
    document.head.appendChild(st);
  }

  /* Phase 2 — full theme CSS (async, detailed overrides) */
  var CSS_MAP={urban:'theme-urban.css',nolimit:'theme-hub.css',luxe:'theme-luxe.css',minimal:'theme-minimal.css',linen:'theme-linen.css',denim:'theme-denim.css',street:'theme-street.css',edit:'theme-edit.css'};
  if(CSS_MAP[tmpl]){
    var lk=document.createElement('link'); lk.rel='stylesheet'; lk.href='css/'+CSS_MAP[tmpl];
    document.head.appendChild(lk);
  }

  /* Extra fonts (async) */
  var FONTS={denim:'https://fonts.googleapis.com/css2?family=Oswald:wght@500;600;700&display=swap',luxe:'https://fonts.googleapis.com/css2?family=Cormorant+Garamond:wght@600;700&display=swap',linen:'https://fonts.googleapis.com/css2?family=Cormorant+Garamond:wght@600;700&display=swap',street:'https://fonts.googleapis.com/css2?family=Oswald:wght@500;600;700&display=swap',edit:'https://fonts.googleapis.com/css2?family=Cormorant+Garamond:ital,wght@0,400;0,600;0,700;1,400;1,600&display=swap'};
  if(FONTS[tmpl]){var fo=document.createElement('link');fo.rel='stylesheet';fo.href=FONTS[tmpl];document.head.appendChild(fo);}

  document.documentElement.setAttribute('data-theme',tmpl);
})();

/* ── Colombo Wardrobe product photos (rich editorial, used in denim theme) ── */
const CW_IMGS = [
  'https://images.pexels.com/photos/774909/pexels-photo-774909.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/2219257/pexels-photo-2219257.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/2760783/pexels-photo-2760783.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/1721558/pexels-photo-1721558.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/2220320/pexels-photo-2220320.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/2897482/pexels-photo-2897482.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/2955378/pexels-photo-2955378.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/1192609/pexels-photo-1192609.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/3622608/pexels-photo-3622608.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/2529148/pexels-photo-2529148.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/1598507/pexels-photo-1598507.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/1040881/pexels-photo-1040881.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/1148998/pexels-photo-1148998.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/3662849/pexels-photo-3662849.jpeg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
  'https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=400&h=533&fit=crop',
];

/* ---------- Format currency ---------- */
function formatPrice(amount) {
  return `${siteConfig.currencySymbol} ${amount.toLocaleString('en-LK')}`;
}

/* ---------- Render Stars ---------- */
function renderStars(rating) {
  const full = Math.floor(rating);
  const half = rating % 1 >= 0.5 ? 1 : 0;
  let s = '';
  for (let i = 0; i < full; i++) s += '★';
  if (half) s += '½';
  for (let i = full + half; i < 5; i++) s += '☆';
  return s;
}

/* ---------- Toast ---------- */
function showToast(msg, type = 'default') {
  let container = document.getElementById('toast-container');
  if (!container) {
    container = document.createElement('div');
    container.id = 'toast-container';
    container.className = 'toast-container';
    document.body.appendChild(container);
  }
  const toast = document.createElement('div');
  toast.className = `toast ${type}`;
  toast.innerHTML = `<svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg> ${msg}`;
  container.appendChild(toast);
  setTimeout(() => toast.remove(), 3500);
}

/* ---------- Colour map ---------- */
const colourMap = {
  'white': '#ffffff', 'black': '#111111', 'navy': '#1e3a5f', 'blue': '#2563eb',
  'red': '#dc2626', 'pink': '#ec4899', 'green': '#16a34a', 'olive': '#6b7280',
  'grey': '#9ca3af', 'gray': '#9ca3af', 'beige': '#d4b896', 'cream': '#f5f0e8',
  'yellow': '#f59e0b', 'orange': '#f97316', 'purple': '#9333ea', 'brown': '#92400e',
  'camel': '#c19a6b', 'khaki': '#b5a642', 'sage': '#87a68a', 'teal': '#0d9488',
  'coral': '#f08080', 'mint': '#98f8d8', 'champagne': '#f7e7ce', 'sand': '#c8b89a',
  'rust': '#b7410e', 'cobalt': '#1e40af', 'lilac': '#c3b1e1', 'lavender': '#e6e6fa',
  'mauve': '#e0b0b0', 'ivory': '#fffff0', 'charcoal': '#374151', 'gold': '#d4af37',
  'tan': '#d2b48c', 'nude': '#e8c9a0', 'dusty': '#c4a5a5', 'rose': '#f43f5e',
  'sky': '#38bdf8', 'light': '#e5e7eb', 'dark': '#374151', 'forest': '#166534',
  'burgundy': '#800020', 'terracotta': '#c0603a', 'peach': '#ffcba4'
};

function getColourHex(colourName) {
  const lower = colourName.toLowerCase();
  for (const [key, hex] of Object.entries(colourMap)) {
    if (lower.includes(key)) return hex;
  }
  return '#e5e5e5';
}

/* ---------- Product Card ---------- */
/* ---------- Street Raw — Levi's-style Card ---------- */
function renderStreetCard(product) {
  const imgSrc   = CW_IMGS[product.id % CW_IMGS.length];
  const hoverSrc = CW_IMGS[(product.id * 3 + 1) % CW_IMGS.length];
  const hasDiscount = product.discount > 0;
  const stars = Math.round(product.rating || 4);
  const starHTML = '★'.repeat(stars) + '☆'.repeat(5 - stars);
  const badgeLabel = product.badge === 'Sale' ? 'SALE' : product.badge === 'New' ? 'NEW DROP' : product.badge === 'Low Stock' ? 'LOW STOCK' : product.badge === 'Online Exclusive' ? 'EXCLUSIVE' : null;
  const swatches = (product.colours || []).slice(0, 4).map(c =>
    `<span class="sr-swatch" style="background:${getColourHex(c)}" title="${c}"></span>`
  ).join('');

  return `
    <article class="sr-card reveal" data-id="${product.id}">
      <a href="product.html?id=${product.id}" class="sr-img-wrap" aria-label="${product.name}">
        <img class="sr-img" src="${imgSrc}" alt="${product.name}" width="400" height="533" loading="lazy" onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        <img class="sr-img-hover" src="${hoverSrc}" alt="${product.name} – alternate view" width="400" height="533" loading="lazy" onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        ${badgeLabel ? `<span class="sr-badge">${badgeLabel}</span>` : ''}
        <button class="sr-wish ${isInWishlist(product.id) ? 'active' : ''}" aria-label="Wishlist" data-id="${product.id}" onclick="toggleWishlistFromCard(event, ${product.id})">
          <svg width="14" height="14" fill="${isInWishlist(product.id) ? 'currentColor' : 'none'}" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>
        </button>
        <div class="sr-quick-add" onclick="addToCartFromCard(event, ${product.id})">
          <span>+ ADD TO BAG</span>
        </div>
      </a>
      <div class="sr-swatches">${swatches}</div>
      <div class="sr-info">
        <p class="sr-cat">${(product.category || '').toUpperCase()}</p>
        <h3 class="sr-name"><a href="product.html?id=${product.id}">${product.name}</a></h3>
        <div class="sr-rating"><span class="sr-stars">${starHTML}</span> <span class="sr-rcount">(${product.reviewCount || 0})</span></div>
        <div class="sr-price">
          <span class="sr-now">${formatPrice(product.price)}</span>
          ${hasDiscount ? `<span class="sr-was">${formatPrice(product.originalPrice)}</span>` : ''}
        </div>
      </div>
    </article>`;
}

function renderProductCard(product) {
  const tmpl = localStorage.getItem('tuasl_tmpl') || 'classic';
  if (tmpl === 'street') return renderStreetCard(product);
  /* Use curated editorial photos for themes that need them */
  const useEditorial = (tmpl === 'denim' || tmpl === 'urban' || tmpl === 'luxe' || tmpl === 'street');
  const imgSrc   = useEditorial ? CW_IMGS[product.id % CW_IMGS.length]           : product.image;
  const hoverSrc = useEditorial ? CW_IMGS[(product.id * 3 + 1) % CW_IMGS.length] : (product.hoverImage || product.image);

  const hasDiscount = product.discount > 0;
  const badgeClass = product.badge === 'Sale' ? 'sale' : product.badge === 'New' ? 'new' : product.badge === 'Online Exclusive' ? 'online' : product.badge === 'Low Stock' ? 'low' : '';
  const colourDots = (product.colours || []).slice(0, 4).map(c =>
    `<span class="colour-dot" style="background:${getColourHex(c)}" title="${c}"></span>`
  ).join('');

  return `
    <article class="product-card reveal" data-id="${product.id}">
      <a href="product.html?id=${product.id}" class="product-card__image-wrap" aria-label="${product.name}">
        <img class="product-card__img"
          src="${imgSrc}"
          alt="${product.name}"
          width="400" height="533"
          loading="lazy"
          onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        <img class="product-card__img-hover"
          src="${hoverSrc}"
          alt="${product.name} – alternate view"
          width="400" height="533"
          loading="lazy"
          onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        ${product.badge ? `<span class="product-card__badge ${badgeClass}">${product.badge}</span>` : ''}
        <button class="product-card__wishlist ${isInWishlist(product.id) ? 'active' : ''}"
          aria-label="${isInWishlist(product.id) ? 'Remove from wishlist' : 'Add to wishlist'}"
          data-id="${product.id}"
          onclick="toggleWishlistFromCard(event, ${product.id})">
          <svg width="16" height="16" fill="${isInWishlist(product.id) ? 'currentColor' : 'none'}" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24">
            <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/>
          </svg>
        </button>
      </a>
      <div class="product-card__info">
        <p class="product-card__category">${product.category}</p>
        <h3 class="product-card__name">
          <a href="product.html?id=${product.id}">${product.name}</a>
        </h3>
        <div class="product-card__price">
          <span class="product-card__price-now">${formatPrice(product.price)}</span>
          ${hasDiscount ? `<span class="product-card__price-was">${formatPrice(product.originalPrice)}</span>
          <span class="product-card__discount">-${product.discount}%</span>` : ''}
        </div>
        <div class="product-card__rating">
          <span class="product-card__stars">${renderStars(product.rating)}</span>
          <span>(${product.reviewCount})</span>
        </div>
        <div class="product-card__colours">${colourDots}</div>
      </div>
      <div class="product-card__actions">
        <button class="btn btn-sm btn-outline-dark" style="flex:1" onclick="openQuickView(${product.id})">Quick View</button>
        <button class="btn btn-sm btn-primary" style="flex:1" onclick="addToCartFromCard(event, ${product.id})">Add to Bag</button>
      </div>
    </article>`;
}

/* ---------- Add to Cart from Card ---------- */
function addToCartFromCard(e, id) {
  e.preventDefault();
  const product = getProductById(id);
  if (!product) return;
  const item = {
    id: product.id,
    name: product.name,
    price: product.price,
    image: product.image,
    size: product.sizes[0] || 'One Size',
    colour: product.colours[0] || 'Default',
    qty: 1
  };
  cartManager.addItem(item);
  showToast(`${product.name} added to bag!`, 'success');
  updateHeaderCounts();
  const badge = document.querySelector('.bag-count');
  if (badge) badge.classList.add('bag-bounce');
  setTimeout(() => badge && badge.classList.remove('bag-bounce'), 500);
}

/* ---------- Wishlist from Card ---------- */
function toggleWishlistFromCard(e, id) {
  e.preventDefault();
  e.stopPropagation();
  const product = getProductById(id);
  if (!product) return;
  if (isInWishlist(id)) {
    wishlistManager.removeItem(id);
    showToast(`Removed from wishlist.`);
  } else {
    wishlistManager.addItem(product);
    showToast(`Added to wishlist!`, 'success');
  }
  updateHeaderCounts();
  // Update the button state
  const btn = e.currentTarget;
  if (btn) {
    btn.classList.toggle('active');
    btn.innerHTML = isInWishlist(id)
      ? `<svg width="16" height="16" fill="currentColor" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>`
      : `<svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>`;
    btn.classList.add('heart-pop');
    setTimeout(() => btn.classList.remove('heart-pop'), 400);
  }
}

/* ---------- Quick View ---------- */
function openQuickView(id) {
  window.location.href = `product.html?id=${id}`;
}

/* ---------- Update Header Counts ---------- */
function updateHeaderCounts() {
  const cart = cartManager.getCart();
  const wishlist = wishlistManager.getWishlist();
  const cartCount = cart.reduce((s, i) => s + i.qty, 0);
  const wCount = wishlist.length;
  document.querySelectorAll('.bag-count').forEach(el => el.textContent = cartCount || '');
  document.querySelectorAll('.wishlist-count').forEach(el => el.textContent = wCount || '');
}

/* ---------- Header HTML ---------- */
function buildHeader() {
  const tmpl = localStorage.getItem('tuasl_tmpl') || 'classic';

  /* Logo config per template */
  const LOGO_CFG = {
    classic: { href:'index.html',       label:`${siteConfig.storeName} — Home`,
      html:`${siteConfig.storeName}<span>${siteConfig.tagline}</span>` },
    urban:   { href:'index-urban.html', label:'TU@SL Urban Dark — Home',
      html:`<span style="font-weight:900;letter-spacing:.08em;color:#f0f0f0">TU<span style="color:#e63946">@</span>SL</span><span style="font-size:.52rem;letter-spacing:.18em;color:#999;display:block;margin-top:1px;text-transform:uppercase">Urban Dark</span>` },
    nolimit: { href:'index-v3.html',    label:'Fashion Hub — Home',
      html:`<span style="font-weight:900;letter-spacing:.06em">TU<span style="color:#8b1e3f">@</span>SL</span><span style="font-size:.52rem;letter-spacing:.16em;color:#7a5a65;display:block;margin-top:1px;text-transform:uppercase">Fashion Hub</span>` },
    luxe:    { href:'index-v4.html',    label:'TU@SL Luxe Gold — Home',
      html:`<span style="font-family:'Cormorant Garamond','Playfair Display',serif;font-size:1.45rem;font-weight:700;letter-spacing:.1em;color:#f5f0e8;line-height:1.1">TU<span style="color:#c9a84c">@</span>SL</span><span style="font-size:.5rem;letter-spacing:.22em;display:block;color:#9a8a70;font-weight:700;text-transform:uppercase;margin-top:2px">Luxe Gold</span>` },
    minimal: { href:'index-v5.html',    label:'TU@SL Fresh Minimal — Home',
      html:`<span style="font-weight:900;letter-spacing:.02em;color:#111">TU<span style="color:#e63c26">@</span>SL</span><span style="font-size:.5rem;letter-spacing:.18em;display:block;color:#666;font-weight:700;text-transform:uppercase;margin-top:1px">Fresh Minimal</span>` },
    linen:   { href:'index-v6.html',    label:'TU@SL Natural Linen — Home',
      html:`<span style="font-family:'Cormorant Garamond','Playfair Display',serif;font-size:1.5rem;font-weight:700;letter-spacing:.06em;color:#2a1f13;line-height:1.1">TU<span style="color:#7c5533">@</span>SL</span><span style="font-size:.5rem;letter-spacing:.16em;display:block;color:#7c6952;font-weight:700;text-transform:uppercase;margin-top:2px">Natural Linen</span>` },
    denim:   { href:'index-v7.html',    label:'Colombo Wardrobe — Home',
      html:`<span style="font-family:'Oswald',sans-serif;font-size:1.3rem;font-weight:700;letter-spacing:.05em;color:#192d47;line-height:1.1;display:block"><span style="color:#2e6fad">C</span>OLOMBO W.</span><span style="font-size:.5rem;letter-spacing:.13em;display:block;color:#5a6e84;font-weight:700;text-transform:uppercase;margin-top:1px">Wardrobe &amp; Denim Co.</span>` },
    street:  { href:'index-v8.html',    label:'TU@SL Street Raw — Home',
      html:`<span style="font-family:'Oswald',sans-serif;font-size:1.35rem;font-weight:700;letter-spacing:.06em;color:#fff;line-height:1.1;display:block">TU<span style="color:#E8002D">@</span>SL</span><span style="font-size:.5rem;letter-spacing:.2em;display:block;color:rgba(255,255,255,.38);font-weight:700;text-transform:uppercase;margin-top:1px">Street Raw</span>` },
    edit:    { href:'index-v9.html',    label:'TU@SL The Edit — Home',
      html:`<span style="font-family:'Cormorant Garamond','Playfair Display',serif;font-size:1.5rem;font-weight:700;letter-spacing:.08em;color:#111;line-height:1.1;display:block">TU@SL</span><span style="font-size:.5rem;letter-spacing:.2em;display:block;color:#999;font-weight:500;text-transform:uppercase;margin-top:2px;font-family:'Inter',sans-serif">The Edit</span>` }
  };

  const lc = LOGO_CFG[tmpl] || LOGO_CFG.classic;
  const logoHref  = lc.href;
  const logoLabel = lc.label;
  const logoHTML  = lc.html;

  const nav = [
    { label: 'Women', href: 'women.html', mega: 'women' },
    { label: 'Men', href: 'men.html', mega: 'men' },
    { label: 'Kids', href: 'kids.html', mega: 'kids' },
    { label: 'Baby', href: 'baby.html', mega: 'baby' },
    { label: 'Activewear', href: 'category.html?category=activewear' },
    { label: 'Footwear', href: 'category.html?category=footwear' },
    { label: 'Accessories', href: 'category.html?category=accessories' },
    { label: 'New Arrivals', href: 'new-arrivals.html', cls: 'new' },
    { label: 'Sale', href: 'sale.html', cls: 'sale' }
  ];
  const megaMenus = {
    women: buildMegaWomen(),
    men: buildMegaMen(),
    kids: buildMegaKids(),
    baby: buildMegaBaby()
  };
  const navItems = nav.map(item => {
    const hasMega = item.mega && megaMenus[item.mega];
    return `
      <li class="nav-item">
        <a href="${item.href}" class="nav-link ${item.cls || ''}" ${hasMega ? 'aria-haspopup="true" aria-expanded="false"' : ''}>
          ${item.label}
          ${hasMega ? `<svg class="nav-chevron" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m6 9 6 6 6-6"/></svg>` : ''}
        </a>
        ${hasMega ? megaMenus[item.mega] : ''}
      </li>`;
  }).join('');

  return `
    <a href="#main-content" class="skip-link">Skip to main content</a>
    <div class="announcement-bar" id="announcement-bar" aria-live="polite">
      <span class="announcement-bar__msg active" id="announce-msg">${siteConfig.announcementMessages[0]}</span>
    </div>
    <header class="site-header" role="banner">
      <div class="header-main">
        <a href="${logoHref}" class="header-logo" aria-label="${logoLabel}">
          ${logoHTML}
        </a>
        <div class="header-search" role="search">
          <input type="search" placeholder="Search products, brands, categories…" aria-label="Search" id="header-search-input" autocomplete="off">
          <button class="header-search__btn" aria-label="Search">
            <svg width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.35-4.35"/></svg>
          </button>
        </div>
        <nav class="header-actions" aria-label="Account and shopping">
          <a href="index.html" class="header-action-btn" aria-label="Sign in">
            <svg class="icon" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M15.75 6a3.75 3.75 0 1 1-7.5 0 3.75 3.75 0 0 1 7.5 0ZM4.501 20.118a7.5 7.5 0 0 1 14.998 0A17.933 17.933 0 0 1 12 21.75c-2.676 0-5.216-.584-7.499-1.632Z"/></svg>
            <span class="label">Sign In</span>
          </a>
          <a href="wishlist.html" class="header-action-btn" aria-label="Wishlist">
            <svg class="icon" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M21 8.25c0-2.485-2.099-4.5-4.688-4.5-1.935 0-3.597 1.126-4.312 2.733-.715-1.607-2.377-2.733-4.313-2.733C5.1 3.75 3 5.765 3 8.25c0 7.22 9 12 9 12s9-4.78 9-12Z"/></svg>
            <span class="label">Wishlist</span>
            <span class="count-badge wishlist-count" aria-label="Wishlist items"></span>
          </a>
          <a href="cart.html" class="header-action-btn" aria-label="Shopping bag">
            <svg class="icon" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M15.75 10.5V6a3.75 3.75 0 1 0-7.5 0v4.5m11.356-1.993 1.263 12c.07.665-.45 1.243-1.119 1.243H4.25a1.125 1.125 0 0 1-1.12-1.243l1.264-12A1.125 1.125 0 0 1 5.513 7.5h12.974c.576 0 1.059.435 1.119 1.007Z"/></svg>
            <span class="label">Bag</span>
            <span class="count-badge bag-count" aria-label="Bag items"></span>
          </a>
          <button class="mobile-menu-btn header-action-btn" aria-label="Open menu" id="mobile-menu-btn">
            <svg class="icon" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M3.75 6.75h16.5M3.75 12h16.5m-16.5 5.25h16.5"/></svg>
          </button>
        </nav>
      </div>
      <nav class="site-nav" aria-label="Main navigation">
        <ul class="nav-inner">${navItems}</ul>
      </nav>
    </header>
    ${buildMobileNav()}`;
}

function buildMegaWomen() {
  return `<div class="mega-menu" role="region" aria-label="Women's categories">
    <div class="mega-col">
      <p class="mega-col-title">Clothing</p>
      <div class="mega-links">
        <a href="category.html?department=women&category=new-arrivals">New Arrivals</a>
        <a href="category.html?department=women&category=dresses">Dresses</a>
        <a href="category.html?department=women&category=tops">Tops &amp; T-Shirts</a>
        <a href="category.html?department=women&category=shirts">Shirts &amp; Blouses</a>
        <a href="category.html?department=women&category=jumpsuits">Jumpsuits</a>
        <a href="category.html?department=women&category=jeans">Jeans</a>
        <a href="category.html?department=women&category=trousers">Trousers</a>
        <a href="category.html?department=women&category=leggings">Leggings</a>
        <a href="category.html?department=women&category=shorts">Shorts</a>
        <a href="category.html?department=women&category=skirts">Skirts</a>
        <a href="category.html?department=women&category=coords">Co-ords</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">Occasion &amp; Style</p>
      <div class="mega-links">
        <a href="category.html?department=women&category=jackets">Jackets</a>
        <a href="category.html?department=women&category=knitwear">Knitwear</a>
        <a href="category.html?department=women&category=activewear">Activewear</a>
        <a href="category.html?department=women&category=sleepwear">Sleepwear</a>
        <a href="category.html?department=women&category=loungewear">Loungewear</a>
        <a href="category.html?department=women&category=innerwear">Innerwear</a>
        <a href="category.html?department=women&category=occasionwear">Occasionwear</a>
        <a href="category.html?department=women&category=traditional">Traditional Wear</a>
        <a href="category.html?department=women&category=plus-size">Plus Size</a>
        <a href="category.html?department=women&category=maternity">Maternity</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">Accessories</p>
      <div class="mega-links">
        <a href="category.html?department=women&category=footwear">Footwear</a>
        <a href="category.html?department=women&category=bags">Bags</a>
        <a href="category.html?department=women&category=jewellery">Jewellery</a>
        <a href="category.html?department=women&category=accessories">Accessories</a>
      </div>
    </div>
    <div class="mega-promo">
      <a href="women.html">
        <img src="https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=300&h=400&fit=crop"
          alt="Women's new collection" width="180" height="240" loading="lazy"
          onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        <p class="mega-promo-label">Shop Women</p>
      </a>
    </div>
  </div>`;
}

function buildMegaMen() {
  return `<div class="mega-menu" role="region" aria-label="Men's categories">
    <div class="mega-col">
      <p class="mega-col-title">Tops</p>
      <div class="mega-links">
        <a href="category.html?department=men&category=new-arrivals">New Arrivals</a>
        <a href="category.html?department=men&category=tshirts">T-Shirts</a>
        <a href="category.html?department=men&category=polo">Polo Shirts</a>
        <a href="category.html?department=men&category=casual-shirts">Casual Shirts</a>
        <a href="category.html?department=men&category=formal-shirts">Formal Shirts</a>
        <a href="category.html?department=men&category=hoodies">Hoodies</a>
        <a href="category.html?department=men&category=jackets">Jackets</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">Bottoms &amp; Lounge</p>
      <div class="mega-links">
        <a href="category.html?department=men&category=jeans">Jeans</a>
        <a href="category.html?department=men&category=trousers">Trousers</a>
        <a href="category.html?department=men&category=shorts">Shorts</a>
        <a href="category.html?department=men&category=joggers">Joggers</a>
        <a href="category.html?department=men&category=activewear">Activewear</a>
        <a href="category.html?department=men&category=loungewear">Loungewear</a>
        <a href="category.html?department=men&category=innerwear">Innerwear</a>
        <a href="category.html?department=men&category=traditional">Traditional Wear</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">Accessories</p>
      <div class="mega-links">
        <a href="category.html?department=men&category=footwear">Footwear</a>
        <a href="category.html?department=men&category=bags">Bags</a>
        <a href="category.html?department=men&category=belts">Belts</a>
        <a href="category.html?department=men&category=wallets">Wallets</a>
        <a href="category.html?department=men&category=accessories">Accessories</a>
      </div>
    </div>
    <div class="mega-promo">
      <a href="men.html">
        <img src="https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=300&h=400&fit=crop"
          alt="Men's new collection" width="180" height="240" loading="lazy"
          onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        <p class="mega-promo-label">Shop Men</p>
      </a>
    </div>
  </div>`;
}

function buildMegaKids() {
  return `<div class="mega-menu" role="region" aria-label="Kids' categories">
    <div class="mega-col">
      <p class="mega-col-title">Girls</p>
      <div class="mega-links">
        <a href="category.html?department=girls&category=dresses">Dresses</a>
        <a href="category.html?department=girls&category=tops">Tops &amp; T-Shirts</a>
        <a href="category.html?department=girls&category=leggings">Leggings &amp; Jeggings</a>
        <a href="category.html?department=girls&category=skirts">Skirts</a>
        <a href="category.html?department=girls&category=shorts">Shorts</a>
        <a href="category.html?department=girls&category=sets">Clothing Sets</a>
        <a href="category.html?department=girls&category=partywear">Partywear</a>
        <a href="category.html?department=girls&category=school">Schoolwear</a>
        <a href="category.html?department=girls&category=sleepwear">Sleepwear</a>
        <a href="category.html?department=girls&category=footwear">Footwear</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">Boys</p>
      <div class="mega-links">
        <a href="category.html?department=boys&category=tshirts">T-Shirts</a>
        <a href="category.html?department=boys&category=shirts">Shirts &amp; Polo</a>
        <a href="category.html?department=boys&category=jeans">Jeans</a>
        <a href="category.html?department=boys&category=trousers">Trousers</a>
        <a href="category.html?department=boys&category=shorts">Shorts</a>
        <a href="category.html?department=boys&category=sets">Clothing Sets</a>
        <a href="category.html?department=boys&category=school">Schoolwear</a>
        <a href="category.html?department=boys&category=sleepwear">Sleepwear</a>
        <a href="category.html?department=boys&category=footwear">Footwear</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">By Age</p>
      <div class="mega-links">
        <a href="category.html?agegroup=0-2">0–2 Years</a>
        <a href="category.html?agegroup=2-4">2–4 Years</a>
        <a href="category.html?agegroup=4-6">4–6 Years</a>
        <a href="category.html?agegroup=6-10">6–10 Years</a>
        <a href="category.html?agegroup=10-14">10–14 Years</a>
        <a href="category.html?agegroup=14-16">14–16 Years</a>
      </div>
    </div>
    <div class="mega-promo">
      <a href="kids.html">
        <img src="https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=300&h=400&fit=crop"
          alt="Kids' new collection" width="180" height="240" loading="lazy"
          onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        <p class="mega-promo-label">Shop Kids</p>
      </a>
    </div>
  </div>`;
}

function buildMegaBaby() {
  return `<div class="mega-menu" role="region" aria-label="Baby categories">
    <div class="mega-col">
      <p class="mega-col-title">By Gender</p>
      <div class="mega-links">
        <a href="category.html?department=baby&gender=girls">Baby Girls</a>
        <a href="category.html?department=baby&gender=boys">Baby Boys</a>
        <a href="category.html?department=baby">All Baby</a>
      </div>
    </div>
    <div class="mega-col">
      <p class="mega-col-title">Clothing</p>
      <div class="mega-links">
        <a href="category.html?department=baby&category=bodysuits">Bodysuits</a>
        <a href="category.html?department=baby&category=sleepsuits">Sleepsuits</a>
        <a href="category.html?department=baby&category=sets">Clothing Sets</a>
        <a href="category.html?department=baby&category=dresses">Dresses</a>
        <a href="category.html?department=baby&category=tops">Tops</a>
        <a href="category.html?department=baby&category=bottoms">Bottoms</a>
        <a href="category.html?department=baby&category=essentials">Essentials</a>
        <a href="category.html?department=baby&category=footwear">Footwear</a>
        <a href="category.html?department=baby&category=accessories">Accessories</a>
      </div>
    </div>
    <div class="mega-promo" style="width:140px">
      <a href="baby.html">
        <img src="https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=300&h=400&fit=crop"
          alt="Baby collection" width="140" height="186" loading="lazy"
          onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        <p class="mega-promo-label">Shop Baby</p>
      </a>
    </div>
  </div>`;
}

function buildMobileNav() {
  const depts = [
    { label: 'Women', href: 'women.html', sub: ['New Arrivals','Dresses','Tops & T-Shirts','Jeans','Skirts','Jackets','Activewear','Footwear','Bags'] },
    { label: 'Men', href: 'men.html', sub: ['New Arrivals','T-Shirts','Polo Shirts','Casual Shirts','Jeans','Shorts','Hoodies','Activewear','Footwear'] },
    { label: 'Kids', href: 'kids.html', sub: ['Girls Clothing','Boys Clothing','Schoolwear','Partywear','Footwear'] },
    { label: 'Baby', href: 'baby.html', sub: ['Bodysuits','Sleepsuits','Clothing Sets','Essentials','Footwear'] },
    { label: 'Activewear', href: 'category.html?category=activewear' },
    { label: 'Footwear', href: 'category.html?category=footwear' },
    { label: 'Accessories', href: 'category.html?category=accessories' },
    { label: 'New Arrivals', href: 'new-arrivals.html' },
    { label: 'Sale', href: 'sale.html', cls: 'sale' }
  ];
  const links = depts.map((d, i) => {
    if (d.sub) {
      return `<li>
        <button class="mobile-nav-link ${d.cls||''}" aria-expanded="false" aria-controls="mobile-sub-${i}" onclick="toggleMobileAccordion(this, 'mobile-sub-${i}')">
          ${d.label}
          <svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
        </button>
        <div id="mobile-sub-${i}" class="mobile-nav-accordion-content">
          <a href="${d.href}" class="mobile-nav-sub-link" style="font-weight:600;color:var(--primary)">Shop All ${d.label}</a>
          ${d.sub.map(s => `<a href="${d.href}" class="mobile-nav-sub-link">${s}</a>`).join('')}
        </div>
      </li>`;
    }
    return `<li><a href="${d.href}" class="mobile-nav-link ${d.cls||''}">${d.label}</a></li>`;
  }).join('');

  return `
    <div class="mobile-nav-overlay" id="mobile-nav-overlay" role="dialog" aria-label="Navigation menu" aria-modal="true">
      <div class="mobile-nav-drawer">
        <div class="mobile-nav-header">
          <span class="logo">${siteConfig.storeName}</span>
          <button class="mobile-nav-close" aria-label="Close menu" onclick="closeMobileNav()">
            <svg width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
          </button>
        </div>
        <div class="mobile-nav-body">
          <ul>${links}</ul>
          <div style="padding:1.25rem;border-top:1px solid var(--border);display:flex;flex-direction:column;gap:0.5rem;margin-top:0.5rem">
            <a href="wishlist.html" style="font-size:0.875rem;font-weight:600">♡ Wishlist</a>
            <a href="cart.html" style="font-size:0.875rem;font-weight:600">🛍 Shopping Bag</a>
            <a href="contact.html" style="font-size:0.875rem">Contact Us</a>
          </div>
        </div>
      </div>
    </div>`;
}

/* ---------- Footer HTML ---------- */
function buildFooter() {
  return `
    <footer class="site-footer" role="contentinfo">
      <div class="container">
        <div class="footer-main">
          <div class="footer-brand footer-col">
            <p class="footer-logo">${siteConfig.storeName}</p>
            <p class="footer-tagline">${siteConfig.tagline}</p>
            <p class="footer-desc">Sri Lanka's family fashion destination — quality clothing for Women, Men, Kids and Baby at great prices.</p>
            <div class="footer-social">
              <a href="${siteConfig.facebook}" class="footer-social-link" aria-label="Facebook">
                <svg width="16" height="16" fill="currentColor" viewBox="0 0 24 24"><path d="M18 2h-3a5 5 0 0 0-5 5v3H7v4h3v8h4v-8h3l1-4h-4V7a1 1 0 0 1 1-1h3z"/></svg>
              </a>
              <a href="${siteConfig.instagram}" class="footer-social-link" aria-label="Instagram">
                <svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20" rx="5"/><circle cx="12" cy="12" r="5"/><circle cx="17.5" cy="6.5" r="1" fill="currentColor"/></svg>
              </a>
              <a href="${siteConfig.tiktok}" class="footer-social-link" aria-label="TikTok">
                <svg width="16" height="16" fill="currentColor" viewBox="0 0 24 24"><path d="M19.59 6.69a4.83 4.83 0 0 1-3.77-4.25V2h-3.45v13.67a2.89 2.89 0 0 1-2.88 2.5 2.89 2.89 0 0 1-2.89-2.89 2.89 2.89 0 0 1 2.89-2.89c.28 0 .54.04.79.1V9.01a6.27 6.27 0 0 0-.79-.05 6.34 6.34 0 0 0-6.34 6.34 6.34 6.34 0 0 0 6.34 6.34 6.34 6.34 0 0 0 6.33-6.34V8.93a8.13 8.13 0 0 0 4.84 1.56V7.02a4.85 4.85 0 0 1-1.07-.33Z"/></svg>
              </a>
              <a href="${siteConfig.youtube}" class="footer-social-link" aria-label="YouTube">
                <svg width="16" height="16" fill="currentColor" viewBox="0 0 24 24"><path d="M22.54 6.42a2.78 2.78 0 0 0-1.94-1.96C18.88 4 12 4 12 4s-6.88 0-8.6.46A2.78 2.78 0 0 0 1.46 6.42 29 29 0 0 0 1 12a29 29 0 0 0 .46 5.58 2.78 2.78 0 0 0 1.94 1.96C5.12 20 12 20 12 20s6.88 0 8.6-.46a2.78 2.78 0 0 0 1.94-1.96A29 29 0 0 0 23 12a29 29 0 0 0-.46-5.58zM9.75 15.02V8.98L15.5 12l-5.75 3.02z"/></svg>
              </a>
            </div>
          </div>
          <div class="footer-col">
            <h3 class="footer-col-title">Shop</h3>
            <ul class="footer-links">
              <li><a href="women.html">Women</a></li>
              <li><a href="men.html">Men</a></li>
              <li><a href="kids.html">Kids</a></li>
              <li><a href="baby.html">Baby</a></li>
              <li><a href="new-arrivals.html">New Arrivals</a></li>
              <li><a href="sale.html">Sale</a></li>
            </ul>
          </div>
          <div class="footer-col">
            <h3 class="footer-col-title">Help</h3>
            <ul class="footer-links">
              <li><a href="contact.html">Contact Us</a></li>
              <li><a href="about.html">About Us</a></li>
              <li><a href="#">Delivery Info</a></li>
              <li><a href="#">Returns Policy</a></li>
              <li><a href="#">Size Guide</a></li>
              <li><a href="#">FAQ</a></li>
            </ul>
          </div>
          <div class="footer-col">
            <h3 class="footer-col-title">Contact</h3>
            <div class="footer-contact">
              <div class="footer-contact-item">
                <svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0z"/><circle cx="12" cy="10" r="3"/></svg>
                <span>${siteConfig.address}</span>
              </div>
              <div class="footer-contact-item">
                <svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07A19.5 19.5 0 0 1 4.69 12 19.79 19.79 0 0 1 1.61 3.41 2 2 0 0 1 3.6 1.22h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L7.91 8.8a16 16 0 0 0 7.29 7.29l.95-.95a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"/></svg>
                <a href="tel:${siteConfig.phone}">${siteConfig.phone}</a>
              </div>
              <div class="footer-contact-item">
                <svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z"/><polyline points="22,6 12,13 2,6"/></svg>
                <a href="mailto:${siteConfig.email}">${siteConfig.email}</a>
              </div>
              <div class="footer-contact-item">
                <svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>
                <span>Mon–Sat: 9am – 6pm</span>
              </div>
            </div>
          </div>
        </div>
      </div>
      <div class="footer-bottom">
        <div class="container" style="width:100%;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:0.5rem">
          <p>© 2026 ${siteConfig.companyName}. All rights reserved. Prototype — demonstration only.</p>
          <div class="footer-payments">
            <span class="payment-badge">Cash on Delivery</span>
            <span class="payment-badge">Card</span>
            <span class="payment-badge">Bank Transfer</span>
          </div>
        </div>
      </div>
    </footer>
    <a href="https://wa.me/${siteConfig.whatsapp}" class="whatsapp-fab" aria-label="Chat on WhatsApp" target="_blank" rel="noopener">
      <svg width="24" height="24" fill="currentColor" viewBox="0 0 24 24"><path d="M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 0 1-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 0 1-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 0 1 2.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0 0 12.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 0 0 5.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 0 0-3.48-8.413Z"/></svg>
    </a>`;
}

/* ---------- Inject header/footer ---------- */
function initPage() {
  const headerEl = document.getElementById('header-placeholder');
  const footerEl = document.getElementById('footer-placeholder');
  if (headerEl) headerEl.innerHTML = buildHeader();
  if (footerEl) footerEl.innerHTML = buildFooter();

  // Announcement bar rotation
  initAnnouncement();
  // Mobile menu
  initMobileMenu();
  // Header search
  initHeaderSearch();
  // Scroll reveal
  initScrollReveal();
  // Update counts
  updateHeaderCounts();
}

/* ---------- Announcement Bar ---------- */
function initAnnouncement() {
  const msgs = siteConfig.announcementMessages;
  if (!msgs || msgs.length < 2) return;
  let idx = 0;
  const el = document.getElementById('announce-msg');
  if (!el) return;
  setInterval(() => {
    idx = (idx + 1) % msgs.length;
    el.style.animation = 'none';
    el.offsetHeight; // reflow
    el.style.animation = '';
    el.classList.add('announce-active');
    el.textContent = msgs[idx];
    setTimeout(() => el.classList.remove('announce-active'), 4000);
  }, 4500);
}

/* ---------- Mobile Menu ---------- */
function initMobileMenu() {
  const btn = document.getElementById('mobile-menu-btn');
  if (btn) btn.addEventListener('click', openMobileNav);
  const overlay = document.getElementById('mobile-nav-overlay');
  if (overlay) overlay.addEventListener('click', e => {
    if (e.target === overlay) closeMobileNav();
  });
}
function openMobileNav() {
  const overlay = document.getElementById('mobile-nav-overlay');
  if (overlay) { overlay.classList.add('open'); document.body.style.overflow = 'hidden'; }
}
function closeMobileNav() {
  const overlay = document.getElementById('mobile-nav-overlay');
  if (overlay) { overlay.classList.remove('open'); document.body.style.overflow = ''; }
}
function toggleMobileAccordion(btn, targetId) {
  const target = document.getElementById(targetId);
  if (!target) return;
  const isOpen = target.classList.contains('open');
  // Close all
  document.querySelectorAll('.mobile-nav-accordion-content').forEach(el => el.classList.remove('open'));
  document.querySelectorAll('.mobile-nav-link[aria-expanded]').forEach(el => el.setAttribute('aria-expanded','false'));
  if (!isOpen) {
    target.classList.add('open');
    btn.setAttribute('aria-expanded', 'true');
  }
}

/* ---------- Header Search ---------- */
function initHeaderSearch() {
  const input = document.getElementById('header-search-input');
  if (!input) return;
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
      const q = input.value.trim();
      if (q) window.location.href = `search.html?q=${encodeURIComponent(q)}`;
    }
  });
}

/* ---------- Scroll Reveal ---------- */
function initScrollReveal() {
  const observer = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (entry.isIntersecting) {
        entry.target.classList.add('visible');
        observer.unobserve(entry.target);
      }
    });
  }, { threshold: 0.1, rootMargin: '0px 0px -40px 0px' });
  document.querySelectorAll('.reveal, .reveal-left').forEach(el => observer.observe(el));
}

/* ---------- Filter Group Toggle ---------- */
function toggleFilterGroup(header) {
  const body = header.nextElementSibling;
  if (body) body.classList.toggle('open');
  const arrow = header.querySelector('svg');
  if (arrow) arrow.style.transform = body.classList.contains('open') ? 'rotate(180deg)' : '';
}

/* ---------- Filter Drawer (Mobile) ---------- */
function openFilterDrawer() {
  const overlay = document.getElementById('filter-overlay');
  if (overlay) { overlay.classList.add('open'); document.body.style.overflow = 'hidden'; }
}
function closeFilterDrawer() {
  const overlay = document.getElementById('filter-overlay');
  if (overlay) { overlay.classList.remove('open'); document.body.style.overflow = ''; }
}

/* ---------- Image Modal ---------- */
function openImageModal(src) {
  let modal = document.getElementById('image-modal');
  if (!modal) {
    modal = document.createElement('div');
    modal.id = 'image-modal';
    modal.className = 'image-modal';
    modal.innerHTML = `<button class="image-modal-close" aria-label="Close image" onclick="closeImageModal()">✕</button><img id="modal-img" src="" alt="Product image enlarged">`;
    modal.addEventListener('click', e => { if (e.target === modal) closeImageModal(); });
    document.body.appendChild(modal);
  }
  document.getElementById('modal-img').src = src;
  modal.classList.add('open');
  document.body.style.overflow = 'hidden';
}
function closeImageModal() {
  const modal = document.getElementById('image-modal');
  if (modal) { modal.classList.remove('open'); document.body.style.overflow = ''; }
}

document.addEventListener('DOMContentLoaded', initPage);

/* =====================================================
   TEMPLATE SWITCHER — floating widget on all pages
   ===================================================== */
(function initTemplateSwitcher() {

  const TEMPLATES = [
    { key:'classic', label:'Classic',          sub:'TU@SL Original',       href:'index.html',        colors:['#8b1e3f','#f8f6f3','#e8c3b5'], photo:'1536619', dark:false },
    { key:'urban',   label:'Urban Dark',       sub:'TU@SL Night',          href:'index-urban.html',  colors:['#e63946','#080808','#2a2a2a'], photo:'1040881', dark:true  },
    { key:'nolimit', label:'Fashion Hub',      sub:'No Limit Edition',     href:'index-v3.html',     colors:['#8b1e3f','#f9f5f6','#e8d5da'], photo:'2219257', dark:false },
    { key:'luxe',    label:'Luxe Gold',        sub:'Premium Collection',   href:'index-v4.html',     colors:['#c9a84c','#0a0a0a','#141414'], photo:'3622608', dark:true  },
    { key:'minimal', label:'Fresh Minimal',    sub:'Clean & Bold',         href:'index-v5.html',     colors:['#e63c26','#ffffff','#111111'], photo:'2760783', dark:false },
    { key:'linen',   label:'Natural Linen',    sub:'Warm & Earthy',        href:'index-v6.html',     colors:['#7c5533','#f4f1ec','#c4a882'], photo:'774909',  dark:false },
    { key:'denim',   label:'Colombo Wardrobe', sub:'Wardrobe & Denim Co.', href:'index-v7.html',     colors:['#2e6fad','#eef3f9','#192d47'], photo:'2897482', dark:false },
    { key:'street',  label:'Street Raw',       sub:'Built for the Streets',href:'index-v8.html',     colors:['#E8002D','#0a0a0a','#141414'], photo:'2955378', dark:true  },
    { key:'edit',    label:'The Edit',         sub:'Curated & Refined',    href:'index-v9.html',     colors:['#111111','#f7f5f2','#c4a882'], photo:'1721558', dark:false },
  ];

  const current = localStorage.getItem('tuasl_tmpl') || 'classic';

  /* ── Inject styles ── */
  const style = document.createElement('style');
  style.textContent = `
    #ts-btn {
      position: fixed; bottom: 24px; right: 24px; z-index: 9999;
      width: 52px; height: 52px; border-radius: 50%;
      background: #111; color: #fff; border: none; cursor: pointer;
      box-shadow: 0 4px 20px rgba(0,0,0,.35);
      display: flex; align-items: center; justify-content: center;
      transition: transform .2s, box-shadow .2s;
    }
    #ts-btn:hover { transform: scale(1.08); box-shadow: 0 6px 28px rgba(0,0,0,.45); }
    #ts-btn.active { background: #E8002D; }

    #ts-panel {
      position: fixed; bottom: 88px; right: 24px; z-index: 9998;
      width: 370px; max-height: 82vh;
      background: #fff; border-radius: 16px;
      box-shadow: 0 12px 48px rgba(0,0,0,.22), 0 2px 8px rgba(0,0,0,.1);
      display: none; flex-direction: column;
      overflow: hidden; font-family: 'Inter', sans-serif;
    }
    #ts-panel.open { display: flex; }

    .ts-head {
      padding: 1rem 1.25rem .875rem;
      border-bottom: 1px solid #f0ece6;
      display: flex; align-items: center; justify-content: space-between;
      flex-shrink: 0;
    }
    .ts-head-title { font-size: .88rem; font-weight: 700; color: #111; }
    .ts-head-sub   { font-size: .7rem; color: #999; margin-top: 1px; }
    .ts-close {
      width: 28px; height: 28px; border-radius: 50%;
      background: #f5f5f5; border: none; cursor: pointer;
      display: flex; align-items: center; justify-content: center;
      color: #666; font-size: 1rem; line-height: 1;
      transition: background .18s;
    }
    .ts-close:hover { background: #eee; color: #111; }

    .ts-grid {
      display: grid; grid-template-columns: repeat(3, 1fr);
      gap: .6rem; padding: .875rem; overflow-y: auto;
      flex: 1; min-height: 0;
    }
    .ts-card {
      border-radius: 10px; overflow: hidden; cursor: pointer;
      border: 2px solid transparent; transition: border-color .2s, transform .18s;
      background: #fafafa;
    }
    .ts-card:hover { transform: translateY(-2px); border-color: #ddd; }
    .ts-card.ts-active { border-color: #111 !important; }
    .ts-card.ts-active.ts-dark-active { border-color: #fff !important; }

    .ts-thumb {
      position: relative; aspect-ratio: 2/3; overflow: hidden; background: #eee;
    }
    .ts-thumb img { width: 100%; height: 100%; object-fit: cover; display: block; }
    .ts-check {
      position: absolute; top: .4rem; right: .4rem;
      width: 20px; height: 20px; border-radius: 50%;
      background: #111; color: #fff;
      display: none; align-items: center; justify-content: center;
      font-size: .65rem; font-weight: 800;
    }
    .ts-active .ts-check { display: flex; }

    .ts-dots {
      display: flex; gap: 3px; padding: .4rem .5rem .2rem;
    }
    .ts-dot { width: 14px; height: 6px; border-radius: 3px; }

    .ts-info { padding: .1rem .5rem .55rem; }
    .ts-name { font-size: .72rem; font-weight: 700; color: #111; line-height: 1.2; }
    .ts-sub  { font-size: .6rem; color: #999; margin-top: 1px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

    .ts-footer {
      padding: .75rem 1rem;
      border-top: 1px solid #f0ece6;
      font-size: .68rem; color: #bbb; text-align: center;
      flex-shrink: 0;
    }

    @media (max-width: 420px) {
      #ts-panel { width: calc(100vw - 24px); right: 12px; bottom: 80px; }
      #ts-btn   { right: 16px; bottom: 16px; }
    }
  `;
  document.head.appendChild(style);

  /* ── Inject HTML ── */
  const btn = document.createElement('button');
  btn.id = 'ts-btn';
  btn.setAttribute('aria-label', 'Switch template');
  btn.setAttribute('title', 'Switch theme');
  if (current !== 'classic') btn.classList.add('active');
  btn.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="20" height="20">
    <circle cx="12" cy="12" r="3"/>
    <path d="M19.07 4.93a10 10 0 0 1 0 14.14M4.93 4.93a10 10 0 0 0 0 14.14"/>
  </svg>`;

  const panel = document.createElement('div');
  panel.id = 'ts-panel';
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-label', 'Template switcher');

  const cardsHTML = TEMPLATES.map(t => {
    const isActive = t.key === current;
    const imgUrl = `https://images.pexels.com/photos/${t.photo}/pexels-photo-${t.photo}.jpeg?auto=compress&cs=tinysrgb&w=180&h=270&fit=crop`;
    return `
      <div class="ts-card${isActive ? ' ts-active' : ''}" data-href="${t.href}" data-key="${t.key}" role="button" tabindex="0" aria-label="Switch to ${t.label} template${isActive ? ' (current)' : ''}">
        <div class="ts-thumb">
          <img src="${imgUrl}" alt="${t.label}" loading="lazy" width="120" height="180"
               onerror="this.style.display='none'">
          <div class="ts-check" aria-hidden="true">✓</div>
        </div>
        <div class="ts-dots">
          ${t.colors.map(c => `<span class="ts-dot" style="background:${c}"></span>`).join('')}
        </div>
        <div class="ts-info">
          <div class="ts-name">${t.label}</div>
          <div class="ts-sub">${t.sub}</div>
        </div>
      </div>`;
  }).join('');

  panel.innerHTML = `
    <div class="ts-head">
      <div>
        <div class="ts-head-title">Choose Template</div>
        <div class="ts-head-sub">9 themes available · Click to preview</div>
      </div>
      <button class="ts-close" id="ts-close-btn" aria-label="Close">✕</button>
    </div>
    <div class="ts-grid">${cardsHTML}</div>
    <div class="ts-footer">Navigates to that template's home page</div>`;

  document.body.appendChild(btn);
  document.body.appendChild(panel);

  /* ── Toggle panel ── */
  btn.addEventListener('click', () => {
    panel.classList.toggle('open');
  });
  document.getElementById('ts-close-btn').addEventListener('click', () => {
    panel.classList.remove('open');
  });

  /* ── Close on outside click ── */
  document.addEventListener('click', e => {
    if (!panel.contains(e.target) && e.target !== btn) {
      panel.classList.remove('open');
    }
  });

  /* ── Navigate on card click ── */
  panel.querySelectorAll('.ts-card').forEach(card => {
    const go = () => {
      const key  = card.dataset.key;
      const href = card.dataset.href;
      localStorage.setItem('tuasl_tmpl', key);
      window.location.href = href;
    };
    card.addEventListener('click', go);
    card.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') go(); });
  });

})();
