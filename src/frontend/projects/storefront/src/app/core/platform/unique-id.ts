import { Injectable } from '@angular/core';

/**
 * Document-unique element ids, counted **per application instance** rather than per module.
 *
 * `let nextId = 0` at the top of a component file is the usual way to do this and it is wrong on a
 * server. The module is loaded once and the counter lives for the life of the Node process, so ids
 * climb across requests — measured on the search box: one render served `sd-search-76/77/78` and the
 * next `sd-search-79/80/81`, while the browser that hydrated either of them started again at 0. No
 * accessibility defect followed, because `aria-controls` and `aria-activedescendant` are resolved
 * within one document and the ids are internally consistent in each — but it is per-request state in
 * a long-lived process, which is the thing the whole application is otherwise careful not to have
 * (doc 03 §5), and it means the server's HTML and the browser's DOM disagree about every generated
 * id the moment hydration finishes.
 *
 * Provided in root, which on the server is a **new injector for every request** — so the count
 * restarts per render and the browser's first ids are the ones the server sent.
 *
 * `listing-filters.ts` still has the module-level form (`nextPanelId`). It is the same shape of
 * problem and it is not changed here only because it is outside this task's diff; this service is
 * where it should move when someone touches that file.
 */
@Injectable({ providedIn: 'root' })
export class UniqueId {
  private next = 0;

  /** `prefix-0`, `prefix-1`, … counted across every caller in this application instance. */
  for(prefix: string): string {
    return `${prefix}-${this.next++}`;
  }
}
