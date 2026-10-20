import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, tap, timeout } from 'rxjs';

import { ServerHandover } from '../platform/server-handover';

/** The live sale campaign's banner, as `GET /api/content/sale-banner` sends it (#191). */
export interface SaleBannerImageDto {
  /** The `large` rendition, or the original when there is none. */
  readonly imageUrl: string;
  /** Every rendition up to `zoom` with its true width, or null when there is only one file. */
  readonly imageSrcSet: string | null;
  readonly width: number | null;
  readonly height: number | null;
  /** Empty, never null, for a picture with no description: it is then decorative. */
  readonly altText: string;
}

/** The payload: the banner, or null on the ordinary day with no live campaign that has one. */
export interface SaleBannerDto {
  readonly banner: SaleBannerImageDto | null;
}

/**
 * How long the Sale page waits for its banner. The banner is decoration above the products, so a slow
 * answer must never hold the products back; the same two seconds the announcement strip allows.
 */
export const SALE_BANNER_TIMEOUT_MS = 2_000;

/** Where the server leaves the banner for the browser. */
const SALE_BANNER_HANDOVER = 'content:sale-banner';

/**
 * The Sale page's campaign banner (#191, DEPT-03; doc 11 P3 step 1's optional banner).
 *
 * **Fetched during the server render and handed to the browser with the page**, so the HTML a shopper or
 * a crawler receives already has the banner in it and hydration does not fetch it a second time. The
 * answer is the same for every visitor — the API reads only the campaign and the media library — so
 * handing one render's answer to the browser that asked for that render leaks nothing about anybody.
 *
 * **What it hands over is the whole payload, `{ banner: null }` included.** A bare `null` is what
 * `ServerHandover.take` answers when the server left nothing, so handing over the banner alone would make
 * "there is no banner today" look like "the server did not ask", and the browser would ask again.
 *
 * Any failure — a timeout, a 5xx, a payload of the wrong shape — is no banner. The page under it is the
 * Sale listing, and that must render whatever happens to a picture above it.
 *
 * Provided in root but **only injected by the listing page in its Sale mode**, so it lives in that lazy
 * chunk and adds nothing to the storefront's initial bundle.
 */
@Injectable({ providedIn: 'root' })
export class SaleBannerService {
  private readonly http = inject(HttpClient);
  private readonly handover = inject(ServerHandover);

  /** The banner of the campaign that is live now, or null. Never errors. */
  load(): Observable<SaleBannerImageDto | null> {
    const handedOver = this.handover.take<SaleBannerDto>(SALE_BANNER_HANDOVER);
    if (handedOver) {
      return of(toBanner(handedOver));
    }

    return this.http.get<SaleBannerDto>('/api/content/sale-banner').pipe(
      timeout(SALE_BANNER_TIMEOUT_MS),
      tap((payload) => this.handover.give(SALE_BANNER_HANDOVER, payload)),
      map(toBanner),
      catchError(() => of(null)),
    );
  }
}

/**
 * The banner, when the payload holds a usable one. Defensive because a `200 {"oops":true}` is not an
 * answer `catchError` will ever see, and an `<img>` with no `src` is a broken picture on the Sale page.
 */
export function toBanner(payload: SaleBannerDto | null | undefined): SaleBannerImageDto | null {
  const banner = payload?.banner;

  if (!banner || typeof banner.imageUrl !== 'string' || banner.imageUrl.trim().length === 0) {
    return null;
  }

  return {
    imageUrl: banner.imageUrl,
    imageSrcSet: typeof banner.imageSrcSet === 'string' && banner.imageSrcSet.length > 0 ? banner.imageSrcSet : null,
    width: typeof banner.width === 'number' && banner.width > 0 ? banner.width : null,
    height: typeof banner.height === 'number' && banner.height > 0 ? banner.height : null,
    altText: typeof banner.altText === 'string' ? banner.altText.trim() : '',
  };
}
