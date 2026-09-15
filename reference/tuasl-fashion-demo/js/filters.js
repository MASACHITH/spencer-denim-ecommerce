/* =====================================================
   TU@SL Fashion — filters.js
   ===================================================== */
let filteredProducts = [];
let activeFilters = {};
let activeSort = 'recommended';
let visibleCount = 16;
const PAGE_SIZE = 16;

/* ---------- Parse URL params ---------- */
function getUrlFilters() {
  const params = new URLSearchParams(window.location.search);
  const f = {};
  if (params.get('department')) f.department = params.get('department').toLowerCase();
  if (params.get('category')) f.category = params.get('category').toLowerCase();
  if (params.get('sale') === 'true') f.sale = true;
  if (params.get('new') === 'true') f.new = true;
  if (params.get('q')) f.q = params.get('q');
  return f;
}

/* ---------- Apply Filters & Sort ---------- */
function applyFilters() {
  let result = [...products];

  // Department
  if (activeFilters.department) {
    result = result.filter(p => p.department.toLowerCase() === activeFilters.department);
  }
  // Category
  if (activeFilters.category) {
    result = result.filter(p => p.category.toLowerCase().includes(activeFilters.category));
  }
  // Sale
  if (activeFilters.sale) {
    result = result.filter(p => p.discount > 0);
  }
  // New arrivals
  if (activeFilters.new) {
    result = result.filter(p => p.badge === 'New');
  }
  // Search
  if (activeFilters.q) {
    const q = activeFilters.q.toLowerCase();
    result = result.filter(p =>
      p.name.toLowerCase().includes(q) ||
      p.category.toLowerCase().includes(q) ||
      p.department.toLowerCase().includes(q) ||
      (p.colours && p.colours.some(c => c.toLowerCase().includes(q))) ||
      (p.material && p.material.toLowerCase().includes(q)) ||
      (p.tags && p.tags.some(t => t.toLowerCase().includes(q)))
    );
  }
  // Price range
  if (activeFilters.minPrice !== undefined) result = result.filter(p => p.price >= activeFilters.minPrice);
  if (activeFilters.maxPrice !== undefined) result = result.filter(p => p.price <= activeFilters.maxPrice);
  // Size
  if (activeFilters.sizes && activeFilters.sizes.length > 0) {
    result = result.filter(p => p.sizes && p.sizes.some(s => activeFilters.sizes.includes(s)));
  }
  // Colour
  if (activeFilters.colours && activeFilters.colours.length > 0) {
    result = result.filter(p => p.colours && p.colours.some(c =>
      activeFilters.colours.some(fc => c.toLowerCase().includes(fc.toLowerCase()))
    ));
  }
  // Material
  if (activeFilters.material) {
    result = result.filter(p => p.material && p.material.toLowerCase().includes(activeFilters.material));
  }
  // Fit
  if (activeFilters.fit) {
    result = result.filter(p => p.fit && p.fit.toLowerCase() === activeFilters.fit);
  }
  // Occasion
  if (activeFilters.occasion) {
    result = result.filter(p => p.occasion && p.occasion.toLowerCase() === activeFilters.occasion);
  }
  // Rating
  if (activeFilters.minRating) {
    result = result.filter(p => p.rating >= activeFilters.minRating);
  }
  // Discount
  if (activeFilters.hasDiscount) {
    result = result.filter(p => p.discount > 0);
  }

  // Sort
  switch (activeSort) {
    case 'newest':    result.sort((a, b) => (b.badge === 'New' ? 1 : 0) - (a.badge === 'New' ? 1 : 0)); break;
    case 'bestselling': result.sort((a, b) => b.reviewCount - a.reviewCount); break;
    case 'price-asc': result.sort((a, b) => a.price - b.price); break;
    case 'price-desc': result.sort((a, b) => b.price - a.price); break;
    case 'rating':    result.sort((a, b) => b.rating - a.rating); break;
    case 'discount':  result.sort((a, b) => b.discount - a.discount); break;
    default: break; // recommended = original order
  }

  filteredProducts = result;
}

