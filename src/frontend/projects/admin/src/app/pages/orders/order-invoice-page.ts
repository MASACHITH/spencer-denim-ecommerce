import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BRAND } from '@spencer-denim/shared';

import { AdminOrderApi } from '../../core/orders/admin-order-api';
import { OrderInvoice } from '../../core/orders/admin-order.model';
import { colomboDate, money } from '../../core/orders/order-formatting';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/**
 * The invoice and packing copy for one order (ADM-09, doc 11 P9 step 3).
 *
 * **The number is not issued here.** It is allocated inside the Paid transaction, gaplessly (doc 08 §4.1),
 * so this screen renders a document that already exists — and an order with no number has no invoice,
 * which is why an unpaid order answers 404 rather than printing a draft.
 *
 * **The seller's legal details are settings and nothing is invented for them.** If the client has not
 * filled in the registered name, the registered address or the registration numbers, those lines are
 * simply absent from the printout. This project has twice shipped invented client data as a Major finding,
 * and an invoice is a legal document: an invented registered address on one is worse than a blank somebody
 * can see and ask about. The staff member is warned *before* printing, which is the one place saying so
 * helps.
 *
 * **It carries no VAT line** (doc 08 D2, doc 11 P9): prices already include VAT, so a VAT row would be
 * double-counting. The VAT registration number in the header is the seller's identity, not a calculation.
 *
 * **The shop's name and the company's name are both on it and they are not the same thing.** The heading
 * is the shop a customer bought from (`BRAND.name`, docs/02 §I2); the seller block is the registered
 * company that issues the invoice, from the settings. A printed invoice needs both and conflating them is
 * the mistake the brand decision exists to prevent.
 */
