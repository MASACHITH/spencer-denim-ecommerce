import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CONTENT_PAGE_KEYS } from '../../core/content/content-page.model';
import { ContentPageService } from '../../core/content/content-page.service';

/** One thing the sentence names: its words, and where it lives when the page is on the shop. */
interface LegalPart {
  readonly key: string;
  readonly label: string;
  readonly slug: string | null;
}

/**
 * A sentence that points at the terms and conditions and the privacy policy, where the shop asks for agreement or
 * collects an address (issue #252): `lead`, then the pages, then a full stop.
 *
 * **The pages are linked only when they are published, and the whole sentence goes when none is.** The same
 * reasoning as the footer's newsletter line: an unconditional link is a 404 beside a statement of consent on every
 * checkout, and "see our privacy policy" with no policy to see is a worse answer to "where is it" than saying
 * nothing. The pages are the client's to unpublish, so the published-page index is asked rather than assumed.
 *
 * **It never hard-codes an address.** A slug is editable, so each link is built from the live index by system key,
 * as the footer builds its own: a client who renames `terms` to `conditions-of-sale` does not leave a broken link
 * here, and the words are the page's own title.
 *
 * `termsFallback` is for the one sentence that already existed before the pages did: checkout has always said "you
 * agree to our terms and conditions", and keeps saying it in plain words while the page is unpublished rather than
 * dropping a line the shop has long carried. The privacy policy has no such fallback, because nothing in the shop
 * ever claimed there was one.
 */
@Component({
  selector: 'sd-legal-links',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (parts().length > 0) {
      {{ lead() }}
      @for (part of parts(); track part.key; let first = $first; let last = $last) {
        @if (!first) {
          {{ last ? ' and ' : ', ' }}
        }
        @if (part.slug; as slug) {
          <a class="link" [routerLink]="'/' + slug" [attr.data-testid]="'legal-link-' + part.key">{{ part.label }}</a>
        } @else {
          <span [attr.data-testid]="'legal-text-' + part.key">{{ part.label }}</span>
        }
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

  /** The words before the pages, for example "By placing this order you agree to our". */
  readonly lead = input.required<string>();

  /** Name the terms and conditions. */
  readonly terms = input(false);

  /** Name the privacy policy. */
  readonly privacy = input(false);

  /** Say "terms and conditions" in plain words while that page is not published, instead of leaving it out. */
  readonly termsFallback = input(false);

  protected readonly parts = computed<readonly LegalPart[]>(() => {
    const parts: LegalPart[] = [];

    if (this.terms()) {
      const page = this.pages.bySystemKey(CONTENT_PAGE_KEYS.terms);

      if (page) {
        parts.push({ key: CONTENT_PAGE_KEYS.terms, label: page.title, slug: page.slug });
      } else if (this.termsFallback()) {
        parts.push({ key: CONTENT_PAGE_KEYS.terms, label: 'terms and conditions', slug: null });
      }
    }

    if (this.privacy()) {
      const page = this.pages.bySystemKey(CONTENT_PAGE_KEYS.privacy);

      if (page) {
        parts.push({ key: CONTENT_PAGE_KEYS.privacy, label: page.title, slug: page.slug });
      }
    }

    return parts;
  });
}
