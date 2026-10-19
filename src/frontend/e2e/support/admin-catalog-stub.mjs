/**
 * A stand-in for the admin API, and a static host for the built admin application, while the browser tests
 * run (ADM-11, W1-12).
 *
 * The admin app is a plain SPA — no SSR, no server of its own — so the browser suite needs something to
 * serve `dist/admin` with an index fallback, and something to answer `/api/admin/*` from the same origin
 * (the session cookie and the antiforgery cookie are both same-origin, and in a deployment Caddy puts the
 * app and the API on one host). This is both, in one process, so one `webServer` entry starts it.
 *
 * **Why a stub rather than the real API.** Starting .NET would mean a SQL Server instance, migrations and a
 * seeded catalogue for every `npx playwright test`. What these tests need is a predictable tree at a
 * predictable shape; the endpoint behaviour itself is proved against a real SQL Server in
 * `AdminCatalogEndpointTests` and `AdminDeactivationEffectTests`.
 *
 * **It is deliberately not more polite than the real API**, and fix round 1 made that true rather than
 * merely claimed. It answers the same field names (`SpencerDenim.Application.Catalog.Admin.Contracts`), the
 * same statuses and the same RFC 9457 bodies with a `code` beside the sentence, and it enforces the four
 * refusals a screen can actually get wrong:
 *
 * - no `X-XSRF-TOKEN` on a mutating request is a 400, as all eight real write routes answer;
 * - a missing or stale row version is a 409 `stale_row_version` on the five routes that take one, and a
 *   successful write moves the row version, so a screen that re-sent a stale token would be caught;
 * - a sibling list that is not the set the server holds is a 409 `stale_order`;
 * - a category something still points at is a 409 `category_in_use`.
 *
 * The first two were missing, and the consequence was precise: if the screen had stopped sending the
 * antiforgery header or the row version, all 246 browser tests would still have passed.
 *
 * `PUT /categories/{id}` also honours `parentCategoryId` and re-paths the subtree, so re-parenting — the
 * largest thing this screen does — has browser coverage at all.
 */
import { readFileSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve } from 'node:path';

import { freshCustomerState, handleCustomerRoute } from './admin-customer-stub.mjs';
import { freshReportingState, handleReportingRoute } from './admin-reporting-stub.mjs';
import { freshInventoryState, handleInventoryRoute } from './admin-inventory-stub.mjs';
import { freshProductState, handleProductRoute } from './admin-product-stub.mjs';

const PORT = Number(process.env.E2E_ADMIN_PORT ?? 4430);
const DIST = resolve(process.env.E2E_ADMIN_DIST ?? 'dist/admin/browser');

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

/** The signed-in staff user every test runs as: no pending password change, no pending enrolment. */
const SESSION = {
  userId: 7,
  email: 'catalogue@example.com',
  firstName: 'Cata',
  lastName: 'Logue',
  grantsAllPermissions: true,
  permissions: [
    'Categories.View',
    'Categories.Create',
    'Categories.Edit',
    'Categories.Delete',

    // ADM-10 (W1-03): the product screens.
    'Products.View',
    'Products.Create',
    'Products.Edit',
    'Products.Delete',

    // ADM-22. Settings.Edit and Media.Create: the settings screen's logo flow uploads to the library before
    // it points the setting at the result, so both are needed to drive it.
    'Settings.Edit',
    'Media.Create',
  ],
  mustChangePassword: false,
  twoFactorEnabled: true,
  twoFactorEnrolmentRequired: false,
  twoFactorRequired: false,
};

/** The token this stub issues; a mutating request has to carry it, as the real API requires. */
const ANTIFORGERY_TOKEN = 'e2e-antiforgery-token';

/** `rowversion` is eight bytes; the shape matters because the screen round-trips it untouched. */
function rowVersion(seed) {
  return Buffer.from([0, 0, 0, 0, 0, 0, 0, seed]).toString('base64');
}

/** Moves a row version on, so a token a screen cached before a write is genuinely stale afterwards. */
let rowVersionTick = 200;

function nextRowVersion() {
  rowVersionTick += 1;
  return rowVersion(rowVersionTick);
}

/**
 * The row version the caller is working from: `If-Match` if it sent one, otherwise the body's own field —
 * the same order `AdminWriteRequest.RowVersion` reads them in, quotes and all.
 *
 * One input is read differently, and it is written down rather than chased: for `If-Match: ""abc""` the
 * real reader's `Trim('"')` strips every quote and gives `abc`, while this strips one from each end and
 * gives `"abc"`. Nothing sends a doubly quoted ETag — the screen builds the header itself from a base64
 * token — so the divergence is unreachable, and making the stub match exactly would mean reimplementing
 * `Trim` rather than reading a header.
 */
