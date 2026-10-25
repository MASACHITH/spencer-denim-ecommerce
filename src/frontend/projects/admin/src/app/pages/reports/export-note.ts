import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { ExportNote } from './report-shared';

/**
 * What happened to the last "Export CSV" press, **on the screen** where a sighted staff member can read it.
 *
 * The export used to tell only a screen reader: "Preparing the export…", "Export downloaded as…" and "The export
 * failed. …" went into a visually hidden live region. A failed export (on UAT, for instance) therefore showed
 * nothing at all, which reads as "the button does not work". The live region still announces; this adds the same
 * sentence in view, in red when it failed.
 */
@Component({
  selector: 'sda-export-note',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (note(); as shown) {
      <p [class]="shown.tone === 'error' ? 'adm-field-error' : 'adm-muted'" [attr.data-testid]="testId()">
        {{ shown.text }}
      </p>
    }
  `,
  styles: `
    :host { display: block; }
    p { margin: 0; max-width: 70ch; }
  `,
})
export class ExportNoteView {
  readonly note = input<ExportNote | null>(null);
  readonly testId = input.required<string>();
}
