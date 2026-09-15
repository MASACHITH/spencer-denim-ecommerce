/* =====================================================
   TU@SL Fashion — cart.js
   ===================================================== */
const cartManager = {
  key: 'tuasl_cart',
  promoKey: 'tuasl_promo',

  getCart() { return JSON.parse(localStorage.getItem(this.key) || '[]'); },
  saveCart(cart) { localStorage.setItem(this.key, JSON.stringify(cart)); },

  getActivePromo() { return JSON.parse(localStorage.getItem(this.promoKey) || 'null'); },
  savePromo(promo) { localStorage.setItem(this.promoKey, JSON.stringify(promo)); },
  clearPromo() { localStorage.removeItem(this.promoKey); },

  addItem(item) {
    const cart = this.getCart();
    const existing = cart.find(c => c.id === item.id && c.size === item.size && c.colour === item.colour);
    if (existing) {
      existing.qty = Math.min(existing.qty + (item.qty || 1), 10);
    } else {
      cart.push({ ...item, qty: item.qty || 1 });
    }
    this.saveCart(cart);
  },

  removeItem(id, size, colour) {
    const cart = this.getCart().filter(c => !(c.id === id && c.size === size && c.colour === colour));
    this.saveCart(cart);
  },

  updateQty(id, size, colour, qty) {
    const cart = this.getCart();
    const item = cart.find(c => c.id === id && c.size === size && c.colour === colour);
    if (item) {
      if (qty <= 0) { this.removeItem(id, size, colour); return; }
      item.qty = Math.min(qty, 10);
      this.saveCart(cart);
    }
  },

  moveToWishlist(id, size, colour) {
    const cart = this.getCart();
    const item = cart.find(c => c.id === id && c.size === size && c.colour === colour);
    if (item) {
      const product = getProductById(item.id);
      if (product) wishlistManager.addItem(product);
      this.removeItem(id, size, colour);
    }
  },

  clearCart() { localStorage.removeItem(this.key); this.clearPromo(); },

  getSubtotal() {
    return this.getCart().reduce((s, i) => s + i.price * i.qty, 0);
  },

  getDiscount() {
    const promo = this.getActivePromo();
    if (!promo) return 0;
    if (promo.type === 'percent') return Math.round(this.getSubtotal() * promo.value / 100);
    return 0;
  },

  getDeliveryFee() {
    const promo = this.getActivePromo();
    const sub = this.getSubtotal() - this.getDiscount();
    if (promo && promo.type === 'shipping') return 0;
    if (sub >= siteConfig.freeDeliveryMinimum) return 0;
    return siteConfig.standardDeliveryFee;
  },

  getTotal() {
    return this.getSubtotal() - this.getDiscount() + this.getDeliveryFee();
  },

  applyPromo(code) {
    const promo = siteConfig.promoCodes[code.toUpperCase()];
    if (promo) {
      this.savePromo({ code: code.toUpperCase(), ...promo });
      return { success: true, label: promo.label };
    }
    return { success: false };
  }
};

/* ---------- Render Cart Page ---------- */
function renderCartPage() {
  const cart = cartManager.getCart();
  const itemsEl = document.getElementById('cart-items');
  const summaryEl = document.getElementById('cart-summary');
  const emptyEl = document.getElementById('cart-empty');
  if (!itemsEl) return;

  if (cart.length === 0) {
    itemsEl.innerHTML = '';
    if (summaryEl) summaryEl.style.display = 'none';
    if (emptyEl) emptyEl.style.display = 'block';
    return;
  }
  if (emptyEl) emptyEl.style.display = 'none';
  if (summaryEl) summaryEl.style.display = '';

  itemsEl.innerHTML = cart.map(item => `
    <div class="cart-item">
      <img class="cart-item-img"
        src="${item.image}" alt="${item.name}"
        loading="lazy" width="90" height="110"
        onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
      <div>
        <p class="cart-item-name">${item.name}</p>
        <p class="cart-item-meta">Size: ${item.size} &nbsp;|&nbsp; Colour: ${item.colour}</p>
        <p class="cart-item-price">${formatPrice(item.price)}</p>
        <div class="qty-wrap" style="margin-top:0.5rem;margin-bottom:0">
          <button class="qty-btn" aria-label="Decrease quantity" onclick="changeCartQty(${item.id},'${item.size}','${item.colour}',-1)">−</button>
          <input class="qty-input" type="number" min="1" max="10" value="${item.qty}" style="width:44px"
            onchange="setCartQty(${item.id},'${item.size}','${item.colour}',this.value)">
          <button class="qty-btn" aria-label="Increase quantity" onclick="changeCartQty(${item.id},'${item.size}','${item.colour}',1)">+</button>
          <span style="margin-left:0.5rem;font-size:0.875rem;font-weight:600;color:var(--primary)">${formatPrice(item.price * item.qty)}</span>
        </div>
        <div style="display:flex;gap:1rem;margin-top:0.5rem">
          <button class="cart-item-remove" onclick="removeFromCart(${item.id},'${item.size}','${item.colour}')">
            <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4h6v2"/></svg>
            Remove
          </button>
          <button class="cart-item-remove" style="color:var(--primary)" onclick="moveToWishlist(${item.id},'${item.size}','${item.colour}')">
            ♡ Move to Wishlist
          </button>
        </div>
      </div>
    </div>`).join('');

  updateCartSummary();
}

function changeCartQty(id, size, colour, delta) {
  const cart = cartManager.getCart();
  const item = cart.find(c => c.id === id && c.size === size && c.colour === colour);
  if (item) cartManager.updateQty(id, size, colour, item.qty + delta);
  renderCartPage();
  updateHeaderCounts();
}

function setCartQty(id, size, colour, val) {
  cartManager.updateQty(id, size, colour, parseInt(val) || 1);
  renderCartPage();
  updateHeaderCounts();
}

function removeFromCart(id, size, colour) {
  cartManager.removeItem(id, size, colour);
  renderCartPage();
  updateHeaderCounts();
  showToast('Item removed from bag.');
}

function moveToWishlist(id, size, colour) {
  cartManager.moveToWishlist(id, size, colour);
  renderCartPage();
  updateHeaderCounts();
  showToast('Moved to wishlist!', 'success');
}

function applyPromoCode() {
  const input = document.getElementById('promo-input');
  if (!input) return;
  const result = cartManager.applyPromo(input.value.trim());
  if (result.success) {
    showToast(`Promo applied: ${result.label}`, 'success');
    updateCartSummary();
  } else {
    showToast('Invalid promo code.', 'error');
  }
}

function updateCartSummary() {
  const sub = cartManager.getSubtotal();
  const disc = cartManager.getDiscount();
  const delivery = cartManager.getDeliveryFee();
  const total = cartManager.getTotal();
  const promo = cartManager.getActivePromo();
  const update = (id, val) => { const el = document.getElementById(id); if (el) el.textContent = val; };
  update('summary-subtotal', formatPrice(sub));
  update('summary-discount', disc > 0 ? `−${formatPrice(disc)}` : '—');
  update('summary-delivery', delivery === 0 ? 'FREE' : formatPrice(delivery));
  update('summary-total', formatPrice(total));
  const promoRow = document.getElementById('promo-row');
  if (promoRow) promoRow.style.display = disc > 0 ? '' : 'none';
  const promoLabel = document.getElementById('promo-label');
  if (promoLabel && promo) promoLabel.textContent = `Promo: ${promo.code}`;
}
