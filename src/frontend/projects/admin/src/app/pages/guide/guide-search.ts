import { GuideBlock, GuideSection } from './guide.model';
import { plainGuideText } from './guide-text';

function blockWords(block: GuideBlock): string[] {
  switch (block.kind) {
    case 'text':
    case 'heading':
    case 'callout':
      return [plainGuideText(block.text)];
    case 'steps':
    case 'list':
      return [block.title ?? '', ...block.items.map(plainGuideText)];
    case 'table':
      return [block.caption, ...block.head, ...block.rows.flat().map(plainGuideText)];
  }
}

/** Everything a reader could type to find a section, lower-cased: its title, summary and every block. */
export function searchTextOf(section: GuideSection): string {
  return [section.group, section.title, section.summary, ...section.blocks.flatMap(blockWords)]
    .join(' ')
    .toLowerCase();
}

/** The words of a search box: lower-case, split on spaces, empty strings gone. */
export function searchTerms(query: string): readonly string[] {
  return query.toLowerCase().split(/\s+/).filter(Boolean);
}

/**
 * The sections that contain **every** word typed, in any order. An empty search keeps them all. Plain
 * substring matching on purpose: staff type "bank", "banner size" or "refund", and a fuzzy search would
 * answer a mistyped word with something that looks right and is not.
 */
export function matchSections(
  sections: readonly GuideSection[],
  index: ReadonlyMap<string, string>,
  terms: readonly string[],
): readonly GuideSection[] {
  if (terms.length === 0) {
    return sections;
  }

  return sections.filter((section) => {
    const text = index.get(section.id) ?? '';
    return terms.every((term) => text.includes(term));
  });
}
