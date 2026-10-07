import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { WhatsappButton } from './whatsapp-button';
import { SiteSettingsDto } from '../../core/site-settings/site-settings.model';
import { whatsAppUrl, DEFAULT_SITE_CONFIG } from '../../core/config/site-config';

/**
 * The floating WhatsApp button (GLB-08, ADM-22 part 2, W2-11).
 *
 * **What this file exists to pin is an absence.** The button used to fall back to a placeholder number
 * held in `SITE_CONFIG`, so a shop whose client had not set `Contact.WhatsApp` still put a float on
 * every page — one that opens a chat with nobody. The argument for it was that GLB-08 says the button is
 * site-wide; but a button that reaches nobody is not that feature, and a shopper who messages it
 * concludes the shop does not answer. It was the last invented value left in the bundle after the
 * address, email, telephone number and opening hours came out, and the W2-11 tester found it there.
 *
 * So the assertions below are mostly "there is no link, and no landmark either" — the second half
 * because a named `complementary` region with nothing inside it is a worse defect than the one the
 * landmark was added to fix (#65): a screen-reader user navigating by region would arrive at an empty
 * "Chat with us".
 */
describe('WhatsappButton (GLB-08)', () => {
  let fixture: ComponentFixture<WhatsappButton>;
  let host: HTMLElement;
  let http: HttpTestingController;

  async function render(settings: SiteSettingsDto) {
    TestBed.configureTestingModule({
      imports: [WhatsappButton],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    fixture = TestBed.createComponent(WhatsappButton);
    host = fixture.nativeElement as HTMLElement;
    http = TestBed.inject(HttpTestingController);

    fixture.detectChanges();

    // The settings arrive from an HTTP call, which is why the component reads them through a computed
    // rather than in its constructor.
    http.expectOne('/api/site/settings').flush(settings);

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => {
    http?.verify();
    TestBed.resetTestingModule();
  });

  it('renders no link at all when the client has not set a number', async () => {
    await render({ logo: null, whatsAppNumber: null, social: [] });

    expect(host.querySelector('a')).toBeNull();
    expect(host.querySelector('.wa-float')).toBeNull();

    // Nothing that reads as a dead number, in any spelling.
    expect(host.innerHTML).not.toContain('wa.me');
  });

  it('claims no landmark while there is no button in it', async () => {
    await render({ logo: null, whatsAppNumber: null, social: [] });

    expect(host.getAttribute('role')).toBeNull();
    expect(host.getAttribute('aria-label')).toBeNull();
  });

  it('renders the link, the landmark and the text name once a number is set', async () => {
    await render({ logo: null, whatsAppNumber: '94771234567', social: [] });

    const link = host.querySelector<HTMLAnchorElement>('a.wa-float');

    expect(link).not.toBeNull();
    expect(link!.getAttribute('href')).toMatch(/^https:\/\/wa\.me\/94771234567\?text=/);
    expect(link!.getAttribute('rel')).toContain('noopener');
    expect(link!.querySelector('.sr-only')?.textContent).toContain('WhatsApp');

    expect(host.getAttribute('role')).toBe('complementary');
    expect(host.getAttribute('aria-label')).toBe('Chat with us');
  });

  /**
   * The specific placeholder that used to be rendered. Named rather than checked as "no link", because
   * what must never come back is this number: an assertion about emptiness would pass again the moment
   * somebody introduced a different invented value.
   */
  it('never renders the old placeholder number', async () => {
    await render({ logo: null, whatsAppNumber: null, social: [] });

    expect(host.innerHTML).not.toContain('94770000000');
  });
});

describe('whatsAppUrl (GLB-08, ADM-22 part 2)', () => {
  it('is null when there is no number, so every caller has to handle the absence', () => {
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, null)).toBeNull();
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, undefined)).toBeNull();
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG)).toBeNull();
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, '')).toBeNull();
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, '   ')).toBeNull();
  });

  /**
   * A value that holds no digits is treated as unset, because `https://wa.me/` is not a link. The API
   * refuses such a value on the way in; this is the layer that holds for a row written around it.
   */
  it('treats a value with no digits in it as unset', () => {
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, '+')).toBeNull();
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, '--')).toBeNull();
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, 'call us')).toBeNull();
  });

  it('strips the punctuation wa.me fails silently on, rather than refusing the number', () => {
    expect(whatsAppUrl(DEFAULT_SITE_CONFIG, '+94 77 123 4567')).toMatch(
      /^https:\/\/wa\.me\/94771234567\?text=/,
    );
  });

  it('carries the shop opening line, which is the shop own words rather than a contact detail', () => {
    const url = whatsAppUrl(DEFAULT_SITE_CONFIG, '94771234567') ?? '';

    expect(url).toContain('?text=');
    expect(decodeURIComponent(url.split('?text=')[1])).toBe(DEFAULT_SITE_CONFIG.whatsAppMessage);
  });
});
