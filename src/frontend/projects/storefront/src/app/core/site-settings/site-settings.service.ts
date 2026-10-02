import { HttpClient } from '@angular/common/http';
import { Injectable, Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, catchError, map, of, tap, timeout } from 'rxjs';
import { BRAND } from '@spencer-denim/shared';

import {
  SOCIAL_NETWORKS,
  SiteLogo,
  SiteSettings,
  SiteSettingsDto,
  SiteSocialLink,
} from './site-settings.model';
import { ServerHandover } from '../platform/server-handover';

/**
 * What the shell shows when the settings cannot be read.
 *
 * **No logo and no social icons**, which is also the honest state of a shop whose client has not
 * uploaded a logo or sent their profile addresses — and therefore the state most visitors will see.
 * That equivalence is the point: there is no second rendering for "we could not find out", so the
 * failure path has already been looked at by everyone who has looked at the normal one.
 */
export const EMPTY_SITE_SETTINGS: SiteSettings = {
  logo: null,
  whatsAppNumber: null,
  social: [],
};

/**
 * How long the shell waits before it falls back.
 *
 * Two seconds, matching `ANNOUNCEMENTS_TIMEOUT_MS`, and for the reason that one records: this is
 * fetched on every page, so a slow settings feed would otherwise be a slow site — and an answer that
 * never comes is not a failure, so `catchError` alone cannot help (the W2-03 lesson).
 */
export const SITE_SETTINGS_TIMEOUT_MS = 2_000;

/** Where the server leaves the settings for the browser. */
const SITE_SETTINGS_HANDOVER = 'site:settings';

/**
 * The shop's logo, WhatsApp number and social links (GLB-02, GLB-07, GLB-08), from
 * `GET /api/site/settings`.
 *
 * The fetch runs during server-side rendering and the result is handed to the browser with the page, so
 * the served HTML already carries the real logo and hydration does not swap it for the wordmark — the
 * same arrangement `AnnouncementService` uses, and for the same reason.
 *
 * **Nothing here can take a page down (W2-10's lesson).** A timeout, a 500, a payload of the wrong
 * shape and a `null` body all end at `EMPTY_SITE_SETTINGS`. The API answers 200 with an empty payload
 * rather than 500 as well, so there are two independent floors under the same outcome.
 */
@Injectable({ providedIn: 'root' })
export class SiteSettingsService {
  private readonly http = inject(HttpClient);
  private readonly handover = inject(ServerHandover);

  private readonly source: Signal<SiteSettings> = toSignal(this.load(), {
    initialValue: EMPTY_SITE_SETTINGS,
  });

  readonly settings: Signal<SiteSettings> = this.source;

  /** The header's logo, or null to use the text wordmark. */
  readonly logo: Signal<SiteLogo | null> = computed(() => this.source().logo);

  /** The footer's social links — only the ones the client has actually supplied. */
  readonly social: Signal<readonly SiteSocialLink[]> = computed(() => this.source().social);

  /** The configured WhatsApp number, or null when the client has not set one. */
  readonly whatsAppNumber: Signal<string | null> = computed(() => this.source().whatsAppNumber);

  private load(): Observable<SiteSettings> {
    const handedOver = this.handover.take<SiteSettings>(SITE_SETTINGS_HANDOVER);
    if (handedOver) {
      return of(handedOver);
    }

    // **Order matters, and the first version had it wrong.** `tap(give)` sat *upstream* of `timeout` and
    // `catchError`, so a read that was slow or failed handed over nothing at all — and the browser then
    // re-fetched on hydration, which is the logo pop-in `site-header.ts` claims to prevent. This is W2-07's
    // Major 3 one feature over: hand over only what has been vetted, and hand over *whatever the render
    // actually used*, fallback included, so the browser never has to ask again.
    return this.http.get<SiteSettingsDto>('/api/site/settings').pipe(
      map(toSiteSettings),
      timeout(SITE_SETTINGS_TIMEOUT_MS),
      catchError(() => of(EMPTY_SITE_SETTINGS)),
      tap((settings) => this.handover.give(SITE_SETTINGS_HANDOVER, settings)),
    );
  }
}

/**
 * Maps the API's payload, dropping anything that cannot be rendered safely.
 *
 * **Defensive about the shape, not just about the status.** A `200 {"oops":true}` never reaches
 * `catchError` — that is the lesson `toMessages` records in `announcement.service.ts` — so every field
 * is checked for the type it is supposed to be rather than trusted because the request succeeded. The
 * exported-and-tested shape is the point: this is where a malformed payload stops.
 */
export function toSiteSettings(payload: SiteSettingsDto | null | undefined): SiteSettings {
  if (typeof payload !== 'object' || payload === null) {
    return EMPTY_SITE_SETTINGS;
  }

  return {
    logo: toLogo(payload.logo),
    whatsAppNumber: toWhatsAppNumber(payload.whatsAppNumber),
    social: toSocialLinks(payload.social),
  };
}

/**
 * The logo, or null.
 *
 * **A blank or non-string URL is "no logo".** An `<img src="">` re-requests the current document in
 * every browser and then fails to decode it, so the header would show a broken-image glyph where the
 * wordmark should be — which is precisely the outcome the fallback exists to avoid, arrived at from the
 * other direction.
 *
 * `width`/`height` are kept only when they are finite positive numbers, so the attributes either
 * reserve the right box or are omitted entirely. A `width="0"` would collapse the logo to nothing.
 */
