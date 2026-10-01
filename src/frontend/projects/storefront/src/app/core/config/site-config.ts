import { InjectionToken } from '@angular/core';
import { BRAND, IconName } from '@spencer-denim/shared';

export interface SocialLink {
  readonly label: string;
  readonly url: string;
  readonly icon: IconName;
}

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
  readonly social: readonly SocialLink[];
}

/**
 * Placeholder contact details (GLB-07, GLB-08).
 *
 * TODO(ADM-19): these become admin settings so the client can change them without a deploy.
 * Every value here is a placeholder awaiting the client's real details (docs/04 §4 decision 8).
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
  social: [
    { label: `${BRAND.name} on Facebook`, url: 'https://www.facebook.com/', icon: 'facebook' },
    { label: `${BRAND.name} on Instagram`, url: 'https://www.instagram.com/', icon: 'instagram' },
    { label: `${BRAND.name} on TikTok`, url: 'https://www.tiktok.com/', icon: 'tiktok' },
  ],
};

export const SITE_CONFIG = new InjectionToken<SiteConfig>('SITE_CONFIG', {
  providedIn: 'root',
  factory: () => DEFAULT_SITE_CONFIG,
});

/** The `wa.me` deep link for the configured number. */
export function whatsAppUrl(config: SiteConfig): string {
  const digits = config.whatsAppNumber.replace(/\D/g, '');
  const text = config.whatsAppMessage.trim();

  return text.length > 0
    ? `https://wa.me/${digits}?text=${encodeURIComponent(text)}`
    : `https://wa.me/${digits}`;
}
