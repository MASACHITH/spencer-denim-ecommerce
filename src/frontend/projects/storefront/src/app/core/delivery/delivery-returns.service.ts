import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, shareReplay, tap, timeout } from 'rxjs';

import {
  DeliveryMethodInfoDto,
  DeliveryRateDto,
  DeliveryReturnsDto,
  DeliveryZoneDto,
  DeliveryZoneName,
  PickupPointDto,
} from './delivery-returns.model';
import { ServerHandover } from '../platform/server-handover';

/**
 * How long a product page waits for the panel. The panel sits below the buy box, so a slow answer must never
 * hold the page back; the same two seconds the announcement strip and the sale banner allow.
 */
export const DELIVERY_RETURNS_TIMEOUT_MS = 2_000;

/** Where the server leaves the panel for the browser. */
const DELIVERY_RETURNS_HANDOVER = 'content:delivery-returns';

const ZONES: ReadonlySet<string> = new Set<DeliveryZoneName>(['Colombo', 'Western', 'IslandWide']);

/**
 * The product page's delivery and returns panel (PDP-05, issue #33), and the live fees on the Shipping page.
 *
 * **Read once per session and handed from the server render to the browser**, so hydration does not fetch it
 * again. The answer is the same for every visitor, so handing it over leaks nothing about anybody.
 *
 * Any failure — a timeout, a 5xx, a payload of the wrong shape — is `null`, and the panel is simply not drawn:
 * the page under it is a product, and it must render whatever happens to delivery fees.
 */
@Injectable({ providedIn: 'root' })
export class DeliveryReturnsService {
  private readonly http = inject(HttpClient);
  private readonly handover = inject(ServerHandover);

  private cached: Observable<DeliveryReturnsDto | null> | null = null;

  /** The panel, or null when it could not be read. Never errors; shared by every caller in the session. */
  load(): Observable<DeliveryReturnsDto | null> {
    this.cached ??= this.read().pipe(shareReplay({ bufferSize: 1, refCount: false }));
    return this.cached;
  }

  private read(): Observable<DeliveryReturnsDto | null> {
    const handedOver = this.handover.take<DeliveryReturnsDto>(DELIVERY_RETURNS_HANDOVER);
    if (handedOver) {
      return of(toDeliveryReturns(handedOver));
    }

    return this.http.get<DeliveryReturnsDto>('/api/content/delivery-returns').pipe(
      timeout(DELIVERY_RETURNS_TIMEOUT_MS),
      map(toDeliveryReturns),
      // Only a usable answer is handed over, so a browser that finds nothing asks again.
      tap((panel) => {
        if (panel) {
          this.handover.give(DELIVERY_RETURNS_HANDOVER, panel);
        }
      }),
      catchError(() => of(null)),
    );
  }
}

/**
 * The panel, vetted. Defensive because a `200 {"oops":true}` is not an answer `catchError` will ever see, and
 * because the handover crosses a process boundary. A rate or point that is not well formed is dropped rather
 * than drawn as "Rs. NaN".
 */
export function toDeliveryReturns(payload: unknown): DeliveryReturnsDto | null {
  if (typeof payload !== 'object' || payload === null) {
    return null;
  }

  const body = payload as Record<string, unknown>;
  if (!Array.isArray(body['zones']) || !Array.isArray(body['methods'])) {
    return null;
  }

  const zones = (body['zones'] as unknown[]).flatMap((row): DeliveryZoneDto[] => {
    const zone = row as Record<string, unknown> | null;
    return zone && isZone(zone['zone']) && isText(zone['label'])
      ? [
          {
            zone: zone['zone'],
            label: zone['label'].trim(),
            districts: Array.isArray(zone['districts'])
              ? (zone['districts'] as unknown[]).filter(isText).map((name) => name.trim())
              : [],
          },
        ]
      : [];
  });

  const methods = (body['methods'] as unknown[]).flatMap((row): DeliveryMethodInfoDto[] => {
    const method = row as Record<string, unknown> | null;
    if (!method || !isText(method['code']) || !isText(method['name']) || !Array.isArray(method['rates'])) {
      return [];
    }

    const rates = (method['rates'] as unknown[]).flatMap(toRate);
    return rates.length > 0
      ? [
          {
            code: method['code'],
            name: method['name'].trim(),
            description: isText(method['description']) ? method['description'].trim() : null,
            rates,
          },
        ]
      : [];
  });

  const pickupPoints = Array.isArray(body['pickupPoints'])
    ? (body['pickupPoints'] as unknown[]).flatMap(toPickupPoint)
    : [];

  const estimate = body['estimate'] as Record<string, unknown> | null | undefined;

  return {
    estimate:
      estimate && isDays(estimate['daysMin']) && isDays(estimate['daysMax'])
        ? { daysMin: estimate['daysMin'], daysMax: estimate['daysMax'] }
        : null,
    zones,
    methods,
    pickupPoints,
    deliveryInfoHtml: typeof body['deliveryInfoHtml'] === 'string' ? body['deliveryInfoHtml'] : '',
    returnsInfoHtml: typeof body['returnsInfoHtml'] === 'string' ? body['returnsInfoHtml'] : '',
  };
}

function toRate(row: unknown): DeliveryRateDto[] {
  const rate = row as Record<string, unknown> | null;
  if (!rate || !isZone(rate['zone']) || !isMoney(rate['fee']) || !isDays(rate['daysMin']) || !isDays(rate['daysMax'])) {
    return [];
  }

  return [
    {
      zone: rate['zone'],
      fee: rate['fee'],
      freeAboveAmount: isMoney(rate['freeAboveAmount']) ? rate['freeAboveAmount'] : null,
      daysMin: rate['daysMin'],
      daysMax: rate['daysMax'],
    },
  ];
}

function toPickupPoint(row: unknown): PickupPointDto[] {
  const point = row as Record<string, unknown> | null;
  if (!point || !isText(point['name']) || !isText(point['city']) || !isMoney(point['fee'])
    || !isDays(point['daysMin']) || !isDays(point['daysMax'])) {
    return [];
  }

  return [
    {
      name: point['name'].trim(),
      city: point['city'].trim(),
      openingHours: isText(point['openingHours']) ? point['openingHours'].trim() : null,
      fee: point['fee'],
      daysMin: point['daysMin'],
      daysMax: point['daysMax'],
    },
  ];
}

function isZone(value: unknown): value is DeliveryZoneName {
  return typeof value === 'string' && ZONES.has(value);
}

function isText(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isMoney(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

function isDays(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 255;
}
