import type { ChartConfiguration, ChartType, TooltipItem } from 'chart.js';

import { ChartPalette, withAlpha } from './chart-palette';

/**
 * What the admin's charts can draw, as data, and the Chart.js configuration each one becomes (#186).
 *
 * **The builders are pure** — a spec and a palette in, a configuration out — so a unit test can prove the
 * numbers on the chart are the numbers it was given, and the colours are the theme's, without a canvas
 * (jsdom has none). `chart.ts` is the only place that hands a configuration to Chart.js.
 *
 * The three shapes are the client demo's: a filled, smoothed line with an optional second axis (Revenue &amp;
 * Orders), a doughnut with a text legend beside it (Sales by department, Order status, Traffic sources,
 * Payment methods), and a bar chart whose first bar is the accent and the rest the accent at 55% (Top
 * categories, horizontal; the reports' per-period bars, vertical).
 */

/** One slice of a doughnut, and one row of its legend. */
export interface ChartSlice {
  readonly key: string;
  readonly label: string;
  readonly value: number;
  /** `value` as the legend and the tooltip print it, e.g. `Rs. 21,150.00` or `3`. */
  readonly display: string;
  /** Its share of the whole, 0–100. From the API when it sends one; otherwise worked out from the values. */
  readonly share: number;
  /** A token such as `--status-paid` for a slice whose colour is fixed; otherwise the categorical order. */
  readonly token?: string;
}

export interface LineSeries {
  readonly key: string;
  readonly label: string;
  readonly values: readonly number[];
  /** The demo draws revenue on the left axis and order counts on a second, right-hand one. */
  readonly axis: 'left' | 'right';
  readonly token: string;
  /** A filled area under the line, as the demo's revenue series is. */
  readonly fill: boolean;
  /** A dashed line, as the demo's orders series is: a second cue besides colour. */
  readonly dashed: boolean;
  /** The tooltip's wording for one point. */
  readonly format: (value: number) => string;
  /** The axis tick's wording; shorter than `format`. */
  readonly tick: (value: number) => string;
}

export interface LineSpec {
  readonly kind: 'line';
  readonly labels: readonly string[];
  readonly series: readonly LineSeries[];
}

export interface DoughnutSpec {
  readonly kind: 'doughnut';
  readonly slices: readonly ChartSlice[];
  /** The hole, as a percentage of the radius: the demo's 68% for the large doughnut, 65% for the small. */
  readonly cutout: number;
}

export interface BarSpec {
  readonly kind: 'bar';
  readonly labels: readonly string[];
  readonly values: readonly number[];
  /** `y` draws horizontal bars (labels down the side), `x` vertical ones. */
  readonly indexAxis: 'x' | 'y';
  /** The series name the tooltip uses. */
  readonly seriesLabel: string;
  /** Whether the first bar is the solid accent and the rest the accent at 55% (the demo's ranking cue). */
  readonly highlightFirst: boolean;
  readonly format: (value: number) => string;
  readonly tick: (value: number) => string;
}

export type ChartSpec = LineSpec | DoughnutSpec | BarSpec;

/** The share the demo dims every bar after the first to. */
export const DIMMED_ALPHA = 0.55;

/** The demo's fill under the revenue line. */
export const AREA_ALPHA = 0.08;

/** Whether there is anything to draw: an empty period is said in words, never shown as an empty doughnut. */
export function hasData(spec: ChartSpec): boolean {
  switch (spec.kind) {
    case 'doughnut':
      return spec.slices.some((slice) => slice.value > 0);
    case 'bar':
      return spec.values.some((value) => value > 0);
    case 'line':
      return spec.series.some((series) => series.values.some((value) => value > 0));
  }
}

/** The colour of each slice, in order: its own token when it has one, else the categorical colour for its place. */
export function sliceColours(slices: readonly ChartSlice[], palette: ChartPalette): string[] {
  return slices.map((slice, index) => (slice.token ? palette.token(slice.token) : palette.series(index)));
}

/** Shares worked out from the values, one decimal place, for a breakdown the API does not send shares with. */
export function sharesOf(values: readonly number[]): number[] {
  const total = values.reduce((sum, value) => sum + Math.max(0, value), 0);

  return values.map((value) => (total <= 0 ? 0 : Math.round((Math.max(0, value) / total) * 1000) / 10));
}

/** `Rs.12K`, `Rs.1.2M`: an axis tick, where the full figure is in the tooltip and the table. */
export function compactRupees(value: number): string {
  const size = Math.abs(value);

  if (size >= 1_000_000) {
    return `Rs.${trim(value / 1_000_000)}M`;
  }

  if (size >= 1_000) {
    return `Rs.${trim(value / 1_000)}K`;
  }

  return `Rs.${trim(value)}`;
}

function trim(value: number): string {
  return value.toLocaleString('en-LK', { maximumFractionDigits: 1 });
}