@Component({
  selector: 'sda-order-invoice-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        <div class="adm-actions">
          <a class="adm-link" [routerLink]="['/orders', orderPublicId()]">Back to the order</a>
        </div>
      </div>
    }

    @if (invoice(); as doc) {
      <nav class="invoice-bar adm-actions">
        <a class="adm-link" [routerLink]="['/orders', orderPublicId()]">← Back to the order</a>
        <button class="adm-btn--primary" type="button" data-testid="print" (click)="print()">
          Print
        </button>
      </nav>

      @if (!doc.seller.isComplete) {
        <div class="adm-state adm-state--warning invoice-bar" data-testid="seller-incomplete">
          <p>
            This invoice will print without the company's registered name and address, because they have
            not been filled in. Add them under Settings before sending it to a customer — nothing is
            invented for a legal document.
          </p>
        </div>
      }

      <article class="invoice" data-testid="invoice">
        <header class="invoice__head">
          <div>
            <p class="invoice__shop">{{ shop }}</p>
            @if (doc.seller.legalName) {
              <p data-testid="seller-legal-name">{{ doc.seller.legalName }}</p>
            }
            @for (line of doc.seller.addressLines; track line) {
              <p>{{ line }}</p>
            }
            @if (doc.seller.brNumber) {
              <p>Business registration {{ doc.seller.brNumber }}</p>
            }
            @if (doc.seller.vatNumber) {
              <p>VAT registration {{ doc.seller.vatNumber }}</p>
            }
          </div>
          <div class="invoice__meta">
            <h1 class="invoice__title">Invoice</h1>
            <p data-testid="invoice-number">{{ doc.invoiceNumber }}</p>
            <p>Issued {{ day(doc.issuedAtUtc) }}</p>
            <p>Order {{ doc.orderNumber }}, placed {{ day(doc.placedAtUtc) }}</p>
          </div>
        </header>

        <section class="invoice__parties">
          <div>
            <h2 class="adm-label">Billed to</h2>
            <p>{{ doc.customer.name }}</p>
            <p>{{ doc.customer.email }}</p>
            @if (doc.customer.phone) {
              <p>{{ doc.customer.phone }}</p>
            }
          </div>
          <div>
            <h2 class="adm-label">{{ doc.delivery.isPickup ? 'Collection' : 'Delivered to' }}</h2>
            @if (doc.delivery.isPickup) {
              <p>{{ doc.delivery.methodName }}</p>
              @if (doc.delivery.pickupLocationName) {
                <p>{{ doc.delivery.pickupLocationName }}</p>
              }
            } @else {
              <p>{{ doc.delivery.recipientName }}</p>
              @if (doc.delivery.recipientPhone) {
                <p>{{ doc.delivery.recipientPhone }}</p>
              }
              <p>{{ doc.delivery.line1 }}</p>
              @if (doc.delivery.line2) {
                <p>{{ doc.delivery.line2 }}</p>
              }
              <p>{{ doc.delivery.city }}@if (doc.delivery.region) {, {{ doc.delivery.region }}}@if (doc.delivery.postalCode) {, {{ doc.delivery.postalCode }}}</p>
              @if (doc.delivery.landmark) {
                <p>Near {{ doc.delivery.landmark }}</p>
              }
              @if (doc.delivery.districtName) {
                <p>{{ doc.delivery.districtName }}</p>
              }
              <p data-testid="invoice-country">{{ doc.delivery.countryName }}</p>
            }
          </div>
        </section>

        <table class="invoice__lines">
          <caption class="adm-label">Items</caption>
          <thead>
            <tr>
              <th scope="col">Item</th>
              <th scope="col">SKU</th>
              <th scope="col" class="invoice__right">Qty</th>
              <th scope="col" class="invoice__right">Unit</th>
              <th scope="col" class="invoice__right">Total</th>
            </tr>
          </thead>
          <tbody>
            @for (line of doc.lines; track line.lineNumber) {
              <tr [attr.data-testid]="'invoice-line-' + line.sku">
                <td>{{ line.productName }} · {{ line.colourName }} · {{ line.sizeLabel }}</td>
                <td>{{ line.sku }}</td>
                <td class="invoice__right">{{ line.quantity }}</td>
                <td class="invoice__right">{{ cash(line.unitPrice, doc.money.currency) }}</td>
                <td class="invoice__right">{{ cash(line.lineTotal, doc.money.currency) }}</td>
              </tr>
            }
          </tbody>
        </table>

        <dl class="invoice__totals">
          <dt>Items</dt>
          <dd>{{ cash(doc.money.itemsSubtotal, doc.money.currency) }}</dd>
          @if (doc.money.promoCodeText) {
            <dt>Promo code {{ doc.money.promoCodeText }}</dt>
            <dd>−{{ cash(doc.money.promoDiscountAmount, doc.money.currency) }}</dd>
          }
          <dt>Delivery</dt>
          <dd>{{ cash(doc.money.deliveryFeeAmount, doc.money.currency) }}</dd>
          @if (doc.money.deliveryDiscountAmount > 0) {
            <dt>Delivery discount</dt>
            <dd>−{{ cash(doc.money.deliveryDiscountAmount, doc.money.currency) }}</dd>
          }
          <dt class="invoice__total">Total</dt>
          <dd class="invoice__total" data-testid="invoice-total">{{ cash(doc.money.grandTotal, doc.money.currency) }}</dd>
        </dl>

        <!-- No VAT line: prices are VAT-inclusive (doc 08 D2). Said on the document, because a customer
             who looks for one has to find the answer rather than an omission. -->
        <p class="invoice__note">All prices include VAT.</p>

        <dl class="invoice__totals">
          <dt>Paid by</dt>
          <dd>{{ doc.paymentMethodLabel }}</dd>
          @if (doc.paymentReference) {
            <dt>Their reference</dt>
            <dd>{{ doc.paymentReference }}</dd>
          }
        </dl>
      </article>
    } @else if (isLoading()) {
      <p class="adm-muted">Loading the invoice…</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-2);
    }
    .invoice {
      display: grid;
      gap: var(--space-3);
      padding: var(--space-3);
      background: #fff;
      color: #000;
      border: 1px solid var(--color-border);
      font-size: var(--text-data);
      max-width: 820px;
      min-width: 0;
    }
    .invoice__head,
    .invoice__parties {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr));
      gap: var(--space-2);
    }
    .invoice__meta {
      /* Right-aligned on a wide sheet, left-aligned when it wraps underneath on a phone — a right-aligned
         block that has wrapped to full width reads as a mistake. */
      text-align: start;
    }
    @media (min-width: 560px) {
      .invoice__meta {
        text-align: end;
      }
    }
    .invoice__shop {
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
    }
    .invoice__title {
      font-size: var(--text-h3);
      font-weight: var(--weight-regular);
    }
    .invoice__lines {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-small);
    }
    .invoice__lines caption {
      text-align: start;
      padding-bottom: var(--space-1);
    }
    .invoice__lines th,
    .invoice__lines td {
      padding: var(--space-0-5) var(--space-1);
      border-bottom: 1px solid var(--color-border);
      text-align: start;
      vertical-align: top;
      overflow-wrap: anywhere;
    }
    .invoice__right {
      text-align: end;
    }
    .invoice__totals {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 2px var(--space-2);
      margin: 0;
      justify-self: end;
      min-width: min(100%, 280px);
    }
    .invoice__totals dd {
      margin: 0;
      text-align: end;
    }
    .invoice__total {
      font-weight: var(--weight-semibold);
      border-top: 1px solid #000;
      padding-top: 2px;
    }
    .invoice__note {
      font-size: var(--text-small);
    }

    /* The bars are the screen's, not the document's: a printed invoice with a Print button on it is a
       printed invoice with a button on it. */
    @media print {
      .invoice-bar {
        display: none;
      }
      .invoice {
        border: 0;
        padding: 0;
        max-width: none;
      }
    }
  `,
})
export class OrderInvoicePage {
  /** The shop a customer bought from, from the one place it is written down. */
  protected readonly shop = BRAND.name;

  private readonly api = inject(AdminOrderApi);
  private readonly route = inject(ActivatedRoute);

  protected readonly invoice = signal<OrderInvoice | null>(null);
  protected readonly isLoading = signal(true);
  protected readonly problem = signal<CatalogFailure | null>(null);

  private loaded = '';

  constructor() {
    // `ActivatedRoute` rather than a signal `input`, for the reason `OrderDetailPage` records in full:
    // `withComponentInputBinding()` is not in this application's router configuration, so a route input
    // would never be populated. Subscribed rather than read once, because the router reuses this
    // component when a staff member opens a second invoice.
    this.route.paramMap.subscribe((params) =>
      this.ensureLoaded(params.get('orderPublicId') ?? ''),
    );
  }

  /** The order's public token, from the route. */
  protected orderPublicId(): string {
    return this.route.snapshot.paramMap.get('orderPublicId') ?? '';
  }

  /** The invoice's own currency, never an assumed one — see `money`. */
  protected cash(amount: number, currency: string): string {
    return money(amount, currency);
  }

  protected day(utc: string): string {
    return colomboDate(utc);
  }

  /**
   * Prints the sheet.
   *
   * `window.print()` rather than a server-rendered PDF, which is a deliberate limit rather than a
   * shortcut: doc 02 ADM-09 asks for "print invoice" and a browser's own print dialogue produces a PDF on
   * every platform the client uses, with no font embedding, no rendering service and no second copy of the
   * layout to keep in step. The `@media print` rules above are what make it a document rather than a
   * screenshot of a screen.
   */
  protected print(): void {
    globalThis.print();
  }

  private ensureLoaded(token: string): void {
    if (this.loaded === token) {
      return;
    }

    this.loaded = token;
    this.isLoading.set(true);
    this.problem.set(null);

    this.api.invoice(token).subscribe({
      next: (doc) => {
        this.invoice.set(doc);
        this.isLoading.set(false);
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.problem.set(toCatalogFailure(error));
      },
    });
  }
}
