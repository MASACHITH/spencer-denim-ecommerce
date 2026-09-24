import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { timeout } from 'rxjs';

import { ContentApi } from './content-api';
import { HomePageDto, HomeSectionDto } from './content.model';
import { renderableSections } from './home-sections';
import { ServerHandover } from '../platform/server-handover';

/**
 * How long the homepage waits for its content before it falls back.
 *
 * The homepage is the page: a render that hangs is worse than a degraded one, and the storefront's
 * SSR process holds the request open until every in-flight call answers. Longer than the menu's
 * two seconds, because this one call also resolves three product rows against SQL Server, and a
 * slow-but-working store should still get its homepage rather than the fallback.
 */
export const HOME_CONTENT_TIMEOUT_MS = 5_000;

/** Where the server leaves the homepage for the browser that hydrates it. */
const HOME_HANDOVER = 'content:home';

/** What the homepage is doing, which is what the page renders around. */
export type HomeContentPhase = 'loading' | 'ready' | 'error';

/**
 * The homepage's content (HOME-01..10).
 *
 * The request is issued synchronously from {@link load}, so it is already in flight while the
 * server renders — `HttpClient` holds the application "unstable" until it answers, and the HTML a
 * crawler receives has the real sections in it — and what the server fetched is handed to the
 * browser so hydration does not fetch it again.
 *
 * Provided in root rather than by the page: the page is `/` and there is one of it, and a shopper
 * who comes back to the home page from a listing should see it instantly rather than wait for a
 * second render of the same thing.
 */
@Injectable({ providedIn: 'root' })
export class HomeContentStore {
  private readonly api = inject(ContentApi);
  private readonly handover = inject(ServerHandover);
  private readonly destroyRef = inject(DestroyRef);

  private readonly source = signal<readonly HomeSectionDto[]>([]);
  private readonly phase = signal<HomeContentPhase>('loading');

  private started = false;

  /**
   * The sections to draw, in the API's order, with the empty ones already gone.
   *
   * The filtering lives here rather than in the template so "a section with nothing in it draws
   * nothing" is one rule with one test, instead of ten `@if`s that have to agree.
   */
  readonly sections: Signal<readonly HomeSectionDto[]> = computed(() =>
    renderableSections(this.source()),
  );

  readonly status: Signal<HomeContentPhase> = this.phase.asReadonly();

  readonly isLoading = computed(() => this.phase() === 'loading');

  /** True when there is nothing to draw, whether because of a failure or because it is empty. */
  readonly isEmpty = computed(() => this.phase() !== 'loading' && this.sections().length === 0);

  /**
   * True when the content service could not be reached. The page still renders: the shell, and a
   * short line with the way into the catalogue, which is what a shopper came for.
   */
  readonly hasError = computed(() => this.phase() === 'error');

  /** Fetches the homepage once per application. Calling it again does nothing. */
  load(): void {
    if (this.started) {
      return;
    }

    this.started = true;

    const handedOver = this.handover.take<HomePageDto>(HOME_HANDOVER);
    if (handedOver) {
      this.accept(handedOver);
      return;
    }

    this.api
      .home()
      .pipe(timeout(HOME_CONTENT_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          this.handover.give(HOME_HANDOVER, page);
          this.accept(page);
        },
        // No message is kept: the page says one thing whatever went wrong, and a status code or a
        // proxy's HTML error page is not something to put in front of a shopper.
        error: () => this.phase.set('error'),
      });
  }

  /** Fetches again after a failure, which is the only thing "Try again" can usefully do. */
  retry(): void {
    this.started = false;
    this.phase.set('loading');
    this.load();
  }

  private accept(page: HomePageDto): void {
    // A successful response with a useless body is still a successful response: `catchError` and
    // the error branch above never see it. A proxy answering `200 {"oops":true}`, or a JSON naming
    // policy that turns every field into `undefined`, arrives here looking fine.
    if (!Array.isArray(page?.sections)) {
      this.source.set([]);
      this.phase.set('error');
      return;
    }

    // An empty page is **not** an error. The client is allowed to switch every section off, and
    // apologising for a state they chose would be a lie. Both render the same fallback; only the
    // wording differs.
    this.source.set(page.sections);
    this.phase.set('ready');
  }
}
