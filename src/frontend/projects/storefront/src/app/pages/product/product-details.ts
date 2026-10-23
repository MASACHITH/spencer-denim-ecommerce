import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { ProductDetailDto } from '../../core/catalog/catalog.model';

/** One row of the details list: a label and what the catalogue has under it. */
interface DetailRow {
  readonly label: string;
  readonly value: string;
}

/**
 * The written half of the product page (PDP-05): description, material, fit, occasion, age group
 * and care.
 *
 * Each block is a native `<details>`, so it opens and closes with no script, is operable from the
 * keyboard by definition, and — this is the part that matters for SEO — has its contents in the
 * server-rendered HTML whether it is open or shut. The description is open on arrival because
 * GLB-11 wants it read, and because it is what the shopper came for.
 *
 * A block with nothing in it is not rendered at all, rather than rendered empty: every one of
 * these columns is nullable in `catalog.Products` and an admin may fill none of them.
 *
 * **Not here: the delivery estimate, the delivery methods and the returns terms** that PDP-05 also
 * asks for. They are `ProductDeliveryReturns` (issue #33), the disclosures drawn straight after this
 * list, because they come from the delivery rates and the owner's settings rather than from the
 * product — the same for every product, and read once per session.
 */
@Component({
  selector: 'sd-product-details',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="details">
      @if (descriptionHtml(); as html) {
        <details class="details__block" open>
          <summary class="details__summary">Description</summary>
          <div class="details__body rich" [innerHTML]="html"></div>
        </details>
      } @else if (shortDescription(); as text) {
        <details class="details__block" open>
          <summary class="details__summary">Description</summary>
          <div class="details__body">
            <p>{{ text }}</p>
          </div>
        </details>
      }

      @if (rows().length > 0) {
        <details class="details__block">
          <summary class="details__summary">Fabric &amp; fit</summary>
          <div class="details__body">
            <dl class="details__list">
              @for (row of rows(); track row.label) {
                <div class="details__row">
                  <dt>{{ row.label }}</dt>
                  <dd>{{ row.value }}</dd>
                </div>
              }
            </dl>
          </div>
        </details>
      }

      @if (care(); as text) {
        <details class="details__block">
          <summary class="details__summary">Care</summary>
          <div class="details__body">
            <p>{{ text }}</p>
          </div>
        </details>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .details__block {
      border-top: 1px solid var(--color-border);
    }
    .details__block:last-of-type {
      border-bottom: 1px solid var(--color-border);
    }
    .details__summary {
      display: flex;
      align-items: center;
      min-height: var(--tap);
      padding-block: var(--space-1);
      font-size: var(--text-small);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    .details__body {
      padding-bottom: var(--space-2);
      max-width: 62ch;
      color: var(--color-text-muted);
    }
    .details__row {
      display: grid;
      grid-template-columns: 9rem minmax(0, 1fr);
      gap: var(--space-1);
      padding-block: var(--space-0-5);
    }
    .details__row dt {
      color: var(--color-text);
    }
    @media (max-width: 374px) {
      .details__row {
        grid-template-columns: minmax(0, 1fr);
        gap: 0;
      }
    }
  `,
})
export class ProductDetails {
  readonly product = input.required<ProductDetailDto>();

  /**
   * The long copy, which is **rich text, not a sentence**.
   *
   * `catalog.Products.Description` is `nvarchar(max)` documented in doc 08 §3.2 as "Sanitised
   * HTML", and the demo seeder writes real markup into it (`<p>…</p><p>…</p>`). Interpolating it
   * printed the tags at the shopper, which is what clicking through the real API showed.
   *
   * **`[innerHTML]` is the only sanitiser in the chain, not the second line of defence.** It puts
   * the value through Angular's own `SecurityContext.HTML`, which strips scripts, event handlers
   * and `javascript:` URLs whatever an admin pasted in. This comment used to end "the field is
   * sanitised on the way in", and that was true of no code path: there is no HTML sanitiser
   * anywhere in the backend — the only `Sanitise*` in `src/backend` is `MediaService
   * .SanitiseFileName`. What makes the field safe today is that nothing but the seeder can write
   * it, because there is no admin write path yet. Inbound sanitisation is owed before ADM-11
   * ships (issue #70), and until it exists everything rendered here rests on this one binding.
   */
  protected readonly descriptionHtml = computed(() => trimmed(this.product().description));

  /** The teaser, which is plain text. Shown when a product has no long copy of its own. */
  protected readonly shortDescription = computed(() =>
    trimmed(this.product().shortDescription),
  );

  protected readonly care = computed(() => trimmed(this.product().careInstructions));

  /** Only the attributes this product actually carries. */
  protected readonly rows = computed<readonly DetailRow[]>(() => {
    const attributes = this.product().attributes;

    return [
      { label: 'Material', value: trimmed(attributes.material) },
      { label: 'Fit', value: trimmed(attributes.fit) },
      { label: 'Occasion', value: trimmed(attributes.occasion) },
      { label: 'Age group', value: trimmed(attributes.ageGroup) },
    ].filter((row): row is DetailRow => row.value !== null);
  });
}

function trimmed(value: string | null | undefined): string | null {
  const text = value?.trim();
  return text ? text : null;
}
