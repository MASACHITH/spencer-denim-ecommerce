import { InjectionToken } from '@angular/core';
import { IconName } from '@spencer-denim/shared';

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
 */
export const DEFAULT_SITE_CONFIG: SiteConfig = {
  whatsAppNumber: '94770000000',
  whatsAppMessage: 'Hello Spencer Denim, I have a question about',
  contact: {
    email: 'hello@spencerdenim.lk',
    phone: '+94 77 000 0000',
    phoneHref: '+94770000000',
    addressLines: ['Spencer Denim Industries (Pvt) Ltd', 'Colombo', 'Sri Lanka'],
    openingHours: ['Monday to Friday, 9am – 6pm', 'Saturday, 9am – 1pm'],
  },
  social: [
    { label: 'Spencer Denim on Facebook', url: 'https://www.facebook.com/', icon: 'facebook' },
    { label: 'Spencer Denim on Instagram', url: 'https://www.instagram.com/', icon: 'instagram' },
    { label: 'Spencer Denim on TikTok', url: 'https://www.tiktok.com/', icon: 'tiktok' },
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
