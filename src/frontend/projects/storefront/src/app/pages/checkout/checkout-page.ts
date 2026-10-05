import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  RESPONSE_INIT,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Meta, Title } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

import { CartStore } from '../../core/cart/cart.store';
import { formatLkr } from '../../core/catalog/money';
import {
  CheckoutApi,
  changedTotalsOf,
  checkoutErrorMessage,
  checkoutFailure,
  fieldErrorsOf,
  shortLinesOf,
} from '../../core/checkout/checkout-api';
import {
  CheckoutLineDto,
  CheckoutSessionDto,
  DeliveryOptionDto,
  EMPTY_CHECKOUT,
  checkoutBlockerMessage,
  deliveryEstimate,
  looksLikeEmail,
} from '../../core/checkout/checkout.model';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { ServerHandover } from '../../core/platform/server-handover';

/** Where the server leaves the checkout it rendered, for the browser that hydrates the page. */
const HANDOVER_KEY = 'checkout';

/** The fields this form owns. The keys are the API's field names, so an error lands on its own input. */
interface CheckoutForm {
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  recipientName: string;
  addressPhone: string;
  line1: string;
  line2: string;
  city: string;
  postalCode: string;
  landmark: string;
  customerNote: string;
}

const EMPTY_FORM: CheckoutForm = {
  firstName: '',
  lastName: '',
  email: '',
  phone: '',
  recipientName: '',
  addressPhone: '',
  line1: '',
  line2: '',
  city: '',
  postalCode: '',
  landmark: '',
  customerNote: '',
};

/**
 * Checkout (CHK-01, CHK-02, CHK-03).
 *
 * **Every checkout here is a guest checkout, and that is a fact rather than a choice.** There is no
 * customer authentication anywhere in the storefront — ACC-01 is W3-01 and unbuilt — so there is no
 * sign-in step, no saved address to pick from and no "create an account" box. Doc 11 P6 step 1 offers a
 * signed-in shopper pre-filled details and up to ten saved addresses; that arrives with W3-01, and none
 * of it is stubbed here, because a sign-in link that went nowhere would be worse than its absence.
 *
 * **It renders on the server**, so the request is started from the constructor and the HTML carries the
 * bag and the form. That makes the markup personal in a way no other page's is: it holds a name, an email
 * address, a phone number, a street address and the idempotency token that places the order. So the
 * response says `Cache-Control: no-store, private` with `Vary: Cookie` — a shared cache holding this page
 * would hand one shopper another shopper's details — and `noindex`, because nothing about a checkout
 * belongs in a search result.
 *
 * ## What is not here, and why
 *
 * **No promo code field.** CART-01 and doc 11 P6 step 5 both ask for one, and `marketing.PromoCodes`
 * does not exist (ADM-13, W3-05). Every code a shopper typed would be refused, which teaches them the
 * shop is broken. The server's arithmetic already applies the allocation and the Rs. 1.00 clamp, so the
 * field arrives with the table.
 *
 * **No bank details and no payment step.** Doc 11 P6 step 10 shows the account to transfer to, the
 * reference to quote and a slip upload; all of that is doc 11 P7 — PAY-01, W3-03's step 3. Checkout ends
 * by placing the order and showing its number (CHK-04), and the success page says the order is waiting
 * for payment without promising details it does not have.
 *
 * **No "I agree to the Terms" checkbox.** Doc 11 §7 G-31 asks for the sentence and explicitly records
 * that v1 stores no acceptance flag, so the sentence is here and the checkbox is not. It does not link to
 * `/terms`, because GLB-10's content pages are unpublished and a link to an empty page is worse than
 * none.
 *
 * ## The layout rules worth stating, and why they are not in the stylesheet
 *
 * Component styles count against `angular.json`'s 4 kB `anyComponentStyle` budget **comments included**,
 * and this page's reasoning does not fit inside it. So it lives here, where TypeScript strips it:
 *
 * - **`min-width: 0` on the form, the fieldsets, the fields and the summary.** A grid child defaults to
 *   `min-width: auto`, so a long unbroken value in an input — an email address with no spaces, a product
 *   name — pushes its column and therefore the page wider than the viewport. These declarations are what
 *   keep 320 px free of horizontal scroll, and `checkout.e2e.ts` measures it at all eight breakpoints
 *   with an error message on screen as well as without one.
 * - **`min-height: 48px` and `width: 100%` on every input**, matching `.btn` so a field and a button
 *   beside each other line up. 48 is above doc 02 GLB-09's 44 px floor; `box-sizing: border-box` comes
 *   from the shared reset, which is what stops the padding adding to the column's width.
 * - **The whole choice row is the `<label>`**, so the tap target is the row rather than the 16 px radio,
 *   and the focus ring is drawn on the row with `:has(:focus-visible)`. A ring on the dot inside a
 *   bordered row is almost invisible — doc 06's lesson L4 — and `checkout.e2e.ts` photographs the strip
 *   outside each edge rather than reading `outlineWidth`.
 * - **One layout breakpoint at 48rem** (the two-column field rows) and one at 64rem (the summary beside
 *   the form, sticky). Below 48rem the summary sits under the form, which is the order a shopper fills
 *   them in.
 * - **`overflow-wrap: anywhere`** on product names and the order number, which are the two strings long
 *   enough to widen a column on a 320 px screen.
 */
