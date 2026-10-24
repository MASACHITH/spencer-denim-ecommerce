import { GuideSection } from './guide.model';
import { matchSections, searchTerms, searchTextOf } from './guide-search';

const orders: GuideSection = {
  id: 'orders',
  group: 'Orders and delivery',
  title: 'Orders',
  summary: 'Process what customers buy.',
  blocks: [
    { kind: 'text', text: 'Approve a **bank transfer** first.' },
    { kind: 'steps', title: 'Ship it', items: ['Open the order', 'Enter the tracking number'] },
    {
      kind: 'table',
      caption: 'Order statuses',
      head: ['Status', 'Meaning'],
      rows: [['Paid', 'Money confirmed']],
    },
    { kind: 'callout', tone: 'warning', text: 'Refunds are done by hand.' },
  ],
};

const photos: GuideSection = {
  id: 'photos',
  group: 'Photos and video',
  title: 'Photos',
  summary: 'Prepare pictures.',
  blocks: [{ kind: 'list', items: ['Use JPEG or WebP'] }],
};

describe('searchTextOf', () => {
  it('holds the title, summary, group and every kind of block, lower-cased and without markup', () => {
    const text = searchTextOf(orders);

    for (const word of ['orders and delivery', 'process what customers buy', 'bank transfer', 'ship it', 'tracking number', 'order statuses', 'money confirmed', 'refunds are done by hand']) {
      expect(text).toContain(word);
    }
    expect(text).not.toContain('**');
  });
});

describe('searchTerms', () => {
  it('splits on any spaces and ignores case and empties', () => {
    expect(searchTerms('  Bank   TRANSFER ')).toEqual(['bank', 'transfer']);
    expect(searchTerms('   ')).toEqual([]);
  });
});

describe('matchSections', () => {
  const sections = [orders, photos];
  const index = new Map(sections.map((section) => [section.id, searchTextOf(section)]));

  it('keeps everything for an empty search', () => {
    expect(matchSections(sections, index, [])).toEqual(sections);
  });

  it('needs every word, in any order', () => {
    expect(matchSections(sections, index, ['transfer', 'bank'])).toEqual([orders]);
    expect(matchSections(sections, index, ['bank', 'webp'])).toEqual([]);
  });

  it('finds a word inside a longer one', () => {
    expect(matchSections(sections, index, ['jpe'])).toEqual([photos]);
  });
});
