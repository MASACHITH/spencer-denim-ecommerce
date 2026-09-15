/* =====================================================
   TU@SL Fashion — wishlist.js
   ===================================================== */
const wishlistManager = {
  key: 'tuasl_wishlist',

  getWishlist() { return JSON.parse(localStorage.getItem(this.key) || '[]'); },
  saveWishlist(list) { localStorage.setItem(this.key, JSON.stringify(list)); },

  addItem(product) {
    const list = this.getWishlist();
    if (!list.find(p => p.id === product.id)) {
      list.push(product);
      this.saveWishlist(list);
    }
  },

  removeItem(id) {
    const list = this.getWishlist().filter(p => p.id !== id);
    this.saveWishlist(list);
  },

  clearWishlist() { localStorage.removeItem(this.key); },

  moveToCart(id) {
    const product = this.getWishlist().find(p => p.id === id);
    if (product) {
      cartManager.addItem({
        id: product.id, name: product.name, price: product.price,
        image: product.image, size: product.sizes[0] || 'One Size',
        colour: product.colours[0] || 'Default', qty: 1
      });
      this.removeItem(id);
    }
  }
};

function isInWishlist(id) {
  return wishlistManager.getWishlist().some(p => p.id === id);
}

/* ---------- Render Wishlist Page ---------- */
function renderWishlistPage() {
  const list = wishlistManager.getWishlist();
  const gridEl = document.getElementById('wishlist-grid');
  const emptyEl = document.getElementById('wishlist-empty');
  const countEl = document.getElementById('wishlist-item-count');
  if (!gridEl) return;

  if (countEl) countEl.textContent = `${list.length} item${list.length !== 1 ? 's' : ''}`;

  if (list.length === 0) {
    gridEl.innerHTML = '';
    if (emptyEl) emptyEl.style.display = 'block';
    return;
  }
  if (emptyEl) emptyEl.style.display = 'none';

  gridEl.innerHTML = list.map(product => {
    const hasDiscount = product.discount > 0;
    return `
      <article class="product-card" data-id="${product.id}">
        <a href="product.html?id=${product.id}" class="product-card__image-wrap">
          <img class="product-card__img" src="${product.image}" alt="${product.name}"
            loading="lazy" width="400" height="533"
            onerror="this.src='assets/images/placeholders/product-placeholder.svg'">
          ${product.badge ? `<span class="product-card__badge">${product.badge}</span>` : ''}
          <button class="product-card__wishlist active" aria-label="Remove from wishlist"
            onclick="removeWishlistItem(event, ${product.id})">
            <svg width="16" height="16" fill="currentColor" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24">
              <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/>
            </svg>
          </button>
        </a>
        <div class="product-card__info">
          <p class="product-card__category">${product.category}</p>
          <h3 class="product-card__name"><a href="product.html?id=${product.id}">${product.name}</a></h3>
          <div class="product-card__price">
            <span class="product-card__price-now">${formatPrice(product.price)}</span>
            ${hasDiscount ? `<span class="product-card__price-was">${formatPrice(product.originalPrice)}</span>
            <span class="product-card__discount">-${product.discount}%</span>` : ''}
          </div>
        </div>
        <div class="product-card__actions" style="transform:translateY(0);display:flex">
          <button class="btn btn-sm btn-outline-dark" style="flex:1" onclick="moveWishlistToCart(event, ${product.id})">Move to Bag</button>
          <button class="btn btn-sm btn-primary" style="flex:1" onclick="removeWishlistItem(event, ${product.id})">Remove</button>
        </div>
      </article>`;
  }).join('');
}

function removeWishlistItem(e, id) {
  e.preventDefault();
  wishlistManager.removeItem(id);
  renderWishlistPage();
  updateHeaderCounts();
  showToast('Removed from wishlist.');
}

function moveWishlistToCart(e, id) {
  e.preventDefault();
  wishlistManager.moveToCart(id);
  renderWishlistPage();
  updateHeaderCounts();
  showToast('Moved to bag!', 'success');
}

function clearAllWishlist() {
  wishlistManager.clearWishlist();
  renderWishlistPage();
  updateHeaderCounts();
  showToast('Wishlist cleared.');
}
