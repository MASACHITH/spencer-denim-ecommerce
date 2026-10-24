import { Injector, afterNextRender, signal } from '@angular/core';

/** The outcome of the last CSV export, drawn on the screen (`ExportNoteView`). */
export interface ExportNote {
  readonly tone: 'ok' | 'error';
  readonly text: string;
}

export function exportSucceeded(fileName: string): ExportNote {
  return {
    tone: 'ok',
    text: `Export downloaded as ${fileName}. Look in your browser's Downloads. If nothing arrived, your browser may have blocked the download: allow it and press Export CSV again.`,
  };
}

export function exportFailed(reason: string): ExportNote {
  return { tone: 'error', text: `The export failed. ${reason}` };
}

/** Hands a downloaded file to the browser to save, the way the newsletter export does. */
export function saveFile(document: Document, blob: Blob, fileName: string): void {
  const view = document.defaultView;

  if (!view?.URL?.createObjectURL) {
    return;
  }

  const href = view.URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = href;
  link.download = fileName;
  link.rel = 'noopener';
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  link.remove();
  view.setTimeout(() => view.URL.revokeObjectURL(href), 0);
}

/**
 * A live region's text. Writing the same sentence twice is re-announced by clearing it first and writing it
 * again after the next render — never with `queueMicrotask`, which can land in the same update (#109).
 */
export class Announcer {
  readonly text = signal('');

  constructor(private readonly injector: Injector) {}

  say(message: string): void {
    if (this.text() !== message) {
      this.text.set(message);
      return;
    }

    this.text.set('');
    afterNextRender(() => this.text.set(message), { injector: this.injector });
  }
}

/** `aria-disabled` paging: whether moving to `page` is allowed at all. */
export function canGo(page: number, pageSize: number, totalCount: number): boolean {
  return page >= 1 && (page - 1) * pageSize < totalCount;
}

/** "26–50 of 120". */
export function showing(page: number, pageSize: number, totalCount: number): string {
  if (totalCount === 0) {
    return '0 of 0';
  }

  const first = (page - 1) * pageSize + 1;
  const last = Math.min(page * pageSize, totalCount);

  return `${first}–${last} of ${totalCount}`;
}

/** Whether a period is one the server will answer: both days, in order, at most 366 days. */
export function periodProblem(from: string, to: string, addDays: (day: string, days: number) => string): string | null {
  if (!from || !to) {
    return 'Choose both a start and an end date.';
  }

  if (from > to) {
    return 'The end date is before the start date.';
  }

  return addDays(from, 365) < to ? 'A period can be at most 366 days long.' : null;
}

/**
 * After the next render, if the element that had focus has been removed, puts focus on the live region
 * named by `testId` so the keyboard is not thrown to `<body>` (#109). Uses `afterNextRender`, never
 * `queueMicrotask`: a microtask can run before the update that removes the element.
 */
export function rehomeFocus(document: Document, injector: Injector, focused: HTMLElement | null, testId: string): void {
  afterNextRender(
    () => {
      if (focused !== null && focused.isConnected) {
        return;
      }

      document.querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.focus({ preventScroll: true });
    },
    { injector },
  );
}
