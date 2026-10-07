import { InjectionToken } from '@angular/core';
import { BRAND } from '@spencer-denim/shared';

export interface SiteConfig {
  /** Pre-filled first message. Not a contact detail: it is the shop's own words. */
  readonly whatsAppMessage: string;
}

/**
 * The last build-time constants the storefront shell has (GLB-07, GLB-08).
 *
 * **The contact details are gone from here, and that is W2-11's correction to itself.** This file used
 * to hold an email, a telephone number, a three-line address and two lines of opening hours, and the
 * footer fell back to them whenever the matching setting was unset — which is the state of a fresh shop.
 * So a freshly installed shop published `hello@` at its own real domain, `+94 77 000 0000`
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
 * **`whatsAppNumber` has gone too, and the argument that kept it was wrong.** It read: an unset social
 * address hides one icon, while GLB-08's floating button is on every page, so removing it over a setting
 * nobody has filled in would take a scoped feature off the shop. But `+94 77 000 0000` is not this shop's
 * number, and `whatsAppUrl` never returned null — so every page of a fresh shop carried a float, and the
 * Contact page carried four links, that open a chat with **nobody**. A float that opens a chat with nobody
 * is not the feature; it is the feature's shape with a dead number in it, and a shopper who messages it
 * concludes the shop does not answer. The W2-11 tester found it as the last invented value left in the
 * bundle after the address, email, telephone and opening hours came out.
 *
 * So the button appears when `Contact.WhatsApp` is set and not before, exactly as the other five now do.
 * `whatsAppMessage` stays, because it is the shop's own opening line rather than a contact detail, and it
 * is only ever used alongside a number the client supplied.
 *
 * What the client types into `Contact.Address` is the **shop's** address, not the registered company's
 * (W1-15, docs/02 §I2): it is the footer a shopper reads, beside the shop's own email and telephone
 * number. The registered name, the registered address and the VAT number are separate values, and the
 * first thing that will need them is invoices (W3, issue #53) — captured per invoice, from settings the
 * client fills in, and never from a constant here.
 */
export const DEFAULT_SITE_CONFIG: SiteConfig = {
  whatsAppMessage: `Hello ${BRAND.name}, I have a question about`,
};

export const SITE_CONFIG = new InjectionToken<SiteConfig>('SITE_CONFIG', {
  providedIn: 'root',
  factory: () => DEFAULT_SITE_CONFIG,
});

/**
 * The `wa.me` deep link for the admin setting `Contact.WhatsApp`, or <b>null when the client has not set
 * one</b> (GLB-08, ADM-22 part 2).
 *
 * **Null is the whole point of the signature.** This used to fall back to a placeholder number and so
 * could never return null, which meant every caller rendered a link whether or not there was anybody at
 * the other end of it. Returning null makes "there is no WhatsApp yet" a state the callers have to
 * handle, and each of the three now hides its link rather than pointing it at a dead number.
 *
 * Non-digits are still stripped: the API refuses anything else on the way in, but `wa.me` fails silently
 * on a `+`, and a number that arrives from a data fix should not take the link down — it should work.
 * A value that holds no digits at all is treated as unset, because a link to `wa.me/` is not a link.
 */
export function whatsAppUrl(config: SiteConfig, number?: string | null): string | null {
  const digits = (number ?? '').replace(/\D/g, '');

  if (digits.length === 0) {
    return null;
  }

  const text = config.whatsAppMessage.trim();

  return text.length > 0
    ? `https://wa.me/${digits}?text=${encodeURIComponent(text)}`
    : `https://wa.me/${digits}`;
}
