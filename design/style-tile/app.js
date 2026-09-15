/* Spencer Denim style tile: small demo interactions only. DOM text is set with textContent. */
(function () {
  'use strict';

  var FREE_DELIVERY = 10000;
  var FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

  function $(sel, ctx) { return (ctx || document).querySelector(sel); }
  function $$(sel, ctx) { return Array.prototype.slice.call((ctx || document).querySelectorAll(sel)); }
  function fmt(n) { return 'Rs. ' + n.toLocaleString('en-US'); }
  function setText(sel, text) { $$(sel).forEach(function (el) { el.textContent = text; }); }

  /* ---------- Toast ---------- */
  var toast = $('[data-toast]');
  var toastText = $('[data-toast-text]');
  var toastTimer, toastClear;
  function showToast(msg) {
    if (!toast || !toastText) return;
    clearTimeout(toastTimer);
    clearTimeout(toastClear);
    toastText.textContent = msg;
    toast.classList.add('is-visible');
    toastTimer = setTimeout(function () {
      toast.classList.remove('is-visible');
      toastClear = setTimeout(function () { toastText.textContent = ''; }, 400);
    }, 4000);
  }
  var toastDemo = $('[data-toast-demo]');
  if (toastDemo) toastDemo.addEventListener('click', function () { showToast('Added to your bag'); });

  /* ---------- Demo links, preview-only controls and forms ---------- */
  document.addEventListener('click', function (e) {
    if (!e.target.closest) return;
    if (e.target.closest('a[href="#"]')) e.preventDefault();
    var preview = e.target.closest('[data-preview]');
    if (preview) showToast('Preview only: ' + preview.getAttribute('data-preview') + '.');
  });
  document.addEventListener('submit', function (e) {
    e.preventDefault();
    var isSearch = e.target.matches && e.target.matches('[role="search"]');
    showToast(isSearch ? 'Search preview: results appear here in the live store.' : 'Preview only: this form sends in the live store.');
  });

  /* ---------- Drawers ---------- */
  var openDrawer = null;
  var lastTrigger = null;
  var inertTargets = $$('[data-modal-inert]');

  function visibleFocusables(root) {
    return $$(FOCUSABLE, root).filter(function (el) { return el.getClientRects().length > 0; });
  }
  function setExpanded(id, value) {
    $$('[data-open][aria-controls="' + id + '"]').forEach(function (t) { t.setAttribute('aria-expanded', String(value)); });
  }
  function setInert(value) {
    inertTargets.forEach(function (el) {
      if (value) el.setAttribute('inert', ''); else el.removeAttribute('inert');
    });
  }
  function focusFirst(drawer) {
    var target = visibleFocusables(drawer)[0];
    if (!target) return;
    target.focus();
    if (document.activeElement !== target) {
      /* Fallback: try again once styles have been applied (next frame, then shortly after) */
      var retry = function () {
        if (openDrawer === drawer && !drawer.contains(document.activeElement)) target.focus();
      };
      requestAnimationFrame(retry);
      setTimeout(retry, 60);
    }
  }
  function openDrawerEl(drawer, trigger) {
    if (openDrawer) closeDrawer(false);
    openDrawer = drawer;
    lastTrigger = trigger || null;
    drawer.classList.add('is-open');
    void drawer.offsetWidth; /* apply the visible state before moving focus */
    setExpanded(drawer.id, true);
    document.body.classList.add('no-scroll');
    setInert(true);
    focusFirst(drawer);
  }
  function closeDrawer(returnFocus) {
    if (!openDrawer) return;
    var drawer = openDrawer;
    openDrawer = null;
    drawer.classList.remove('is-open');
    setExpanded(drawer.id, false);
    document.body.classList.remove('no-scroll');
    setInert(false);
    if (returnFocus !== false && lastTrigger) lastTrigger.focus();
  }
  $$('[data-open]').forEach(function (trigger) {
    trigger.addEventListener('click', function () {
      var drawer = document.getElementById(trigger.getAttribute('aria-controls'));
      if (drawer) openDrawerEl(drawer, trigger);
    });
  });
  $$('[data-drawer] [data-close]').forEach(function (el) {
    el.addEventListener('click', function () { closeDrawer(); });
  });
  document.addEventListener('keydown', function (e) {
    if (!openDrawer) return;
    if (e.key === 'Escape' || e.key === 'Esc') {
      e.preventDefault();
      closeDrawer();
      return;
    }
    if (e.key === 'Tab') {
      var items = visibleFocusables(openDrawer);
      if (!items.length) { e.preventDefault(); return; }
      var index = items.indexOf(document.activeElement);
      /* index is -1 when focus is on a non-tabbable element such as the drawer title */
      if (e.shiftKey && index <= 0) {
        e.preventDefault();
        items[items.length - 1].focus();
      } else if (!e.shiftKey && (index === -1 || index === items.length - 1)) {
        e.preventDefault();
        items[0].focus();
      }
    }
  });
  /* Safety net: focus can never rest outside an open drawer */
  document.addEventListener('focusin', function (e) {
    if (openDrawer && !openDrawer.contains(e.target)) focusFirst(openDrawer);
  });

  /* ---------- Mobile menu accordion ---------- */
  $$('[data-accordion]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var panel = document.getElementById(btn.getAttribute('aria-controls'));
      var expanded = btn.getAttribute('aria-expanded') === 'true';
      btn.setAttribute('aria-expanded', String(!expanded));
      if (panel) panel.hidden = expanded;
    });
  });

  /* ---------- Announcement bar ---------- */
  var messages = [
    'Free island-wide delivery on orders over Rs. 10,000',
    'New season linen has arrived. Shop the edit',
    'Chat with us on WhatsApp for size advice'
  ];
  $$('[data-announce]').forEach(function (bar) {
    var index = 0;
    var text = $('[data-announce-text]', bar);
    function go(step) {
      index = (index + step + messages.length) % messages.length;
      text.textContent = messages[index];
    }
    $('[data-announce-prev]', bar).addEventListener('click', function () { go(-1); });
    $('[data-announce-next]', bar).addEventListener('click', function () { go(1); });
  });

  /* ---------- Wishlist (synced by product id) ---------- */
  var wishButtons = $$('[data-wish]');
  var wished = {};
  wishButtons.forEach(function (b) {
    if (b.getAttribute('aria-pressed') === 'true') wished[b.getAttribute('data-wish-id')] = true;
  });
  var wishCount = parseInt(($('[data-wish-count]') || {}).textContent, 10) || 0;
  var wishElsewhere = Math.max(0, wishCount - Object.keys(wished).length); /* saved items not shown on this page */
  wishButtons.forEach(function (btn) {
    btn.addEventListener('click', function () {
      var id = btn.getAttribute('data-wish-id');
      var on = !wished[id];
      if (on) wished[id] = true; else delete wished[id];
      wishButtons.forEach(function (b) {
        if (b.getAttribute('data-wish-id') === id) b.setAttribute('aria-pressed', String(on));
      });
      wishCount = wishElsewhere + Object.keys(wished).length;
      setText('[data-wish-count]', String(wishCount));
      showToast(on ? 'Saved to your wishlist' : 'Removed from your wishlist');
    });
  });

  /* ---------- Product card actions ---------- */
  $$('[data-card-add]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      showToast(btn.getAttribute('data-card-add') + ': choose a size in Quick View to add it to your bag');
    });
  });
  $$('[data-quick-view]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      showToast('Preview only: Quick View opens a product window for ' + btn.getAttribute('data-quick-view') + '.');
    });
  });

  /* ---------- Quantity steppers ---------- */
  $$('[data-stepper]').forEach(function (stepper) {
    var input = $('input', stepper);
    var dec = $('[data-step="-1"]', stepper);
    var inc = $('[data-step="1"]', stepper);
    var min = parseInt(input.min, 10) || 1;
    var max = parseInt(input.max, 10) || 10;

    function setButtons(value) {
      dec.setAttribute('aria-disabled', String(value <= min));
      inc.setAttribute('aria-disabled', String(value >= max));
    }
    function notify(value) {
      stepper.dispatchEvent(new CustomEvent('qtychange', { bubbles: true, detail: value }));
    }
    function apply(value, shouldNotify) {
      if (isNaN(value)) value = min;
      value = Math.max(min, Math.min(max, value));
      input.value = String(value);
      setButtons(value);
      if (shouldNotify) notify(value);
    }
    dec.addEventListener('click', function () { apply(parseInt(input.value, 10) - 1, true); });
    inc.addEventListener('click', function () { apply(parseInt(input.value, 10) + 1, true); });
    input.addEventListener('change', function () { apply(parseInt(input.value, 10), true); });
    /* Live update while typing, only for valid values (blur/Enter still tidies up) */
    input.addEventListener('input', function () {
      var value = parseInt(input.value, 10);
      if (!isNaN(value) && value >= min && value <= max) { setButtons(value); notify(value); }
    });
    apply(parseInt(input.value, 10), false);
  });

  /* ---------- Filter chips ---------- */
  var plp = $('[data-plp]');
  if (plp) {
    var base = parseInt(plp.getAttribute('data-base'), 10) || 0;
    var chips = $$('[data-chip]', plp);
    var countEl = $('[data-plp-count]', plp);
    var pill = $('[data-plp-pill]', plp);
    var clearBtn = $('[data-plp-clear]', plp);
    var resetBtn = $('[data-plp-reset]', plp);

    var updatePlp = function () {
      var active = chips.filter(function (c) { return !c.parentElement.hidden; });
      var total = base;
      chips.forEach(function (c) {
        if (c.parentElement.hidden) total += parseInt(c.getAttribute('data-add'), 10) || 0;
      });
      countEl.textContent = String(total);
      pill.textContent = String(active.length);
      pill.hidden = active.length === 0;
      clearBtn.hidden = active.length === 0;
      resetBtn.hidden = active.length !== 0;
      return active;
    };

    chips.forEach(function (chip) {
      chip.addEventListener('click', function () {
        var index = chips.indexOf(chip);
        chip.parentElement.hidden = true;
        var active = updatePlp();
        var next = active.filter(function (c) { return chips.indexOf(c) > index; })[0] || active[active.length - 1];
        (next || resetBtn).focus();
      });
    });
    clearBtn.addEventListener('click', function () {
      chips.forEach(function (c) { c.parentElement.hidden = true; });
      updatePlp();
      resetBtn.focus();
    });
    resetBtn.addEventListener('click', function () {
      chips.forEach(function (c) { c.parentElement.hidden = false; });
      updatePlp();
      chips[0].focus();
    });
    updatePlp();
  }

  /* ---------- Product page ---------- */
  var pdp = $('[data-pdp]');
  if (pdp) {
    var main = $('[data-pdp-main]', pdp);
    var mainLabel = $('[data-pdp-main-label]', pdp);
    var thumbs = $$('[data-thumb]', pdp);
    var tones = ['ph--t1', 'ph--t2', 'ph--t3', 'ph--t4', 'ph--t5', 'ph--t6'];

    thumbs.forEach(function (thumb) {
      thumb.addEventListener('click', function () {
        thumbs.forEach(function (t) { t.setAttribute('aria-pressed', String(t === thumb)); });
        tones.forEach(function (t) { main.classList.remove(t); });
        main.classList.add(thumb.getAttribute('data-tone'));
        mainLabel.textContent = thumb.getAttribute('data-caption');
      });
    });

    var colourName = $('[data-pdp-colour]', pdp);
    var sizeName = $('[data-pdp-size]', pdp);
    var addBtn = $('[data-pdp-add]', pdp);
    var help = $('[data-pdp-help]', pdp);
    var stock = $('[data-pdp-stock]', pdp);
    var stockText = $('[data-pdp-stock-text]', pdp);
    var qtyInput = $('[data-pdp-qty]', pdp);
    var colour = '';
    var size = '';

    var refresh = function () {
      var ready = Boolean(colour && size);
      addBtn.disabled = !ready;
      if (ready) help.textContent = colour + ', size ' + size + ' selected. Ready to add to your bag.';
      else if (!colour && !size) help.textContent = 'Select a colour and size to add this to your bag.';
      else if (!colour) help.textContent = 'Now select a colour.';
      else help.textContent = 'Now select a size.';
    };

    $$('input[name="pdp-colour"]', pdp).forEach(function (input) {
      input.addEventListener('change', function () {
        colour = input.value;
        colourName.textContent = colour;
        refresh();
      });
    });
    $$('input[name="pdp-size"]', pdp).forEach(function (input) {
      input.addEventListener('change', function () {
        size = input.value;
        sizeName.textContent = size;
        var low = input.getAttribute('data-low');
        stock.classList.toggle('stock--low', Boolean(low));
        stock.classList.toggle('stock--in', !low);
        stockText.textContent = low ? 'Only ' + low + ' left in size ' + size : 'In stock';
        refresh();
      });
    });
    addBtn.addEventListener('click', function () {
      if (addBtn.disabled) return;
      var qty = parseInt(qtyInput.value, 10) || 1;
      showToast('Added to bag: High-Rise Wide Leg Jeans, ' + colour + ', size ' + size + ', quantity ' + qty);
    });
  }

  /* ---------- Mini bag ---------- */
  var bag = $('[data-bag]');
  if (bag) {
    var list = $('[data-bag-list]', bag);
    var empty = $('[data-bag-empty]', bag);
    var meter = $('[data-meter]', bag);
    var meterText = $('[data-meter-text]', bag);
    var meterFill = $('[data-meter-fill]', bag);
    var subtotalEl = $('[data-bag-subtotal]', bag);
    var linesEl = $('[data-bag-lines]', bag);
    var actions = $$('[data-bag-action]', bag);
    var title = $('[data-bag-title]', bag);
    var bagItems = $$('[data-bag-item]', bag);
    var restoreBtn = $('[data-bag-restore]', bag);

    var recalc = function () {
      var items = bagItems.filter(function (li) { return !li.hidden; });
      var subtotal = 0;
      var qty = 0;
      items.forEach(function (li) {
        var q = parseInt($('input', li).value, 10) || 1;
        var price = parseInt(li.getAttribute('data-price'), 10) || 0;
        subtotal += price * q;
        qty += q;
        $('[data-line-total]', li).textContent = fmt(price * q);
      });
      subtotalEl.textContent = fmt(subtotal);
      linesEl.textContent = '(' + qty + ')';
      setText('[data-bag-count]', String(qty));
      var gap = FREE_DELIVERY - subtotal;
      meterText.textContent = gap > 0
        ? 'You\'re ' + fmt(gap) + ' away from free delivery.'
        : 'You\'ve unlocked free delivery.';
      meterFill.style.width = Math.min(100, (subtotal / FREE_DELIVERY) * 100).toFixed(1) + '%';
      var isEmpty = items.length === 0;
      list.hidden = isEmpty;
      meter.hidden = isEmpty;
      empty.hidden = !isEmpty;
      actions.forEach(function (b) { b.disabled = isEmpty; });
      return items;
    };

    bag.addEventListener('qtychange', recalc);
    bagItems.forEach(function (li) {
      $('[data-bag-remove]', li).addEventListener('click', function () {
        li.hidden = true;
        var items = recalc();
        var nextRemove = items.length ? $('[data-bag-remove]', items[0]) : null;
        (nextRemove || title).focus();
        showToast('Item removed from your bag');
      });
    });
    if (restoreBtn) {
      restoreBtn.addEventListener('click', function () {
        bagItems.forEach(function (li) {
          li.hidden = false;
          var input = $('input', li);
          input.value = '1';
          input.dispatchEvent(new Event('change'));
        });
        recalc();
        $('[data-bag-remove]', bagItems[0]).focus();
        showToast('Example items restored');
      });
    }
    actions.forEach(function (b) {
      b.addEventListener('click', function () { showToast('Preview only: this opens the ' + b.textContent.trim().toLowerCase() + ' page in the live store.'); });
    });
    recalc();
  }
})();
