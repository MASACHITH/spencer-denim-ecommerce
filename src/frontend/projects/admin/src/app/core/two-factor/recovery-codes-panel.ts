import { ChangeDetectionStrategy, Component, DOCUMENT, ElementRef, inject, input, signal, viewChild } from '@angular/core';
import { BRAND } from '@spencer-denim/shared';

/**
 * W1-10: shows a freshly issued set of recovery codes, once, with copy and download. The codes live only in this
 * component's input; nothing stores them, and they are gone when the page is left.
 */
@Component({
  selector: 'sda-recovery-codes-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="codes" aria-labelledby="codes-title">
      <h2 class="codes__title" id="codes-title" tabindex="-1" #heading>Your recovery codes</h2>
      <p class="codes__lead">
        Each code signs you in once if you lose your phone. Store them somewhere safe, such as a password manager.
        They will not be shown again.
      </p>
      <ol class="codes__list" aria-label="Recovery codes">
        @for (code of codes(); track code) {
          <li class="codes__code">{{ code }}</li>
        }
      </ol>
      <div class="codes__actions">
        <button class="codes__button" type="button" (click)="copy()">Copy codes</button>
        <button class="codes__button" type="button" (click)="download()">Download as a file</button>
      </div>
      <p class="codes__status" role="status">{{ status() }}</p>
    </section>
  `,
  styles: `
    :host { display: block; }
    .codes { display: grid; gap: var(--space-1-5); }
    .codes__title { font-size: var(--text-h4); font-weight: var(--weight-semibold); }
    .codes__title:focus { outline: none; }
    .codes__lead,
    .codes__status { color: var(--color-text-muted); font-size: var(--text-small); }
    .codes__status:empty { display: none; }
    .codes__list {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(8.5rem, 1fr));
      gap: var(--space-1);
      margin: 0;
      padding: var(--space-1-5);
      list-style: none;
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }
    .codes__code {
      font-size: var(--text-data);
      font-variant-numeric: tabular-nums;
      letter-spacing: var(--tracking-label);
      overflow-wrap: anywhere;
    }
    .codes__actions { display: flex; flex-wrap: wrap; gap: var(--space-1); }
    .codes__button {
      flex: 1 1 10rem;
      min-height: var(--tap);
      padding-inline: var(--space-2);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
      cursor: pointer;
    }
  `,
})
export class RecoveryCodesPanel {
  private readonly document = inject(DOCUMENT);

  readonly codes = input.required<readonly string[]>();
  /** The account the codes belong to, written at the top of the downloaded file. */
  readonly account = input('');

  protected readonly status = signal('');

  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  /** Moves keyboard and screen-reader focus to the panel's heading, e.g. right after new codes arrive. */
  focus(): void {
    this.heading().nativeElement.focus();
  }

  protected copy(): void {
    const clipboard = this.document.defaultView?.navigator.clipboard;
    if (!clipboard) {
      this.status.set('Copying is not available in this browser. Select the codes and copy them instead.');
      return;
    }

    clipboard.writeText(this.codes().join('\n')).then(
      () => this.status.set('Codes copied.'),
      () => this.status.set('Could not copy. Select the codes and copy them instead.'),
    );
  }

  protected download(): void {
    const view = this.document.defaultView;
    if (!view) {
      return;
    }

    const heading = this.account()
      ? `${BRAND.adminName} recovery codes for ${this.account()}`
      : `${BRAND.adminName} recovery codes`;
    const text = [heading, 'Each code works once.', '', ...this.codes(), ''].join('\n');
    const url = view.URL.createObjectURL(new Blob([text], { type: 'text/plain' }));

    const link = this.document.createElement('a');
    link.href = url;
    link.download = `${fileNameSlug(BRAND.adminName)}-recovery-codes.txt`;
    link.click();
    // Revoked on the next tick: some browsers start the download only after the click handler returns.
    view.setTimeout(() => view.URL.revokeObjectURL(url));
    this.status.set('Codes downloaded.');
  }
}

/**
 * The downloaded file's name, derived from the application's name rather than written out beside it,
 * so a rename reaches the file a staff user saves as well as the heading inside it.
 */
function fileNameSlug(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}
