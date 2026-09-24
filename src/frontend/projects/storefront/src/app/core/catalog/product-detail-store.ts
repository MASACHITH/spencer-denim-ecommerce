import { DestroyRef, Injectable, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { CatalogApi, ProductFailure, productFailure } from './catalog-api';
import { ProductDetailDto } from './catalog.model';
import { ServerHandover } from '../platform/server-handover';

/** What the product page is doing. */
export type ProductPhase = 'loading' | 'ready' | 'error';

/**
 * The one product a product page is showing (PDP-01..06).
 *
 * Provided by the page rather than in root: two products never share this, and a page that is
 * torn down takes its state with it.
 *
 * The request is issued synchronously from {@link load}, so it is already in flight while the
 * server renders — `HttpClient` holds the application unstable until it answers — and the HTML a
 * crawler receives carries the product's name, price and description rather than a spinner.
 *
 * A stale response can never overwrite a newer one: every load takes a ticket, and a response
 * that is not holding the current ticket is dropped. That matters here because a client-side
 * navigation between two related products reuses the same page instance.
 */
@Injectable()
export class ProductDetailStore {
  private readonly api = inject(CatalogApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly handover = inject(ServerHandover);

  private readonly current = signal<ProductDetailDto | null>(null);
  private readonly phase = signal<ProductPhase>('loading');
  private readonly failure = signal<ProductFailure | null>(null);

  private ticket = 0;

  readonly product: Signal<ProductDetailDto | null> = this.current.asReadonly();

  readonly status: Signal<ProductPhase> = this.phase.asReadonly();

  /** How the request failed, or null. The page decides what each one looks like. */
  readonly error: Signal<ProductFailure | null> = this.failure.asReadonly();

  readonly isLoading = computed(() => this.phase() === 'loading');

  /** The product does not exist: an unknown slug, or one the API will not even accept. */
  readonly isMissing = computed(() => this.failure() === 'missing');

  /** The store answered badly, or not at all. The product may well still exist. */
  readonly isUnavailable = computed(
    () => this.failure() === 'failed' || this.failure() === 'offline',
  );

  /** Loads one product by slug, replacing whatever was here. */
  load(slug: string): void {
    const ticket = ++this.ticket;
    this.failure.set(null);

    const handedOver = this.handover.take<Handover>(handoverKey(slug));
    if (handedOver) {
      if (isMissing(handedOver)) {
        this.current.set(null);
        this.failure.set('missing');
        this.phase.set('error');
      } else {
        this.accept(ticket, handedOver);
      }
      return;
    }

    this.current.set(null);
    this.phase.set('loading');

    this.api
      .product(slug)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (product) => {
          this.handover.give<Handover>(handoverKey(slug), product);
          this.accept(ticket, product);
        },
        error: (error: unknown) => this.reject(ticket, error, slug),
      });
  }

  /** Asks again for the same product, after the shopper presses "Try again". */
  retry(slug: string): void {
    this.load(slug);
  }

  private accept(ticket: number, product: ProductDetailDto): void {
    if (ticket !== this.ticket) {
      return;
    }

    this.current.set(product);
    this.failure.set(null);
    this.phase.set('ready');
  }

  private reject(ticket: number, error: unknown, slug?: string): void {
    if (ticket !== this.ticket) {
      return;
    }

    const failure = productFailure(error);

    // A product that is not there will not be there a second later either, so the browser is
    // told and does not ask again — that repeat request was a guaranteed 404 in the console on
    // every not-found page. A *fault* is handed over to nobody: a 500 while the server rendered
    // may well be over by the time the page hydrates, and replaying it would freeze a passing
    // problem into the page.
    if (failure === 'missing' && slug !== undefined) {
      this.handover.give<Handover>(handoverKey(slug), MISSING);
    }

    this.current.set(null);
    this.failure.set(failure);
    this.phase.set('error');
  }
}

/**
 * What the server leaves for the browser: the product, or a note that there is no such product.
 */
type Handover = ProductDetailDto | typeof MISSING;

const MISSING = { missing: true } as const;

function isMissing(value: Handover): value is typeof MISSING {
  return (value as { missing?: boolean }).missing === true;
}

/** Where one product's server-rendered answer waits for the browser. */
function handoverKey(slug: string): string {
  return `pdp:${slug}`;
}
