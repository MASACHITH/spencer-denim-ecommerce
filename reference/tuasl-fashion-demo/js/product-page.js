/* =====================================================
   TU@SL Fashion — product-page.js
   ===================================================== */
const RECENTLY_VIEWED_KEY = 'tuasl_recently_viewed';

function getRecentlyViewed() { return JSON.parse(localStorage.getItem(RECENTLY_VIEWED_KEY) || '[]'); }
function addToRecentlyViewed(id) {
  let rv = getRecentlyViewed().filter(i => i !== id);
  rv.unshift(id);
  rv = rv.slice(0, 8);
  localStorage.setItem(RECENTLY_VIEWED_KEY, JSON.stringify(rv));
}

/* ---------- Init Product Page ---------- */
function initProductPage() {
  const params = new URLSearchParams(window.location.search);
  const id = parseInt(params.get('id'));
  const product = getProductById(id);
  if (!product) {
    document.getElementById('product-container').innerHTML = `
      <div class="wishlist-empty" style="padding:5rem 2rem">
        <h2>Product not found</h2>
        <p>The product you are looking for does not exist or may have been removed.</p>
        <a href="index.html" class="btn btn-primary" style="margin-top:1rem">Back to Home</a>
      </div>`;
    return;
  }

  addToRecentlyViewed(product.id);
  document.title = `${product.name} | TU@SL Fashion`;

  renderProductDetail(product);
  renderRelatedProducts(product);
  renderRecentlyViewedProducts(product.id);
}