function suppliedRowVersion(request, body) {
  const header = request.headers['if-match'];
  if (typeof header === 'string' && header.trim().length > 0) {
    return header.trim().replace(/^W\//i, '').replace(/^"|"$/g, '');
  }

  return body && typeof body.rowVersion === 'string' ? body.rowVersion : null;
}

function department(id, name, slug, sortOrder, isActive, categoryCount, productCount) {
  return {
    id,
    name,
    slug,
    description: `${name} description.`,

    // Not null, so a save that drops the field cannot round-trip null to null unnoticed — the shape of the
    // fix-round-1 blocker.
    heroImageAssetId: 41,
    sortOrder,
    isActive,
    seoTitle: `${name} | Colombo Wardrobe`,
    seoDescription: null,
    rowVersion: rowVersion(id),
    categoryCount,
    storefrontProductCount: productCount,
  };
}

function category(id, name, slug, parentCategoryId, depth, path, sortOrder, options = {}) {
  return {
    id,
    departmentId: 1,
    parentCategoryId,
    path,
    depth,
    name,
    slug,
    description: null,
    imageAssetId: null,
    bannerAssetId: null,
    bannerLinkUrl: null,
    ageGroupId: null,
    sizeGuideId: null,
    sortOrder,
    showInMegaMenu: true,
    megaMenuColumn: null,
    isActive: options.isActive ?? true,
    seoTitle: null,
    seoDescription: null,
    rowVersion: rowVersion(id),
    productCount: options.productCount ?? 0,
    primaryProductCount: options.primaryProductCount ?? 0,
    children: options.children ?? [],
  };
}

/** Rebuilt per run, because the tests write to it. */
/**
 * One admin order, as `GET /api/admin/orders/{token}` answers it (ADM-09).
 *
 * **Every field carries a real value**, and the detail's collections are never empty. W1-12's blocker
 * survived 1,571 passing tests because every fixture set the columns it cleared to `null`, so a round
 * trip compared null with null and agreed; a fixture whose lines, payments and history are real is the
 * only kind that can fail when a panel stops rendering them.
 */
function adminOrder(overrides) {
  const actions = {
    canStartProcessing: false,
    canMarkShipped: false,
    canMarkReadyForCollection: false,
    canMarkDelivered: false,
    canEditDispatch: false,
    canAcceptLatePayment: false,
    canCancel: false,
    couriersConfigured: false,
    blockedReason: null,
    ...(overrides.actions ?? {}),
  };

  const base = {
    orderPublicId: '44444444-4444-4444-8444-444444444444',
    orderNumber: 'SD-2026-000123',
    status: 'Paid',
    statusLabel: 'Paid',
    stockState: 'Committed',
    customerName: 'Nimali Perera',
    customerEmail: 'nimali@example.com',
    grandTotal: 15596.6,
    currency: 'LKR',
    paymentProviderCode: 'BankTransfer',
    deliveryMethodName: 'Standard delivery',
    isPickup: false,
    courierName: null,
    trackingNumber: null,
    invoiceNumber: null,
    invoiceIssuedAtUtc: null,
    placedAtUtc: '2026-10-05T04:30:00Z',
    paymentDueAtUtc: '2026-10-07T04:30:00Z',
    secondsUntilExpiry: null,
    paidAtUtc: null,
    shippedAtUtc: null,
    deliveredAtUtc: null,
    closedAtUtc: null,
    customerNote: 'Please leave it with the security desk.',
    internalNote: null,
    cancelReason: null,
    ...overrides,
    actions,
  };

  return base;
}

/** The order list row for one order: the detail's own fields, minus the panels. */
function orderRow(order) {
  return {
    orderPublicId: order.orderPublicId,
    orderNumber: order.orderNumber,
    status: order.status,
    statusLabel: order.statusLabel,
    customerName: order.customerName,
    customerEmail: order.customerEmail,
    grandTotal: order.grandTotal,
    currency: order.currency,
    paymentProviderCode: order.paymentProviderCode,
    deliveryMethodName: order.deliveryMethodName,
    isPickup: order.isPickup,
    courierName: order.courierName,
    trackingNumber: order.trackingNumber,
    invoiceNumber: order.invoiceNumber,
    placedAtUtc: order.placedAtUtc,
    paidAtUtc: order.paidAtUtc,
    secondsUntilExpiry: order.secondsUntilExpiry,
  };
}

/** The full detail payload for one order, with its panels filled in. */
function orderDetail(state, order) {
  const courier = state.couriers.find((candidate) => candidate.name === order.courierName);
  const couriersConfigured = state.couriers.some((candidate) => candidate.isActive);

  // The same sentence the real reader builds, and it is here rather than in the fixture because the
  // *condition* is the thing under test: with no courier configured a delivery order genuinely cannot
  // ship, and what the shop is owed is a sentence saying what to go and do rather than a dead control.
  // A stub that always sent null would make that assertion unreachable.
  const blockedReason =
    order.status === 'Processing' && !order.isPickup && !couriersConfigured
      ? 'This order cannot be marked as shipped until a courier has been added. Add one under '
        + 'Settings \u2192 Couriers, with the tracking address its website uses.'
      : null;

  return {
    ...order,
    actions: {
      ...order.actions,
      canMarkShipped: order.status === 'Processing' && !order.isPickup && couriersConfigured,
      canEditDispatch:
        !order.isPickup && (order.status === 'Shipped' || order.status === 'Delivered')
        && couriersConfigured,

      // Doc 11 P10 flow A. **Recomputed here rather than taken from the fixture**, for the reason
      // `blockedReason` is: the *condition* is the thing under test, and the one assertion that matters is
      // that the panel is gone after a cancel. A stub that kept the fixture's flag would draw the form on
      // an order it had just closed, and the test would pass.
      //
      // Only the status is modelled, which is less than the real reader checks: doc 11 P10 step 2's other
      // three guards (stock Reserved, no capture lock, no settling payment) have no representation in
      // this stub's order shape, and issue #126 is exactly why that is acceptable here and not a gap in
      // coverage - a browser test cannot see them at all, so they are pinned by
      // `CheckoutEndpointTests` and `OrderCancellationTests` against real SQL Server instead.
      canCancel: order.status === 'PendingPayment',
      couriersConfigured,
      blockedReason,
    },
    customer: {
      name: order.customerName,
      email: order.customerEmail,
      phone: '0771234567',
      hasAccount: false,
      isAnonymised: false,
    },
    delivery: order.isPickup
      ? {
          methodCode: 'PICKUP',
          methodName: order.deliveryMethodName,
          isPickup: true,
          pickupLocationName: 'The counter',
          districtName: 'Colombo',
          zone: 'Colombo',
          estimatedDaysMin: 1,
          estimatedDaysMax: 2,
          recipientName: null,
          recipientPhone: null,
          line1: null,
          line2: null,
          city: null,
          postalCode: null,
          landmark: null,
        }
      : {
          methodCode: 'STANDARD',
          methodName: order.deliveryMethodName,
          isPickup: false,
          pickupLocationName: null,
          districtName: 'Colombo',
          zone: 'Colombo',
          estimatedDaysMin: 2,
          estimatedDaysMax: 4,
          recipientName: 'Nimali Perera',
          recipientPhone: '0771234567',
          line1: '14 Galle Road',
          line2: 'Flat 3',
          city: 'Kollupitiya',
          postalCode: '00300',
          landmark: 'Opposite the temple',
        },
    money: {
      currency: 'LKR',
      itemsOriginalTotal: 17990,
      itemsListTotal: 16490,
      itemsSubtotal: 15096.6,
      markdownDiscountTotal: 1500,
      campaignDiscountTotal: 1393.4,
      promoCodeText: null,
      promoDiscountAmount: 0,
      deliveryFeeAmount: 500,
      deliveryDiscountAmount: 0,
      grandTotal: order.grandTotal,
      refundedAmount: 0,
    },
    lines: [
      {
        lineNumber: 1,
        sku: 'SKU-470',
        productName: 'Slim jeans',
        productCode: 'PRD-470',
        colourName: 'Indigo',
        sizeLabel: '32',
        quantity: 1,
        unitOriginalPrice: 11990,
        unitListPrice: 10990,
        unitPrice: 9990,
        lineSubtotal: 9990,
        promoDiscountAllocated: 0,
        lineTotal: 9990,
        refundedQuantity: 0,
      },
      {
        lineNumber: 2,
        sku: 'SKU-471',
        productName: 'Denim jacket',
        productCode: 'PRD-471',
        colourName: 'Washed blue',
        sizeLabel: 'M',
        quantity: 1,
        unitOriginalPrice: 6000,
        unitListPrice: 5500,
        unitPrice: 5106.6,
        lineSubtotal: 5106.6,
        promoDiscountAllocated: 0,
        lineTotal: 5106.6,
        refundedQuantity: 0,
      },
    ],
    payments: [
      {
        providerCode: 'BankTransfer',
        status: order.status === 'Expired' ? 'Expired' : 'Succeeded',
        isOrderSettlement: order.status !== 'Expired',
        amount: order.grandTotal,
        currency: 'LKR',

        // Null, as every payment this build can create is: a bank transfer is taken in rupees and
        // BankTransferProvider is the only provider. The screen renders these only when set.
        providerAmount: null,
        providerCurrency: null,
        payerReference: 'CBC-778812',
        slipPublicId: '66666666-6666-4666-8666-666666666666',
        slipContentType: 'image/jpeg',
        slipFileName: 'slip.jpg',
        failureCode: null,
        failureMessage: null,
        approvedByUserEmail: order.status === 'Expired' ? null : 'staff@example.com',
        approvedAtUtc: order.status === 'Expired' ? null : '2026-10-05T09:00:00Z',
        approvalNote: order.status === 'Expired' ? null : 'Credit on the statement at 14:02.',
        createdAtUtc: '2026-10-05T04:31:00Z',
        completedAtUtc: order.status === 'Expired' ? '2026-10-07T04:30:00Z' : '2026-10-05T09:00:00Z',
      },
    ],
    refunds: [],
    history: [
      {
        fromStatus: null,
        toStatus: 'PendingPayment',
        fromStatusLabel: null,
        toStatusLabel: 'Awaiting payment',
        source: 'Customer',
        changedByUserEmail: null,
        note: 'Order placed',
        occurredAtUtc: '2026-10-05T04:30:00Z',
      },
      order.status === 'Expired'
        ? {
            fromStatus: 'PendingPayment',
            toStatus: 'Expired',
            fromStatusLabel: 'Awaiting payment',
            toStatusLabel: 'Expired',
            source: 'SystemJob',
            changedByUserEmail: null,
            note: 'Payment deadline passed',
            occurredAtUtc: '2026-10-07T04:30:00Z',
          }
        : {
            fromStatus: 'PendingPayment',
            toStatus: 'Paid',
            fromStatusLabel: 'Awaiting payment',
            toStatusLabel: 'Paid',
            source: 'Staff',
            changedByUserEmail: 'staff@example.com',
            note: 'Bank transfer approved',
            occurredAtUtc: '2026-10-05T09:00:00Z',
          },
    ],
    dispatch:
      order.trackingNumber === null
        ? null
        : {
            courierId: courier ? courier.id : null,
            courierName: order.courierName,
            trackingNumber: order.trackingNumber,
            trackingUrl:
              courier && courier.trackingUrlTemplate
                ? courier.trackingUrlTemplate.replace('{trackingNumber}', order.trackingNumber)
                : null,
            shippedAtUtc: order.shippedAtUtc,
          },
  };
}

/**
 * The field errors a courier write would answer, or null.
 *
 * **It refuses a body that leaves a field out**, exactly as the real endpoint does: a save that did not
 * mention a field has silently cleared it four times in this project, so an absent field is a 400 naming
 * it rather than a null. A stub that filled in the gaps would let the screen regress to a partial PUT
 * with every browser test still passing.
 */
/**
 * The promo-code validator (ADM-13), refusing what `PromoCodeWriteHandlers` refuses.
 *
 * **A missing key is a field error, not a default.** "A field this body does not carry is left as it is"
 * and "a field it carries as null is cleared" are different instructions, and the real endpoint refuses
 * the ambiguity rather than resolving it — here the figure that would be dropped is a usage limit, which
 * is money.
 */
function promoCodeErrors(state, body, exceptId) {
  const errors = {};

  for (const field of [
    'code',
    'description',
    'discountType',
    'discountValue',
    'maxDiscountAmount',
    'minSpendAmount',
    'startsAt',
    'endsAt',
    'excludeSaleItems',
    'totalUsageLimit',
    'perCustomerLimit',
    'isActive',
  ]) {
    if (!Object.hasOwn(body, field)) {
      errors[field] = [`Send ${field}.`];
    }
  }

  if (Object.keys(errors).length > 0) {
    return errors;
  }

  const code = typeof body.code === 'string' ? body.code.trim().toUpperCase() : '';

  if (code === '') {
    errors.code = ['Enter the code shoppers will type.'];
  } else if (!/^[A-Z0-9_-]{1,40}$/.test(code)) {
    errors.code = [
      'A code may be up to 40 letters, numbers, hyphens or underscores, with no spaces.',
    ];
  } else if (
    state.promoCodes.some((candidate) => candidate.code === code && candidate.id !== exceptId)
  ) {
    errors.code = ['A promo code with that code already exists.'];
  }

  if (!['Percent', 'FixedAmount', 'FreeDelivery'].includes(body.discountType)) {
    errors.discountType = ['Choose a percentage, a fixed amount off, or free delivery.'];
  }

  if (body.discountType === 'Percent') {
    if (typeof body.discountValue !== 'number' || body.discountValue <= 0 || body.discountValue > 100) {
      errors.discountValue = ['Enter a percentage above 0 and up to 100.'];
    }
  } else if (body.discountType === 'FixedAmount') {
    if (typeof body.discountValue !== 'number' || body.discountValue <= 0) {
      errors.discountValue = ['Enter an amount above zero.'];
    }
  } else if (body.discountValue !== null) {
    errors.discountValue = [
      'A free-delivery code has no amount. Clear the value or choose another type.',
    ];
  }

  if (body.discountType !== 'Percent' && body.maxDiscountAmount !== null) {
    errors.maxDiscountAmount = ['A maximum discount only applies to a percentage code.'];
  }

  if (typeof body.startsAt !== 'string' || body.startsAt.trim() === '') {
    errors.startsAt = ['Choose when the code starts working.'];
  } else if (/[Zz]/.test(body.startsAt) || /T.*[+-]/.test(body.startsAt)) {
    // The Colombo contract: the shop's own time, with no offset. The real endpoint refuses an offset
    // rather than guessing, because guessing wrong moves a code's window by five and a half hours.
    errors.startsAt = [
      "Send startsAt as a date and time in the shop's own time with no offset, like 2026-10-12T23:59.",
    ];
  } else if (typeof body.endsAt === 'string' && body.endsAt <= body.startsAt) {
    errors.endsAt = ['The end must be after the start.'];
  }

  if (
    typeof body.perCustomerLimit === 'number' &&
    typeof body.totalUsageLimit === 'number' &&
    body.perCustomerLimit > body.totalUsageLimit
  ) {
    errors.perCustomerLimit = ['A customer cannot be allowed more uses than the code has in total.'];
  }

  return Object.keys(errors).length > 0 ? errors : null;
}

/** One row, in the shape `AdminPromoCodeDto` has — field for field (#126). */
function buildPromoCode(state, body, id, usageCount) {
  const startsAt = withSeconds(body.startsAt);
  const endsAt = body.endsAt === null ? null : withSeconds(body.endsAt);

  return {
    id,
    code: String(body.code).trim().toUpperCase(),
    description: body.description === null ? null : String(body.description).trim(),
    discountType: body.discountType,
    discountValue: body.discountValue,
    maxDiscountAmount: body.maxDiscountAmount,
    minSpendAmount: body.minSpendAmount,
    startsAt,
    endsAt,
    excludeSaleItems: body.excludeSaleItems === true,
    totalUsageLimit: body.totalUsageLimit,
    perCustomerLimit: body.perCustomerLimit,
    usageCount,
    isActive: body.isActive === true,
    status: promoStatus(body, startsAt, endsAt),
    rowVersion: nextRowVersion(),
  };
}

/**
 * Doc 11 P3's derived status, against the stub's own clock in the shop's time.
 *
 * Inactive wins over the dates, which is the rule `PromoCodeSchedule.StatusOf` applies — a staff member
 * asking why their code is not working is owed "we have turned this off" before "the dates".
 */
function promoStatus(body, startsAt, endsAt) {
  if (body.isActive !== true) {
    return 'Inactive';
  }

  // The shop is UTC+05:30 and has no daylight saving (doc 08 §1.1's collation note aside, this is
  // `ColomboTime.Offset`), so "now" in the shop's time is now plus five and a half hours.
  const nowLocal = new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 19);

  if (startsAt > nowLocal) {
    return 'Scheduled';
  }

  return endsAt !== null && endsAt <= nowLocal ? 'Ended' : 'Live';
}

/** Doc 08 §3.6 DB19's warning, and the one for a window that is already over. */
function promoWarnings(body, promoCode) {
  const warnings = [];

  if (typeof body.totalUsageLimit === 'number' && promoCode.usageCount > body.totalUsageLimit) {
    warnings.push(
      `This code has already been used ${promoCode.usageCount} times, which is above the new total ` +
        `limit of ${body.totalUsageLimit}. No new customer can use it; orders already placed keep ` +
        'their use.',
    );
  }

  if (promoCode.status === 'Ended') {
    warnings.push("This code's end date has already passed, so no shopper can use it.");
  }

  return warnings;
}

/**
 * The field errors a sale-campaign write would answer, or null (ADM-14).
 *
 * **It refuses what `SaleCampaignWriteHandlers` refuses**, which is the rule this whole stub is written
 * to (#126): every key must be *present* including `targets`, the percentage ceiling is 90 and not 100,
 * `endsAt` is required and must be after `startsAt`, a date carries no offset, a target names exactly one
 * of the three ids, and the same target twice is refused rather than tidied away.
 */
