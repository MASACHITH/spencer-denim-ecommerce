import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { Modal } from '../../ui/modal/modal';
import { SizeGuideRefDto } from '../../core/catalog/catalog.model';

/**
 * The size guide (PDP-04), managed per category in admin and overridable per product.
 *
 * **The API has no measurements to give it.** `catalog.SizeGuides` holds `IntroHtml`,
 * `TableJson`, `MeasuringTipsHtml` and an image, and `CatalogReferenceData` seeds all four for
 * Women's apparel, Men's apparel and Kids and baby — but `SizeGuideRefDto`, the only size-guide
 * shape the catalogue API projects, is `(int Id, string Name)`. There is no endpoint that serves
 * the content, and this task does not change the backend.
 *
 * So this is built against the shape that exists and nothing is invented: no table is hard-coded
 * here, and no field is read that the API does not send. The dialog names the guide the product
 * is on and says, in the shopper's language, that the measurements are not published yet, with
 * the one route to help that does exist today (GLB-08, the WhatsApp button).
 *
 * The follow-up is an API change, not a component change: project `IntroHtml`, the parsed
 * `TableJson` and `MeasuringTipsHtml` onto `SizeGuideRefDto` (or add
 * `GET /api/catalog/size-guides/{id}`), and this component grows a table where the notice is.
 */
@Component({
  selector: 'sd-size-guide-modal',
  imports: [Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-modal
      [open]="open()"
      [heading]="heading()"
      closeLabel="Close size guide"
      panelId="size-guide"
      (closed)="closed.emit()"
    >
      <div class="guide">
        <p class="guide__lead">
          This product is sized to our <strong>{{ guide().name }}</strong> guide.
        </p>
        <p class="guide__note">
          The full measurement chart for it is not published on the site yet. If you are between
          sizes or want a check before you order, message us on WhatsApp — the green button at the
          bottom of every page — with the size you usually wear and we will tell you which one to
          take.
        </p>
      </div>
    </sd-modal>
  `,
  styles: `
    :host {
      display: contents;
    }
    .guide {
      display: grid;
      gap: var(--space-2);
      max-width: 62ch;
    }
    .guide__note {
      color: var(--color-text-muted);
    }
  `,
})
export class SizeGuideModal {
  readonly guide = input.required<SizeGuideRefDto>();
  readonly open = input.required<boolean>();

  readonly closed = output<void>();

  protected readonly heading = computed(() => `${this.guide().name} size guide`);
}