function renderProductDetail(product) {
  const container = document.getElementById('product-container');
  if (!container) return;

  const hasDiscount = product.discount > 0;
  const badgeMap = { 'New': 'success', 'Best Seller': 'primary', 'Online Exclusive': 'warning', 'Sale': 'sale', 'Low Stock': 'low' };

  const images = [product.image, ...(product.gallery || []).filter(g => g !== product.image)];
  const thumbsHTML = images.map((src, i) => `
    <button class="product-thumb ${i === 0 ? 'active' : ''}" onclick="switchGalleryImage(this, '${src}')" aria-label="View image ${i+1}">
      <img src="${src}" alt="${product.name} view ${i+1}" width="72" height="90" loading="lazy"
        onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
    </button>`).join('');

  const colourOptions = product.colours.map(c => `
    <button class="colour-option" style="background:${getColourHex(c)}" title="${c}"
      data-colour="${c}" onclick="selectColour(this, '${c}')" aria-label="${c}"></button>`).join('');

  const sizeOptions = product.sizes.map((s, i) => `
    <button class="size-option ${i === 0 ? 'selected' : ''}" data-size="${s}"
      onclick="selectSize(this, '${s}')">${s}</button>`).join('');

  container.innerHTML = `
    <div class="product-detail">
      <div class="product-gallery">
        <div class="product-gallery-main" onclick="openImageModal('${product.image}')" id="gallery-main">
          <img id="gallery-main-img" src="${product.image}" alt="${product.name}" width="600" height="800"
            onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
        </div>
        <div class="product-gallery-thumbs">${thumbsHTML}</div>
      </div>

      <div class="product-info">
        ${product.badge ? `<span class="product-info-badge" style="background:var(--${badgeMap[product.badge]||'primary'})">${product.badge}</span>` : ''}
        <h1 class="product-info-name">${product.name}</h1>
        <p class="product-info-code">Product Code: TUASL-${String(product.id).padStart(5,'0')}</p>
        <div class="product-info-rating">
          <span style="color:#f59e0b">${renderStars(product.rating)}</span>
          <span>${product.rating} (${product.reviewCount} reviews)</span>
        </div>
        <div class="product-info-price">
          <span class="product-price-now">${formatPrice(product.price)}</span>
          ${hasDiscount ? `<span class="product-price-was">${formatPrice(product.originalPrice)}</span>
          <span class="product-price-discount">Save ${product.discount}%</span>` : ''}
        </div>

        <div class="variant-group" id="colour-group">
          <p class="variant-label">Colour: <span id="selected-colour-label">${product.colours[0]}</span></p>
          <div class="colour-options">${colourOptions}</div>
        </div>

        <div class="variant-group" id="size-group">
          <p class="variant-label">
            Size: <span id="selected-size-label">${product.sizes[0]}</span>
            <a href="#" onclick="openSizeGuide(); return false" style="font-size:0.75rem;color:var(--primary)">Size Guide</a>
          </p>
          <div class="size-options">${sizeOptions}</div>
        </div>

        <div class="qty-wrap">
          <span class="qty-label">Qty:</span>
          <button class="qty-btn" onclick="changeQty(-1)" aria-label="Decrease">−</button>
          <input class="qty-input" type="number" id="qty-input" value="1" min="1" max="${product.stock}">
          <button class="qty-btn" onclick="changeQty(1)" aria-label="Increase">+</button>
          <span style="margin-left:0.5rem;font-size:0.8125rem;color:${product.stock <= 5 ? 'var(--sale)' : 'var(--success)'};font-weight:600">
            ${product.stock <= 5 ? `Only ${product.stock} left` : 'In Stock'}
          </span>
        </div>

        <div class="product-cta">
          <button class="btn btn-primary btn-lg" id="add-to-bag-btn" onclick="addProductToBag()">
            <svg width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M15.75 10.5V6a3.75 3.75 0 1 0-7.5 0v4.5m11.356-1.993 1.263 12c.07.665-.45 1.243-1.119 1.243H4.25a1.125 1.125 0 0 1-1.12-1.243l1.264-12A1.125 1.125 0 0 1 5.513 7.5h12.974c.576 0 1.059.435 1.119 1.007Z"/></svg>
            Add to Bag
          </button>
          <button class="btn btn-outline-dark btn-lg" onclick="buyNow()">Buy Now</button>
          <button class="btn-icon ${isInWishlist(product.id)?'active':''}" id="wishlist-btn"
            onclick="toggleProductWishlist(${product.id})" aria-label="Add to wishlist" style="padding:0.875rem">
            <svg width="20" height="20" fill="${isInWishlist(product.id)?'currentColor':'none'}" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" d="M21 8.25c0-2.485-2.099-4.5-4.688-4.5-1.935 0-3.597 1.126-4.312 2.733-.715-1.607-2.377-2.733-4.313-2.733C5.1 3.75 3 5.765 3 8.25c0 7.22 9 12 9 12s9-4.78 9-12Z"/>
            </svg>
          </button>
        </div>

        <div class="delivery-info">
          <div class="delivery-info-item">
            <svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path d="M9 17H5a2 2 0 0 0-2 2"/><path d="M11 17h8a2 2 0 0 1 2 2"/><rect x="2" y="3" width="20" height="14" rx="2"/></svg>
            <div>
              <p style="font-weight:600">Standard Delivery</p>
              <p style="color:var(--muted)">Rs. 450 | Free above Rs. 10,000 | 3–5 working days</p>
            </div>
          </div>
          <div class="delivery-info-item">
            <svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>
            <div>
              <p style="font-weight:600">Express Delivery</p>
              <p style="color:var(--muted)">Rs. 850 | Next working day (select areas)</p>
            </div>
          </div>
          <div class="delivery-info-item">
            <svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" viewBox="0 0 24 24"><path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><polyline points="9 22 9 12 15 12 15 22"/></svg>
            <div>
              <p style="font-weight:600">Easy Returns</p>
              <p style="color:var(--muted)">14-day return policy for unworn items with tags attached</p>
            </div>
          </div>
        </div>

        <div class="product-tabs">
          <div class="tab-nav" role="tablist">
            <button class="tab-btn active" role="tab" aria-selected="true" onclick="switchTab(this,'tab-desc')">Description</button>
            <button class="tab-btn" role="tab" onclick="switchTab(this,'tab-details')">Details</button>
            <button class="tab-btn" role="tab" onclick="switchTab(this,'tab-care')">Care</button>
            <button class="tab-btn" role="tab" onclick="switchTab(this,'tab-returns')">Returns</button>
          </div>
          <div id="tab-desc" class="tab-panel active">
            <p>${product.description}</p>
          </div>
          <div id="tab-details" class="tab-panel">
            <table style="width:100%;border-collapse:collapse;font-size:0.875rem">
              ${[
                ['Material', product.material],
                ['Fit', product.fit],
                ['Occasion', product.occasion],
                ['Colours Available', product.colours.join(', ')],
                ['Sizes Available', product.sizes.join(', ')],
                ['Department', product.department],
                ['Category', product.category],
                ['Product Code', `TUASL-${String(product.id).padStart(5,'0')}`]
              ].map(([k,v]) => `<tr style="border-bottom:1px solid var(--border)">
                <td style="padding:0.5rem 0;font-weight:600;width:40%;color:var(--muted)">${k}</td>
                <td style="padding:0.5rem 0">${v}</td>
              </tr>`).join('')}
            </table>
          </div>
          <div id="tab-care" class="tab-panel">
            <ul style="list-style:disc;padding-left:1.25rem;display:flex;flex-direction:column;gap:0.375rem">
              <li>Machine wash cold at 30°C with similar colours</li>
              <li>Do not bleach or tumble dry</li>
              <li>Iron on low heat if required</li>
              <li>Dry flat or hang to dry in shade</li>
              <li>Do not dry clean</li>
              <li>Avoid direct sunlight to prevent colour fading</li>
            </ul>
          </div>
          <div id="tab-returns" class="tab-panel">
            <p>We accept returns within <strong>14 days</strong> of delivery for items that are unworn, unwashed and in their original condition with all tags attached.</p>
            <br>
            <p>To initiate a return, contact us at <a href="mailto:${siteConfig.email}" style="color:var(--primary)">${siteConfig.email}</a> or call <a href="tel:${siteConfig.phone}" style="color:var(--primary)">${siteConfig.phone}</a>.</p>
            <br>
            <p style="color:var(--muted);font-size:0.875rem">Note: Sale items may have a different return policy. Please refer to the product listing for details.</p>
          </div>
        </div>
      </div>
    </div>`;

  // Set initial selections
  window._selectedColour = product.colours[0];
  window._selectedSize = product.sizes[0];
  window._currentProduct = product;

  // First colour selected
  const firstColourBtn = container.querySelector('.colour-option');
  if (firstColourBtn) firstColourBtn.classList.add('selected');
}