function saleCampaignErrors(state, body) {
  const errors = {};

  for (const field of [
    'name',
    'description',
    'discountType',
    'discountValue',
    'startsAt',
    'endsAt',
    'isActive',
    'bannerAssetId',
    'targets',
  ]) {
    if (!Object.hasOwn(body, field)) {
      errors[field] = [`Send ${field}.`];
    }
  }

  if (Object.keys(errors).length > 0) {
    return errors;
  }

  const name = typeof body.name === 'string' ? body.name.trim() : '';

  if (name === '') {
    errors.name = ['Give the campaign a name.'];
  } else if (name.length > 100) {
    // 100, which is `SaleCampaign.MaxNameLength` and doc 08 section 3.6's `nvarchar(100)`. It read 120
    // until W3-05c's tester pass, and the form's own `maxlength` read 120 with it — so a 110-character
    // name was accepted by the screen and by this stub and refused only by the real API, which is the
    // #126 shape in miniature: a stub that is more permissive than the validator hides a screen defect
    // instead of catching it.
    errors.name = ['Keep the name to 100 characters or fewer.'];
  }

  if (!['Percent', 'FixedAmount'].includes(body.discountType)) {
    errors.discountType = ['Choose a percentage off or a fixed amount off.'];
  } else if (body.discountType === 'Percent') {
    // 90, not 100: doc 08 section 3.6's own ceiling for a campaign, and one of the two places this form
    // differs from the promo-code form.
    if (typeof body.discountValue !== 'number' || body.discountValue <= 0 || body.discountValue > 90) {
      errors.discountValue = ['Enter a percentage above 0 and up to 90.'];
    }
  } else if (typeof body.discountValue !== 'number' || body.discountValue <= 0) {
    errors.discountValue = ['Enter an amount above zero.'];
  }

  if (typeof body.startsAt !== 'string' || body.startsAt.trim() === '') {
    errors.startsAt = ['Choose when the campaign starts.'];
  } else if (/[Zz]/.test(body.startsAt) || /T.*[+-]/.test(body.startsAt)) {
    errors.startsAt = [
      "Send startsAt as a date and time in the shop's own time with no offset, like 2026-10-19T00:00.",
    ];
  }

  if (typeof body.endsAt !== 'string' || body.endsAt.trim() === '') {
    // Required, unlike a promo code's end: doc 02 line 119 is "for a date range".
    errors.endsAt = ['Choose when the campaign ends.'];
  } else if (typeof body.startsAt === 'string' && body.endsAt <= body.startsAt) {
    errors.endsAt = ['The end must be after the start.'];
  }

  if (!Array.isArray(body.targets) || body.targets.length === 0) {
    errors.targets = [
      'Choose at least one department, category or product for the campaign to apply to.',
    ];

    return errors;
  }

  const seen = new Set();

  body.targets.forEach((target, index) => {
    const field = `targets[${index}]`;

    if (!['Department', 'Category', 'Product'].includes(target?.targetType)) {
      errors[field] = ['Choose a department, a category or a product.'];
      return;
    }

    const id =
      target.targetType === 'Department'
        ? target.departmentId
        : target.targetType === 'Category'
          ? target.categoryId
          : target.productId;

    if (typeof id !== 'number' || id <= 0) {
      errors[field] = ['Choose what the campaign applies to.'];
      return;
    }

    const key = `${target.targetType}:${id}`;

    if (seen.has(key)) {
      errors[field] = ['This target is already on the campaign.'];
      return;
    }

    seen.add(key);

    if (target.targetType === 'Department' && !state.departments.some((own) => own.id === id)) {
      errors[field] = [`No department has the id ${id}.`];
    }

    if (target.targetType === 'Category' && categoryNode(state, id) === undefined) {
      errors[field] = [`No category has the id ${id}.`];
    }
  });

  return Object.keys(errors).length > 0 ? errors : null;
}

/** One row, in the shape `AdminSaleCampaignDto` has - field for field (#126). */
function buildCampaign(state, body, id) {
  const startsAt = withSeconds(body.startsAt);
  const endsAt = withSeconds(body.endsAt);

  return {
    id,
    name: String(body.name).trim(),
    description: body.description === null ? null : String(body.description).trim(),
    discountType: body.discountType,
    discountValue: body.discountValue,
    startsAt,
    endsAt,
    isActive: body.isActive === true,
    bannerAssetId: body.bannerAssetId,
    targets: body.targets.map((target) => ({
      targetType: target.targetType,
      departmentId: target.departmentId ?? null,
      categoryId: target.categoryId ?? null,
      productId: target.productId ?? null,

      // Forced true for a department and a product, as the real write path does: neither has a subtree
      // the flag could mean anything about.
      includeSubcategories:
        target.targetType === 'Category' ? target.includeSubcategories === true : true,
      name: campaignTargetName(state, target),
    })),
    status: campaignStatus(body, startsAt, endsAt),
    rowVersion: nextRowVersion(),
  };
}

/**
 * What the API calls a target: `Women`, `Women > Shirts & Blouses` (with the real separator), a product's
 * name, or `(removed from the catalogue)` when the row it names has gone.
 */
function campaignTargetName(state, target) {
  if (target.targetType === 'Department') {
    const found = state.departments.find((own) => own.id === target.departmentId);

    return found === undefined ? '(removed from the catalogue)' : found.name;
  }

  if (target.targetType === 'Category') {
    const node = categoryNode(state, target.categoryId);

    if (node === undefined) {
      return '(removed from the catalogue)';
    }

    const department = state.departments.find((own) =>
      flatten(state.categories[own.id] ?? []).some((candidate) => candidate.id === node.id),
    );

    return department === undefined ? node.name : `${department.name} \u203a ${node.name}`;
  }

  // No product is reachable through this stub's catalogue state, so a product target can only come from
  // a test that sent one deliberately. Named rather than left blank, which is what the API does.
  return '(removed from the catalogue)';
}

function categoryNode(state, categoryId) {
  return Object.values(state.categories)
    .flatMap((roots) => flatten(roots))
    .find((candidate) => candidate.id === categoryId);
}

/**
 * Doc 11 line 474's derived status, against the stub's own clock in the shop's time.
 *
 * Inactive wins over the dates, which is the rule `CampaignWindow.StatusOf` applies - it is
 * `PromoCodeSchedule.StatusOf`, so a campaign and a promo code cannot disagree about a window.
 */
function campaignStatus(body, startsAt, endsAt) {
  if (body.isActive !== true) {
    return 'Inactive';
  }

  const nowLocal = new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 19);

  if (startsAt > nowLocal) {
    return 'Scheduled';
  }

  return endsAt <= nowLocal ? 'Ended' : 'Live';
}

/**
 * How many products a campaign's targets reach, by this stub's own arithmetic over the seeded
 * `productCount`s.
 *
 * **It is not the real recompute and does not try to be.** The real one is a T-SQL `UPDATE` over the
 * catalogue, and #126 is the standing note that nothing in a browser test can reach it. What the screen's
 * tests depend on is only whether the figure is zero, because zero is what produces the warning that
 * stops a staff member creating a campaign that silently discounts nothing. The stub's `Traditional wear`
 * has no products, so that path is reachable here.
 */
function campaignProductsRepriced(state, campaign) {
  let total = 0;

  for (const target of campaign.targets) {
    if (target.targetType === 'Department') {
      total += flatten(state.categories[target.departmentId] ?? []).reduce(
        (sum, node) => sum + node.productCount,
        0,
      );
      continue;
    }

    if (target.targetType === 'Category') {
      const node = categoryNode(state, target.categoryId);

      if (node === undefined) {
        continue;
      }

      total += target.includeSubcategories
        ? flatten([node]).reduce((sum, candidate) => sum + candidate.productCount, 0)
        : node.productCount;
    }
  }

  return total;
}

/** The sentences `SaleCampaignWriteHandlers.Warnings` answers on a save that worked. */
function campaignWarnings(campaign, productsRepriced) {
  const warnings = [];

  if (campaign.status === 'Ended') {
    warnings.push("This campaign's end date has already passed, so it is not discounting anything.");
  }

  if (productsRepriced === 0) {
    warnings.push(
      "This campaign's targets do not reach any product, so no price has changed. Check the " +
        'departments, categories and products it applies to.',
    );
  }

  if (campaign.discountType === 'Percent' && campaign.discountValue === 90) {
    warnings.push(
      '90% is the largest discount a campaign may take, and it leaves a tenth of each price.',
    );
  }

  return warnings;
}

/** `yyyy-MM-ddTHH:mm` from the form becomes `yyyy-MM-ddTHH:mm:ss`, which is what the API answers with. */
function withSeconds(value) {
  const text = String(value).trim();

  return text.length === 16 ? `${text}:00` : text;
}


function courierErrors(state, body, selfId) {
  const errors = {};

  for (const field of ['name', 'trackingUrlTemplate', 'phone', 'isActive', 'sortOrder']) {
    if (!Object.hasOwn(body, field)) {
      errors[field] = [`Send ${field}.`];
    }
  }

  if (Object.keys(errors).length > 0) {
    return errors;
  }

  const name = String(body.name ?? '').trim();

  if (name.length === 0) {
    errors.name = ['Give the courier a name.'];
  } else if (
    state.couriers.some((candidate) => candidate.id !== selfId && candidate.name === name)
  ) {
    errors.name = [`There is already a courier called \u201c${name}\u201d.`];
  }

  const template = body.trackingUrlTemplate === null ? null : String(body.trackingUrlTemplate).trim();

  if (template !== null && template.length > 0) {
    if (!template.includes('{trackingNumber}')) {
      errors.trackingUrlTemplate = [
        'The tracking address must contain {trackingNumber} where the tracking number goes.',
      ];
    } else if (!template.startsWith('https://')) {
      errors.trackingUrlTemplate = ['The tracking address must start with https://.'];
    }
  }

  return Object.keys(errors).length > 0 ? errors : null;
}

/** The invoice payload for one order (ADM-09, doc 11 P9 step 3). */
function orderInvoice(state, order) {
  const detail = orderDetail(state, order);

  return {
    invoiceNumber: order.invoiceNumber,
    issuedAtUtc: order.paidAtUtc,
    orderNumber: order.orderNumber,
    orderPublicId: order.orderPublicId,
    placedAtUtc: order.placedAtUtc,

    // **Every field null and the address empty, which is how the real seeder leaves them.** The client
    // has not given us their registered name or address, and an invoice is a legal document — so the
    // screen's own "this will print without them" warning is the state a browser test has to be able to
    // see. `/__test__/store-legal-details` fills them in for the other half.
    seller: state.storeLegalDetails,
    customer: detail.customer,
    delivery: detail.delivery,
    money: detail.money,
    lines: detail.lines,
    paymentMethodLabel: 'Bank transfer',
    paymentReference: 'CBC-778812',
  };
}

/**
 * `ops.AdminAlerts` as `GET /api/admin/alerts` sends it (doc 08 §3.8 N2, doc 11 §7 G-37, W4-02a).
 *
 * **Field for field from `AdminAlertDto`**, and `AdminAlertShapeTests` on the API side reads this file to
 * keep it that way. Issue #126: Playwright never starts the .NET API, so a field the real payload carries
 * and this fixture omits is a field no browser test can miss — a dropped field once left all 860 tests
 * green.
 *
 * **Every field carries a real value.** W1-12's blocker survived 1,571 passing tests because the fixtures
 * set the columns they cleared to `null`, so `null → null` round-tripped vacuously.
 */
function deadEmailAlert(alertId, overrides = {}) {
  return {
    // Written as an explicit pair rather than with object shorthand: `AdminAlertShapeTests` reads this
    // file looking for every field the real DTO sends, spelled `name:`, and a shorthand `id` would make
    // the one field most likely to be dropped the one field the check cannot see.
    id: alertId,
    alertType: 'EmailDeliveryFailed',
    severity: 2,
    title: `Email not delivered: BankTransferInstructions for order SD-2026-00012${alertId} (outbox row 482${alertId})`,
    createdAtUtc: '2026-10-09T03:11:00.000Z',
    relatedEntityType: 'EmailOutbox',
    relatedEntityId: `482${alertId}`,
    orderPublicId: '44444444-4444-4444-8444-444444444444',
    orderNumber: `SD-2026-00012${alertId}`,
    templateCode: 'BankTransferInstructions',
    ...overrides,
  };
}

/**
 * The alert sets a test can ask for.
 *
 * **`none` is the default, and that is deliberate.** The banner lives in the admin shell (W4-02a), so an
 * alert seeded by default would appear on every admin screen and every one of this suite's existing
 * layout, tap-size and no-horizontal-scroll sweeps would silently be measuring a different page. A test
 * that wants the banner asks for it.
 */
function alertsFor(name) {
  switch (name) {
    case 'dead':
      return [deadEmailAlert(1)];
    case 'dead-no-order':
      return [deadEmailAlert(1, { orderPublicId: null, orderNumber: null, templateCode: 'PasswordReset' })];
    case 'dead-staff-invite':
      return [deadEmailAlert(1, { orderPublicId: null, orderNumber: null, templateCode: 'StaffInvite' })];
    // The order the message names is not in the database: the raiser could not resolve the public id, so
    // the number is null and the public id is not. The link must not be offered for it. The title is the
    // one `DeadEmailAlert.Title` really writes for this case, so the screen is measured against the
    // payload it will actually get.
    case 'dead-order-gone':
      return [
        deadEmailAlert(1, {
          orderNumber: null,
          title:
            'Email not delivered: BankTransferInstructions for an order that can no longer be found'
            + ' (outbox row 4821)',
        }),
      ];
    case 'many':
      return Array.from({ length: 12 }, (_, index) => deadEmailAlert(index + 1));
    default:
      return [];
  }
}

