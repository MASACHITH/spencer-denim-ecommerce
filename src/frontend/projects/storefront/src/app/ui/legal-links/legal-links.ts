import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CONTENT_PAGE_KEYS } from '../../core/content/content-page.model';
import { ContentPageService } from '../../core/content/content-page.service';

/** One sentence of the component: its lead-in words, and the page it names (or plain words when unpublished). */
interface LegalSentence {
  readonly key: string;
  readonly lead: string;
  readonly label: string;
  readonly slug: string | null;
}

/**
 * Sentences that point at the terms and conditions and the privacy policy, where the shop asks for agreement or
 * collects an address (issue #252): a lead-in, the page, a full stop. The terms and the privacy policy are separate
 * sentences with their own lead-ins, because agreeing to terms and being told how your details are used are not the
 * same act ("you agree to our terms. How we use your details is explained in our privacy policy.").
 *
 * **A page is linked only when it is published, and its sentence goes when it is not.** The same reasoning as the
 * footer's newsletter line: an unconditional link is a 404 beside a statement of consent on every checkout, and "see
 * our privacy policy" with no policy to see is a worse answer to "where is it" than saying nothing. The pages are the
 * client's to unpublish, so the published-page index is asked rather than assumed.
 *
 * **It never hard-codes an address.** A slug is editable, so each link is built from the live index by system key, as
 * the footer builds its own: a client who renames `terms` to `conditions-of-sale` does not leave a broken link here.
 * The words are the page's own title in lower case, so the sentence reads the same whichever pages are published
 * ("our terms and conditions", "our privacy policy") whatever capitals an admin gave a title.
 *
 * `termsFallback` is for the one sentence that already existed before the pages did: checkout has always said "you
 * agree to our terms and conditions", and keeps saying it in plain words while the page is unpublished rather than
 * dropping a line the shop has long carried. The privacy policy has no such fallback, because nothing in the shop
 * ever claimed there was one.
 *
 * (`target` is bound as RouterLink's own input, not as an attribute: RouterLink owns the `target` host binding on an
 * anchor, and a plain `[attr.target]` is overwritten by it. That is measured, in `legal-links.spec.ts`.)
 *
 * **`newTab` is for a page where leaving would lose the shopper's work.** Checkout and register hold a half-typed
 * form in component state (no draft, no route guard), so a link that navigated away would throw the form away; there
 * the link opens in a new tab, with text a screen reader announces ("opens in a new tab"). Where leaving loses
 * nothing, such as the homepage newsletter line, the link stays in the same tab.
 *
 * **Target size: these links are inline in a sentence and are the one exemption from the project's 44 px floor.**
 * docs/04 section 4 asks for touch targets of at least 44 px (`--tap`), and every stand-alone link or button in the
 * shop meets it. A link that is part of a line of prose cannot, without opening a 44 px gap in the line it sits in,
 * and WCAG 2.2 success criterion 2.5.8 (Target Size, Minimum) exempts exactly that case: "inline" targets in a
 * sentence. So the links are text-sized, their words are long (two to three words each), and each one ends a short
 * sentence of its own. If a layout ever puts one of them on a line by itself, give it `min-height: var(--tap)`.
 */
@Component({
  selector: 'sd-legal-links',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (sentence of sentences(); track sentence.key; let first = $first) {
      @if (!first) {
        {{ ' ' }}
      }
      {{ sentence.lead }}
      @if (sentence.slug; as slug) {
        <a
          class="link"
          [routerLink]="'/' + slug"
          [target]="newTab() ? '_blank' : undefined"
          [attr.rel]="newTab() ? 'noopener' : null"
          [attr.data-testid]="'legal-link-' + sentence.key"
          >{{ sentence.label }}@if (newTab()) {<span class="sr-only">{{ ' (opens in a new tab)' }}</span>}</a
        >
      } @else {
        <span [attr.data-testid]="'legal-text-' + sentence.key">{{ sentence.label }}</span>
      }.
    }
  `,
  styles: `
    :host {
      display: inline;
    }
    /* Nothing published, so the sentence is gone: the host must not keep a row of a grid it sits in. Comments
       do not count against :empty, and the @if leaves only a comment behind. */
    :host(:empty) {
      display: none;
    }
  `,
})
export class LegalLinks {
  private readonly pages = inject(ContentPageService);

  /** The words before the terms and conditions, for example "By placing this order you agree to our". */
  readonly termsLead = input('');

  /** The words before the privacy policy, for example "How we use your details is explained in our". */
  readonly privacyLead = input('');

  /** Name the terms and conditions. */
  readonly terms = input(false);

  /** Name the privacy policy. */
  readonly privacy = input(false);

  /** Say "terms and conditions" in plain words while that page is not published, instead of leaving it out. */
  readonly termsFallback = input(false);

  /** Open each link in a new tab (checkout and register, where leaving would lose a half-typed form). */
  readonly newTab = input(false);

  protected readonly sentences = computed<readonly LegalSentence[]>(() => {
    const sentences: LegalSentence[] = [];

    if (this.terms()) {
      const page = this.pages.bySystemKey(CONTENT_PAGE_KEYS.terms);

      if (page) {
        sentences.push({
          key: CONTENT_PAGE_KEYS.terms,
          lead: this.termsLead(),
          label: page.title.toLocaleLowerCase('en'),
          slug: page.slug,
        });
      } else if (this.termsFallback()) {
        sentences.push({
          key: CONTENT_PAGE_KEYS.terms,
          lead: this.termsLead(),
          label: 'terms and conditions',
          slug: null,
        });
      }
    }

    if (this.privacy()) {
      const page = this.pages.bySystemKey(CONTENT_PAGE_KEYS.privacy);

      if (page) {
        sentences.push({
          key: CONTENT_PAGE_KEYS.privacy,
          lead: this.privacyLead(),
          label: page.title.toLocaleLowerCase('en'),
          slug: page.slug,
        });
      }
    }

    return sentences;
  });
}
