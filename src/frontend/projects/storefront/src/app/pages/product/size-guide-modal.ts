import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { Modal } from '../../ui/modal/modal';
import { SizeGuideContentDto, SizeGuideRefDto, SizeGuideTableDto } from '../../core/catalog/catalog.model';

/**
 * The size guide (PDP-04), managed per category in admin and overridable per product.
 *
 * The measurements arrive on the product payload (`SizeGuideContentDto`), so this draws them
 * directly and fetches nothing: by the time the trigger exists, `hasSizeGuideContent` has already
 * had to be true, which is why the content could never have been loaded on open.
 *
 * **Every field is optional and the dialog is built for that.** A guide can have a table and no
 * prose, prose and an unreadable table (`TableJson` is `nvarchar(max)` behind an `ISJSON` check
 * that says nothing about shape), or a diagram and nothing else. Each block renders only when it
 * has something in it, so there is never a heading over a gap — and the trigger does not exist at
 * all when every block would be empty.
 *
 * **What is markup and what is not, because the two are mixed here deliberately.**
 * `introHtml` and `measuringTipsHtml` are rich text and go through `[innerHTML]`, exactly as
 * `Products.Description` does in `ProductDetails` — which is also the only thing that sanitises
 * them (see `SizeGuideContentDto`; there is no sanitiser in the backend, issue #70). Table cells
 * are **plain text and are interpolated**, never bound to `innerHTML`: `SizeGuideTableDto` says so,
 * and it is what makes a cell holding `</script>` or `<b>` print those characters at the shopper
 * instead of being parsed.
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
        @if (introHtml(); as intro) {
          <div class="guide__intro rich" [innerHTML]="intro"></div>
        }

        @if (table(); as measurements) {
          <!--
            The scroller, which is the whole answer to four columns of measurements at 320 px.
            tabindex and a role make it a real scrollable region, so a keyboard user can reach the
            columns a pointer user drags to - an overflowing div alone does not give them that.
          -->
          <div
            class="guide__scroll"
            tabindex="0"
            role="group"
            [attr.aria-label]="tableLabel()"
          >
            <table class="guide__table">
              <caption class="sr-only">{{ tableLabel() }}</caption>
              <thead>
                <tr>
                  @for (column of measurements.columns; track $index) {
                    <th scope="col">{{ column }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of measurements.rows; track $index) {
                  <tr>
                    @for (cell of row; track $index) {
                      @if ($first) {
                        <th scope="row">{{ cell }}</th>
                      } @else {
                        <td>{{ cell }}</td>
                      }
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        @if (imageUrl(); as diagram) {
          <img class="guide__image" [src]="diagram" [alt]="imageAlt()" loading="lazy" />
        }

        @if (measuringTipsHtml(); as tips) {
          <div class="guide__tips">
            <h3 class="guide__subhead">How to measure</h3>
            <div class="rich" [innerHTML]="tips"></div>
          </div>
        }
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
      /* A grid item's default min-width of auto lets a wide table stretch its track, which is how
         a dialog ends up wider than the screen and the page scrolls sideways. The scroller below
         can only do its job if its track refuses to grow. */
      min-width: 0;
    }
    .guide__intro {
      max-width: 62ch;
    }
    .guide__scroll {
      min-width: 0;
      overflow-x: auto;
      overscroll-behavior-x: contain;
      /* Momentum scrolling on iOS, and a visible hint that there is more to the right. */
      -webkit-overflow-scrolling: touch;
    }
    .guide__table {
      border-collapse: collapse;
      /* auto, not 100%: at 320 px a four-column table forced to the container width wraps every
         heading onto three lines and the numbers stop being readable. Letting it take its natural
         width and scroll keeps "88-93" on one line, which is the point of a measurement table. */
      width: auto;
      font-size: var(--text-data);
    }
    .guide__table th,
    .guide__table td {
      padding: var(--space-0-5) var(--space-1);
      border: 1px solid var(--color-border);
      text-align: left;
      /* A measurement is never worth wrapping: "100-106" on two lines reads as two numbers. */
      white-space: nowrap;
    }
    .guide__table thead th {
      background: var(--color-bg-alt);
      font-weight: var(--weight-semibold);
    }
    .guide__table tbody th {
      font-weight: var(--weight-medium);
    }
    .guide__image {
      display: block;
      width: 100%;
      max-width: 360px;
      height: auto;
    }
    .guide__subhead {
      font-size: var(--text-label);
      font-weight: var(--weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--color-text-muted);
      padding-bottom: var(--space-0-5);
    }
    .guide__tips {
      max-width: 62ch;
    }
  `,
})
export class SizeGuideModal {
  readonly guide = input.required<SizeGuideRefDto>();
  readonly open = input.required<boolean>();

  readonly closed = output<void>();

  protected readonly heading = computed(() => `${this.guide().name} size guide`);

  private readonly content = computed<SizeGuideContentDto | null>(() => this.guide().content ?? null);

  protected readonly introHtml = computed(() => trimmed(this.content()?.introHtml));

  protected readonly measuringTipsHtml = computed(() => trimmed(this.content()?.measuringTipsHtml));

  protected readonly imageUrl = computed(() => trimmed(this.content()?.imageUrl));

  /**
   * The table, **only when it has a header and at least one row**.
   *
   * The API already answers null for a table it could not read, so this is not a second opinion on
   * the JSON; it is the guard for the shapes that survive it — a header with no rows, or rows the
   * server padded to zero columns — because a `<table>` with a `<thead>` and an empty `<tbody>` is
   * a heading over a gap.
   */
  protected readonly table = computed<SizeGuideTableDto | null>(() => {
    const table = this.content()?.table ?? null;
    if (!table || table.columns.length === 0 || table.rows.length === 0) {
      return null;
    }

    return table;
  });

  /** Names the table for a screen reader without repeating the dialog heading on screen. */
  protected readonly tableLabel = computed(() => `${this.guide().name} measurements`);

  /**
   * The diagram's alt text.
   *
   * The asset's own alt text when it has one. The fallback names the guide rather than describing
   * the picture, because this component cannot see it: inventing "a woman measuring her bust" would
   * be a worse answer than a plain label, and `alt=""` would hide a measurement diagram from a
   * screen reader altogether. ADM-11 should require alt text when a diagram is attached.
   */
  protected readonly imageAlt = computed(
    () => trimmed(this.content()?.imageAltText) ?? `Measuring diagram for the ${this.guide().name} guide`,
  );
}

function trimmed(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}