@Component({
  selector: 'sd-checkout-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container checkout">
      <h1 class="checkout__title" id="checkout-title">Checkout</h1>

      <!-- One live region for the whole page, so a screen reader hears a refusal it did not scroll to.
           "role="alert"" on the error and "role="status"" on the quiet updates: a sold-out line is an
           interruption, a recalculated delivery fee is not. -->
      @if (error(); as message) {
        <p class="checkout__error" role="alert">{{ message }}</p>
      }

      @if (isLoading() && isEmpty()) {
        <p class="checkout__muted" role="status">Loading your checkout…</p>
      } @else if (isEmpty()) {
        <div class="checkout__empty">
          <p class="checkout__muted">Your bag is empty, so there is nothing to check out.</p>
          <a class="btn btn--primary" routerLink="/">Start shopping</a>
        </div>
      } @else {
        @for (blocker of blockers(); track blocker) {
          <p class="checkout__notice checkout__notice--warn" role="status">
            {{ blockerMessage(blocker) }}
            @if (blocker === 'LineNotPurchasable') {
              <a class="link" routerLink="/bag">Back to your bag</a>
            }
          </p>
        }

        <div class="checkout__layout">
          <!-- The submit button lives in the summary, which is a sibling of the form, so it is
               associated with it by "form="checkout-form"" rather than by nesting. That keeps the native
               submit behaviour — Enter in any field places the order — which nesting the button inside
               the summary would have thrown away. -->
          <form class="checkout__form" id="checkout-form" novalidate (submit)="placeOrder($event)">
            <fieldset class="checkout__group">
              <legend class="checkout__legend">Your details</legend>

              <div class="checkout__row">
                <div class="checkout__field">
                  <label class="checkout__label" for="co-firstName">First name</label>
                  <input
                    class="checkout__input"
                    id="co-firstName"
                    name="firstName"
                    type="text"
                    autocomplete="given-name"
                    maxlength="80"
                    required
                    [value]="form().firstName"
                    [attr.aria-invalid]="messageFor('firstName') ? 'true' : null"
                    [attr.aria-describedby]="messageFor('firstName') ? 'co-firstName-error' : null"
                    (input)="set('firstName', $event)"
                  />
                  @if (messageFor('firstName'); as message) {
                    <p class="checkout__fielderror" id="co-firstName-error">{{ message }}</p>
                  }
                </div>

                <div class="checkout__field">
                  <label class="checkout__label" for="co-lastName">
                    Last name <span class="checkout__optional">(optional)</span>
                  </label>
                  <input
                    class="checkout__input"
                    id="co-lastName"
                    name="lastName"
                    type="text"
                    autocomplete="family-name"
                    maxlength="80"
                    [value]="form().lastName"
                    (input)="set('lastName', $event)"
                  />
                </div>
              </div>

              <div class="checkout__row">
                <div class="checkout__field">
                  <label class="checkout__label" for="co-email">Email</label>
                  <input
                    class="checkout__input"
                    id="co-email"
                    name="email"
                    type="email"
                    inputmode="email"
                    autocomplete="email"
                    maxlength="256"
                    required
                    [value]="form().email"
                    [attr.aria-invalid]="messageFor('email') ? 'true' : null"
                    [attr.aria-describedby]="
                      messageFor('email') ? 'co-email-error co-email-hint' : 'co-email-hint'
                    "
                    (input)="set('email', $event)"
                  />
                  <p class="checkout__hint" id="co-email-hint">
                    We send your order number and its link here.
                  </p>
                  @if (messageFor('email'); as message) {
                    <p class="checkout__fielderror" id="co-email-error">{{ message }}</p>
                  }
                </div>

                <div class="checkout__field">
                  <label class="checkout__label" for="co-phone">Phone</label>
                  <input
                    class="checkout__input"
                    id="co-phone"
                    name="phone"
                    type="tel"
                    inputmode="tel"
                    autocomplete="tel"
                    maxlength="20"
                    required
                    [value]="form().phone"
                    [attr.aria-invalid]="messageFor('phone') ? 'true' : null"
                    [attr.aria-describedby]="messageFor('phone') ? 'co-phone-error' : null"
                    (input)="set('phone', $event)"
                  />
                  @if (messageFor('phone'); as message) {
                    <p class="checkout__fielderror" id="co-phone-error">{{ message }}</p>
                  }
                </div>
              </div>
            </fieldset>

            <fieldset class="checkout__group">
              <legend class="checkout__legend">Where it is going</legend>

              <div class="checkout__field">
                <label class="checkout__label" for="co-district">District</label>
                <select
                  class="checkout__input"
                  id="co-district"
                  name="districtId"
                  required
                  [attr.aria-invalid]="messageFor('districtId') ? 'true' : null"
                  [attr.aria-describedby]="
                    messageFor('districtId') ? 'co-district-error co-district-hint' : 'co-district-hint'
                  "
                  (change)="chooseDistrict($event)"
                >
                  <option value="" [selected]="districtId() === null">Choose a district</option>
                  @for (district of session().districts; track district.id) {
                    <option [value]="district.id" [selected]="district.id === districtId()">
                      {{ district.name }}
                    </option>
                  }
                </select>
                <p class="checkout__hint" id="co-district-hint">
                  The delivery options and the fee depend on the district.
                </p>
                @if (messageFor('districtId'); as message) {
                  <p class="checkout__fielderror" id="co-district-error">{{ message }}</p>
                }
              </div>

              @if (districtId() !== null) {
                <fieldset class="checkout__group checkout__group--nested">
                  <legend class="checkout__legend checkout__legend--sub">Delivery</legend>

                  @if (deliveryOptions().length === 0) {
                    <p class="checkout__muted" role="status">
                      No delivery option is available for that district.
                    </p>
                  }

                  @for (option of deliveryOptions(); track option.id) {
                    <label class="checkout__choice" [class.is-unavailable]="!option.isAvailable">
                      <input
                        class="checkout__radio"
                        type="radio"
                        name="deliveryMethodId"
                        [value]="option.id"
                        [checked]="option.id === deliveryMethodId()"
                        [disabled]="!option.isAvailable"
                        (change)="chooseDeliveryMethod(option)"
                      />
                      <span class="checkout__choice-body">
                        <span class="checkout__choice-head">
                          <span class="checkout__choice-name">{{ option.name }}</span>
                          @if (option.isAvailable) {
                            <span class="checkout__choice-fee">
                              @if (option.feeAfterDiscount === 0) {
                                Free
                              } @else {
                                {{ money(option.feeAfterDiscount ?? 0) }}
                              }
                            </span>
                          } @else {
                            <span class="checkout__choice-fee checkout__muted">Not available here</span>
                          }
                        </span>
                        @if (estimate(option); as days) {
                          <span class="checkout__choice-meta">Estimated {{ days }}</span>
                        }
                        @if (option.description) {
                          <span class="checkout__choice-meta">{{ option.description }}</span>
                        }
                        <!-- Doc 11 P6 step 4's "free delivery above Rs. 10,000.00" message. Only shown
                             when the threshold exists *and* has not already been met, because once it is
                             met the fee says "Free" and repeating the condition is noise. -->
                        @if (
                          option.isAvailable &&
                          option.freeAboveAmount !== null &&
                          option.feeAfterDiscount !== 0
                        ) {
                          <span class="checkout__choice-meta">
                            Free above {{ money(option.freeAboveAmount) }}
                          </span>
                        }
                      </span>
                    </label>
                  }

                  @if (messageFor('deliveryMethodId'); as message) {
                    <p class="checkout__fielderror">{{ message }}</p>
                  }
                </fieldset>

                @if (needsAddress()) {
                  <div class="checkout__row">
                    <div class="checkout__field">
                      <label class="checkout__label" for="co-recipient">Recipient's name</label>
                      <input
                        class="checkout__input"
                        id="co-recipient"
                        name="address.recipientName"
                        type="text"
                        autocomplete="shipping name"
                        maxlength="120"
                        required
                        [value]="form().recipientName"
                        [attr.aria-invalid]="messageFor('address.recipientName') ? 'true' : null"
                        [attr.aria-describedby]="
                          messageFor('address.recipientName') ? 'co-recipient-error' : null
                        "
                        (input)="set('recipientName', $event)"
                      />
                      @if (messageFor('address.recipientName'); as message) {
                        <p class="checkout__fielderror" id="co-recipient-error">{{ message }}</p>
                      }
                    </div>

                    <div class="checkout__field">
                      <label class="checkout__label" for="co-address-phone">
                        Phone at this address
                      </label>
                      <input
                        class="checkout__input"
                        id="co-address-phone"
                        name="address.phone"
                        type="tel"
                        inputmode="tel"
                        autocomplete="shipping tel"
                        maxlength="20"
                        required
                        [value]="form().addressPhone"
                        [attr.aria-invalid]="messageFor('address.phone') ? 'true' : null"
                        [attr.aria-describedby]="messageFor('address.phone') ? 'co-address-phone-error' : null"
                        (input)="set('addressPhone', $event)"
                      />
                      @if (messageFor('address.phone'); as message) {
                        <p class="checkout__fielderror" id="co-address-phone-error">{{ message }}</p>
                      }
                    </div>
                  </div>

                  <div class="checkout__field">
                    <label class="checkout__label" for="co-line1">Address</label>
                    <input
                      class="checkout__input"
                      id="co-line1"
                      name="address.line1"
                      type="text"
                      autocomplete="shipping address-line1"
                      maxlength="150"
                      required
                      [value]="form().line1"
                      [attr.aria-invalid]="messageFor('address.line1') ? 'true' : null"
                      [attr.aria-describedby]="messageFor('address.line1') ? 'co-line1-error' : null"
                      (input)="set('line1', $event)"
                    />
                    @if (messageFor('address.line1'); as message) {
                      <p class="checkout__fielderror" id="co-line1-error">{{ message }}</p>
                    }
                  </div>

                  <div class="checkout__field">
                    <label class="checkout__label" for="co-line2">
                      Apartment, floor or area <span class="checkout__optional">(optional)</span>
                    </label>
                    <input
                      class="checkout__input"
                      id="co-line2"
                      name="address.line2"
                      type="text"
                      autocomplete="shipping address-line2"
                      maxlength="150"
                      [value]="form().line2"
                      (input)="set('line2', $event)"
                    />
                  </div>

                  <div class="checkout__row">
                    <div class="checkout__field">
                      <label class="checkout__label" for="co-city">Town or city</label>
                      <input
                        class="checkout__input"
                        id="co-city"
                        name="address.city"
                        type="text"
                        autocomplete="shipping address-level2"
                        maxlength="80"
                        required
                        [value]="form().city"
                        [attr.aria-invalid]="messageFor('address.city') ? 'true' : null"
                        [attr.aria-describedby]="messageFor('address.city') ? 'co-city-error' : null"
                        (input)="set('city', $event)"
                      />
                      @if (messageFor('address.city'); as message) {
                        <p class="checkout__fielderror" id="co-city-error">{{ message }}</p>
                      }
                    </div>

                    <div class="checkout__field">
                      <label class="checkout__label" for="co-postcode">
                        Postal code <span class="checkout__optional">(optional)</span>
                      </label>
                      <input
                        class="checkout__input"
                        id="co-postcode"
                        name="address.postalCode"
                        type="text"
                        inputmode="numeric"
                        autocomplete="shipping postal-code"
                        maxlength="10"
                        [value]="form().postalCode"
                        (input)="set('postalCode', $event)"
                      />
                    </div>
                  </div>

                  <div class="checkout__field">
                    <label class="checkout__label" for="co-landmark">
                      Landmark <span class="checkout__optional">(optional)</span>
                    </label>
                    <input
                      class="checkout__input"
                      id="co-landmark"
                      name="address.landmark"
                      type="text"
                      maxlength="150"
                      [value]="form().landmark"
                      (input)="set('landmark', $event)"
                    />
                  </div>
                } @else if (deliveryMethodId() !== null) {
                  <!-- Store pickup (CHK-02). The list is empty until the client gives us a shop address
                       (doc 08 §9 seeds zero pickup locations, D8), and the method ships disabled for
                       exactly that reason — so this branch is reachable only once both exist. -->
                  <fieldset class="checkout__group checkout__group--nested">
                    <legend class="checkout__legend checkout__legend--sub">Collect from</legend>

                    @if (pickupLocations().length === 0) {
                      <p class="checkout__muted" role="status">
                        No store is available for collection at the moment.
                      </p>
                    }

                    @for (location of pickupLocations(); track location.id) {
                      <label class="checkout__choice">
                        <input
                          class="checkout__radio"
                          type="radio"
                          name="pickupLocationId"
                          [value]="location.id"
                          [checked]="location.id === pickupLocationId()"
                          (change)="choosePickupLocation(location.id)"
                        />
                        <span class="checkout__choice-body">
                          <span class="checkout__choice-name">{{ location.name }}</span>
                          <span class="checkout__choice-meta">
                            {{ location.addressLine }}, {{ location.city }}
                          </span>
                          @if (location.openingHours) {
                            <span class="checkout__choice-meta">{{ location.openingHours }}</span>
                          }
                        </span>
                      </label>
                    }

                    @if (messageFor('pickupLocationId'); as message) {
                      <p class="checkout__fielderror">{{ message }}</p>
                    }
                  </fieldset>
                }
              }
            </fieldset>

            <fieldset class="checkout__group">
              <legend class="checkout__legend">How you would like to pay</legend>

              @if (session().paymentMethods.length === 0) {
                <p class="checkout__muted" role="status">
                  No payment method is available at the moment.
                </p>
              }

              @for (method of session().paymentMethods; track method.code) {
                <label class="checkout__choice">
                  <input
                    class="checkout__radio"
                    type="radio"
                    name="paymentMethodCode"
                    [value]="method.code"
                    [checked]="method.code === paymentMethodCode()"
                    (change)="choosePaymentMethod(method.code)"
                  />
                  <span class="checkout__choice-body">
                    <span class="checkout__choice-name">{{ method.displayName }}</span>
                    @if (method.checkoutDescription) {
                      <span class="checkout__choice-meta">{{ method.checkoutDescription }}</span>
                    }
                  </span>
                </label>
              }

              @if (messageFor('paymentMethodCode'); as message) {
                <p class="checkout__fielderror">{{ message }}</p>
              }
            </fieldset>

            <fieldset class="checkout__group">
              <legend class="checkout__legend">Anything else</legend>

              <div class="checkout__field">
                <label class="checkout__label" for="co-note">
                  Note for us <span class="checkout__optional">(optional)</span>
                </label>
                <textarea
                  class="checkout__input checkout__textarea"
                  id="co-note"
                  name="customerNote"
                  rows="3"
                  maxlength="500"
                  [value]="form().customerNote"
                  (input)="set('customerNote', $event)"
                ></textarea>
              </div>
            </fieldset>
          </form>

          <aside class="checkout__summary" aria-labelledby="checkout-summary-title">
            <h2 class="checkout__summary-title" id="checkout-summary-title">Order summary</h2>

            <ul class="checkout__lines">
              @for (line of lines(); track line.variantId) {
                <li class="checkout__line" [class.is-unavailable]="line.status !== 'Buyable'">
                  @if (line.imageUrl) {
                    <img
                      class="checkout__thumb"
                      [src]="line.imageUrl"
                      [alt]="line.imageAltText ?? ''"
                      width="56"
                      height="75"
                      loading="lazy"
                    />
                  }
                  <span class="checkout__line-body">
                    <span class="checkout__line-name">{{ line.productName }}</span>
                    <span class="checkout__line-meta">
                      {{ line.colourName }} · {{ line.sizeLabel }} · Qty {{ line.quantity }}
                    </span>
                    @if (lineNotice(line); as notice) {
                      <span class="checkout__line-notice">{{ notice }}</span>
                    }
                  </span>
                  <span class="checkout__line-price">{{ money(line.lineSubtotal) }}</span>
                </li>
              }
            </ul>

            <!-- **The ladder has to add up, and this is the arithmetic:**
                 Subtotal − Savings − Discount + Delivery − Free delivery = Total.
                 Subtotal is the original ("was") total and Total is the net figure, which is the labelling
                 the bag settled on after it shipped a summary whose subtraction was false. The two pages
                 print the same three rows from the same server-side arithmetic, so a shopper moving
                 between them never sees one word mean two things. "checkout-page.spec.ts" reads the
                 figures back off the rendered DOM and asserts the sum. -->
            <dl class="checkout__sums">
              <div>
                <dt>Subtotal ({{ totals().itemCount }} {{ totals().itemCount === 1 ? 'item' : 'items' }})</dt>
                <dd data-sum="subtotal">{{ money(totals().itemsOriginalTotal) }}</dd>
              </div>

              @if (totals().savedTotal > 0) {
                <div>
                  <dt>Savings</dt>
                  <dd class="text-sale" data-sum="savings">&minus;{{ money(totals().savedTotal) }}</dd>
                </div>
              }

              @if (totals().promoDiscountAmount > 0) {
                <div>
                  <dt>Discount</dt>
                  <dd class="text-sale" data-sum="promo">
                    &minus;{{ money(totals().promoDiscountAmount) }}
                  </dd>
                </div>
              }

              <div>
                <dt>Delivery</dt>
                @if (totals().deliveryFeeAmount === null) {
                  <!-- Null, not zero. Doc 08 §6 step 10 has no fee without a district and a method, and
                       printing 0.00 here would be a promise of free delivery. -->
                  <dd class="checkout__muted" data-sum="delivery-pending">Choose a district</dd>
                } @else {
                  <dd data-sum="delivery">{{ money(totals().deliveryFeeAmount ?? 0) }}</dd>
                }
              </div>

              @if (totals().deliveryDiscountAmount > 0) {
                <div>
                  <dt>Free delivery</dt>
                  <dd class="text-sale" data-sum="delivery-discount">
                    &minus;{{ money(totals().deliveryDiscountAmount) }}
                  </dd>
                </div>
              }

              <div class="checkout__sums-total">
                <dt>Total</dt>
                @if (totals().grandTotal === null) {
                  <dd class="checkout__muted" data-sum="total-pending">Choose a district</dd>
                } @else {
                  <dd data-sum="total">{{ money(totals().grandTotal ?? 0) }}</dd>
                }
              </div>
            </dl>

            @if (shortLines(); as short) {
              <ul class="checkout__notice checkout__notice--warn">
                @for (line of short; track line.variantId) {
                  <li>
                    {{ line.productName }}, {{ line.colourName }} / {{ line.sizeLabel }}:
                    @if (line.available === 0) {
                      sold out.
                    } @else if (line.available === 1) {
                      only 1 left.
                    } @else {
                      only {{ line.available }} left.
                    }
                  </li>
                }
              </ul>
              <a class="link" routerLink="/bag">Back to your bag</a>
            }

            <button
              class="btn btn--primary checkout__place"
              type="submit"
              form="checkout-form"
              [disabled]="!canSubmit()"
            >
              @if (isPlacing()) {
                Placing your order…
              } @else {
                Place order
              }
            </button>

            <p class="checkout__muted checkout__fineprint">
              By placing this order you agree to our terms and conditions. We will email your order
              number and a link to it.
            </p>
          </aside>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .checkout {
      display: grid;
      gap: var(--space-3);
      padding-block: var(--space-4) var(--space-12);
    }
    .checkout__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .checkout__muted {
      color: var(--color-text-muted);
    }
    .checkout__error,
    .checkout__notice--warn {
      padding: var(--space-1-5);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
    }
    .checkout__notice {
      display: grid;
      gap: var(--space-0-5);
      margin: 0;
      padding-inline-start: var(--space-1-5);
    }
    .checkout__empty {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      padding-block: var(--space-4);
    }
    .checkout__layout {
      display: grid;
      gap: var(--space-4);
      align-items: start;
    }
    .checkout__form {
      display: grid;
      gap: var(--space-3);
      /* Keeps 320 px free of horizontal scroll — see the class remarks. */
      min-width: 0;
    }
    .checkout__group {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
      padding: 0;
      border: 0;
    }
    .checkout__group--nested {
      gap: var(--space-1);
    }
    .checkout__legend {
      padding: 0;
      font-size: var(--text-h3);
      letter-spacing: -0.01em;
    }
    .checkout__legend--sub {
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .checkout__row {
      display: grid;
      gap: var(--space-2);
    }
    .checkout__field {
      display: grid;
      gap: var(--space-0-5);
      min-width: 0;
    }
    .checkout__label {
      font-size: var(--text-small);
      font-weight: var(--weight-medium);
    }
    .checkout__optional {
      font-weight: var(--weight-regular);
      color: var(--color-text-muted);
    }
    .checkout__input {
      /* GLB-09's tap-target floor, matched to .btn. See the class remarks. */
      min-height: 48px;
      width: 100%;
      padding: var(--space-1) var(--space-1-5);
      border: 1px solid var(--color-border);
      background: var(--color-bg);
    }
    .checkout__textarea {
      min-height: 96px;
      padding: var(--space-1-5);
      resize: vertical;
    }
    .checkout__hint,
    .checkout__line-meta,
    .checkout__choice-meta {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .checkout__fielderror {
      font-size: var(--text-small);
      color: var(--color-sale);
    }
    .checkout__choice {
      display: flex;
      gap: var(--space-1-5);
      align-items: flex-start;
      /* The row is the label, so the tap target is the row, not the dot. */
      min-height: 48px;
      padding: var(--space-1-5);
      border: 1px solid var(--color-border);
      cursor: pointer;
    }
    .checkout__choice:has(.checkout__radio:checked) {
      border-color: var(--color-primary);
    }
    /* The ring goes on the row, not the dot (doc 06 L4). */
    .checkout__choice:has(.checkout__radio:focus-visible) {
      outline: var(--focus-width) solid var(--color-primary);
      outline-offset: 2px;
    }
    .checkout__choice.is-unavailable,
    .checkout__line.is-unavailable {
      color: var(--color-text-muted);
    }
    .checkout__radio {
      width: 20px;
      height: 20px;
      margin-block-start: 2px;
      flex: none;
      accent-color: var(--color-primary);
    }
    .checkout__choice-body {
      display: grid;
      gap: var(--space-0-5);
      min-width: 0;
      flex: 1 1 auto;
    }
    .checkout__choice-head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      justify-content: space-between;
    }
    .checkout__choice-name {
      font-weight: var(--weight-medium);
    }
    .checkout__summary {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
      padding: var(--space-2);
      background: var(--color-bg-alt);
    }
    .checkout__summary-title {
      font-size: var(--text-h3);
      letter-spacing: -0.01em;
    }
    .checkout__lines {
      display: grid;
      gap: var(--space-1-5);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .checkout__line {
      display: flex;
      gap: var(--space-1);
      align-items: flex-start;
    }
    .checkout__thumb {
      flex: none;
      width: 56px;
      height: auto;
      object-fit: cover;
      background: var(--color-bg);
    }
    .checkout__line-body {
      display: grid;
      gap: 2px;
      min-width: 0;
      flex: 1 1 auto;
    }
    .checkout__line-name {
      font-size: var(--text-small);
      overflow-wrap: anywhere;
    }
    .checkout__line-price {
      flex: none;
      font-size: var(--text-small);
      font-variant-numeric: tabular-nums;
    }
    .checkout__sums {
      display: grid;
      gap: var(--space-1);
      margin: 0;
      padding-block-start: var(--space-1-5);
      border-block-start: 1px solid var(--color-border);
    }
    .checkout__sums > div {
      display: flex;
      gap: var(--space-1);
      justify-content: space-between;
    }
    .checkout__sums dd {
      margin: 0;
      font-variant-numeric: tabular-nums;
      text-align: end;
    }
    .checkout__sums-total {
      padding-block-start: var(--space-1);
      border-block-start: 1px solid var(--color-border);
      font-weight: var(--weight-medium);
    }
    .checkout__place {
      width: 100%;
    }
    .checkout__fineprint {
      font-size: var(--text-small);
    }

    @media (min-width: 48rem) {
      .checkout__row {
        grid-template-columns: 1fr 1fr;
      }
    }
    @media (min-width: 64rem) {
      .checkout__layout {
        grid-template-columns: minmax(0, 1fr) minmax(18rem, 22rem);
      }
      .checkout__summary {
        position: sticky;
        top: var(--space-2);
      }
    }
  `,
})
export class CheckoutPage {
  private readonly api = inject(CheckoutApi);
  private readonly cart = inject(CartStore);
  private readonly handover = inject(ServerHandover);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly current = signal<CheckoutSessionDto>(EMPTY_CHECKOUT);
  private readonly loading = signal(false);
  private readonly placing = signal(false);
  private readonly failure = signal<string | null>(null);
  private readonly serverErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly localErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly short = signal<ReadonlyMap<number, number> | null>(null);

  private readonly chosenDistrictId = signal<number | null>(null);
  private readonly chosenDeliveryMethodId = signal<number | null>(null);
  private readonly chosenPickupLocationId = signal<number | null>(null);
  private readonly chosenPaymentMethodCode = signal<string | null>(null);
  private readonly values = signal<CheckoutForm>({ ...EMPTY_FORM });

  /**
   * The idempotency token, captured **once** and never replaced.
   *
   * Every `GET /api/checkout` answers with a fresh one, and taking the newest would mean that changing
   * the district rotated the token — so a shopper who changed a district and then double-clicked Place
   * order would have sent two different tokens and placed two orders. The token's whole job is to be the
   * same across one shopper's attempts at one checkout, so it is pinned at the first read.
   */
  private checkoutKey = '';

  /** Guards against two reads in flight: the later answer wins and an earlier one is discarded. */
  private ticket = 0;

  constructor() {
    inject(Title).setTitle(brandPageTitle('Checkout'));
    inject(Meta).updateTag({
      name: 'description',
      content: `Complete your ${BRAND.name} order.`,
    });

    // Nothing about a checkout belongs in a search result, and unlike a 404 there is no status code
    // saying so. Cleared on the way out, or a client-side navigation carries `noindex` onto the next page.
    const robots = inject(RobotsMeta);
    robots.noindex();
    this.destroyRef.onDestroy(() => robots.clear());

    // This page's markup holds a name, an email address, a phone number and a street address. Nothing may
    // store it. `RESPONSE_INIT` is absent in the browser, where a client-side navigation has no response.
    const response = inject(RESPONSE_INIT, { optional: true });
    if (response) {
      const headers = new Headers(response.headers ?? {});
      headers.set('Cache-Control', 'no-store, no-cache, private');
      headers.set('Vary', 'Cookie');
      response.headers = headers;
    }

    // Taken in the constructor, before anything renders, so the browser's first render is identical to
    // the server's and hydration finds the DOM it expects (the reason `CartStore` does the same).
    const handedOver = this.handover.take<CheckoutSessionDto>(HANDOVER_KEY);
    if (handedOver) {
      this.apply(handedOver);
    }

    // From the constructor, so the request is in flight before the first change detection and the server
    // renders the bag and the form rather than a spinner. The browser reads again even when it was handed
    // a session: the handover is a snapshot of the stock and prices at the moment the server rendered,
    // and checkout is the page where "nearly right" is a commercial problem.
    this.read();
  }

  protected readonly session = this.current.asReadonly();
  protected readonly isLoading = this.loading.asReadonly();
  protected readonly isPlacing = this.placing.asReadonly();
  protected readonly error = this.failure.asReadonly();
  protected readonly form = this.values.asReadonly();
  protected readonly districtId = this.chosenDistrictId.asReadonly();
  protected readonly deliveryMethodId = this.chosenDeliveryMethodId.asReadonly();
  protected readonly pickupLocationId = this.chosenPickupLocationId.asReadonly();
  protected readonly paymentMethodCode = this.chosenPaymentMethodCode.asReadonly();

  protected readonly lines = computed(() => this.current().lines);
  protected readonly totals = computed(() => this.current().totals);
  protected readonly blockers = computed(() => this.current().blockers);
  protected readonly deliveryOptions = computed(() => this.current().deliveryOptions);
  protected readonly pickupLocations = computed(() => this.current().pickupLocations);

  protected readonly isEmpty = computed(() => this.current().lines.length === 0);

  /** Whether the chosen delivery method wants an address rather than a pickup location. */
  protected readonly needsAddress = computed(() => {
    const option = this.selectedOption();
    // True when nothing is chosen yet: the address fields are the ordinary case, and revealing them only
    // after a radio is pressed would make the form jump under the shopper.
    return option === null || option.requiresAddress;
  });

  /**
   * The lines a refused placement said were short, matched back to the bag so the message names the
   * product rather than a variant id.
   */
  protected readonly shortLines = computed(() => {
    const short = this.short();

    if (!short || short.size === 0) {
      return null;
    }

    return this.lines()
      .filter((line) => short.has(line.variantId))
      .map((line) => ({
        variantId: line.variantId,
        productName: line.productName,
        colourName: line.colourName,
        sizeLabel: line.sizeLabel,
        available: short.get(line.variantId) ?? 0,
      }));
  });

  /**
   * Whether Place order may be pressed.
   *
   * It checks what the shopper can see and fix — a blocker, a missing field, a delivery option that is
   * not available — and nothing else. **It is a courtesy, not the rule:** the server validates every one
   * of these again, refuses the order if the total has moved (G-10) and refuses it if the stock has gone,
   * because between this button being enabled and the request arriving the shop can have changed.
   */
  protected readonly canSubmit = computed(() => {
    if (this.isPlacing() || this.isEmpty() || this.blockers().length > 0) {
      return false;
    }

    const option = this.selectedOption();

    if (option === null || !option.isAvailable) {
      return false;
    }

    if (option.requiresAddress) {
      if (this.chosenDistrictId() === null) {
        return false;
      }
    } else if (this.chosenPickupLocationId() === null) {
      return false;
    }

    return (
      this.totals().grandTotal !== null &&
      this.chosenPaymentMethodCode() !== null &&
      this.missingFields().size === 0
    );
  });

  protected money(amount: number): string {
    return formatLkr(amount);
  }

  protected estimate(option: DeliveryOptionDto): string | null {
    return deliveryEstimate(option);
  }

  protected blockerMessage(blocker: CheckoutSessionDto['blockers'][number]): string {
    return checkoutBlockerMessage(blocker);
  }

  /** What a summary line says about itself, or null. */
  protected lineNotice(line: CheckoutLineDto): string | null {
    switch (line.status) {
      case 'Unavailable':
        return 'No longer available.';
      case 'OutOfStock':
        return 'Out of stock.';
      case 'NotEnoughStock':
        return line.availableQuantity === 1
          ? 'Only 1 left — reduce the quantity in your bag.'
          : `Only ${line.availableQuantity} left — reduce the quantity in your bag.`;
      default:
        return null;
    }
  }

  /**
   * The message to show against one field: the server's if it sent one, otherwise this page's own.
   *
   * The server's wins, and deliberately: if the two disagree, the storefront's validation is the one that
   * is wrong, and the server is the thing that knows what it refused.
   */
  protected messageFor(field: string): string | null {
    return this.serverErrors().get(field) ?? this.localErrors().get(field) ?? null;
  }

  protected set(field: keyof CheckoutForm, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement).value;

    this.values.update((current) => ({ ...current, [field]: value }));

    // A field the shopper is fixing stops complaining as they fix it, for both sources: a stale server
    // message next to a corrected value reads as "still wrong" and is the most common way a form traps
    // somebody.
    this.clearError(this.apiFieldFor(field));
    this.localErrors.set(this.missingFields());
  }

  protected chooseDistrict(event: Event): void {
    const raw = (event.target as HTMLSelectElement).value;
    const districtId = raw === '' ? null : Number(raw);

    this.chosenDistrictId.set(Number.isInteger(districtId) ? districtId : null);

    // The chosen method may not exist in the new district's zone at all, so it is dropped rather than
    // carried over: doc 11 P6's "this delivery option is no longer available, choose another", applied
    // before the shopper can submit it rather than after.
    this.chosenDeliveryMethodId.set(null);
    this.chosenPickupLocationId.set(null);
    this.clearError('districtId');
    this.read();
  }

  protected chooseDeliveryMethod(option: DeliveryOptionDto): void {
    if (!option.isAvailable) {
      return;
    }

    this.chosenDeliveryMethodId.set(option.id);

    if (option.requiresAddress) {
      this.chosenPickupLocationId.set(null);
    }

    this.clearError('deliveryMethodId');
    this.localErrors.set(this.missingFields());

    // Re-read, because the fee and the estimate come from the zone rate for *this* method and the summary
    // has to show the figure the order will be placed at.
    this.read();
  }

  protected choosePickupLocation(pickupLocationId: number): void {
    this.chosenPickupLocationId.set(pickupLocationId);
    this.clearError('pickupLocationId');
  }

  protected choosePaymentMethod(code: string): void {
    this.chosenPaymentMethodCode.set(code);
    this.clearError('paymentMethodCode');
  }

  protected placeOrder(event: Event): void {
    event.preventDefault();

    const missing = this.missingFields();
    this.localErrors.set(missing);

    if (missing.size > 0 || !this.canSubmit()) {
      return;
    }

    const option = this.selectedOption();
    const form = this.values();
    const trimmed = (value: string): string | null => (value.trim() === '' ? null : value.trim());

    this.placing.set(true);
    this.failure.set(null);
    this.short.set(null);
    this.serverErrors.set(new Map());

    this.api
      .place({
        checkoutKey: this.checkoutKey,
        firstName: trimmed(form.firstName),
        lastName: trimmed(form.lastName),
        email: trimmed(form.email),
        phone: trimmed(form.phone),
        deliveryMethodId: this.chosenDeliveryMethodId(),

        // Store pickup takes its district from the location (doc 08 §3.5 R7), so sending one would be
        // sending a field the shopper never chose.
        districtId: option?.requiresAddress ? this.chosenDistrictId() : null,
        pickupLocationId: option?.requiresAddress ? null : this.chosenPickupLocationId(),
        address: option?.requiresAddress
          ? {
              recipientName: trimmed(form.recipientName),
              phone: trimmed(form.addressPhone),
              line1: trimmed(form.line1),
              line2: trimmed(form.line2),
              city: trimmed(form.city),
              postalCode: trimmed(form.postalCode),
              landmark: trimmed(form.landmark),
            }
          : null,
        paymentMethodCode: this.chosenPaymentMethodCode(),
        expectedGrandTotal: this.totals().grandTotal,
        customerNote: trimmed(form.customerNote),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (order) => {
          this.placing.set(false);

          // The bag is empty now — the server deleted its lines inside the placement transaction — so the
          // header badge is told rather than left showing the items that have just been bought.
          this.cart.retry();

          // `replaceUrl`, so the back button does not return to a checkout whose bag is gone and whose
          // idempotency token has been used.
          void this.router.navigate(['/order', order.publicId], { replaceUrl: true });
        },
        error: (error: unknown) => {
          this.placing.set(false);
          this.failure.set(checkoutErrorMessage(error));

          switch (checkoutFailure(error)) {
            case 'invalid':
              this.serverErrors.set(fieldErrorsOf(error));
              break;

            case 'soldOut':
              this.short.set(shortLinesOf(error));

              // Re-read, so the lines and the totals on screen are the ones that refused the order rather
              // than the ones that were there a moment ago.
              this.read();
              break;

            case 'totalChanged': {
              // G-10: show the figure the server priced, and ask again. The order is **not** retried with
              // the new total — "we never place an order at a total the customer has not seen" (doc 11 P6)
              // means the shopper presses the button again, having seen it.
              const totals = changedTotalsOf(error);
              if (totals) {
                this.current.update((session) => ({ ...session, totals }));
              }
              this.read();
              break;
            }

            default:
              this.read();
              break;
          }
        },
      });
  }

  /** The delivery option the shopper has chosen, or null. */
  private selectedOption(): DeliveryOptionDto | null {
    const id = this.chosenDeliveryMethodId();

    return id === null
      ? null
      : (this.deliveryOptions().find((option) => option.id === id) ?? null);
  }

  /**
   * This page's own view of what is missing, keyed by the API's field names.
   *
   * **Required and the email shape, and nothing stricter.** The server validates all of it again, and a
   * client rule that refused something the server would have accepted is the expensive direction of being
   * wrong: it stops a shopper buying. Lengths are left to `maxlength`, which stops the typing rather than
   * scolding afterwards.
   */
  private missingFields(): ReadonlyMap<string, string> {
    const form = this.values();
    const errors = new Map<string, string>();
    const blank = (value: string) => value.trim() === '';

    if (blank(form.firstName)) {
      errors.set('firstName', 'Enter your first name.');
    }

    if (blank(form.email)) {
      errors.set('email', 'Enter your email address.');
    } else if (!looksLikeEmail(form.email.trim())) {
      errors.set('email', 'Enter an email address we can send your order to.');
    }

    if (blank(form.phone)) {
      errors.set('phone', 'Enter a phone number for the delivery.');
    }

    const option = this.selectedOption();

    if (option === null) {
      errors.set('deliveryMethodId', 'Choose how you would like your order delivered.');
      return errors;
    }

    if (!option.requiresAddress) {
      if (this.chosenPickupLocationId() === null) {
        errors.set('pickupLocationId', 'Choose which store to collect from.');
      }

      return errors;
    }

    if (this.chosenDistrictId() === null) {
      errors.set('districtId', 'Choose the district your order is going to.');
    }

    if (blank(form.recipientName)) {
      errors.set('address.recipientName', 'Enter the name of the person receiving the order.');
    }

    if (blank(form.addressPhone)) {
      errors.set('address.phone', 'Enter a phone number for the delivery address.');
    }

    if (blank(form.line1)) {
      errors.set('address.line1', 'Enter the street address.');
    }

    if (blank(form.city)) {
      errors.set('address.city', 'Enter the town or city.');
    }

    return errors;
  }

  /** The API's field name for one of this form's keys. */
  private apiFieldFor(field: keyof CheckoutForm): string {
    switch (field) {
      case 'recipientName':
        return 'address.recipientName';
      case 'addressPhone':
        return 'address.phone';
      case 'line1':
        return 'address.line1';
      case 'line2':
        return 'address.line2';
      case 'city':
        return 'address.city';
      case 'postalCode':
        return 'address.postalCode';
      case 'landmark':
        return 'address.landmark';
      default:
        return field;
    }
  }

  private clearError(field: string): void {
    this.serverErrors.update((errors) => {
      if (!errors.has(field)) {
        return errors;
      }

      const next = new Map(errors);
      next.delete(field);
      return next;
    });
  }

  private read(): void {
    const ticket = ++this.ticket;

    this.loading.set(true);

    this.api
      .get(this.chosenDistrictId(), this.chosenDeliveryMethodId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (session) => {
          // An answer to a question the shopper has already moved on from is discarded. Without this, a
          // quick district change followed by a quick method change could land the first answer last and
          // show a fee for the wrong zone.
          if (ticket !== this.ticket) {
            return;
          }

          this.loading.set(false);
          this.apply(session);
          this.handover.give(HANDOVER_KEY, session);
        },
        error: (error: unknown) => {
          if (ticket !== this.ticket) {
            return;
          }

          this.loading.set(false);
          this.failure.set(checkoutErrorMessage(error));
        },
      });
  }

  private apply(session: CheckoutSessionDto): void {
    this.current.set(session);

    // Pinned at the first read; see the field's remarks.
    if (this.checkoutKey === '' && session.checkoutKey !== '') {
      this.checkoutKey = session.checkoutKey;
    }

    // The server echoes back what it priced, so the controls follow the quote rather than the other way
    // round: if a method was dropped because it is unavailable in the new zone, the radio clears too.
    if (session.selectedDistrictId !== null) {
      this.chosenDistrictId.set(session.selectedDistrictId);
    }

    this.chosenDeliveryMethodId.set(session.selectedDeliveryMethodId);

    // One enabled provider is the ordinary case today (bank transfer; doc 08 §9 seeds PayPal disabled),
    // and making the shopper press a radio with one option is a step for nothing.
    if (this.chosenPaymentMethodCode() === null && session.paymentMethods.length === 1) {
      this.chosenPaymentMethodCode.set(session.paymentMethods[0].code);
    }
  }
}