/** The Chart.js configuration for `spec`, coloured from `palette`. */
export function buildChartConfig(spec: ChartSpec, palette: ChartPalette): ChartConfiguration {
  switch (spec.kind) {
    case 'line':
      return lineConfig(spec, palette);
    case 'doughnut':
      return doughnutConfig(spec, palette);
    case 'bar':
      return barConfig(spec, palette);
  }
}

function base(palette: ChartPalette) {
  return {
    responsive: true,
    maintainAspectRatio: false,
    // The page's own legend is text beside the canvas (#186 §4), so Chart.js draws none.
    color: palette.ink,
    animation: false as const,
  };
}

function lineConfig(spec: LineSpec, palette: ChartPalette): ChartConfiguration<'line'> {
  const right = spec.series.some((series) => series.axis === 'right');
  const tickFor = (axis: 'left' | 'right') => spec.series.find((series) => series.axis === axis)?.tick ?? String;

  return {
    type: 'line',
    data: {
      labels: [...spec.labels],
      datasets: spec.series.map((series) => {
        const colour = palette.token(series.token);

        return {
          label: series.label,
          data: [...series.values],
          borderColor: colour,
          backgroundColor: series.fill ? withAlpha(colour, AREA_ALPHA) : 'transparent',
          pointBackgroundColor: colour,
          fill: series.fill,
          tension: 0.4,
          pointRadius: series.fill ? 4 : 3,
          pointHoverRadius: series.fill ? 6 : 5,
          borderDash: series.dashed ? [4, 3] : [],
          yAxisID: series.axis === 'left' ? 'y' : 'y1',
        };
      }),
    },
    options: {
      ...base(palette),
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            label: (item: TooltipItem<'line'>) => ` ${item.dataset.label}: ${spec.series[item.datasetIndex]?.format(item.parsed.y ?? 0) ?? ''}`,
          },
        },
      },
      scales: {
        x: { grid: { display: false }, ticks: { color: palette.ink, maxRotation: 0, autoSkip: true } },
        y: {
          position: 'left',
          beginAtZero: true,
          grid: { color: palette.grid },
          ticks: { color: palette.ink, callback: (value) => tickFor('left')(Number(value)) },
        },
        ...(right
          ? {
              y1: {
                position: 'right' as const,
                beginAtZero: true,
                grid: { drawOnChartArea: false },
                ticks: { color: palette.ink, precision: 0, callback: (value: string | number) => tickFor('right')(Number(value)) },
              },
            }
          : {}),
      },
    },
  };
}

function doughnutConfig(spec: DoughnutSpec, palette: ChartPalette): ChartConfiguration<'doughnut'> {
  return {
    type: 'doughnut',
    data: {
      labels: spec.slices.map((slice) => slice.label),
      datasets: [
        {
          data: spec.slices.map((slice) => slice.value),
          backgroundColor: sliceColours(spec.slices, palette),
          // The demo's white gap between slices: the card's own surface colour, so it is a gap, not a line.
          borderColor: palette.surface,
          borderWidth: 2,
        },
      ],
    },
    options: {
      ...base(palette),
      cutout: `${spec.cutout}%`,
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            label: (item: TooltipItem<'doughnut'>) => {
              const slice = spec.slices[item.dataIndex];
              return slice ? ` ${slice.display} (${shareText(slice.share)})` : '';
            },
          },
        },
      },
    },
  };
}

function barConfig(spec: BarSpec, palette: ChartPalette): ChartConfiguration<'bar'> {
  const value = spec.indexAxis === 'y' ? 'x' : 'y';
  const index = spec.indexAxis;

  return {
    type: 'bar',
    data: {
      labels: [...spec.labels],
      datasets: [
        {
          label: spec.seriesLabel,
          data: [...spec.values],
          backgroundColor: spec.values.map((_, position) =>
            !spec.highlightFirst || position === 0 ? palette.accent : withAlpha(palette.accent, DIMMED_ALPHA),
          ),
          borderRadius: 4,
          borderSkipped: false,
          maxBarThickness: 28,
        },
      ],
    },
    options: {
      ...base(palette),
      indexAxis: spec.indexAxis,
      plugins: {
        legend: { display: false },
        tooltip: { callbacks: { label: (item: TooltipItem<'bar'>) => ` ${spec.format(Number(item.parsed[value]) || 0)}` } },
      },
      scales: {
        [value]: {
          beginAtZero: true,
          grid: { color: palette.grid },
          ticks: { color: palette.ink, callback: (tick: string | number) => spec.tick(Number(tick)) },
        },
        [index]: { grid: { display: false }, ticks: { color: palette.ink, autoSkip: true, maxRotation: 0 } },
      },
    },
  };
}

/** `75.0%`. */
export function shareText(share: number): string {
  return `${share.toLocaleString('en-LK', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
}

/** The type Chart.js is given, for the template's `[type]`. */
export function chartType(spec: ChartSpec): ChartType {
  return spec.kind;
}
