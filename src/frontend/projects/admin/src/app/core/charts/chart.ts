import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, inject, input, signal } from '@angular/core';
import {
  ArcElement,
  BarController,
  BarElement,
  CategoryScale,
  Chart,
  DoughnutController,
  Filler,
  LineController,
  LineElement,
  LinearScale,
  PointElement,
  Tooltip,
} from 'chart.js';
import { BaseChartDirective } from 'ng2-charts';

import { ChartSpec, buildChartConfig, hasData, shareText, sliceColours } from './chart-config';
import { ChartPalette, readPalette } from './chart-palette';

// Only the pieces the three chart shapes use, registered here — in the lazy chunk of a page that draws a chart —
// and never through `provideCharts` in the application config, which would put Chart.js in the initial bundle.
Chart.register(LineController, LineElement, PointElement, Filler, DoughnutController, ArcElement, BarController, BarElement, CategoryScale, LinearScale, Tooltip);

/** The figures under a chart: the same numbers, as a real table. */
export interface ChartTable {
  readonly caption: string;
  readonly testId: string;
  readonly headers: readonly string[];
  readonly rows: readonly { readonly key: string | number; readonly label: string; readonly cells: readonly string[] }[];
}

/**
 * The admin's one chart (#186): a Chart.js canvas, its text legend, and its figures as a table.
 *
 * **Nothing on it is only a picture.** The canvas is `role="img"` with a one-sentence `summary`; a doughnut's
 * legend (colour, name, figure, share) and a line chart's series names are real text beside it, not drawn on
 * the canvas; and every figure is in a table under "Show the figures as a table". An empty period shows
 * `emptyText` instead of an empty doughnut or a flat line.
 *
 * Colours come from the theme's tokens (`chart-palette.ts`), read once the element is on the page.
 */
@Component({
  selector: 'sda-chart',
  imports: [BaseChartDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!drawable()) {
      <p class="adm-muted chart__empty" [attr.data-testid]="testId() + '-empty'">{{ emptyText() }}</p>
    } @else {
      @if (lineKeys().length > 1) {
        <ul class="chart__keys" [attr.data-testid]="testId() + '-keys'">
          @for (key of lineKeys(); track key.label) {
            <li><span class="chart__dot" [class.chart__dot--dash]="key.dashed" aria-hidden="true" [style.background]="key.colour"></span>{{ key.label }}</li>
          }
        </ul>
      }
      <div class="chart__canvas" [style.height.px]="height()">
        @if (config(); as configured) {
          <canvas baseChart role="img" [attr.aria-label]="summary()" [attr.data-testid]="testId()" [type]="configured.type" [data]="configured.data" [options]="configured.options"></canvas>
        }
      </div>
      @if (legend().length > 0) {
        <ul class="chart__legend" [attr.data-testid]="testId() + '-legend'">
          @for (row of legend(); track row.key) {
            <li>
              <span class="chart__dot" aria-hidden="true" [style.background]="row.colour"></span>
              <span class="chart__name">{{ row.label }}</span>
              <span class="chart__figure">{{ row.display }}</span>
              <span class="chart__share">{{ row.share }}</span>
            </li>
          }
        </ul>
      }
    }
    @if (table(); as figures) {
      <details class="chart__table">
        <summary class="adm-link">Show the figures as a table</summary>
        <div class="adm-table-scroll" tabindex="0" role="region" [attr.aria-label]="figures.caption">
          <table class="adm-table" [attr.data-testid]="figures.testId">
            <caption class="sr-only">{{ figures.caption }}</caption>
            <thead>
              <tr>
                @for (header of figures.headers; track $index) {
                  <th scope="col" [class.adm-num]="$index > 0">{{ header }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of figures.rows; track row.key) {
                <tr>
                  <th scope="row">{{ row.label }}</th>
                  @for (cell of row.cells; track $index) {
                    <td class="adm-num">{{ cell }}</td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      </details>
    }
  `,
  styles: `
    :host { display: grid; gap: var(--space-1-5); min-width: 0; }
    .chart__canvas { position: relative; min-width: 0; }
    .chart__canvas canvas { position: absolute; inset: 0; width: 100% !important; height: 100% !important; }
    .chart__keys { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: var(--space-0-5) var(--space-2); margin: 0; padding: 0; list-style: none; font-size: var(--text-label); color: var(--color-text-muted); }
    .chart__keys li, .chart__legend li { display: flex; align-items: center; gap: var(--space-0-5); min-width: 0; }
    .chart__dot { flex: 0 0 auto; width: 10px; height: 10px; border-radius: 50%; }
    .chart__dot--dash { height: 3px; width: 14px; border-radius: 0; }
    .chart__legend { display: grid; gap: var(--space-0-5); margin: 0; padding: 0; list-style: none; font-size: var(--text-small); }
    .chart__name { flex: 1; min-width: 0; overflow-wrap: anywhere; color: var(--color-text-muted); }
    .chart__figure, .chart__share { font-variant-numeric: tabular-nums; white-space: nowrap; }
    .chart__share { min-width: 3.5em; text-align: right; font-weight: var(--weight-semibold); }
    .chart__table summary { width: fit-content; }
  `,
})
export class ChartView {
  readonly spec = input.required<ChartSpec>();
  /** One sentence for the canvas's accessible name: what is drawn, over what, and its largest figure. */
  readonly summary = input.required<string>();
  readonly height = input(200);
  readonly emptyText = input('No sales in this period.');
  readonly table = input<ChartTable | null>(null);
  readonly testId = input('chart');

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Null until the element is on the page, where the theme's tokens can be read. */
  protected readonly palette = signal<ChartPalette | null>(null);

  protected readonly drawable = computed(() => hasData(this.spec()));

  protected readonly config = computed(() => {
    const palette = this.palette();
    return palette ? buildChartConfig(this.spec(), palette) : null;
  });

  protected readonly legend = computed(() => {
    const spec = this.spec();
    const palette = this.palette();

    if (spec.kind !== 'doughnut') {
      return [];
    }

    // The text is there from the first render; the colour dots fill in once the theme can be read.
    const colours = palette ? sliceColours(spec.slices, palette) : [];
    return spec.slices.map((slice, index) => ({ key: slice.key, label: slice.label, display: slice.display, share: shareText(slice.share), colour: colours[index] ?? '' }));
  });

  protected readonly lineKeys = computed(() => {
    const spec = this.spec();
    const palette = this.palette();

    return spec.kind === 'line'
      ? spec.series.map((series) => ({ label: series.label, dashed: series.dashed, colour: palette?.token(series.token) ?? '' }))
      : [];
  });

  constructor() {
    afterNextRender(() => this.palette.set(readPalette(this.host.nativeElement)));
  }
}
