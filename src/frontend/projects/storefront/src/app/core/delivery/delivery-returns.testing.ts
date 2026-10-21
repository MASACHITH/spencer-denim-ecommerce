import { DeliveryReturnsDto } from './delivery-returns.model';

/** The seeded placeholder rates as the API sends them, for specs (issue #33). */
export const PANEL: DeliveryReturnsDto = {
  estimate: { daysMin: 1, daysMax: 5 },
  zones: [
    { zone: 'Colombo', label: 'Colombo' },
    { zone: 'Western', label: 'Gampaha and Kalutara' },
    { zone: 'IslandWide', label: 'Rest of Sri Lanka' },
  ],
  methods: [
    {
      code: 'STANDARD',
      name: 'Standard delivery',
      description: null,
      rates: [
        { zone: 'Colombo', fee: 400, freeAboveAmount: 10000, daysMin: 1, daysMax: 2 },
        { zone: 'Western', fee: 450, freeAboveAmount: 10000, daysMin: 2, daysMax: 3 },
        { zone: 'IslandWide', fee: 600, freeAboveAmount: 15000, daysMin: 3, daysMax: 5 },
      ],
    },
    {
      code: 'EXPRESS',
      name: 'Express delivery',
      description: null,
      rates: [{ zone: 'Colombo', fee: 950, freeAboveAmount: null, daysMin: 1, daysMax: 1 }],
    },
  ],
  pickupPoints: [],
  deliveryInfoHtml: '<ul><li>We also deliver worldwide.</li></ul>',
  returnsInfoHtml: '<ul><li>14 days.</li></ul>',
};
