/**
 * The shape of the admin user guide (OPS-04, #199).
 *
 * The guide is data, not markup: every section is a `GuideSection` made of a few block kinds, and one
 * renderer draws them all. That is what lets `guide-content.spec.ts` check the whole guide at once
 * (unique ids, no dead links, rectangular tables) and what keeps a new screen's section from needing a
 * new template.
 *
 * Text fields accept the small inline markup in `guide-text.ts`: `**a button or label as it reads on
 * screen**`, `` `a value to type` ``, and `[a link](/admin/route)` or `[a link](#section-id)`. Nothing is
 * ever rendered as HTML.
 */

/** The groups, in the order the contents list shows them. */
export const GUIDE_GROUPS = [
  'Start here',
  'Access and security',
  'Photos and video',
  'Catalogue',
  'Orders and delivery',
  'Marketing and customers',
  'Shop content',
  'Reports',
  'Settings and records',
  'Help',
] as const;

export type GuideGroup = (typeof GUIDE_GROUPS)[number];

/**
 * The colour each group is drawn in, in the contents list and on its section cards, so a reader can tell where
 * they are by colour as well as by name. Taken from the admin's chart palette (`styles.css`), whose colours were
 * measured against each other and against white. **Colour is never the only cue**: every group is also named in
 * words wherever its colour appears. Text in a group colour is mixed toward the page text colour first (62% colour, 38% text: 5.2:1 or more on every tint, measured for all nine), and
 * white text sits on the colour mixed toward black, so both stay above 4.5:1.
 */
export const GUIDE_GROUP_HUE: Readonly<Record<GuideGroup, string>> = {
  'Start here': 'var(--chart-1)',
  'Access and security': 'var(--chart-5)',
  'Photos and video': 'var(--chart-4)',
  Catalogue: 'var(--chart-3)',
  'Orders and delivery': 'var(--chart-2)',
  'Marketing and customers': 'var(--chart-6)',
  'Shop content': 'var(--chart-8)',
  Reports: 'var(--chart-7)',
  'Settings and records': 'var(--status-cancelled)',
  Help: 'var(--chart-1)',
};

export type CalloutTone = 'tip' | 'warning' | 'note';

export type GuideBlock =
  /** A paragraph. */
  | { readonly kind: 'text'; readonly text: string }
  /** A sub-heading inside a section (an `h3`). */
  | { readonly kind: 'heading'; readonly text: string }
  /** Numbered steps, in the order staff do them. */
  | { readonly kind: 'steps'; readonly title?: string; readonly items: readonly string[] }
  /** A bulleted list. */
  | { readonly kind: 'list'; readonly title?: string; readonly items: readonly string[] }
  /** A table. `caption` is read by screen readers and shown above the table. */
  | {
      readonly kind: 'table';
      readonly caption: string;
      readonly head: readonly string[];
      readonly rows: readonly (readonly string[])[];
    }
  /** A box that stands out: a tip, a warning about something that cannot be undone, or a plain note. */
  | { readonly kind: 'callout'; readonly tone: CalloutTone; readonly text: string };

/** A screen of the admin a section is about, offered as an "Open" link at the top of the section. */
export interface GuideScreen {
  readonly label: string;
  readonly path: string;
}

export interface GuideSection {
  /** The anchor: lower-case words and hyphens. Unique across the guide. */
  readonly id: string;
  readonly group: GuideGroup;
  readonly title: string;
  /** One or two sentences: what this section is for and who needs it. */
  readonly summary: string;
  readonly screens?: readonly GuideScreen[];
  readonly blocks: readonly GuideBlock[];
}