function freshState() {
  return {
    departments: [
      department(1, 'Women', 'women', 0, true, 4, 24),
      department(2, 'Men', 'men', 1, true, 2, 16),
      department(3, 'Kids', 'kids', 2, false, 1, 8),
    ],
    categories: {
      1: [
        category(10, 'Dresses', 'dresses', null, 1, '/10/', 0, {
          productCount: 6,
          primaryProductCount: 6,
          children: [
            category(11, 'Midi dresses', 'midi-dresses', 10, 2, '/10/11/', 0, {
              productCount: 3,
              primaryProductCount: 3,
            }),
            category(12, 'Maxi dresses', 'maxi-dresses', 10, 2, '/10/12/', 1, {
              productCount: 2,
              primaryProductCount: 2,
            }),
          ],
        }),
        category(20, 'Jeans', 'jeans', null, 1, '/20/', 1, {
          productCount: 5,
          primaryProductCount: 5,
        }),
        category(30, 'Traditional wear', 'traditional-wear', null, 1, '/30/', 2, {
          isActive: false,
        }),
        category(40, 'Knitwear', 'knitwear', null, 1, '/40/', 3),
      ],
      2: [
        category(50, 'Shirts', 'shirts', null, 1, '/50/', 0, { productCount: 7, primaryProductCount: 7 }),
        category(60, 'Shorts', 'shorts', null, 1, '/60/', 1, { productCount: 2, primaryProductCount: 2 }),
      ],
      3: [category(70, 'Girls', 'girls', null, 1, '/70/', 0, { productCount: 4, primaryProductCount: 4 })],
    },

    // ADM-22. Every value null, which is how the real seeder leaves them: the client has uploaded no logo
    // and sent no profile addresses. Any other starting state would hide the fallback the screen spends
    // most of its life in.
    settings: {
      'Store.LogoAssetId': null,
      'Contact.WhatsApp': null,
      'Social.FacebookUrl': null,
      'Social.InstagramUrl': null,
      'Social.TikTokUrl': null,

      // PAY-01 / doc 11 P7's preconditions. Seeded null, like the real seeder: the client fills them in.
      'Bank.BankName': null,
      'Bank.Branch': null,
      'Bank.AccountName': null,
      'Bank.AccountNumber': null,
      'Bank.Instructions': null,
    },
    logo: null,
    uploads: new Map(),

    // PAY-01 / ADM-09 (W3-03): doc 11 P7 step 4's queue. Two rows, deliberately different — one with a
    // reference and a slip and most of its window left, one overdue with neither — because the screen
    // renders a different action and a different pill for each, and a fixture of two identical rows would
    // exercise one of them twice.
    bankTransfers: [
      {
        orderPublicId: '44444444-4444-4444-8444-444444444444',
        orderNumber: 'SD-2026-000123',
        customerName: 'Nimali Perera',
        customerEmail: 'nimali@example.com',
        amountLkr: 15596.6,
        currency: 'LKR',
        payerReference: 'CBC-778812',
        slipAssetPublicId: '66666666-6666-4666-8666-666666666666',
        slipContentType: 'image/jpeg',
        placedAtUtc: '2026-10-05T04:30:00Z',
        paymentDueAtUtc: '2026-10-07T04:30:00Z',
        secondsUntilExpiry: 19 * 60 * 60,
      },
      {
        orderPublicId: '55555555-5555-4555-8555-555555555555',
        orderNumber: 'SD-2026-000124',
        customerName: 'Ruwan Jayasuriya',
        customerEmail: 'ruwan@example.com',
        amountLkr: 4290,
        currency: 'LKR',
        payerReference: null,
        slipAssetPublicId: null,
        slipContentType: null,
        placedAtUtc: '2026-10-04T06:00:00Z',
        paymentDueAtUtc: '2026-10-06T06:00:00Z',
        secondsUntilExpiry: -37 * 60,
      },
    ],

    // What the next decision answers. A fixture switch rather than a payload, so a test names a *state*
    // and this file owns what that state's bytes are — the rule the site-settings switch follows.
    bankTransferOutcome: 'ok',

    // ADM-09 (W3-04): the order list and the order detail. Three orders, deliberately different, because
    // the screen draws a different action and a different wording for each and a fixture of three
    // identical rows would exercise one of them three times:
    //
    //   * a Paid delivery order, which is what doc 11 P9 step 1's queue is made of;
    //   * a store-pickup order in Processing, whose wording and actions differ (G-18);
    //   * an Expired order, which is issue #124 — no screen could reach one before this one.
    orders: [
      adminOrder({
        orderPublicId: '44444444-4444-4444-8444-444444444444',
        orderNumber: 'SD-2026-000123',
        status: 'Paid',
        statusLabel: 'Paid',
        invoiceNumber: 'INV-2026-000045',
        paidAtUtc: '2026-10-05T09:00:00Z',
        actions: { canStartProcessing: true },
      }),
      adminOrder({
        orderPublicId: '55555555-5555-4555-8555-555555555555',
        orderNumber: 'SD-2026-000124',
        status: 'Processing',
        statusLabel: 'Processing',
        customerName: 'Ruwan Jayasuriya',
        customerEmail: 'ruwan@example.com',
        grandTotal: 4290,
        isPickup: true,
        deliveryMethodName: 'Collect in store',
        invoiceNumber: 'INV-2026-000046',
        paidAtUtc: '2026-10-04T10:00:00Z',
        actions: { canMarkReadyForCollection: true },
      }),
      adminOrder({
        orderPublicId: '66666666-6666-4666-8666-666666666666',
        orderNumber: 'SD-2026-000125',
        status: 'Expired',
        statusLabel: 'Expired',
        customerName: 'Sanduni Fernando',
        customerEmail: 'sanduni@example.com',
        grandTotal: 8750,
        actions: { canAcceptLatePayment: true },
      }),

      // W3-04b: an order awaiting payment, which is the only kind doc 11 P10 flow A can cancel. The
      // three above cannot be - which is the point of adding a fourth rather than changing one of them.
      adminOrder({
        orderPublicId: '77777777-7777-4777-8777-777777777777',
        orderNumber: 'SD-2026-000126',
        status: 'PendingPayment',
        statusLabel: 'Awaiting payment',
        customerName: 'Dilani Gunawardena',
        customerEmail: 'dilani@example.com',
        grandTotal: 6240,
        secondsUntilExpiry: 90_000,
        actions: { canCancel: true },
      }),
    ],

    // Seeded empty, exactly as the real `sales.Couriers` is (doc 08 §9): which carriers the client uses is
    // their information. A stub that invented one would make every "you cannot ship yet" assertion
    // unreachable, which is the one state a new installation is actually in.
    couriers: [],

    // ADM-13 (W3-05). Empty for the same reason: doc 02 line 167 replaces the demo's hard-coded codes
    // with admin-managed ones and the shop seeds none, so a stub code would make the "nothing a shopper
    // types will work" state unreachable — which is the state a new installation is in.
    promoCodes: [],

    // ADM-14 (W3-05c). Empty for the same reason: the shop seeds no campaign, which is the state a new
    // installation is in and the state the screen's "nothing is discounted by one" sentence is about.
    saleCampaigns: [],

    // What the next transition answers. `wrong-status` makes the 409 the real API answers when somebody
    // else moved the order first, which is how the screen's refusal branch is reachable at all.
    transitionOutcome: 'ok',

    // The four Store.* legal keys an invoice prints, unset — which is how the real seeder leaves them,
    // because they are the client's. `isComplete` false is what makes the screen warn before printing.
    storeLegalDetails: {
      legalName: null,
      addressLines: [],
      brNumber: null,
      vatNumber: null,
      isComplete: false,
    },

    // ADM-10 (W1-03). Its own module, because this file is already a thousand lines about ADM-11.
    ...freshProductState(),

    // ADM-15 / ADM-21 (W3-07): customers and newsletter subscribers, both empty until a test asks.
    ...freshCustomerState(),

    // ADM-04–08 / ADM-19 (W4-02): the dashboard and the reports, empty (real zeros) until a test asks.
    ...freshReportingState(),

    // ADM-12 (#190): the inventory screen, empty until a test asks.
    ...freshInventoryState(),

    /** Open admin alerts (W4-02a). Empty unless a test asks for some — see `alertsFor`. */
    alerts: [],
  };
}

/**
 * The settings groups, built from the state's values (ADM-22).
 *
 * <b>The row version is per *save unit*, not per group</b>, exactly as the real read model publishes it:
 * `Contact` and `Social` are one form with one Save button, so their token covers both groups' rows. The first
 * version of the real API got this wrong and answered 409 to every valid save; a stub that published a
 * per-group token would let the screen regress to it with every browser test still passing.
 */
/**
 * The one catalogued key the real seeder writes a value for (doc 08 section 3.8's 7). Seeded here too, so
 * the screen opens showing what production shows rather than an empty box.
 */
const SEEDED_SETTINGS = { 'Orders.AutoDeliverAfterDays': '7' };

function settingsPayload(state) {
  for (const [key, value] of Object.entries(SEEDED_SETTINGS)) {
    if (!Object.hasOwn(state.settings, key)) {
      state.settings[key] = value;
    }
  }

  const groups = [
    // W3-04 added the four legal keys to this group, and `Orders` below. They are **one save unit** with
    // `Orders` (SettingCatalog.SaveUnits), so the logo form and the invoice form are handed the same token
    // — which is why `unitFor` spans both groups rather than one.
    {
      groupKey: 'Store',
      keys: [
        'Store.LogoAssetId',
        'Store.LegalName',
        'Store.Address',
        'Store.BRNumber',
        'Store.VatNumber',
      ],
      types: { 'Store.LogoAssetId': 'MediaAssetRef' },
    },
    { groupKey: 'Orders', keys: ['Orders.AutoDeliverAfterDays'], types: {} },
    { groupKey: 'Contact', keys: ['Contact.WhatsApp'], types: {} },
    {
      groupKey: 'Social',
      keys: ['Social.FacebookUrl', 'Social.InstagramUrl', 'Social.TikTokUrl'],
      types: {},
    },

    // PAY-01. Its own group and its own save unit, because it is the one group guarded by a different
    // permission (Settings.Payments) — see SettingCatalog.SaveUnits.
    {
      groupKey: 'Bank',
      keys: [
        'Bank.BankName',
        'Bank.Branch',
        'Bank.AccountName',
        'Bank.AccountNumber',
        'Bank.Instructions',
      ],
      types: {},
      permission: 'Settings.Payments',
    },
  ];

  const bankKeys = [
    'Bank.BankName',
    'Bank.Branch',
    'Bank.AccountName',
    'Bank.AccountNumber',
    'Bank.Instructions',
  ];

  const storeUnit = [
    'Store.LogoAssetId',
    'Store.LegalName',
    'Store.Address',
    'Store.BRNumber',
    'Store.VatNumber',
    'Orders.AutoDeliverAfterDays',
  ];

  const unitFor = (groupKey) => {
    if (groupKey === 'Store' || groupKey === 'Orders') {
      return storeUnit;
    }

    if (groupKey === 'Bank') {
      return bankKeys;
    }

    return ['Contact.WhatsApp', 'Social.FacebookUrl', 'Social.InstagramUrl', 'Social.TikTokUrl'];
  };

  const tokenFor = (groupKey) =>
    Buffer.from(unitFor(groupKey).map((key) => `${key}=${state.settings[key] ?? ''}`).join('|')).toString('base64');

  return {
    groups: groups.map((group) => ({
      groupKey: group.groupKey,
      rowVersion: tokenFor(group.groupKey),
      settings: group.keys.map((key) => ({
        key,
        valueType: group.types[key] ?? 'String',
        value: state.settings[key],
        isSecret: false,
        hasValue: Boolean(state.settings[key]),
        requiredPermission: group.permission ?? 'Settings.Edit',
        description: `What ${key} is for.`,
        updatedAtUtc: null,
      })),
    })),
    logo: state.logo,
  };
}

/** The token a save of `groupKey` must carry, so a stale one is genuinely refused. */
function settingsToken(state, groupKey) {
  return settingsPayload(state).groups.find((group) => group.groupKey === groupKey).rowVersion;
}

