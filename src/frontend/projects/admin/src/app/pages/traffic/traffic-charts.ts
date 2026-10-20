import { ChartTable } from '../../core/charts/chart';
import { DoughnutSpec, shareText } from '../../core/charts/chart-config';
import { count } from '../../core/reporting/reporting-format';
import { CHANNEL_MEANINGS, TrafficChannelRow, TrafficDeviceRow } from '../../core/traffic/traffic.model';

/**
 * The traffic breakdowns as charts (#186), shared by the dashboard's Traffic sources card and the Traffic page,
 * so the two draw a channel the same way. Shares are the API's own `sharePercent`, not worked out again here.
 */

type Breakdown = Pick<TrafficChannelRow, 'label' | 'visits' | 'pageViews' | 'sharePercent'> & { readonly key: string; readonly meaning?: string };

function channels(rows: readonly TrafficChannelRow[]): Breakdown[] {
  return rows.map((row) => ({ ...row, key: row.channel, meaning: CHANNEL_MEANINGS[row.channel] }));
}

function devices(rows: readonly TrafficDeviceRow[]): Breakdown[] {
  return rows.map((row) => ({ ...row, key: row.device }));
}

function doughnut(rows: readonly Breakdown[]): DoughnutSpec {
  return {
    kind: 'doughnut',
    cutout: 65,
    slices: rows.map((row) => ({ key: row.key, label: row.label, value: row.visits, display: `${count(row.visits)} ${row.visits === 1 ? 'visit' : 'visits'}`, share: row.sharePercent })),
  };
}

function table(rows: readonly Breakdown[], testId: string, caption: string, first: string): ChartTable {
  return {
    caption,
    testId,
    headers: [first, 'Visits', 'Page views', 'Share'],
    rows: rows.map((row) => ({
      key: row.key,
      label: row.meaning ? `${row.label} — ${row.meaning}` : row.label,
      cells: [count(row.visits), count(row.pageViews), shareText(row.sharePercent)],
    })),
  };
}

function summary(rows: readonly Breakdown[], what: string): string {
  const top = rows.reduce<Breakdown | null>((best, row) => (best === null || row.visits > best.visits ? row : best), null);
  const total = rows.reduce((sum, row) => sum + row.visits, 0);

  return top === null || total === 0
    ? `Visits by ${what}: none in these days.`
    : `Visits by ${what}, ${count(total)} in all. Most: ${top.label}, ${count(top.visits)} (${shareText(top.sharePercent)}).`;
}

export function channelDoughnut(rows: readonly TrafficChannelRow[]): DoughnutSpec {
  return doughnut(channels(rows));
}

export function channelTable(rows: readonly TrafficChannelRow[], testId: string): ChartTable {
  return table(channels(rows), testId, 'Visits by traffic source', 'Source');
}

export function channelSummary(rows: readonly TrafficChannelRow[]): string {
  return summary(channels(rows), 'traffic source');
}

export function deviceDoughnut(rows: readonly TrafficDeviceRow[]): DoughnutSpec {
  return doughnut(devices(rows));
}

export function deviceTable(rows: readonly TrafficDeviceRow[], testId: string): ChartTable {
  return table(devices(rows), testId, 'Visits by device', 'Device');
}

export function deviceSummary(rows: readonly TrafficDeviceRow[]): string {
  return summary(devices(rows), 'device');
}
