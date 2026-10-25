import { GUIDE_GROUPS, GuideSection } from '../guide.model';
import { searchTextOf } from '../guide-search';

import { ACCESS_SECTIONS } from './access';
import { CATALOGUE_SECTIONS } from './catalogue';
import { HELP_SECTIONS } from './help';
import { MARKETING_SECTIONS } from './marketing';
import { MEDIA_SECTIONS } from './media';
import { ORDER_SECTIONS } from './orders';
import { REPORT_SECTIONS } from './reports';
import { SETTINGS_SECTIONS } from './settings';
import { SHOP_CONTENT_SECTIONS } from './shop-content';
import { START_SECTIONS } from './start';

/**
 * Every section of the guide, in reading order: by group (the order of `GUIDE_GROUPS`), and within a group
 * in the order its file lists them. Sorting here means a file never has to be imported in the right order.
 */
export const GUIDE_SECTIONS: readonly GuideSection[] = [
  ...START_SECTIONS,
  ...ACCESS_SECTIONS,
  ...MEDIA_SECTIONS,
  ...CATALOGUE_SECTIONS,
  ...ORDER_SECTIONS,
  ...MARKETING_SECTIONS,
  ...SHOP_CONTENT_SECTIONS,
  ...REPORT_SECTIONS,
  ...SETTINGS_SECTIONS,
  ...HELP_SECTIONS,
]
  .map((section, position) => ({ section, position }))
  .sort(
    (a, b) =>
      GUIDE_GROUPS.indexOf(a.section.group) - GUIDE_GROUPS.indexOf(b.section.group) || a.position - b.position,
  )
  .map(({ section }) => section);

/** Lower-cased searchable text per section id, built once. */
export const GUIDE_SEARCH_INDEX: ReadonlyMap<string, string> = new Map(
  GUIDE_SECTIONS.map((section) => [section.id, searchTextOf(section)]),
);