/* ---------- Render Products ---------- */
function renderProducts() {
  const grid = document.getElementById('products-grid');
  const countEl = document.getElementById('product-count');
  const loadMoreBtn = document.getElementById('load-more-btn');
  const emptyEl = document.getElementById('no-products');
  if (!grid) return;

  if (filteredProducts.length === 0) {
    grid.innerHTML = '';
    if (emptyEl) emptyEl.style.display = 'block';
    if (countEl) countEl.textContent = '0 products found';
    if (loadMoreBtn) loadMoreBtn.style.display = 'none';
    return;
  }
  if (emptyEl) emptyEl.style.display = 'none';

  const visible = filteredProducts.slice(0, visibleCount);
  grid.innerHTML = visible.map(p => renderProductCard(p)).join('');

  if (countEl) countEl.textContent = `${filteredProducts.length} product${filteredProducts.length !== 1 ? 's' : ''}`;
  if (loadMoreBtn) {
    loadMoreBtn.style.display = visibleCount < filteredProducts.length ? '' : 'none';
  }

  // Re-observe scroll reveals
  initScrollReveal();
}

/* ---------- Load More ---------- */
function loadMore() {
  visibleCount += PAGE_SIZE;
  renderProducts();
}

/* ---------- Sort change ---------- */
function onSortChange(val) {
  activeSort = val;
  visibleCount = PAGE_SIZE;
  applyFilters();
  renderProducts();
}

/* ---------- Filter sidebar render ---------- */
function renderFilterSidebar() {
  const sidebar = document.getElementById('filter-sidebar');
  const drawerBody = document.getElementById('filter-drawer-body');
  const html = buildFilterHTML();
  if (sidebar) sidebar.innerHTML = html;
  if (drawerBody) drawerBody.innerHTML = html;
}

function buildFilterHTML() {
  const depts = [...new Set(products.map(p => p.department))];
  const categories = [...new Set(filteredProducts.map(p => p.category))].slice(0, 12);
  const allSizes = [...new Set(products.flatMap(p => p.sizes || []))].slice(0, 16);
  const colours = ['White','Black','Navy','Blue','Red','Pink','Green','Grey','Beige','Cream','Brown','Yellow','Orange','Purple'];
  const materials = [...new Set(products.map(p => p.material).filter(Boolean))].slice(0, 8);
  const fits = [...new Set(products.map(p => p.fit).filter(Boolean))];
  const occasions = [...new Set(products.map(p => p.occasion).filter(Boolean))];

  return `
    <div class="filter-header">
      <h3>Filters</h3>
      <button class="btn btn-sm" onclick="clearAllFilters()" style="font-size:0.75rem;color:var(--primary)">Clear All</button>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button" aria-expanded="true">
        Department <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body open">
        ${depts.map(d => `
          <label class="filter-option">
            <input type="checkbox" ${activeFilters.department === d.toLowerCase() ? 'checked' : ''}
              onchange="toggleDeptFilter('${d.toLowerCase()}', this.checked)"> ${d}
          </label>`).join('')}
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Category <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        ${categories.map(c => `
          <label class="filter-option">
            <input type="checkbox" ${activeFilters.category === c.toLowerCase() ? 'checked' : ''}
              onchange="toggleCatFilter('${c.toLowerCase()}', this.checked)"> ${c}
          </label>`).join('')}
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Price Range <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        <div class="price-range">
          <div class="price-inputs">
            <input class="price-input" type="number" placeholder="Min" id="price-min" value="${activeFilters.minPrice || ''}"
              onchange="setPriceFilter()" min="0">
            <span>–</span>
            <input class="price-input" type="number" placeholder="Max" id="price-max" value="${activeFilters.maxPrice || ''}"
              onchange="setPriceFilter()" min="0">
          </div>
        </div>
        <div style="margin-top:0.5rem;display:flex;flex-wrap:wrap;gap:0.375rem">
          ${[[0,3000,'Under Rs.3,000'],[3000,7000,'Rs.3K–7K'],[7000,15000,'Rs.7K–15K'],[15000,99999,'Rs.15K+']].map(([min,max,label]) =>
            `<button class="tag" style="cursor:pointer;font-size:0.6875rem" onclick="setQuickPrice(${min},${max})">${label}</button>`
          ).join('')}
        </div>
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Size <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        <div style="display:flex;flex-wrap:wrap;gap:0.375rem">
          ${allSizes.map(s => `
            <button class="size-option ${(activeFilters.sizes||[]).includes(s)?'selected':''}"
              style="padding:0.25rem 0.625rem;font-size:0.75rem"
              onclick="toggleSizeFilter('${s}', this)">${s}</button>`).join('')}
        </div>
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Colour <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        <div style="display:flex;flex-wrap:wrap;gap:0.5rem;margin-bottom:0.5rem">
          ${colours.map(c => `
            <button title="${c}" style="width:24px;height:24px;border-radius:50%;background:${getColourHex(c)};border:${(activeFilters.colours||[]).includes(c)?'3px solid var(--primary)':'2px solid var(--border)'};cursor:pointer"
              onclick="toggleColourFilter('${c}', this)"></button>`).join('')}
        </div>
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Material <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        ${materials.map(m => `
          <label class="filter-option">
            <input type="checkbox" ${activeFilters.material === m.toLowerCase() ? 'checked' : ''}
              onchange="setMaterialFilter('${m.toLowerCase()}', this.checked)"> ${m}
          </label>`).join('')}
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Occasion <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        ${occasions.map(o => `
          <label class="filter-option">
            <input type="checkbox" ${activeFilters.occasion === o.toLowerCase() ? 'checked' : ''}
              onchange="setOccasionFilter('${o.toLowerCase()}', this.checked)"> ${o}
          </label>`).join('')}
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Rating <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        ${[4.5,4,3.5].map(r => `
          <label class="filter-option">
            <input type="radio" name="rating" value="${r}" ${activeFilters.minRating === r ? 'checked' : ''}
              onchange="setRatingFilter(${r})"> ${r}★ & above
          </label>`).join('')}
      </div>
    </div>

    <div class="filter-group">
      <div class="filter-group-header" onclick="toggleFilterGroup(this)" role="button">
        Discount <svg width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
      </div>
      <div class="filter-group-body">
        <label class="filter-option">
          <input type="checkbox" ${activeFilters.hasDiscount ? 'checked' : ''} onchange="toggleDiscountFilter(this.checked)">
          On Sale / Discounted
        </label>
      </div>
    </div>`;
}

