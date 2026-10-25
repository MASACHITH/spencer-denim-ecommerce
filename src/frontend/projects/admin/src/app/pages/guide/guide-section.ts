import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CalloutTone, GUIDE_GROUP_HUE, GuideSection } from './guide.model';
import { GuideText } from './guide-text';

const CALLOUT_LABEL: Readonly<Record<CalloutTone, string>> = {
  tip: 'Tip',
  warning: 'Be careful',
  note: 'Note',
};

/**
 * Draws one `GuideSection`: its heading, a line saying what it is for, links to the screens it covers,
 * then each block. One renderer for the whole guide, so a new section is data and not a new template.
 *
 * Tables sit in a focusable, labelled scroll region (the `.adm-table-scroll` rule in `styles.css`) so a
 * wide one scrolls inside itself at 320 px and a keyboard can still reach it.
 */
@Component({
  selector: 'sda-guide-section',
  imports: [GuideText, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="gs" [id]="section().id" [attr.aria-labelledby]="section().id + '-title'" [style.--g]="hue()">
      <p class="gs__eyebrow">{{ section().group }}</p>
      <h2 class="gs__title" [id]="section().id + '-title'" tabindex="-1" data-guide-heading>{{ section().title }}</h2>
      <p class="gs__summary">{{ section().summary }}</p>

      @if (section().screens; as screens) {
        <p class="gs__screens">
          <span class="gs__screens-label">Open:</span>
          @for (screen of screens; track screen.path) {
            <a class="gs__screen" [routerLink]="screen.path">{{ screen.label }}</a>
          }
        </p>
      }

      @for (block of section().blocks; track $index) {
        @if (block.kind === 'text') {
          <p class="gs__p"><sda-guide-text [text]="block.text" /></p>
        } @else if (block.kind === 'heading') {
          <h3 class="gs__h3">{{ block.text }}</h3>
        } @else if (block.kind === 'steps') {
          @if (block.title) {
            <p class="gs__list-title">{{ block.title }}</p>
          }
          <ol class="gs__list gs__list--steps">
            @for (item of block.items; track $index) {
              <li><sda-guide-text [text]="item" /></li>
            }
          </ol>
        } @else if (block.kind === 'list') {
          @if (block.title) {
            <p class="gs__list-title">{{ block.title }}</p>
          }
          <ul class="gs__list">
            @for (item of block.items; track $index) {
              <li><sda-guide-text [text]="item" /></li>
            }
          </ul>
        } @else if (block.kind === 'table') {
          <div class="adm-table-scroll gs__table" tabindex="0" role="region" [attr.aria-label]="block.caption">
            <table class="adm-table" [style.min-width.rem]="block.head.length * columnRem">
              <caption class="gs__caption">{{ block.caption }}</caption>
              <thead>
                <tr>
                  @for (heading of block.head; track $index) {
                    <th scope="col">{{ heading }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of block.rows; track $index) {
                  <tr>
                    @for (cell of row; track $index) {
                      @if ($first) {
                        <th scope="row"><sda-guide-text [text]="cell" /></th>
                      } @else {
                        <td><sda-guide-text [text]="cell" /></td>
                      }
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <div class="gs__callout" role="note" [attr.data-tone]="block.tone">
            <strong class="gs__callout-label">{{ label(block.tone) }}</strong>
            <span><sda-guide-text [text]="block.text" /></span>
          </div>
        }
      }
    </section>
  `,
  styles: `
    :host { display: block; }
    /*
     * Colour, by group (UAT feedback, 2026-10-24: the guide was all grey). --g is the group's colour, set on the
     * section from GUIDE_GROUP_HUE. Used three ways, each kept above 4.5:1: as a stripe and tint (no text on it
     * but the page's own), as text mixed toward the page text colour, and as a fill for white text mixed toward
     * black.
     */
    .gs { --g-ink: color-mix(in srgb, var(--g) 62%, var(--color-text)); --g-fill: color-mix(in srgb, var(--g) 80%, #000); display: grid; gap: var(--space-1-5); padding: var(--space-3) var(--space-2); background: var(--color-surface); border: 1px solid var(--color-border); border-top: 4px solid var(--g); scroll-margin-top: var(--space-2); }
    .gs > * { min-width: 0; margin: 0; }
    .gs__eyebrow { justify-self: start; padding: 2px var(--space-1); font-size: var(--text-label); font-weight: var(--weight-semibold); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--g-ink); background: color-mix(in srgb, var(--g) 12%, var(--color-surface)); border: 1px solid color-mix(in srgb, var(--g) 35%, var(--color-surface)); }
    .gs__title { font-size: var(--text-h2); font-weight: var(--weight-regular); line-height: 1.2; overflow-wrap: anywhere; }
    .gs__title:focus-visible { outline: 2px solid var(--color-text); outline-offset: 4px; }
    .gs__summary { max-width: 70ch; font-size: var(--text-body); color: var(--color-text-muted); }
    .gs__screens { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-1); font-size: var(--text-small); }
    .gs__screens-label { color: var(--color-text-muted); }
    .gs__screen { display: inline-flex; align-items: center; min-height: var(--tap); padding-inline: var(--space-1-5); color: #1d4ed8; font-weight: var(--weight-semibold); background: color-mix(in srgb, #1d4ed8 8%, var(--color-surface)); border: 1px solid #1d4ed8; text-decoration: none; }
    .gs__screen::after { content: '\\2192' / ''; margin-inline-start: var(--space-0-5); }
    .gs__screen:hover { color: #fff; background: #1d4ed8; }
    .gs__p, .gs__list { max-width: 75ch; font-size: var(--text-body); line-height: 1.6; overflow-wrap: anywhere; }
    .gs__h3 { margin-top: var(--space-2); font-size: var(--text-h3); font-weight: var(--weight-semibold); }
    .gs__list-title { font-weight: var(--weight-semibold); }
    .gs__list { padding-inline-start: var(--space-3); }
    .gs__list li::marker { color: var(--g-fill); }
    .gs__list li + li { margin-top: var(--space-0-5); }
    /* Numbered steps: a coloured number, so the order is the first thing the eye finds. */
    .gs__list--steps { padding-inline-start: 0; list-style: none; counter-reset: step; }
    .gs__list--steps li { position: relative; padding-inline-start: 2.5rem; min-height: 1.75rem; counter-increment: step; }
    .gs__list--steps li::before { content: counter(step); position: absolute; inset-inline-start: 0; top: 0.1em; display: grid; place-items: center; width: 1.75rem; height: 1.75rem; border-radius: 50%; font-size: var(--text-small); font-weight: var(--weight-semibold); line-height: 1; color: #fff; background: var(--g-fill); }
    .gs__caption { padding-block: var(--space-1); text-align: start; font-weight: var(--weight-semibold); caption-side: top; }
    .gs__table { border: 1px solid var(--color-border); }
    .gs__table th, .gs__table td { overflow-wrap: break-word; }
    .gs__table thead th { color: var(--g-ink); background: color-mix(in srgb, var(--g) 12%, var(--color-surface)); border-bottom: 2px solid var(--g); }
    .gs__table tbody tr:nth-child(even) td { background: color-mix(in srgb, var(--g) 3%, var(--color-surface)); }
    .gs__table tbody th { position: sticky; inset-inline-start: 0; font-weight: var(--weight-semibold); background: var(--color-surface); box-shadow: 1px 0 0 var(--color-border); }
    .gs__callout { --callout: #1d4ed8; display: grid; gap: var(--space-0-5); max-width: 75ch; padding: var(--space-1-5) var(--space-2); border-inline-start: 4px solid var(--callout); background: color-mix(in srgb, var(--callout) 8%, var(--color-surface)); line-height: 1.55; overflow-wrap: anywhere; }
    .gs__callout[data-tone='tip'] { --callout: var(--color-success); }
    .gs__callout[data-tone='warning'] { --callout: #b45309; }
    .gs__callout-label { font-size: var(--text-label); font-weight: var(--weight-semibold); letter-spacing: var(--tracking-label); text-transform: uppercase; color: color-mix(in srgb, var(--callout) 80%, var(--color-text)); }
    @media print {
      .gs { border: 0; padding: 0 0 var(--space-2); break-inside: auto; }
      .gs__screens, .gs__callout { break-inside: avoid; }
      .gs__table { overflow: visible; }
      .gs__table .adm-table { min-width: 0 !important; }
      .gs__table tbody th { position: static; box-shadow: none; }
    }
  `,
})
export class GuideSectionView {
  readonly section = input.required<GuideSection>();

  /** The group's colour, as a CSS value the stylesheet reads through `--g`. */
  protected readonly hue = computed(() => GUIDE_GROUP_HUE[this.section().group]);

  /** Each table column gets this much room before the table scrolls inside its box instead of squeezing. */
  protected readonly columnRem = 8.5;

  protected label(tone: CalloutTone): string {
    return CALLOUT_LABEL[tone];
  }
}