/* ---------- Interactions ---------- */
function switchGalleryImage(btn, src) {
  document.querySelectorAll('.product-thumb').forEach(t => t.classList.remove('active'));
  btn.classList.add('active');
  const mainImg = document.getElementById('gallery-main-img');
  if (mainImg) mainImg.src = src;
  const mainWrap = document.getElementById('gallery-main');
  if (mainWrap) mainWrap.onclick = () => openImageModal(src);
}

function selectColour(btn, colour) {
  document.querySelectorAll('.colour-option').forEach(b => b.classList.remove('selected'));
  btn.classList.add('selected');
  window._selectedColour = colour;
  const label = document.getElementById('selected-colour-label');
  if (label) label.textContent = colour;
}

function selectSize(btn, size) {
  document.querySelectorAll('.size-option').forEach(b => b.classList.remove('selected'));
  btn.classList.add('selected');
  window._selectedSize = size;
  const label = document.getElementById('selected-size-label');
  if (label) label.textContent = size;
}

function changeQty(delta) {
  const input = document.getElementById('qty-input');
  if (!input) return;
  const max = parseInt(input.max) || 10;
  const val = Math.max(1, Math.min(max, parseInt(input.value) + delta));
  input.value = val;
}

function addProductToBag() {
  const product = window._currentProduct;
  if (!product) return;
  const qty = parseInt(document.getElementById('qty-input')?.value) || 1;
  cartManager.addItem({
    id: product.id,
    name: product.name,
    price: product.price,
    image: product.image,
    size: window._selectedSize || product.sizes[0],
    colour: window._selectedColour || product.colours[0],
    qty
  });
  showToast(`${product.name} added to bag!`, 'success');
  updateHeaderCounts();
}

function buyNow() {
  addProductToBag();
  window.location.href = 'cart.html';
}

function toggleProductWishlist(id) {
  const product = getProductById(id);
  if (!product) return;
  const btn = document.getElementById('wishlist-btn');
  if (isInWishlist(id)) {
    wishlistManager.removeItem(id);
    showToast('Removed from wishlist.');
    if (btn) { btn.classList.remove('active'); btn.querySelector('svg path').setAttribute('fill','none'); }
  } else {
    wishlistManager.addItem(product);
    showToast('Added to wishlist!', 'success');
    if (btn) { btn.classList.add('active'); btn.querySelector('svg path').setAttribute('fill','currentColor'); btn.classList.add('heart-pop'); setTimeout(()=>btn.classList.remove('heart-pop'),400); }
  }
  updateHeaderCounts();
}

function switchTab(btn, panelId) {
  document.querySelectorAll('.tab-btn').forEach(b => { b.classList.remove('active'); b.setAttribute('aria-selected','false'); });
  document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
  btn.classList.add('active');
  btn.setAttribute('aria-selected','true');
  const panel = document.getElementById(panelId);
  if (panel) panel.classList.add('active');
}

function openSizeGuide() {
  showToast('Size guide: XS=32, S=34, M=36, L=38, XL=40, XXL=42 (UK sizing). Kids sizes by age range.');
}

/* ---------- Related Products ---------- */
function renderRelatedProducts(product) {
  const section = document.getElementById('related-products');
  if (!section) return;
  const related = products
    .filter(p => p.id !== product.id && (p.department === product.department || p.category === product.category))
    .slice(0, 4);
  if (related.length === 0) { section.closest('section')?.remove(); return; }
  section.innerHTML = related.map(p => renderProductCard(p)).join('');
  initScrollReveal();
}

/* ---------- Recently Viewed ---------- */
function renderRecentlyViewedProducts(currentId) {
  const section = document.getElementById('recently-viewed');
  if (!section) return;
  const ids = getRecentlyViewed().filter(i => i !== currentId).slice(0, 4);
  if (ids.length === 0) { section.closest('section')?.remove(); return; }
  const recent = ids.map(id => getProductById(id)).filter(Boolean);
  section.innerHTML = recent.map(p => renderProductCard(p)).join('');
  initScrollReveal();
}