/* ---------- Filter helpers ---------- */
function toggleDeptFilter(dept, checked) {
  if (checked) activeFilters.department = dept;
  else delete activeFilters.department;
  refreshFilters();
}
function toggleCatFilter(cat, checked) {
  if (checked) activeFilters.category = cat;
  else delete activeFilters.category;
  refreshFilters();
}
function setPriceFilter() {
  const min = parseInt(document.getElementById('price-min')?.value);
  const max = parseInt(document.getElementById('price-max')?.value);
  if (!isNaN(min)) activeFilters.minPrice = min; else delete activeFilters.minPrice;
  if (!isNaN(max)) activeFilters.maxPrice = max; else delete activeFilters.maxPrice;
  refreshFilters();
}
function setQuickPrice(min, max) {
  activeFilters.minPrice = min;
  activeFilters.maxPrice = max;
  refreshFilters();
}
function toggleSizeFilter(size, btn) {
  if (!activeFilters.sizes) activeFilters.sizes = [];
  const idx = activeFilters.sizes.indexOf(size);
  if (idx > -1) {
    activeFilters.sizes.splice(idx, 1);
    btn.classList.remove('selected');
  } else {
    activeFilters.sizes.push(size);
    btn.classList.add('selected');
  }
  refreshFilters();
}
function toggleColourFilter(colour, btn) {
  if (!activeFilters.colours) activeFilters.colours = [];
  const idx = activeFilters.colours.indexOf(colour);
  if (idx > -1) {
    activeFilters.colours.splice(idx, 1);
    btn.style.border = '2px solid var(--border)';
  } else {
    activeFilters.colours.push(colour);
    btn.style.border = '3px solid var(--primary)';
  }
  refreshFilters();
}
function setMaterialFilter(mat, checked) {
  if (checked) activeFilters.material = mat;
  else delete activeFilters.material;
  refreshFilters();
}
function setOccasionFilter(occ, checked) {
  if (checked) activeFilters.occasion = occ;
  else delete activeFilters.occasion;
  refreshFilters();
}
function setRatingFilter(r) {
  activeFilters.minRating = r;
  refreshFilters();
}
function toggleDiscountFilter(checked) {
  if (checked) activeFilters.hasDiscount = true;
  else delete activeFilters.hasDiscount;
  refreshFilters();
}
function clearAllFilters() {
  const urlFilters = getUrlFilters();
  activeFilters = { ...urlFilters };
  activeSort = 'recommended';
  visibleCount = PAGE_SIZE;
  const sortEl = document.getElementById('sort-select');
  if (sortEl) sortEl.value = 'recommended';
  applyFilters();
  renderFilterSidebar();
  renderProducts();
  renderActiveTags();
}

