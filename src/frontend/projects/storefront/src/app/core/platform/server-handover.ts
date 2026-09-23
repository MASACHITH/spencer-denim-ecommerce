import { isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, TransferState, inject, makeStateKey } from '@angular/core';

/**
 * Passes what the server fetched while rendering to the browser that hydrates the page, so the
 * browser does not fetch it again.
 *
 * Angular's own HTTP transfer cache should do this and cannot. It keys a response on the request
 * URL as it sees it, and it runs *after* the application's interceptors — `HttpInterceptorHandler`
 * builds its chain as `[...HTTP_INTERCEPTOR_FNS, ...HTTP_ROOT_INTERCEPTOR_FNS]` — so the absolute
 * internal address `apiBaseUrlInterceptor` writes during SSR (`http://api:8080/api/...`) is stored
 * under a key the browser, which asks for `/api/...`, can never look up. Every catalogue call was
 * therefore made twice, and the second one replaced server-rendered content with a loading state.
 *
 * The proper fix is in the shared SSR setup rather than here: either resolve the internal address
 * below the interceptor chain without replacing `HttpBackend` (replacing it turns `withFetch` off
 * — NG02801 — and the render never stabilises), or configure
 * `HTTP_TRANSFER_CACHE_ORIGIN_MAP` once the storefront's public origin is known to the server.
 * Until then this does the same job explicitly, for the two calls that matter.
 *
 * A handover is taken once. A shopper who comes back to a listing later gets fresh stock and
 * prices rather than a replay of the first render.
 */
@Injectable({ providedIn: 'root' })
export class ServerHandover {
  private readonly transferState = inject(TransferState);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  /** What the server left under this key, or `null` — including whenever this is the server. */
  take<T>(key: string): T | null {
    if (!this.isBrowser) {
      return null;
    }

    const stateKey = makeStateKey<T | null>(key);
    const value = this.transferState.get(stateKey, null);
    if (value !== null) {
      this.transferState.remove(stateKey);
    }

    return value;
  }

  /** Leaves a value for the browser. Does nothing when it is already the browser. */
  give<T>(key: string, value: T): void {
    if (!this.isBrowser) {
      this.transferState.set(makeStateKey<T | null>(key), value);
    }
  }
}
