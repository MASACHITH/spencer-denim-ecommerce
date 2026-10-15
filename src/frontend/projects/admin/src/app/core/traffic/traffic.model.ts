import { ComparedFigure, ReportPeriod } from '../reporting/reporting.model';

/**
 * The Traffic page's and the dashboard widget's payloads (ADM-20, ADM-06), field for field with
 * `SpencerDenim.Application.Analytics.Reporting` (`TrafficReportContracts.cs`).
 *
 * **Visits are human sessions per Colombo day**; page views are human page views; bot page views are counted
 * apart and in nothing else. Each visit's page views are credited to the channel, device and source of its first
 * page that day.
 */

/** Doc 08 §3.8's seven channels, in its order. */
export type TrafficChannelName = 'Direct' | 'Organic' | 'Social' | 'Referral' | 'Email' | 'Paid' | 'Other';

export type DeviceName = 'Desktop' | 'Mobile' | 'Tablet' | 'Other';

export interface TrafficDay {
  readonly date: string;
  readonly visits: number;
  readonly pageViews: number;
  readonly botPageViews: number;
}

export interface TrafficChannelRow {
  readonly channel: TrafficChannelName;
  readonly label: string;
  readonly visits: number;
  readonly pageViews: number;
  readonly sharePercent: number;
}

export interface TrafficDeviceRow {
  readonly device: DeviceName;
  readonly label: string;
  readonly visits: number;
  readonly pageViews: number;
  readonly sharePercent: number;
}

export interface TrafficSourceRow {
  readonly source: string;
  readonly visits: number;
  readonly pageViews: number;
}

export interface TrafficPageRow {
  readonly path: string;
  readonly pageType: string;
  readonly productName: string | null;
  readonly pageViews: number;
  readonly visits: number;
}

export interface TrafficReport {
  readonly period: ReportPeriod;
  readonly previousPeriod: ReportPeriod;
  /** Days on or after this are counted live from the raw page views; earlier days come from the nightly tables. */
  readonly liveFrom: string;
  readonly visits: ComparedFigure;
  readonly pageViews: ComparedFigure;
  readonly botPageViews: number;
  readonly days: readonly TrafficDay[];
  readonly channels: readonly TrafficChannelRow[];
  readonly devices: readonly TrafficDeviceRow[];
  readonly sources: readonly TrafficSourceRow[];
  readonly topPages: readonly TrafficPageRow[];
}

export interface TrafficSources {
  readonly period: ReportPeriod;
  readonly visits: number;
  readonly paidOrders: number;
  /** Paid orders ÷ visits × 100, one decimal place; `null` when there were no visits. */
  readonly conversionPercent: number | null;
  readonly channels: readonly TrafficChannelRow[];
}

export const TRAFFIC_PERMISSION = 'Traffic.View';

/** What each channel means, in the words the Traffic page and the dashboard explain it with. */
export const CHANNEL_MEANINGS: Readonly<Record<TrafficChannelName, string>> = {
  Direct: 'Typed the address, used a bookmark, or came from somewhere that did not say',
  Organic: 'Came from a search engine result that was not an ad',
  Social: 'Came from a social network, or a link tagged as social',
  Referral: 'Followed a link on another website',
  Email: 'Followed a link in an email or newsletter',
  Paid: 'Clicked a paid advert',
  Other: 'Followed a tagged campaign link that fits none of the above',
};