function refreshFilters() {
  visibleCount = PAGE_SIZE;
  applyFilters();
  renderProducts();
  renderActiveTags();
}

/* ---------- Active filter tags ---------- */
function renderActiveTags() {
  const container = document.getElementById('active-filters');
  if (!container) return;
  const tags = [];
  if (activeFilters.department) tags.push({ label: `Dept: ${activeFilters.department}`, remove: () => { delete activeFilters.department; } });
  if (activeFilters.category) tags.push({ label: `Cat: ${activeFilters.category}`, remove: () => { delete activeFilters.category; } });
  if (activeFilters.minPrice !== undefined) tags.push({ label: `Min: ${formatPrice(activeFilters.minPrice)}`, remove: () => { delete activeFilters.minPrice; } });
  if (activeFilters.maxPrice !== undefined) tags.push({ label: `Max: ${formatPrice(activeFilters.maxPrice)}`, remove: () => { delete activeFilters.maxPrice; } });
  if (activeFilters.sizes && activeFilters.sizes.length) tags.push({ label: `Sizes: ${activeFilters.sizes.join(',')}`, remove: () => { delete activeFilters.sizes; } });
  if (activeFilters.colours && activeFilters.colours.length) tags.push({ label: `Colours: ${activeFilters.colours.join(',')}`, remove: () => { delete activeFilters.colours; } });
  if (activeFilters.material) tags.push({ label: `Material: ${activeFilters.material}`, remove: () => { delete activeFilters.material; } });
  if (activeFilters.occasion) tags.push({ label: `Occasion: ${activeFilters.occasion}`, remove: () => { delete activeFilters.occasion; } });
  if (activeFilters.minRating) tags.push({ label: `${activeFilters.minRating}★+`, remove: () => { delete activeFilters.minRating; } });
  if (activeFilters.hasDiscount) tags.push({ label: 'On Sale', remove: () => { delete activeFilters.hasDiscount; } });
  if (activeFilters.sale) tags.push({ label: 'Sale', remove: () => { delete activeFilters.sale; } });
  if (activeFilters.new) tags.push({ label: 'New Arrivals', remove: () => { delete activeFilters.new; } });

  container.innerHTML = tags.map((t, i) => `
    <span class="filter-tag">
      ${t.label}
      <button aria-label="Remove filter ${t.label}" onclick="removeFilterTag(${i})">
        <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
      </button>
    </span>`).join('');

  // Store remove functions
  window._filterRemoveFns = tags.map(t => t.remove);
}

function removeFilterTag(idx) {
  if (window._filterRemoveFns && window._filterRemoveFns[idx]) {
    window._filterRemoveFns[idx]();
    refreshFilters();
    renderFilterSidebar();
  }
}

/* ---------- Init Category Page ---------- */
function initCategoryPage() {
  const urlF = getUrlFilters();
  activeFilters = { ...urlF };

  // Set page title
  const headingEl = document.getElementById('page-heading');
  const descEl = document.getElementById('page-desc');
  if (headingEl) {
    if (activeFilters.department) headingEl.textContent = activeFilters.department.charAt(0).toUpperCase() + activeFilters.department.slice(1);
    else if (activeFilters.sale) headingEl.textContent = 'Sale';
    else if (activeFilters.new) headingEl.textContent = 'New Arrivals';
    else if (activeFilters.category) headingEl.textContent = activeFilters.category.charAt(0).toUpperCase() + activeFilters.category.slice(1);
    else headingEl.textContent = 'All Products';
  }

  applyFilters();
  renderFilterSidebar();
  renderProducts();
  renderActiveTags();

  // Sort listener
  const sortEl = document.getElementById('sort-select');
  if (sortEl) sortEl.addEventListener('change', e => onSortChange(e.target.value));
}
