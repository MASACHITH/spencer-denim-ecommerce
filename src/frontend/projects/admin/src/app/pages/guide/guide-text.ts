import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { GuideScroller } from './guide-scroller';

export type TextPart =
  | { readonly kind: 'plain'; readonly text: string }
  | { readonly kind: 'strong'; readonly text: string }
  | { readonly kind: 'code'; readonly text: string }
  /** `path` is an admin route (`/orders`) or an in-guide anchor (`#photos-and-video`). */
  | { readonly kind: 'link'; readonly text: string; readonly path: string };

/** `**strong**`, `` `code` ``, `[label](/route)` or `[label](#anchor)`. Anything else is plain text. */
const MARKUP = /\*\*(.+?)\*\*|`(.+?)`|\[(.+?)\]\((\/[^)\s]*|#[a-z0-9-]+)\)/g;

/**
 * Splits one line of guide text into parts. There is deliberately no HTML in the output: the guide's
 * words are drawn as text nodes, so nothing a content file contains can become markup.
 */
export function parseGuideText(source: string): readonly TextPart[] {
  const parts: TextPart[] = [];
  let from = 0;

  for (const match of source.matchAll(MARKUP)) {
    if (match.index > from) {
      parts.push({ kind: 'plain', text: source.slice(from, match.index) });
    }

    if (match[1] !== undefined) {
      parts.push({ kind: 'strong', text: match[1] });
    } else if (match[2] !== undefined) {
      parts.push({ kind: 'code', text: match[2] });
    } else {
      parts.push({ kind: 'link', text: match[3], path: match[4] });
    }

    from = match.index + match[0].length;
  }

  if (from < source.length) {
    parts.push({ kind: 'plain', text: source.slice(from) });
  }

  return parts;
}

/** The words of a line with the markup taken off, for searching. */
export function plainGuideText(source: string): string {
  return parseGuideText(source)
    .map((part) => part.text)
    .join('');
}

/** One line of guide text, drawn from `parseGuideText`. */
@Component({
  selector: 'sda-guide-text',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Whitespace-only text between tags is dropped by Angular, but a space around an interpolation is kept,
  // so every interpolation sits tight inside its own element and plain words get a span.
  template: `
    @for (part of parts(); track $index) {
      @if (part.kind === 'strong') {
        <strong>{{ part.text }}</strong>
      } @else if (part.kind === 'code') {
        <code>{{ part.text }}</code>
      } @else if (part.kind === 'link') {
        @if (isAnchor(part.path)) {
          <a class="guide-link guide-link--section" [href]="part.path" (click)="jump($event, part.path)">{{ part.text }}</a>
        } @else {
          <a class="guide-link guide-link--screen" [routerLink]="part.path">{{ part.text }}</a>
        }
      } @else {
        <span>{{ part.text }}</span>
      }
    }
  `,
  styles: `
    :host { display: contents; }
    code { padding: 0 var(--space-0-5); font-size: 0.92em; background: var(--color-bg-alt); border: 1px solid var(--color-border); overflow-wrap: anywhere; }
    /*
     * A link reads as a link: blue, bold and underlined, with an arrow that says where it goes. The body text is
     * near-black, so the old black underline looked like emphasis. #1d4ed8 is 6.7:1 on white and 6.2:1 on the
     * admin's ivory; its hover shade is darker still. The arrow is decoration with empty alternative text.
     */
    .guide-link {
      color: #1d4ed8;
      font-weight: var(--weight-semibold);
      text-decoration: underline;
      text-decoration-thickness: 1.5px;
      text-underline-offset: 3px;
      border-radius: 2px;
    }
    .guide-link:hover { color: #1e3a8a; background: color-mix(in srgb, #1d4ed8 10%, transparent); }
    .guide-link--screen::after { content: '\\2192' / ''; margin-inline-start: 0.25em; text-decoration: none; display: inline-block; }
    .guide-link--section::after { content: '\\2193' / ''; margin-inline-start: 0.25em; text-decoration: none; display: inline-block; }
  `,
})
export class GuideText {
  private readonly scroller = inject(GuideScroller);

  readonly text = input.required<string>();

  protected readonly parts = computed(() => parseGuideText(this.text()));

  protected isAnchor(path: string): boolean {
    return path.startsWith('#');
  }

  protected jump(event: Event, path: string): void {
    event.preventDefault();
    this.scroller.to(path.slice(1));
  }
}
