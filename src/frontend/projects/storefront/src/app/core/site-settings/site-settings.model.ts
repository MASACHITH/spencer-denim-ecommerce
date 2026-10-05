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

/** `GET /api/site/settings`. Every field can legitimately be null or empty. */
export interface SiteSettingsDto {
  readonly logo: SiteLogoDto | null;
  readonly whatsAppNumber: string | null;
  readonly social: readonly SiteSocialLinkDto[];
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
}

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
