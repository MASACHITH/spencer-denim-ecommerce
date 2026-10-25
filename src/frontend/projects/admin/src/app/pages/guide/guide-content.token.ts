import { InjectionToken } from '@angular/core';

import { GUIDE_SEARCH_INDEX, GUIDE_SECTIONS } from './content';
import { GuideSection } from './guide.model';

/** What the guide page draws: the sections in reading order and their lower-cased search text by id. */
export interface GuideContent {
  readonly sections: readonly GuideSection[];
  readonly index: ReadonlyMap<string, string>;
}

/**
 * The guide's words, behind a token so the page's own tests can draw three small sections instead of the whole
 * guide (which is thirty-odd sections of tables and takes seconds to render in a test). The real content is
 * what the app gets, and `guide-content.spec.ts` checks all of it.
 */
export const GUIDE_CONTENT = new InjectionToken<GuideContent>('GUIDE_CONTENT', {
  providedIn: 'root',
  factory: () => ({ sections: GUIDE_SECTIONS, index: GUIDE_SEARCH_INDEX }),
});
