import { BarSpec, DoughnutSpec, LineSpec, buildChartConfig, compactRupees, hasData, sharesOf } from './chart-config';
import { ChartPalette, withAlpha } from './chart-palette';

/**
 * #186: the chart builders. Every colour below is a different made-up hex, so a builder that took the wrong token
 * (or a hard-coded colour) cannot pass, and every number is different, so a chart wired to the wrong series cannot.
 */
const TOKENS: Record<string, string> = {
  '--chart-1': '#101010',
  '--chart-2': '#202020',
  '--chart-3': '#303030',
  '--chart-accent': '#0a0b0c',
  '--status-paid': '#00aa00',
  '--status-awaiting': '#aaaa00',
};

const PALETTE: ChartPalette = {
  token: (name) => TOKENS[name] ?? '',
  series: (index) => TOKENS[`--chart-${(index % 3) + 1}`],
  accent: TOKENS['--chart-accent'],
  grid: '#eeeeee',
  ink: '#666666',
  surface: '#ffffff',
};

describe('chart-config (#186)', () => {
  it('draws a doughnut from the slice values, a fixed token where a slice has one and the categorical order otherwise', () => {
    const spec: DoughnutSpec = {
      kind: 'doughnut',
      cutout: 68,
      slices: [
        { key: 'a', label: 'Awaiting payment', value: 1, display: '1', share: 25, token: '--status-awaiting' },
        { key: 'b', label: 'Women', value: 3, display: '3', share: 75 },
      ],
    };
    const config = buildChartConfig(spec, PALETTE);
    const dataset = config.data.datasets[0];

    expect(config.type).toBe('doughnut');
    expect(dataset.data).toEqual([1, 3]);
    expect(dataset.backgroundColor).toEqual(['#aaaa00', '#202020']);
    expect((config.options as { cutout: string }).cutout).toBe('68%');
    // The legend is the page's text, never Chart.js's canvas legend.
    expect(config.options?.plugins?.legend?.display).toBe(false);
  });

  it('marks the first bar with the accent and dims the rest to 55%, horizontally', () => {
    const spec: BarSpec = { kind: 'bar', indexAxis: 'y', highlightFirst: true, seriesLabel: 'Sales', labels: ['A', 'B', 'C'], values: [9, 5, 2], format: String, tick: String };
    const config = buildChartConfig(spec, PALETTE);

    expect(config.data.labels).toEqual(['A', 'B', 'C']);
    expect(config.data.datasets[0].data).toEqual([9, 5, 2]);
    expect(config.data.datasets[0].backgroundColor).toEqual(['#0a0b0c', 'rgb(10 11 12 / 0.55)', 'rgb(10 11 12 / 0.55)']);
    expect((config.options as { indexAxis: string }).indexAxis).toBe('y');
  });

  it('draws every bar in the accent when nothing is ranked', () => {
    const spec: BarSpec = { kind: 'bar', indexAxis: 'x', highlightFirst: false, seriesLabel: 'Sales', labels: ['A', 'B'], values: [1, 2], format: String, tick: String };

    expect(buildChartConfig(spec, PALETTE).data.datasets[0].backgroundColor).toEqual(['#0a0b0c', '#0a0b0c']);
  });

  it('puts each line on its own axis, the filled one tinted from its own token', () => {
    const spec: LineSpec = {
      kind: 'line',
      labels: ['Sep', 'Oct'],
      series: [
        { key: 'r', label: 'Revenue', values: [100, 200], axis: 'left', token: '--chart-accent', fill: true, dashed: false, format: String, tick: String },
        { key: 'o', label: 'Orders', values: [3, 4], axis: 'right', token: '--chart-2', fill: false, dashed: true, format: String, tick: String },
      ],
    };
    const config = buildChartConfig(spec, PALETTE);
    const [revenue, orders] = config.data.datasets as unknown as Record<string, unknown>[];

    expect(revenue).toMatchObject({ data: [100, 200], yAxisID: 'y', borderColor: '#0a0b0c', backgroundColor: 'rgb(10 11 12 / 0.08)', fill: true, tension: 0.4 });
    expect(orders).toMatchObject({ data: [3, 4], yAxisID: 'y1', borderColor: '#202020', fill: false, borderDash: [4, 3] });
    expect(Object.keys(config.options?.scales ?? {})).toEqual(['x', 'y', 'y1']);
  });

  it('calls a period with nothing in it empty, so the page says so instead of drawing it', () => {
    expect(hasData({ kind: 'doughnut', cutout: 65, slices: [{ key: 'a', label: 'A', value: 0, display: '0', share: 0 }] })).toBe(false);
    expect(hasData({ kind: 'doughnut', cutout: 65, slices: [{ key: 'a', label: 'A', value: 1, display: '1', share: 100 }] })).toBe(true);
    expect(hasData({ kind: 'bar', indexAxis: 'y', highlightFirst: true, seriesLabel: '', labels: [], values: [], format: String, tick: String })).toBe(false);
  });

  it('works shares out to one decimal place, and none from nothing', () => {
    expect(sharesOf([1, 3])).toEqual([25, 75]);
    expect(sharesOf([1, 2])).toEqual([33.3, 66.7]);
    expect(sharesOf([0, 0])).toEqual([0, 0]);
  });

  it('shortens axis rupees and keeps a hex colour, with transparency, as rgb', () => {
    expect(compactRupees(28_200)).toBe('Rs.28.2K');
    expect(compactRupees(1_250_000)).toBe('Rs.1.3M');
    expect(compactRupees(450)).toBe('Rs.450');
    expect(withAlpha('#1f3a5f', 0.5)).toBe('rgb(31 58 95 / 0.5)');
    expect(withAlpha('#abc', 1)).toBe('rgb(170 187 204 / 1)');
    expect(withAlpha('navy', 0.5)).toBe('navy');
  });

  // ---- #186 tester: mutations that survived the developer's specs ----------------------------------------------

  it('calls a bar chart of zeros empty, and one positive bar data (tester)', () => {
    const bars = (values: number[]): BarSpec => ({ kind: 'bar', indexAxis: 'x', highlightFirst: false, seriesLabel: '', labels: values.map(String), values, format: String, tick: String });

    expect(hasData(bars([0, 0, 0]))).toBe(false);
    expect(hasData(bars([0, 5, 0]))).toBe(true);
  });

  it('never lets a negative value into a share, or make the others add past 100% (tester)', () => {
    expect(sharesOf([-5, 5])).toEqual([0, 100]);
    expect(sharesOf([-5, 0])).toEqual([0, 0]);
  });

  it('switches to K at exactly one thousand and to M at exactly one million (tester)', () => {
    expect(compactRupees(999)).toBe('Rs.999');
    expect(compactRupees(1_000)).toBe('Rs.1K');
    expect(compactRupees(999_999)).toBe('Rs.1,000K');
    expect(compactRupees(1_000_000)).toBe('Rs.1M');
    expect(compactRupees(12_345_678.9)).toBe('Rs.12.3M');
  });

  it('starts every value axis at zero, so a small rise is not drawn as a steep one (tester)', () => {
    const line: LineSpec = {
      kind: 'line',
      labels: ['Sep'],
      series: [
        { key: 'r', label: 'Revenue', values: [100], axis: 'left', token: '--chart-accent', fill: true, dashed: false, format: String, tick: String },
        { key: 'o', label: 'Orders', values: [3], axis: 'right', token: '--chart-2', fill: false, dashed: true, format: String, tick: String },
      ],
    };
    const scales = buildChartConfig(line, PALETTE).options!.scales as Record<string, { beginAtZero?: boolean }>;

    expect(scales['y'].beginAtZero).toBe(true);
    expect(scales['y1'].beginAtZero).toBe(true);
  });

  it('leaves an unfilled line without an area colour (tester)', () => {
    const line: LineSpec = {
      kind: 'line',
      labels: ['Sep'],
      series: [{ key: 'o', label: 'Orders', values: [3], axis: 'left', token: '--chart-2', fill: false, dashed: true, format: String, tick: String }],
    };

    expect((buildChartConfig(line, PALETTE).data.datasets[0] as { backgroundColor: string }).backgroundColor).toBe('transparent');
  });

  it("words each tooltip from the point's own value: the line's y, the doughnut's share, the bar's value axis (tester)", () => {
    type Label = (item: unknown) => string;
    const labelOf = (config: ReturnType<typeof buildChartConfig>) => (config.options!.plugins!.tooltip!.callbacks as unknown as { label: Label }).label;

    const line: LineSpec = {
      kind: 'line',
      labels: ['Sep', 'Oct'],
      series: [{ key: 'r', label: 'Revenue', values: [100, 200], axis: 'left', token: '--chart-accent', fill: true, dashed: false, format: (value) => `Rs. ${value}`, tick: String }],
    };
    expect(labelOf(buildChartConfig(line, PALETTE))({ datasetIndex: 0, dataset: { label: 'Revenue' }, parsed: { x: 1, y: 200 } })).toBe(' Revenue: Rs. 200');

    const doughnut: DoughnutSpec = { kind: 'doughnut', cutout: 65, slices: [{ key: 'a', label: 'A', value: 3, display: 'Rs. 3.00', share: 75 }] };
    expect(labelOf(buildChartConfig(doughnut, PALETTE))({ dataIndex: 0 })).toBe(' Rs. 3.00 (75.0%)');

    const horizontal: BarSpec = { kind: 'bar', indexAxis: 'y', highlightFirst: true, seriesLabel: 'Sales', labels: ['A'], values: [9000], format: (value) => `Rs. ${value}`, tick: String };
    expect(labelOf(buildChartConfig(horizontal, PALETTE))({ parsed: { x: 9000, y: 0 } })).toBe(' Rs. 9000');

    const vertical: BarSpec = { ...horizontal, indexAxis: 'x' };
    expect(labelOf(buildChartConfig(vertical, PALETTE))({ parsed: { x: 0, y: 9000 } })).toBe(' Rs. 9000');
  });

  it('puts the value scale (gridlines and rupee ticks) on the value axis of a horizontal bar chart (tester)', () => {
    const spec: BarSpec = { kind: 'bar', indexAxis: 'y', highlightFirst: true, seriesLabel: 'Sales', labels: ['A'], values: [9], format: String, tick: (value) => `Rs.${value}` };
    const scales = buildChartConfig(spec, PALETTE).options!.scales as Record<string, { beginAtZero?: boolean; grid: { color?: string; display?: boolean } }>;

    expect(scales['x'].beginAtZero).toBe(true);
    expect(scales['x'].grid.color).toBe('#eeeeee');
    expect(scales['y'].grid.display).toBe(false);
  });

  it("draws the doughnut's gap between slices in the card's own surface colour (tester)", () => {
    const spec: DoughnutSpec = { kind: 'doughnut', cutout: 65, slices: [{ key: 'a', label: 'A', value: 1, display: '1', share: 100 }] };

    expect((buildChartConfig(spec, PALETTE).data.datasets[0] as { borderColor: string }).borderColor).toBe('#ffffff');
  });
});
