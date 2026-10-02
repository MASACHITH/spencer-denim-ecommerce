import { InjectionToken } from '@angular/core';
import { BRAND } from '@spencer-denim/shared';

export interface SiteContact {
  readonly email: string;
  /** For display. */
  readonly phone: string;
  /** For `tel:`, digits and a leading `+` only. */
  readonly phoneHref: string;
  readonly addressLines: readonly string[];
  readonly openingHours: readonly string[];
}

export interface SiteConfig {
  /** WhatsApp number in international form without `+` or spaces, as `wa.me` wants it. */
  readonly whatsAppNumber: string;
  /** Pre-filled first message. */
  readonly whatsAppMessage: string;
  readonly contact: SiteContact;
}

/**
 * Placeholder contact details (GLB-07, GLB-08).
 *
 * TODO(ADM-22 part 2): the email, phone, address and opening hours become admin settings too. Doc 08
 * §3.8 already names their keys (`Contact.Phone`, `Contact.Email`, `Contact.Address`,
 * `Contact.OpeningHours`, `Contact.MapEmbedUrl`); this slice built only the two the client was actually
 * blocked on — the WhatsApp number and the social addresses — and the rest are rows away.
 * Every value still here is a placeholder awaiting the client's real details (docs/04 §4 decision 8).
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
 * The address block names the **shop**, not the registered company (W1-15, docs/02 §I2): this is the
 * footer a shopper reads, and it is shown beside the shop's own email and phone number. The registered
 * name, the registered address and the VAT number are a separate value the client has not supplied, and
 * the first thing that will need them is invoices (W3, issue #53) — where they belong in admin settings
 * the client fills in, captured per invoice, rather than in a constant here.
 */
export const DEFAULT_SITE_CONFIG: SiteConfig = {
  whatsAppNumber: '94770000000',
  whatsAppMessage: `Hello ${BRAND.name}, I have a question about`,
  contact: {
    email: `hello@${BRAND.domain}`,
    phone: '+94 77 000 0000',
    phoneHref: '+94770000000',
    addressLines: [BRAND.name, 'Colombo', 'Sri Lanka'],
    openingHours: ['Monday to Friday, 9am – 6pm', 'Saturday, 9am – 1pm'],
  },
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
