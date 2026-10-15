import { REQUEST_CONTEXT, TransferState, inject, makeStateKey } from '@angular/core';

/**
 * What the storefront's server and its render tell each other about recording one page view (W4-03).
 * The full account of who records what is in `src/server/page-view.ts`.
 */
export interface TrafficRenderContext {
  /** Set by the server before the render: a GET that is not a prefetch. */
  readonly recordable: boolean;
  /** Set by the render: it actually ran (the engine did not fall back to the client-side shell). */
  rendered: boolean;
}

/**
 * The flag the render leaves in the page when the server will record it, so the browser does not record
 * the same view again. Absent means the browser records the first page itself.
 */
export const SERVER_PAGE_VIEW = makeStateKey<boolean>('sdServerPageView');

export function isTrafficRenderContext(value: unknown): value is TrafficRenderContext {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as TrafficRenderContext).recordable === 'boolean' &&
    typeof (value as TrafficRenderContext).rendered === 'boolean'
  );
}

/**
 * Server-only app initializer. Marks the render as having run and, when the server will record it, leaves
 * {@link SERVER_PAGE_VIEW} in the transfer state. With no context — a render nobody is going to record —
 * it leaves nothing, so the browser records the page instead of nobody doing so.
 */
export function markServerPageView(): void {
  const context = inject(REQUEST_CONTEXT, { optional: true });
  if (!isTrafficRenderContext(context)) {
    return;
  }

  context.rendered = true;

  if (context.recordable) {
    inject(TransferState).set(SERVER_PAGE_VIEW, true);
  }
}