/**
 * The same absolute-URL rule the API applies (`SettingFieldRules.IsAcceptableProfileUrl`).
 *
 * The stub enforces it because without it the screen could stop showing field errors and every browser test
 * would still pass — the shape of gap that let W1-12's antiforgery header go unchecked for 246 tests.
 */
function acceptableProfileUrl(value) {
  if (typeof value !== 'string') {
    return false;
  }

  const candidate = value.trim();
  if (candidate.length === 0 || /[\\\u0000-\u001f\s]/u.test(candidate)) {
    return false;
  }

  try {
    const parsed = new URL(candidate);
    return (
      (parsed.protocol === 'https:' || parsed.protocol === 'http:') &&
      parsed.hostname.length > 0 &&
      parsed.username.length === 0
    );
  } catch {
    return false;
  }
}

function acceptableWhatsApp(value) {
  return typeof value === 'string' && /^[1-9][0-9]{6,14}$/.test(value.trim());
}

/**
 * One state per browser context, keyed by the `e2e_state` cookie each test sets to its worker index.
 *
 * <b>Why not one shared state.</b> Playwright runs this file's tests in parallel across workers, and the
 * tests reorder and remove things; with a single mutable state one worker's `__reset` wiped another
 * worker's tree mid-test and a click timed out waiting for a row that had just been recreated. The stub is
 * one process serving several browsers, so the state belongs to the browser rather than to the process.
 */
const states = new Map();

function stateKey(request) {
  const cookies = request.headers.cookie ?? '';
  const match = cookies.match(/(?:^|;\s*)e2e_state=([^;]+)/);
  return match ? match[1] : 'shared';
}

function stateFor(request) {
  const key = stateKey(request);
  if (!states.has(key)) {
    states.set(key, freshState());
  }

  return states.get(key);
}

function json(response, status, body) {
  const payload = JSON.stringify(body);
  response.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'Content-Length': Buffer.byteLength(payload),
  });
  response.end(payload);
}

function problem(response, status, title, detail, code, extra = {}) {
  json(response, status, {
    type: 'about:blank',
    title,
    status,
    detail,
    code,
    ...extra,
  });
}

function noContent(response) {
  response.writeHead(204, { 'Cache-Control': 'no-store' });
  response.end();
}

/** A 1x1 WebP, so the logo preview loads a real image rather than a data URL. */
const LOGO_PIXEL = Buffer.from('UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==', 'base64');

/** Reads a multipart body to the end and hands back its raw text. */
function drain(request) {
  return new Promise((done) => {
    let raw = '';
    request.on('data', (chunk) => {
      raw += chunk;
    });
    request.on('end', () => done(raw));
  });
}

/**
 * The `altText` field of a multipart upload, or null when it was not sent.
 *
 * Echoed rather than invented, because what the library stores decides whether doc 11 §7 G-01's
 * third rule can block a publish. A stub that answered with an alt text of its own made every
 * uploaded photograph look described and quietly disabled the rule in every browser test.
 */
function uploadedAltText(body) {
  const at = body.indexOf('name="altText"');

  if (at < 0) {
    return null;
  }

  // A multipart part's value starts after the blank line that ends its own headers and runs to the
  // next boundary. Parsed by hand because the stub has no dependencies, and only loosely because
  // all it has to tell apart is "a description arrived" from "none did".
  const blank = '\r\n\r\n';
  const headersEnd = body.indexOf(blank, at);

  if (headersEnd < 0) {
    return null;
  }

  const described = body.slice(headersEnd + blank.length).split('\r')[0].trim();

  return described.length > 0 ? described : null;
}

function readBody(request) {
  return new Promise((done) => {
    let raw = '';
    request.on('data', (chunk) => {
      raw += chunk;
    });
    request.on('end', () => {
      try {
        done(raw.length === 0 ? null : JSON.parse(raw));
      } catch {
        done(undefined);
      }
    });
  });
}

/** Every category of a department, flattened depth-first, as the screen sees them. */
function flatten(nodes) {
  return nodes.flatMap((node) => [node, ...flatten(node.children)]);
}

function findSiblings(state, departmentId, parentCategoryId) {
  const roots = state.categories[departmentId] ?? [];
  if (parentCategoryId === null || parentCategoryId === undefined) {
    return roots;
  }

  const parent = flatten(roots).find((node) => node.id === parentCategoryId);
  return parent ? parent.children : null;
}

/**
 * The real API's optimistic-concurrency check: the caller's token has to be the row's current one, and a
 * missing one is refused too (a client that sent none cannot have seen the row). Returns true when the
 * request may go ahead; otherwise it has already answered.
 */
function rowVersionOk(response, request, body, row) {
  const supplied = suppliedRowVersion(request, body);

  if (supplied === row.rowVersion) {
    return true;
  }

  problem(
    response,
    409,
    'This change cannot be applied',
    supplied === null
      ? 'This request did not carry the current version of the row, so it cannot be applied safely.'
      : 'Somebody else changed this while you were editing it. Reload the page to see their change.',
    'stale_row_version',
  );

  return false;
}

