import { InjectionToken } from '@angular/core';
import { BRAND } from '@spencer-denim/shared';

export interface SiteConfig {
  /** WhatsApp number in international form without `+` or spaces, as `wa.me` wants it. */
  readonly whatsAppNumber: string;
  /** Pre-filled first message. */
  readonly whatsAppMessage: string;
}

/**
 * The last build-time constants the storefront shell has (GLB-07, GLB-08).
 *
 * **The contact details are gone from here, and that is W2-11's correction to itself.** This file used
 * to hold an email, a telephone number, a three-line address and two lines of opening hours, and the
 * footer fell back to them whenever the matching setting was unset — which is the state of a fresh shop.
 * So a freshly installed Colombo Wardrobe published `hello@` at the shop's own domain, `+94 77 000 0000`
 * and "Monday to Friday, 9am – 6pm" on **every page**, where a shopper would email an address nobody
 * reads and ring a number that is not the shop's, and the shop would never learn they tried. The W2-11
 * tester found it; the Contact page already did the right thing, and the footer's docblock had argued
 * against itself in writing.
 *
 * It is also exactly the call ADM-22 made when it deleted three placeholder social links: there is no
 * placeholder value for this shop's telephone number that is not a lie. The five `Contact.*` settings
 * (doc 08 §3.8) are now the only source, and an unset one renders nothing anywhere.
 *
 * **`social` is gone, and that is the point of ADM-22's first slice.** It held three links to
 * `facebook.com`, `instagram.com` and `tiktok.com` — the networks' front pages, not this shop's
 * profiles. The footer now reads `Social.*` from `ops.Settings` and shows an icon only for an address the
 * client has supplied (`SiteSettingsService`). A constant could not do that: there is no placeholder
 * value for "this shop's Instagram" that is not a lie.
 *
 * **`whatsAppNumber` is still here, deliberately, and is now a *fallback*.** `Contact.WhatsApp` overrides
 * it when the client fills it in. It was not removed with `social` because the two are not the same
 * situation: an unset social address hides one icon in the footer, while GLB-08's floating button is on
 * every page of the shop and removing it would take a scoped feature off the site over a setting nobody
 * has filled in. The number below is a documented placeholder that predates this task, and it is flagged
 * in the journal as the thing to replace with the client's real one — not quietly dropped as part of a
 * task about logos.
 *
 * What the client types into `Contact.Address` is the **shop's** address, not the registered company's
 * (W1-15, docs/02 §I2): it is the footer a shopper reads, beside the shop's own email and telephone
 * number. The registered name, the registered address and the VAT number are separate values, and the
 * first thing that will need them is invoices (W3, issue #53) — captured per invoice, from settings the
 * client fills in, and never from a constant here.
 */
export const DEFAULT_SITE_CONFIG: SiteConfig = {
  whatsAppNumber: '94770000000',
  whatsAppMessage: `Hello ${BRAND.name}, I have a question about`,
};

export const SITE_CONFIG = new InjectionToken<SiteConfig>('SITE_CONFIG', {
  providedIn: 'root',
  factory: () => DEFAULT_SITE_CONFIG,
});

/**
 * The `wa.me` deep link (GLB-08).
 *
 * `number` is the admin setting `Contact.WhatsApp` when the client has filled it in, and
 * `config.whatsAppNumber`'s documented placeholder otherwise — so the floating button exists on every
 * page either way, and the setting replaces the placeholder rather than deciding whether there is a
 * button at all. Non-digits are still stripped from whichever value arrives: the API refuses anything
 * else on the way in, but this function is also fed a constant, and `wa.me` silently fails on a `+`.
 */
export function whatsAppUrl(config: SiteConfig, number?: string | null): string {
  const digits = (number ?? config.whatsAppNumber).replace(/\D/g, '');
  const text = config.whatsAppMessage.trim();

  return text.length > 0
    ? `https://wa.me/${digits}?text=${encodeURIComponent(text)}`
    : `https://wa.me/${digits}`;
}
