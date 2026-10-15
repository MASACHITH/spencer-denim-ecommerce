import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** One row of a bar table: a label, the cells beside it, and the number its bar is drawn from. */
export interface BarRow {
  readonly key: string | number;
  readonly label: string;
  /** Already formatted, one per header after the first. */
  readonly cells: readonly string[];
  /** What the bar's length is in proportion to. Never negative. */
  readonly value: number;
}

/**
 * A table whose first column carries a horizontal bar — the dashboard's "chart" for every breakdown.
 *
 * **It is a table first and a chart second**, so the text equivalent a screen reader needs is not a copy
 * that can drift from the picture: it *is* the picture. The bar is decoration on the label cell
 * (`aria-hidden`), drawn from the same number the cell beside it prints. A zero is a zero-length bar beside a
 * printed zero, which is the truth — nothing on this table is drawn from anything but the server's figures.
 *
 * **Narrow screens.** The table never sets a width wider than its container and the label column wraps;
 * if a very narrow screen still cannot fit it, the wrapper scrolls *itself* sideways rather than the page.
 */
@Component({
  selector: 'sda-bar-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar-table__scroll" tabindex="0" [attr.aria-label]="caption()" role="region">
      <table class="bar-table" [attr.data-testid]="testId()">
        <caption class="sr-only">{{ caption() }}</caption>
        <thead>
          <tr>
            @for (header of headers(); track $index) {
              <th scope="col" [class.bar-table__num]="$index > 0">{{ header }}</th>
            }
          </tr>
        </thead>
        <tbody>
          @for (row of rows(); track row.key) {
            <tr>
              <th scope="row" class="bar-table__head">
                <div class="bar-table__label">
                  <span class="bar-table__text">{{ row.label }}</span>
                  <span class="bar-table__bar" aria-hidden="true" [style.width.%]="width(row.value)"></span>
                </div>
              </th>
              @for (cell of row.cells; track $index) {
                <td class="bar-table__num">{{ cell }}</td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .bar-table__scroll { overflow-x: auto; max-width: 100%; }
    .bar-table { width: 100%; border-collapse: collapse; font-size: var(--text-data); }
    th, td { padding: var(--space-1) var(--space-1); border-bottom: 1px solid var(--color-border); vertical-align: top; }
    thead th {
      font-size: var(--text-label); font-weight: var(--weight-regular); letter-spacing: var(--tracking-label);
      text-transform: uppercase; color: var(--color-text-muted); text-align: left;
    }
    .bar-table__head { text-align: left; font-weight: var(--weight-regular); min-width: 8rem; }
    .bar-table__label { display: grid; gap: 4px; }
    .bar-table__text { overflow-wrap: anywhere; }
    .bar-table__bar { display: block; height: 6px; min-width: 0; background: var(--color-primary); }
    .bar-table__num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
  `,
})
export class BarTable {
  readonly caption = input.required<string>();
  readonly headers = input.required<readonly string[]>();
  readonly rows = input.required<readonly BarRow[]>();
  readonly testId = input<string | null>(null);

  private readonly largest = computed(() => Math.max(0, ...this.rows().map((row) => row.value)));

  protected width(value: number): number {
    const largest = this.largest();

    return largest <= 0 || value <= 0 ? 0 : Math.max(1, (value / largest) * 100);
  }
}
