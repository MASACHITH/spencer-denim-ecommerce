import { IconName } from '@spencer-denim/shared';

/** `GET /api/site/settings` — the shop's logo, as the API returns it (GLB-02). */
export interface SiteLogoDto {
  readonly url: string;
  readonly width: number | null;
  readonly height: number | null;
  readonly altText: string | null;
}

/** One social profile link, as the API returns it (GLB-07). */
export interface SiteSocialLinkDto {
  /** `facebook`, `instagram` or `tiktok`. */
  readonly network: string;
  readonly url: string;
}

/**
 * The shop's contact details, as the API returns them (GLB-07, GLB-10).
 *
 * Every field can legitimately be null or empty: doc 11 P13 step 13 puts these in admin Settings, so
 * until the client fills them in there is nothing here, and that is the normal state rather than an
 * error state.
 */
export interface SiteContactDto {
  readonly phone: string | null;
  readonly email: string | null;
  readonly addressLines: readonly string[];
  readonly openingHours: readonly string[];
  /** A Google Maps or OpenStreetMap embed address, already host-checked by the API. */
  readonly mapEmbedUrl: string | null;
}

/** `GET /api/site/settings`. Every field can legitimately be null or empty. */
export interface SiteSettingsDto {
  readonly logo: SiteLogoDto | null;
  readonly whatsAppNumber: string | null;
  readonly social: readonly SiteSocialLinkDto[];
  readonly contact: SiteContactDto | null;
}

/** The shop's logo once the storefront has decided it is usable. */
export interface SiteLogo {
  readonly url: string;
  /** Null when the API did not report one; the template then omits the attribute. */
  readonly width: number | null;
  readonly height: number | null;
  /** Never blank: the component supplies the shop's name when the asset has no alt text. */
  readonly alt: string;
}

/** One social link the footer will draw. */
export interface SiteSocialLink {
  readonly network: string;
  readonly url: string;
  readonly icon: IconName;
  /** The accessible name: the shop's name from `BRAND`, then " on ", then the network. */
  readonly label: string;
}

/**
 * The shell's settings as the storefront uses them.
 *
 * Deliberately not the DTO: the mapping drops anything unusable, so a component binding to this
 * cannot render a broken image or a link to nowhere. "No logo" and "could not find out" are the
 * same value here, which is what makes the fallback safe — see `SiteSettingsService`.
 */
export interface SiteSettings {
  readonly logo: SiteLogo | null;
  /** Digits only, or null — in which case the caller uses the placeholder in `SITE_CONFIG`. */
  readonly whatsAppNumber: string | null;
  readonly social: readonly SiteSocialLink[];
  readonly contact: SiteContact;
}

/**
 * The contact details once the storefront has decided they are usable (GLB-07, GLB-10).
 *
 * As with the social links, the mapping drops anything that cannot be rendered safely, so a component
 * binding to this cannot draw an empty line or frame an address the API did not vouch for.
 */
export interface SiteContact {
  /** As the client typed it, for display. Never blank. */
  readonly phone: string | null;
  /** The `tel:` target: digits and a leading plus only, built from `phone`. */
  readonly phoneHref: string | null;
  readonly email: string | null;
  readonly addressLines: readonly string[];
  readonly openingHours: readonly string[];
  /**
   * The map's `src`, or null for no map.
   *
   * **Re-checked in the browser against the same host allow-list the API applies**, not merely passed
   * through. This value ends up in an `iframe src` inside a page served from the shop's own origin, and
   * the rule the project learned from W1-12's banner link is that the side which renders a value is the
   * side that has to decide it is safe — checking one string on the server and following another here is
   * the gap that was defeated twice.
   */
  readonly mapEmbedUrl: string | null;
  /** Whether there is anything at all to draw, so a heading with nothing under it is not rendered. */
  readonly hasAny: boolean;
}

/**
 * The only hosts a map may be framed from, mirroring `SettingFieldRules.MapEmbedHosts` on the server.
 *
 * Two copies of one list is a cost worth paying here: the alternative is trusting a payload to decide
 * what gets framed inside the shop's own page, and this list is five strings that change when the
 * client's mapping provider does.
 */
export const MAP_EMBED_HOSTS: readonly string[] = [
  'www.google.com',
  'maps.google.com',
  'www.google.lk',
  'www.openstreetmap.org',
  'openstreetmap.org',
];

/**
 * The networks the footer can draw, in display order, with the icon and the wording each one gets.
 *
 * **An allow-list, and the second half of the "no fake links" rule.** The API only sends these three
 * today, but a `network` the front end does not recognise must not become an icon chosen at random or
 * an `sd-icon` with an unknown name (which renders nothing and leaves a 44 px hole with a link in it).
 * Anything not in this map is dropped.
 */
export const SOCIAL_NETWORKS: Readonly<Record<string, { readonly icon: IconName; readonly name: string }>> = {
  facebook: { icon: 'facebook', name: 'Facebook' },
  instagram: { icon: 'instagram', name: 'Instagram' },
  tiktok: { icon: 'tiktok', name: 'TikTok' },
};
