import { TrafficChannelRow, TrafficDeviceRow } from '../../core/traffic/traffic.model';
import { channelDoughnut, channelSummary, channelTable, deviceSummary } from './traffic-charts';

/**
 * #186 tester: the one-sentence summaries that name the traffic doughnuts. Mutations that totalled page views
 * as visits, named the smallest channel as the largest, or named a "most" in a period with no visits all passed
 * the developer's suite.
 */
const CHANNELS: TrafficChannelRow[] = [
  { channel: 'Direct', label: 'Direct', visits: 10, pageViews: 90, sharePercent: 25 },
  { channel: 'Organic', label: 'Organic', visits: 25, pageViews: 30, sharePercent: 62.5 },
  { channel: 'Social', label: 'Social', visits: 5, pageViews: 5, sharePercent: 12.5 },
];

describe('traffic charts (#186 tester)', () => {
  it('totals visits, not page views, and names the channel with the most visits', () => {
    expect(channelSummary(CHANNELS)).toBe('Visits by traffic source, 40 in all. Most: Organic, 25 (62.5%).');
  });

  it('names no channel when there were no visits at all', () => {
    const none = CHANNELS.map((row) => ({ ...row, visits: 0, pageViews: 0, sharePercent: 0 }));

    expect(channelSummary(none)).toBe('Visits by traffic source: none in these days.');
    expect(channelSummary([])).toBe('Visits by traffic source: none in these days.');
  });

  it('draws visits with the API share, and tables visits, page views and share in that order', () => {
    expect(channelDoughnut(CHANNELS).slices.map((slice) => [slice.value, slice.share])).toEqual([[10, 25], [25, 62.5], [5, 12.5]]);
    expect(channelTable(CHANNELS, 'x').rows[1].cells).toEqual(['25', '30', '62.5%']);
  });

  it('words devices the same way', () => {
    const devices: TrafficDeviceRow[] = [
      { device: 'Desktop', label: 'Desktop', visits: 1, pageViews: 9, sharePercent: 25 },
      { device: 'Mobile', label: 'Mobile', visits: 3, pageViews: 3, sharePercent: 75 },
    ];

    expect(deviceSummary(devices)).toBe('Visits by device, 4 in all. Most: Mobile, 3 (75.0%).');
  });
});