/** The server's own guard: the list sent must be exactly the sibling set, or nothing is applied. */
function applyOrder(response, siblings, requested) {
  const actual = siblings.map((node) => node.id);
  const missing = actual.filter((id) => !requested.includes(id));
  const unknown = requested.filter((id) => !actual.includes(id));

  if (missing.length > 0 || unknown.length > 0) {
    problem(
      response,
      409,
      'This change cannot be applied',
      'This order was made from an older version of the list. Reload the page and put them in order again.',
      'stale_order',
      { missingIds: missing, unknownIds: unknown },
    );
    return false;
  }

  const byId = new Map(siblings.map((node) => [node.id, node]));
  siblings.length = 0;
  requested.forEach((id, index) => {
    const node = byId.get(id);
    node.sortOrder = index;
    siblings.push(node);
  });

  return true;
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url, `http://localhost:${PORT}`);
  const path = url.pathname;

  // ---- test control -------------------------------------------------------------------------

  // Which worktree's build this process is serving. A derived port makes a collision unlikely, not
  // impossible, and `reuseExistingServer` means a collision is answered by another worktree's server
  // serving another worktree's `dist/admin` against these tests — which is how a run of 113 failures
  // looked the first time. The suite asks before it trusts a single answer.
  if (path === '/__worktree') {
    // Derived from the working directory, which is `src/frontend`, so it is the same two levels the
    // Playwright config computes the port from — the two values have to agree or the check means nothing.
    return json(response, 200, { root: resolve(process.cwd(), '..', '..') });
  }

  if (path === '/__reset') {
    states.set(stateKey(request), freshState());
    return json(response, 200, { ok: true });
  }

  const state = stateFor(request);

  // Every mutating route on the real API validates the antiforgery token by hand and answers a readable
  // 400 when it is missing. Without this the screen could stop sending the header and every browser test
  // would still pass.
  if (request.method !== 'GET' && path.startsWith('/api/')) {
    if (request.headers['x-xsrf-token'] !== ANTIFORGERY_TOKEN) {
      return problem(
        response,
        400,
        'Missing antiforgery token',
        'Send the antiforgery token in the X-XSRF-TOKEN header.',
        'invalid_antiforgery_token',
      );
    }
  }

  // ---- auth ---------------------------------------------------------------------------------
  if (path === '/api/admin/auth/whoami') {
    return json(response, 200, SESSION);
  }

  if (path === '/api/admin/auth/antiforgery-token') {
    return json(response, 200, { token: ANTIFORGERY_TOKEN });
  }

  if (path === '/api/admin/auth/logout') {
    return noContent(response);
  }

  if (path === '/api/ping') {
    return json(response, 200, { message: 'pong', utcNow: new Date().toISOString() });
  }

  // ---- settings (ADM-22, GLB-02) -------------------------------------------------------------
  // ---- PAY-01 / ADM-09: doc 11 P7 step 4's queue and its three decisions -------------------------

  // Test-only, and named so. `outcome=expired|short|paid` makes the next decision answer the 409 the real
  // API answers in that state, which is how the screen's Accept-late branch and its short-line list are
  // reachable at all.
  if (path === '/__test__/bank-transfer-outcome') {
    state.bankTransferOutcome = url.searchParams.get('outcome') ?? 'ok';
    return json(response, 200, { outcome: state.bankTransferOutcome });
  }

  // ---- W4-02a: ops.AdminAlerts, the shell's banner (doc 08 §3.8 N2, doc 11 §7 G-37) ------------

  // Test-only, and named so. `set=dead|dead-no-order|many|none` seeds this browser context's alerts;
  // nothing is seeded by default, because the banner is in the shell and would otherwise change every
  // other admin screen's layout under the suite's existing measurements.
  if (path === '/__test__/alerts') {
    state.alerts = alertsFor(url.searchParams.get('set') ?? 'none');
    return json(response, 200, { count: state.alerts.length });
  }

  if (path === '/api/admin/alerts' && request.method === 'GET') {
    // R10 is the real API's rule and it is not reimplemented here: this stub's SESSION is a GrantsAll
    // holder, so it sees every type. What R10 actually does is proved against a real SQL Server in
    // `AdminAlertVisibilityEndpointTests` and against the rule itself in `AdminAlertVisibilityTests`.
    const open = state.alerts.filter((alert) => alert.acknowledgedAtUtc === undefined);

    // The real reader asks for one row more than the cap and answers "are there more" from the extra
    // row, because a COUNT(*) over the same predicate cannot use IX_AdminAlerts_Open (5,020 logical
    // reads against 38 on 20,000 alerts). The stub answers the same shape.
    return json(response, 200, {
      moreThanShown: open.length > 10,
      alerts: open.slice(0, 10),
    });
  }

  const acknowledge = path.match(/^\/api\/admin\/alerts\/(\d+)\/acknowledge$/);

  if (acknowledge && request.method === 'POST') {
    const id = Number(acknowledge[1]);
    const alert = state.alerts.find((candidate) => candidate.id === id);

    if (!alert) {
      // The real route answers a bare 404 for "no such alert" and for "not a type you may see" alike, so
      // the id space cannot be walked to learn that an alert exists.
      response.writeHead(404, { 'Cache-Control': 'no-store' });
      return response.end();
    }

    const body = await readBody(request);

    // The real route refuses an acknowledgement note rather than dropping one: doc 08 §3.8 gives the
    // table the column and nothing in this build writes it (owner's decision, 2026-10-09). The stub
    // refuses it too, so a screen that started sending one would fail here rather than in production.
    if (body !== null && body !== undefined && body.note !== null && body.note !== undefined) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          note: [
            'This build does not store an acknowledgement note, so one cannot be sent. '
              + 'Acknowledging records who saw the alert and when.',
          ],
        },
      });
    }

    const alreadyAcknowledged = alert.acknowledgedAtUtc !== undefined;
    alert.acknowledgedAtUtc = alert.acknowledgedAtUtc ?? '2026-10-09T04:00:00.000Z';

    return json(response, 200, {
      alertId: alert.id,
      alertType: alert.alertType,
      acknowledgedAtUtc: alert.acknowledgedAtUtc,

      // Explicit pair, not shorthand: see deadEmailAlert.
      alreadyAcknowledged: alreadyAcknowledged,
    });
  }

  if (path === '/api/admin/orders/bank-transfers' && request.method === 'GET') {
    const page = Math.max(1, Number(url.searchParams.get('page') ?? '1'));
    const pageSize = Math.max(1, Number(url.searchParams.get('pageSize') ?? '20'));

    return json(response, 200, {
      rows: state.bankTransfers.slice((page - 1) * pageSize, page * pageSize),
      page,
      pageSize,
      totalCount: state.bankTransfers.length,
    });
  }

  const slipRoute = /^\/api\/admin\/orders\/bank-transfers\/([^/]+)\/slip$/.exec(path);
  if (slipRoute && request.method === 'GET') {
    // The real route streams the private file as an attachment. The stub only has to be openable; the
    // headers are what the e2e asserts, because they are the part that keeps a stranger's bank document
    // out of a browser's cache and out of the page.
    response.writeHead(200, {
      'Content-Type': 'image/jpeg',
      'Content-Disposition': 'attachment',
      'X-Content-Type-Options': 'nosniff',
      'Cache-Control': 'no-store, no-cache, private',
    });

    return response.end('slip-bytes');
  }

  const decision = /^\/api\/admin\/orders\/bank-transfers\/([^/]+)\/(approve|reject|accept-late)$/.exec(path);
  if (decision && request.method === 'POST') {
    const [, orderPublicId, action] = decision;
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const row = state.bankTransfers.find((candidate) => candidate.orderPublicId === orderPublicId);
    if (!row) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    // The same refusal the API makes, so the screen's own guard is not the only thing tested.
    if (action === 'reject' && (body.note === null || body.note === undefined || String(body.note).trim() === '')) {
      return json(response, 400, {
        title: 'One or more fields are invalid.',
        status: 400,
        errors: { note: ['Say why the transfer is being rejected.'] },
      });
    }

    if (state.bankTransferOutcome === 'expired' && action === 'approve') {
      return problem(
        response,
        409,
        'This decision could not be applied',
        'This order expired before the transfer was approved, so its items have been released. If the '
          + 'money has arrived, use Accept late payment and the stock will be re-checked.',
        'order_expired',
        { orderStatus: 'Expired', paymentStatus: 'Expired' },
      );
    }

    if (state.bankTransferOutcome === 'paid') {
      return problem(
        response,
        409,
        'This decision could not be applied',
        'This order has already been paid. Somebody else approved it, or the payment was confirmed '
          + 'another way.',
        'order_already_paid',
        { orderStatus: 'Paid', paymentStatus: 'Succeeded' },
      );
    }

    if (state.bankTransferOutcome === 'short' && action === 'accept-late') {
      return problem(
        response,
        409,
        'Some items are no longer in stock',
        'This order cannot be paid now, because some of its items have been sold since it expired.',
        'short_stock',
        {
          // The order is still expired, and the screen cannot know that unless the refusal says so.
          orderStatus: 'Expired',
          shortLines: [
            {
              variantId: 470,
              sku: 'SKU-470',
              productName: 'Slim jeans',
              colourName: 'Indigo',
              sizeLabel: '32',
              needed: 1,
              available: 0,
            },
            {
              variantId: 471,
              sku: 'SKU-471',
              productName: 'Denim jacket',
              colourName: 'Washed blue',
              sizeLabel: 'M',
              needed: 2,
              available: 1,
            },
          ],
        },
      );
    }

    return json(response, 200, {
      orderNumber: row.orderNumber,
      invoiceNumber: action === 'reject' ? null : 'INV-2026-000045',
    });
  }

  // ---- ADM-09 (W3-04): the order list, the order detail, the transitions and the couriers ------

  // Test-only, and named so. The screen's refusal branch and its "print without the legal details"
  // warning are both reachable only from a *state*, so a test names one and this file owns its bytes —
  // the rule the site-settings and bank-transfer switches already follow.
  if (path === '/__test__/transition-outcome') {
    state.transitionOutcome = url.searchParams.get('outcome') ?? 'ok';
    return json(response, 200, { outcome: state.transitionOutcome });
  }

  if (path === '/__test__/store-legal-details') {
    state.storeLegalDetails = url.searchParams.get('set') === 'yes'
      ? {
          legalName: 'A Registered Company (Pvt) Ltd',
          addressLines: ['A registered address', 'A city'],
          brNumber: 'BR-0000',
          vatNumber: 'VAT-0000',
          isComplete: true,
        }
      : { legalName: null, addressLines: [], brNumber: null, vatNumber: null, isComplete: false };

    return json(response, 200, state.storeLegalDetails);
  }

  if (path === '/api/admin/orders' && request.method === 'GET') {
    const page = Math.max(1, Number(url.searchParams.get('page') ?? '1'));
    const pageSize = Math.max(1, Number(url.searchParams.get('pageSize') ?? '25'));
    const search = (url.searchParams.get('search') ?? '').trim().toLowerCase();
    const status = url.searchParams.get('status');

    let matching = state.orders;

    if (status && status !== 'all') {
      matching = matching.filter((order) => order.status === status);
    }

    if (search.length > 0) {
      // Deliberately crude: the real classification is proved against SQL Server in
      // AdminOrderEndpointTests, and a stub that reimplemented it would be a second implementation to
      // keep in step. What the browser test needs is that typing a number narrows the list.
      matching = matching.filter(
        (order) =>
          order.orderNumber.toLowerCase().includes(search)
          || order.customerName.toLowerCase().includes(search)
          || order.customerEmail.toLowerCase().includes(search),
      );
    }

    // Doc 11 P9 step 1: the Paid filter is worked oldest first, and the *server* chooses it. A stub that
    // echoed whatever the client asked for would let the screen regress to labelling its own default.
    const sort = url.searchParams.get('sort') ?? (status === 'Paid' ? 'Oldest' : 'Newest');

    const counts = {};
    for (const name of [
      'PendingPayment',
      'Paid',
      'Processing',
      'Shipped',
      'Delivered',
      'Cancelled',
      'Expired',
      'Refunded',
    ]) {
      counts[name] = state.orders.filter((order) => order.status === name).length;
    }

    return json(response, 200, {
      rows: matching.slice((page - 1) * pageSize, page * pageSize).map(orderRow),
      page,
      pageSize,
      totalCount: matching.length,
      sort: sort === 'Oldest' ? 'Oldest' : 'Newest',
      statusCounts: counts,
    });
  }

  const invoiceRoute = /^\/api\/admin\/orders\/([^/]+)\/invoice$/.exec(path);
  if (invoiceRoute && request.method === 'GET') {
    const order = state.orders.find((candidate) => candidate.orderPublicId === invoiceRoute[1]);

    // No invoice number means no invoice: doc 11 P9 makes printing available once the order is paid, and
    // the real route answers the same 404 as an order that does not exist.
    if (!order || order.invoiceNumber === null) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    return json(response, 200, orderInvoice(state, order));
  }

  const transitionRoute =
    /^\/api\/admin\/orders\/([^/]+)\/(start-processing|mark-shipped|ready-for-collection|mark-delivered|dispatch|internal-note|cancel)$/.exec(
      path,
    );
  if (transitionRoute && request.method === 'POST') {
    const [, orderPublicId, segment] = transitionRoute;
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const order = state.orders.find((candidate) => candidate.orderPublicId === orderPublicId);
    if (!order) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    if (state.transitionOutcome === 'wrong-status') {
      return problem(
        response,
        409,
        'This order could not be moved',
        'Somebody has already started processing this order.',
        'order_wrong_status',
        { orderStatus: 'Processing', orderStatusLabel: 'Processing' },
      );
    }

    // The same refusal the API makes, so the screen's own guard is not the only thing tested.
    if (segment === 'internal-note' && !Object.hasOwn(body, 'internalNote')) {
      return json(response, 400, {
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          internalNote: ['Send internalNote, as text to set the note or null to clear it.'],
        },
      });
    }

    if (segment === 'mark-shipped' || segment === 'dispatch') {
      const errors = {};

      if (body.courierId === undefined || body.courierId === null) {
        errors.courierId = ['Choose the courier carrying this order.'];
      }

      if (!body.trackingNumber) {
        errors.trackingNumber = ['Enter the tracking number the courier gave you.'];
      }

      if (Object.keys(errors).length > 0) {
        return json(response, 400, { title: 'One or more fields are invalid.', status: 400, errors });
      }

      const courier = state.couriers.find((candidate) => candidate.id === Number(body.courierId));
      order.courierName = courier ? courier.name : null;
      order.trackingNumber = String(body.trackingNumber);
      order.shippedAtUtc = '2026-10-06T06:00:00Z';
    }

    if (segment === 'internal-note') {
      order.internalNote = body.internalNote === null ? null : String(body.internalNote);
    }

    // Doc 11 P10 flow A. **The same refusal the API makes**, so the screen's own guard is not the only
    // thing tested: `cancelReason` is required, and a body without it is a 400 naming the field rather
    // than a cancellation with a blank reason on it.
    if (segment === 'cancel') {
      const reason = typeof body.cancelReason === 'string' ? body.cancelReason.trim() : '';

      if (reason.length === 0) {
        return json(response, 400, {
          title: 'One or more fields are invalid.',
          status: 400,
          errors: {
            cancelReason: [
              'Say why this order is being cancelled. It is kept on the order and read back to the '
              + 'customer if they ask.',
            ],
          },
        });
      }

      order.cancelReason = reason;
      order.closedAtUtc = '2026-10-08T06:00:00Z';
      order.secondsUntilExpiry = null;
      order.stockState = 'Released';
    }

    const moved = {
      'start-processing': ['Processing', 'Processing'],
      'mark-shipped': ['Shipped', 'Shipped'],
      'ready-for-collection': ['Shipped', 'Ready for collection'],
      'mark-delivered': order.isPickup ? ['Delivered', 'Collected'] : ['Delivered', 'Delivered'],
      cancel: ['Cancelled', 'Cancelled'],
    }[segment];

    if (moved) {
      [order.status, order.statusLabel] = moved;

      order.actions = {
        ...order.actions,
        canStartProcessing: false,
        canMarkShipped: order.status === 'Processing' && !order.isPickup,
        canMarkReadyForCollection: order.status === 'Processing' && order.isPickup,
        canMarkDelivered: order.status === 'Shipped',
        canEditDispatch: order.status === 'Shipped' && !order.isPickup,

        // Doc 11 P10: Cancelled is a closing state. Nothing may cancel it again.
        canCancel: false,

        // **And the one edge out of it stays open.** Doc 08 4.1 allows Cancelled -> Paid, and cancelling
        // closes the order's open bank payment to Cancelled - which the real reader now recognises, so a
        // transfer that arrives the next day can still be accepted. The stub says so too, because a
        // screen that offered nothing here was the gap W3-04b left and a stub that kept answering false
        // would hide its fix.
        canAcceptLatePayment:
          order.status === 'Cancelled' && order.paymentProviderCode === 'BankTransfer',
      };
    }

    return json(response, 200, {
      orderNumber: order.orderNumber,
      status: order.status,
      statusLabel: order.statusLabel,
    });
  }

  const orderRoute = /^\/api\/admin\/orders\/([^/]+)$/.exec(path);
  if (orderRoute && request.method === 'GET') {
    const order = state.orders.find((candidate) => candidate.orderPublicId === orderRoute[1]);

    if (!order) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    return json(response, 200, orderDetail(state, order));
  }

  // ADM-13 (W3-05). The promo codes.
  //
  // **It refuses what the real API refuses, field for field**, which is the rule the whole of this stub
  // is written to (#126): every key must be *present*, a free-delivery code may carry no amount, the end
  // must be after the start, a per-customer limit may not exceed the total, and a date must arrive as the
  // shop's own wall-clock time with no offset. A stub that accepted a body the API refuses would make
  // every refusal path on the screen unreachable in a browser.
  if (path === '/api/admin/marketing/promo-codes' && request.method === 'GET') {
    return json(response, 200, state.promoCodes);
  }

  if (path === '/api/admin/marketing/promo-codes' && request.method === 'POST') {
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const invalid = promoCodeErrors(state, body, null);
    if (invalid) {
      return json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: invalid });
    }

    const promoCode = buildPromoCode(state, body, state.promoCodes.length + 1, 0);

    state.promoCodes.unshift(promoCode);

    return json(response, 201, { promoCode, warnings: promoWarnings(body, promoCode) });
  }

  const promoRoute = /^\/api\/admin\/marketing\/promo-codes\/(\d+)$/.exec(path);

  if (promoRoute && request.method === 'PUT') {
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const index = state.promoCodes.findIndex((candidate) => candidate.id === Number(promoRoute[1]));
    if (index < 0) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    const existing = state.promoCodes[index];

    // `rowVersionOk` writes the 409 itself, so the refusal reads identically to every other stale-edit
    // refusal this stub answers — one problem shape for one condition.
    if (!rowVersionOk(response, request, body, existing)) {
      return undefined;
    }

    const invalid = promoCodeErrors(state, body, existing.id);
    if (invalid) {
      return json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: invalid });
    }

    const promoCode = buildPromoCode(state, body, existing.id, existing.usageCount);

    state.promoCodes[index] = promoCode;

    return json(response, 200, { promoCode, warnings: promoWarnings(body, promoCode) });
  }

  if (promoRoute && request.method === 'DELETE') {
    const index = state.promoCodes.findIndex((candidate) => candidate.id === Number(promoRoute[1]));
    if (index < 0) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    if (!rowVersionOk(response, request, null, state.promoCodes[index])) {
      return undefined;
    }

    // A soft delete on the server; from the screen's side the row is gone and its code is free again.
    state.promoCodes.splice(index, 1);

    return noContent(response);
  }

  // ADM-14 (W3-05c). The sale campaigns. The same rule as the promo codes above: it refuses what the
  // real endpoint refuses, field for field, so every refusal path on the screen is reachable in a
  // browser.
  if (path === '/api/admin/marketing/sale-campaigns' && request.method === 'GET') {
    return json(response, 200, state.saleCampaigns);
  }

  if (path === '/api/admin/marketing/sale-campaigns' && request.method === 'POST') {
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const invalid = saleCampaignErrors(state, body);
    if (invalid) {
      return json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: invalid });
    }

    const campaign = buildCampaign(state, body, state.saleCampaigns.length + 1);
    const productsRepriced = campaignProductsRepriced(state, campaign);

    state.saleCampaigns.unshift(campaign);

    return json(response, 201, {
      campaign,
      warnings: campaignWarnings(campaign, productsRepriced),
      productsRepriced,
    });
  }

  const campaignRoute = /^\/api\/admin\/marketing\/sale-campaigns\/(\d+)$/.exec(path);

  if (campaignRoute && request.method === 'PUT') {
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const index = state.saleCampaigns.findIndex((candidate) => candidate.id === Number(campaignRoute[1]));
    if (index < 0) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    if (!rowVersionOk(response, request, body, state.saleCampaigns[index])) {
      return undefined;
    }

    const invalid = saleCampaignErrors(state, body);
    if (invalid) {
      return json(response, 400, { title: 'One or more fields need correcting.', status: 400, errors: invalid });
    }

    const campaign = buildCampaign(state, body, state.saleCampaigns[index].id);
    const productsRepriced = campaignProductsRepriced(state, campaign);

    state.saleCampaigns[index] = campaign;

    return json(response, 200, {
      campaign,
      warnings: campaignWarnings(campaign, productsRepriced),
      productsRepriced,
    });
  }

  if (campaignRoute && request.method === 'DELETE') {
    const index = state.saleCampaigns.findIndex((candidate) => candidate.id === Number(campaignRoute[1]));
    if (index < 0) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    if (!rowVersionOk(response, request, null, state.saleCampaigns[index])) {
      return undefined;
    }

    // A soft delete on the server, which also puts the prices it was discounting back; from the screen's
    // side the row is simply gone.
    state.saleCampaigns.splice(index, 1);

    return noContent(response);
  }

  if (path === '/api/admin/couriers' && request.method === 'GET') {
    return json(response, 200, state.couriers);
  }

  if (path === '/api/admin/couriers' && request.method === 'POST') {
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const invalid = courierErrors(state, body, null);
    if (invalid) {
      return json(response, 400, { title: 'One or more fields are invalid.', status: 400, errors: invalid });
    }

    const courier = {
      id: state.couriers.length + 1,
      name: String(body.name).trim(),
      trackingUrlTemplate: body.trackingUrlTemplate ?? null,
      phone: body.phone ?? null,
      isActive: body.isActive === true,
      sortOrder: Number(body.sortOrder ?? 0),
      ordersShipped: 0,
    };

    state.couriers.push(courier);

    return json(response, 201, courier);
  }

  const courierRoute = /^\/api\/admin\/couriers\/(\d+)$/.exec(path);
  if (courierRoute && request.method === 'PUT') {
    const body = await readBody(request);

    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const courier = state.couriers.find((candidate) => candidate.id === Number(courierRoute[1]));
    if (!courier) {
      return json(response, 404, { title: 'Not found', status: 404 });
    }

    const invalid = courierErrors(state, body, courier.id);
    if (invalid) {
      return json(response, 400, { title: 'One or more fields are invalid.', status: 400, errors: invalid });
    }

    courier.name = String(body.name).trim();
    courier.trackingUrlTemplate = body.trackingUrlTemplate ?? null;
    courier.phone = body.phone ?? null;
    courier.isActive = body.isActive === true;
    courier.sortOrder = Number(body.sortOrder ?? 0);

    return json(response, 200, courier);
  }

  if (path === '/api/admin/settings' && request.method === 'GET') {
    return json(response, 200, settingsPayload(state));
  }

  // The media upload the screen does before it points the setting at the result. Multipart, so the body is
  // not parsed: what matters is that a file arrived, and that the answer carries a publicId the next call
  // can use — which is what makes the two-step flow testable at all.
  if (path === '/api/admin/media/images' && request.method === 'POST') {
    const altText = uploadedAltText(await drain(request));

    const publicId = `0000f00d-0000-4000-8000-${String(state.uploads.size + 1).padStart(12, '0')}`;
    const asset = {
      publicId,

      // media.MediaAssets.MediaType, as a *name* — the real API registers a global
      // JsonStringEnumConverter, so every enum in a response is a string. ADM-10's product form reads
      // this to tell a photograph from the video; the logo flow ignores it.
      mediaType: 'Image',
      url: '/__test__/logo.webp',
      posterUrl: null,
      width: 600,
      height: 150,
      altText,
      originalFileName: 'logo.png',
    };

    state.uploads.set(publicId, asset);

    return json(response, 201, asset);
  }

  // The product video and its poster, in one multipart request (ADM-10, doc 08 §3.3: a video row cannot
  // exist without a poster, and nothing on the server extracts a frame). The body is not parsed; what
  // matters is that two files arrived and that the answer carries a publicId the form can reference.
  if (path === '/api/admin/media/videos' && request.method === 'POST') {
    const altText = uploadedAltText(await drain(request));

    const publicId = `0000f00d-0000-4000-8000-${String(state.uploads.size + 1).padStart(12, '0')}`;
    const asset = {
      publicId,
      mediaType: 'Video',
      url: '/__test__/clip.mp4',
      posterUrl: '/__test__/poster.webp',
      durationSeconds: 12.5,
      altText,
      originalFileName: 'clip.mp4',
    };

    state.uploads.set(publicId, asset);

    return json(response, 201, asset);
  }

  if (path === '/api/admin/settings/store-logo' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Store')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed these settings while you were editing them.',
        'stale_row_version',
      );
    }

    const wanted = body.logoAssetPublicId ?? null;

    if (wanted !== null && !state.uploads.has(wanted)) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          logoAssetPublicId: ['That image is not in the media library, or is not a ready public image.'],
        },
      });
    }

    state.settings['Store.LogoAssetId'] = wanted;
    state.logo = wanted === null ? null : state.uploads.get(wanted);

    return noContent(response);
  }

  if (path === '/api/admin/settings/bank' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Bank')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed the bank details while you were editing them.',
        'stale_row_version',
      );
    }

    // The API's one real format rule, mirrored — so the screen's error display for this field is actually
    // exercised rather than assumed. Without it the field error could stop rendering and every browser
    // test would still pass, which is the gap that let W1-12's antiforgery header go unchecked for 246
    // tests.
    if (
      body.accountNumber !== null
      && body.accountNumber !== undefined
      && String(body.accountNumber).trim() !== ''
      && !/^[0-9 -]+$/.test(String(body.accountNumber))
    ) {
      return json(response, 400, {
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          accountNumber: [
            'An account number can only hold digits, spaces and dashes. Enter the bank name in the bank '
              + 'name box and anything else in the instructions.',
          ],
        },
      });
    }

    // **Only the fields the body carried.** A key the request omits is a key left alone — the W1-12 rule,
    // and the one this group makes real. A stub that assigned all five from `body` would let the screen
    // start sending nulls and no browser test would notice.
    for (const [field, key] of [
      ['bankName', 'Bank.BankName'],
      ['branch', 'Bank.Branch'],
      ['accountName', 'Bank.AccountName'],
      ['accountNumber', 'Bank.AccountNumber'],
      ['instructions', 'Bank.Instructions'],
    ]) {
      if (Object.hasOwn(body, field)) {
        const value = body[field];
        state.settings[key] = value === null || String(value).trim() === '' ? null : String(value).trim();
      }
    }

    return noContent(response);
  }

  if (path === '/api/admin/settings/invoice-and-fulfilment' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Store')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed the shop\u2019s details while you were editing them.',
        'stale_row_version',
      );
    }

    // The window's one real rule, mirrored for the reason the bank group's is: without it the field error
    // could stop rendering and every browser test would still pass. Nought is refused on its own grounds
    // (doc 08 section 4.1 G-18), which is why the range is a range and not a length.
    if (Object.hasOwn(body, 'autoDeliverAfterDays')) {
      const raw = body.autoDeliverAfterDays;
      const text = raw === null || raw === undefined ? '' : String(raw).trim();

      if (text !== '' && !/^(?:[1-9]|[1-8][0-9]|90)$/.test(text)) {
        return json(response, 400, {
          title: 'One or more fields are invalid.',
          status: 400,
          errors: {
            autoDeliverAfterDays: [
              'Use between 1 and 90 days. Nought would mark an order delivered the moment it ships, which '
                + 'would also give the customer a Verified buyer badge for goods still in the van.',
            ],
          },
        });
      }
    }

    // **Only the fields the body carried**, the W1-12 rule again. A stub that assigned all five from
    // `body` would let the screen start sending nulls and clear four legal keys with nothing noticing.
    for (const [field, key] of [
      ['legalName', 'Store.LegalName'],
      ['registeredAddress', 'Store.Address'],
      ['brNumber', 'Store.BRNumber'],
      ['vatNumber', 'Store.VatNumber'],
      ['autoDeliverAfterDays', 'Orders.AutoDeliverAfterDays'],
    ]) {
      if (Object.hasOwn(body, field)) {
        const value = body[field];
        state.settings[key] = value === null || String(value).trim() === '' ? null : String(value).trim();
      }
    }

    return noContent(response);
  }

  if (path === '/api/admin/settings/contact-social' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined || body === null) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (body.rowVersion !== settingsToken(state, 'Contact')) {
      return problem(
        response,
        409,
        'This change cannot be applied',
        'Somebody else changed these settings while you were editing them.',
        'stale_row_version',
      );
    }

    // The same per-field refusals the API makes, so the screen's error display is actually exercised.
    const errors = {};
    const fields = {
      whatsApp: 'Contact.WhatsApp',
      facebookUrl: 'Social.FacebookUrl',
      instagramUrl: 'Social.InstagramUrl',
      tikTokUrl: 'Social.TikTokUrl',
    };

    for (const field of ['facebookUrl', 'instagramUrl', 'tikTokUrl']) {
      const value = body[field];
      if (value !== null && value !== undefined && String(value).trim() !== '' && !acceptableProfileUrl(value)) {
        errors[field] = [
          'The address must be a full web address starting with https://. Leave the box empty to hide the icon.',
        ];
      }
    }

    const whatsApp = body.whatsApp;
    if (whatsApp !== null && whatsApp !== undefined && String(whatsApp).trim() !== '' && !acceptableWhatsApp(whatsApp)) {
      errors.whatsApp = [
        'The WhatsApp number must be 7 to 15 digits in international form, with no plus sign, spaces or '
        + 'leading zero — for example 94771234567.',
      ];
    }

    if (Object.keys(errors).length > 0) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors,
      });
    }

    // PUT replaces: a field the body omits is a field the API is told to clear. The stub does the same, so a
    // screen that stopped sending one would show up as a cleared value rather than as nothing at all —
    // which is the W1-12 blocker's exact shape.
    for (const [field, key] of Object.entries(fields)) {
      const value = body[field];
      state.settings[key] = value === null || value === undefined || String(value).trim() === ''
        ? null
        : String(value).trim();
    }

    return noContent(response);
  }

  // The logo the preview points at. A real 1x1 WebP, so a broken image is visibly broken.
  if (path === '/__test__/logo.webp') {
    response.writeHead(200, { 'Content-Type': 'image/webp', 'Cache-Control': 'no-store' });
    response.end(LOGO_PIXEL);
    return;
  }

  // ---- customers and newsletter subscribers (ADM-15, ADM-21, W3-07) ---------------------------
  if (await handleCustomerRoute({ path, method: request.method, url, response, state, json })) {
    return undefined;
  }

  // ---- dashboard and reports (ADM-04–08, ADM-19, W4-02) ---------------------------------------
  if (await handleReportingRoute({ path, method: request.method, url, response, state, json })) {
    return undefined;
  }

  // ---- inventory (ADM-12, #190) ---------------------------------------------------------------
  if (await handleInventoryRoute({ path, method: request.method, url, request, response, state, json, readBody })) {
    return undefined;
  }

  // ---- products (ADM-10, W1-03) --------------------------------------------------------------
  if (
    await handleProductRoute({
      path,
      method: request.method,
      url,
      request,
      response,
      state,
      json,
      problem,
      noContent,
      readBody,
      suppliedRowVersion,
    })
  ) {
    return undefined;
  }

  // ---- catalogue ----------------------------------------------------------------------------
  if (path === '/api/admin/catalog/departments' && request.method === 'GET') {
    return json(response, 200, state.departments);
  }

  const treeMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)\/categories$/);
  if (treeMatch && request.method === 'GET') {
    const id = Number(treeMatch[1]);
    const found = state.departments.find((candidate) => candidate.id === id);

    if (!found) {
      return problem(response, 404, 'Not found', `No department has the id ${id}.`, 'not_found');
    }

    return json(response, 200, {
      departmentId: found.id,
      departmentName: found.name,
      departmentSlug: found.slug,
      departmentIsActive: found.isActive,
      categories: state.categories[id] ?? [],
    });
  }

  const impactMatch = path.match(/^\/api\/admin\/catalog\/categories\/(\d+)\/deactivation-impact$/);
  if (impactMatch && request.method === 'GET') {
    const id = Number(impactMatch[1]);
    const node = Object.values(state.categories).flatMap(flatten).find((candidate) => candidate.id === id);

    if (!node) {
      return problem(response, 404, 'Not found', `No category has the id ${id}.`, 'not_found');
    }

    const descendants = flatten(node.children);

    return json(response, 200, {
      categoryId: node.id,
      name: node.name,
      isActive: node.isActive,
      productsLeavingTheStorefront: node.primaryProductCount,
      descendantCategories: descendants.length,
      descendantProductsLosingTheirMenuRoute: descendants
        .filter((child) => child.isActive)
        .reduce((total, child) => total + child.primaryProductCount, 0),
      examples: node.primaryProductCount > 0 ? ['Linen shift dress', 'Cotton midi dress'] : [],
    });
  }

  if (path === '/api/admin/catalog/category-form-options' && request.method === 'GET') {
    return json(response, 200, {
      ageGroups: [
        { id: 1, name: 'Baby' },
        { id: 2, name: 'Toddler' },
        { id: 3, name: 'Kids' },
      ],
      sizeGuides: [
        { id: 2, name: "Women's clothing" },
        { id: 5, name: "Kids' clothing" },
      ],
    });
  }

  if (path === '/api/admin/catalog/departments/order' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    return applyOrder(response, state.departments, body.orderedDepartmentIds ?? [])
      ? noContent(response)
      : undefined;
  }

  if (path === '/api/admin/catalog/categories/order' && request.method === 'PUT') {
    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    const siblings = findSiblings(state, body.departmentId, body.parentCategoryId ?? null);
    if (!siblings) {
      return problem(response, 404, 'Not found', 'No such parent category.', 'not_found');
    }

    return applyOrder(response, siblings, body.orderedCategoryIds ?? []) ? noContent(response) : undefined;
  }

  if (path === '/api/admin/catalog/categories' && request.method === 'POST') {
    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    // The one validation worth stubbing, because the screen has to show it against the right box.
    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(body.slug ?? '')) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: {
          slug: [
            'The URL slug may contain only lower-case letters, digits and single hyphens between them, ' +
              "for example 'denim-jackets'.",
          ],
        },
      });
    }

    const siblings = findSiblings(state, body.departmentId, body.parentCategoryId ?? null);
    if (!siblings) {
      return problem(response, 400, 'Invalid', 'No such parent category.', 'invalid');
    }

    const parent =
      body.parentCategoryId === null || body.parentCategoryId === undefined
        ? null
        : Object.values(state.categories).flatMap(flatten).find((node) => node.id === body.parentCategoryId);

    const id = 900 + Object.values(state.categories).flatMap(flatten).length;
    const depth = parent ? parent.depth + 1 : 1;

    siblings.push(
      category(
        id,
        body.name,
        body.slug,
        parent ? parent.id : null,
        depth,
        parent ? `${parent.path}${id}/` : `/${id}/`,
        siblings.length,
      ),
    );

    return json(response, 201, { id });
  }

  const activeMatch = path.match(/^\/api\/admin\/catalog\/categories\/(\d+)\/active$/);
  if (activeMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const node = Object.values(state.categories)
      .flatMap(flatten)
      .find((candidate) => candidate.id === Number(activeMatch[1]));

    if (!node) {
      return problem(response, 404, 'Not found', 'No such category.', 'not_found');
    }

    if (!rowVersionOk(response, request, body, node)) {
      return undefined;
    }

    node.isActive = body.isActive;
    node.rowVersion = nextRowVersion();
    return noContent(response);
  }

  const departmentActiveMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)\/active$/);
  if (departmentActiveMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const found = state.departments.find((candidate) => candidate.id === Number(departmentActiveMatch[1]));

    if (!found) {
      return problem(response, 404, 'Not found', 'No such department.', 'not_found');
    }

    if (!rowVersionOk(response, request, body, found)) {
      return undefined;
    }

    found.isActive = body.isActive;
    found.rowVersion = nextRowVersion();
    return noContent(response);
  }

  const categoryMatch = path.match(/^\/api\/admin\/catalog\/categories\/(\d+)$/);
  if (categoryMatch && (request.method === 'PUT' || request.method === 'DELETE')) {
    const id = Number(categoryMatch[1]);
    const all = Object.values(state.categories).flatMap(flatten);
    const node = all.find((candidate) => candidate.id === id);

    if (!node) {
      return problem(response, 404, 'Not found', `No category has the id ${id}.`, 'not_found');
    }

    if (request.method === 'DELETE') {
      if (!rowVersionOk(response, request, null, node)) {
        return undefined;
      }

      // Doc 08 §3.2's guard, not a cascade — the refusal the screen has to be able to show.
      if (node.children.length > 0 || node.productCount > 0) {
        return problem(
          response,
          409,
          'This change cannot be applied',
          `'${node.name}' still has ` +
            [
              node.children.length > 0 ? `${node.children.length} categories underneath it` : null,
              node.productCount > 0 ? `${node.productCount} products in it` : null,
            ]
              .filter(Boolean)
              .join(' and ') +
            '. Move or remove those first.',
          'category_in_use',
          { childCategories: node.children.length, linkedProducts: node.productCount },
        );
      }

      const siblings = findSiblings(state, node.departmentId, node.parentCategoryId);
      siblings.splice(
        siblings.findIndex((candidate) => candidate.id === id),
        1,
      );

      return noContent(response);
    }

    const body = await readBody(request);
    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(body.slug ?? '')) {
      return json(response, 400, {
        type: 'about:blank',
        title: 'One or more fields are invalid.',
        status: 400,
        errors: { slug: ['The URL slug may contain only lower-case letters, digits and single hyphens.'] },
      });
    }

    if (!rowVersionOk(response, request, body, node)) {
      return undefined;
    }

    const newParentId = body.parentCategoryId ?? null;
    if (newParentId !== node.parentCategoryId) {
      // The move: the subtree is re-pathed and re-levelled, as the real use case does in one transaction.
      // Without this the largest thing the screen does had no browser coverage at all.
      const newParent =
        newParentId === null ? null : all.find((candidate) => candidate.id === newParentId);

      if (newParentId !== null && !newParent) {
        return json(response, 400, {
          type: 'about:blank',
          title: 'One or more fields are invalid.',
          status: 400,
          errors: { parentCategoryId: [`No category has the id ${newParentId}.`] },
        });
      }

      if (newParent && newParent.path.startsWith(node.path)) {
        return json(response, 400, {
          type: 'about:blank',
          title: 'One or more fields are invalid.',
          status: 400,
          errors: {
            parentCategoryId: [
              `'${newParent.name}' is inside '${node.name}', so moving it there would make the tree a loop.`,
            ],
          },
        });
      }

      const oldSiblings = findSiblings(state, node.departmentId, node.parentCategoryId);
      oldSiblings.splice(
        oldSiblings.findIndex((candidate) => candidate.id === id),
        1,
      );

      const oldPath = node.path;
      const newPath = newParent ? `${newParent.path}${id}/` : `/${id}/`;

      for (const descendant of [node, ...flatten(node.children)]) {
        descendant.path = newPath + descendant.path.slice(oldPath.length);
        descendant.depth = descendant.path.split('/').filter(Boolean).length;
      }

      node.parentCategoryId = newParentId;

      const newSiblings = newParent ? newParent.children : (state.categories[node.departmentId] ??= []);
      node.sortOrder = newSiblings.length;
      newSiblings.push(node);
    }

    node.name = body.name;
    node.slug = body.slug;
    node.description = body.description ?? null;
    node.imageAssetId = body.imageAssetId ?? null;
    node.bannerAssetId = body.bannerAssetId ?? null;
    node.bannerLinkUrl = body.bannerLinkUrl ?? null;
    node.ageGroupId = body.ageGroupId ?? null;
    node.sizeGuideId = body.sizeGuideId ?? null;
    node.showInMegaMenu = body.showInMegaMenu ?? true;
    node.megaMenuColumn = body.megaMenuColumn ?? null;
    node.seoTitle = body.seoTitle ?? null;
    node.seoDescription = body.seoDescription ?? null;
    node.rowVersion = nextRowVersion();

    return noContent(response);
  }

  const departmentMatch = path.match(/^\/api\/admin\/catalog\/departments\/(\d+)$/);
  if (departmentMatch && request.method === 'PUT') {
    const body = await readBody(request);
    const found = state.departments.find((candidate) => candidate.id === Number(departmentMatch[1]));

    if (!found) {
      return problem(response, 404, 'Not found', 'No such department.', 'not_found');
    }

    if (body === undefined) {
      return problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON.', 'malformed_json');
    }

    if (!rowVersionOk(response, request, body, found)) {
      return undefined;
    }

    // The name and the slug are not editable at all (owner, 2026-10-02), so the stub ignores them exactly
    // as the real request DTO does — it has no field for either.
    found.description = body.description ?? null;
    found.heroImageAssetId = body.heroImageAssetId ?? null;
    found.seoTitle = body.seoTitle ?? null;
    found.seoDescription = body.seoDescription ?? null;
    found.rowVersion = nextRowVersion();
    return noContent(response);
  }

  if (path.startsWith('/api/')) {
    return problem(response, 404, 'Not found', `The stub has no route for ${path}.`, 'not_found');
  }

  // ---- the built application ----------------------------------------------------------------
  return serveStatic(path, response);
});

/**
 * Serves `dist/admin/browser`, falling back to `index.html` for anything that is not a file — which is what
 * a deployment's reverse proxy does for a client-routed application, and without it a direct visit to
 * `/admin/catalog` would be a 404 and every test would be testing the 404.
 */
function serveStatic(path, response) {
  const withoutBase = path.replace(/^\/admin\/?/, '/');
  const candidate = resolve(join(DIST, normalize(withoutBase)));

  if (!candidate.startsWith(DIST)) {
    response.writeHead(403).end();
    return;
  }

  let file = candidate;
  try {
    if (!statSync(file).isFile()) {
      file = join(DIST, 'index.html');
    }
  } catch {
    file = join(DIST, 'index.html');
  }

  try {
    const body = readFileSync(file);
    response.writeHead(200, {
      'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store',
      'Content-Length': body.length,
    });
    response.end(body);
  } catch {
    response.writeHead(404, { 'Content-Type': 'text/plain' }).end('not found');
  }
}

server.listen(PORT, () => {
  console.log(`admin catalogue stub on http://localhost:${PORT} serving ${DIST}`);
});