function toLogo(logo: SiteSettingsDto['logo']): SiteLogo | null {
  if (typeof logo !== 'object' || logo === null) {
    return null;
  }

  const url = typeof logo.url === 'string' ? logo.url.trim() : '';
  if (url.length === 0) {
    return null;
  }

  const alt = typeof logo.altText === 'string' ? logo.altText.trim() : '';

  return {
    url,
    width: dimension(logo.width),
    height: dimension(logo.height),

    // The shop's name is the fallback alt text, read from the one place it is written down. A logo
    // with no alt text would otherwise be an unlabelled image doing the job of the site's own name
    // (GLB-12, WCAG 2.2 AA 1.1.1) — and the name is a compile-time constant on this side too (W1-15),
    // so the fallback needs no round trip.
    alt: alt.length > 0 ? alt : BRAND.name,
  };
}

function dimension(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.round(value) : null;
}

/** Digits only, 7–15 of them, no leading zero — the same rule the API applies on the way in. */
function toWhatsAppNumber(value: string | null | undefined): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const trimmed = value.trim();

  return /^[1-9][0-9]{6,14}$/.test(trimmed) ? trimmed : null;
}

/**
 * The social links worth drawing.
 *
 * Three independent reasons a row is dropped, and each one is a link a shopper would otherwise click:
 * a network this build has no icon for, a URL that is not an absolute `http(s)` address, and a
 * duplicate (two rows for the same network would render two identical icons).
 *
 * The URL test is not the server's — it cannot be, because `URL` is what a browser actually does with
 * the value. Asking `new URL()` and then checking the protocol is the question the `href` will ask, so
 * `javascript:`, `data:`, a relative path and a value with a backslash or a newline in it all fail here
 * regardless of what the server thought.
 */
function toSocialLinks(rows: SiteSettingsDto['social']): readonly SiteSocialLink[] {
  if (!Array.isArray(rows)) {
    return [];
  }

  const seen = new Set<string>();
  const links: SiteSocialLink[] = [];

  for (const row of rows) {
    const network = typeof row?.network === 'string' ? row.network.trim().toLowerCase() : '';
    const known = SOCIAL_NETWORKS[network];

    const url = normaliseExternalHttpUrl(row?.url);

    if (!known || seen.has(network) || url === null) {
      continue;
    }

    seen.add(network);
    links.push({
      network,
      url,
      icon: known.icon,
      label: `${BRAND.name} on ${known.name}`,
    });
  }

  return links;
}

/**
 * Exported for the spec: whether an `href` built from this would go somewhere external and safe.
 *
 * A thin wrapper over {@link normaliseExternalHttpUrl}, so the predicate and the value that actually gets
 * rendered are one decision rather than two.
 */
export function isExternalHttpUrl(value: unknown): value is string {
  return normaliseExternalHttpUrl(value) !== null;
}

/**
 * The address to put in the `href` — normalised by the browser's own parser — or null if it should not be a
 * link at all.
 */
export function normaliseExternalHttpUrl(value: unknown): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const candidate = value.trim();

  // A browser strips TAB, CR and LF out of a URL before parsing it, and reads a backslash as a slash for a
  // special scheme — so a value containing either is not the value `new URL` is about to see. This is
  // W1-12's banner-link lesson, applied on the rendering side.
  //
  // A character walk rather than a regular expression, and not to appease `no-control-regex`: the rule is
  // right that a control character inside a regex literal is nearly always a typo, and the same intent read
  // aloud here is a condition anybody can check. It also matches the backend's own loop in
  // `SettingFieldRules.TryNormaliseProfileUrl` character for character, which is the point — two
  // implementations of one rule should be legible as the same rule.
  if (candidate.length === 0) {
    return null;
  }

  for (const character of candidate) {
    const code = character.codePointAt(0)!;
    const isControl = code <= 0x1f || (code >= 0x7f && code <= 0x9f);

    if (character === '\\' || isControl || /\s/.test(character)) {
      return null;
    }
  }

  let parsed: URL;
  try {
    parsed = new URL(candidate);
  } catch {
    return null;
  }

  // `hostname` is checked although the WHATWG parser rarely leaves it empty: `http://` throws and
  // `http:///profile` normalises to host `profile`. .NET refuses that second one — measured in round 1, and
  // not for the reason an earlier comment here gave: `Uri.TryCreate` returns false for it outright rather
  // than producing an empty host. The check costs nothing and it is the condition that matters: an href with
  // no host resolves against the current page, so a social icon would link back into the shop.
  const safe =
    (parsed.protocol === 'https:' || parsed.protocol === 'http:') &&
    parsed.hostname.length > 0 &&
    parsed.username.length === 0 &&
    parsed.password.length === 0;

  // **The parsed form, not the text.** The API normalises on write and on read, so a homograph host cannot
  // reach this payload from our own server — but this function is what chooses the `href`, and checking one
  // string while rendering another is the gap W1-12's validator was defeated through twice. `URL.href` is what
  // the browser would resolve anyway, so using it costs nothing and removes the last place where the value
  // checked and the value followed could differ.
  return safe ? parsed.href : null;
}
