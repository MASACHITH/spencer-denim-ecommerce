import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CampaignBannerPicker } from './campaign-banner-picker';
import { AdminProductApi, IMAGE_ACCEPT, MAX_IMAGE_BYTES } from '../../core/products/admin-product-api';
import { AdminSaleCampaignBanner } from '../../core/marketing/admin-campaign.model';

/**
 * The campaign banner picker (#191). The happy path and the removal are proved through the whole form in
 * `sale-campaigns-page.spec.ts`; these are the refusals, which have to happen before anything is sent.
 */
describe('CampaignBannerPicker (#191)', () => {
  let fixture: ComponentFixture<CampaignBannerPicker>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let changes: (AdminSaleCampaignBanner | null)[];

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function attach(file: File): void {
    const input = element.querySelector<HTMLInputElement>('[data-testid="banner-file"]')!;
    Object.defineProperty(input, 'files', { value: [file] });
    input.dispatchEvent(new Event('change'));
  }

  function describeAs(text: string): void {
    const input = element.querySelector<HTMLInputElement>('[data-testid="banner-alt"]')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
  }

  function upload(): void {
    element.querySelector<HTMLElement>('[data-testid="banner-upload"]')!.click();
    http
      .match((request) => request.url.includes('antiforgery'))
      .forEach((request) => request.flush({ token: 'token' }));
  }

  function error(): string {
    return element.querySelector('[data-testid="banner-error"]')?.textContent?.trim() ?? '';
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CampaignBannerPicker);
    element = fixture.nativeElement as HTMLElement;
    changes = [];
    fixture.componentInstance.bannerChange.subscribe((banner) => changes.push(banner));
    await settle();
  });

  afterEach(() => {
    http.verify();
  });

  it('offers the image types the media library takes', () => {
    expect(element.querySelector('[data-testid="banner-file"]')?.getAttribute('accept')).toBe(IMAGE_ACCEPT);
  });

  it('asks for a picture before anything else', async () => {
    describeAs('Linen Week');
    upload();
    await settle();

    expect(error()).toBe('Choose a picture first.');
    http.expectNone(AdminProductApi.mediaImagesUrl);
  });

  it('refuses a picture over the size limit before sending it', async () => {
    const big = new File([new Uint8Array(1)], 'huge.jpg', { type: 'image/jpeg' });
    Object.defineProperty(big, 'size', { value: MAX_IMAGE_BYTES + 1 });

    attach(big);
    describeAs('Linen Week');
    upload();
    await settle();

    expect(error()).toContain('larger than 8 MB');
    http.expectNone(AdminProductApi.mediaImagesUrl);
    expect(changes).toEqual([]);
  });

  it('names the permission when the upload is forbidden, and chooses nothing', async () => {
    attach(new File([new Uint8Array([1])], 'b.jpg', { type: 'image/jpeg' }));
    describeAs('Linen Week');
    upload();
    await settle();

    http.expectOne(AdminProductApi.mediaImagesUrl).flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(error()).toContain('"Media library" permission');
    expect(changes).toEqual([]);
    expect(element.querySelector('[data-testid="banner-upload"]')?.textContent?.trim()).toBe('Upload the picture');
  });

  it('chooses the uploaded picture and empties the description for the next one', async () => {
    attach(new File([new Uint8Array([1])], 'b.jpg', { type: 'image/jpeg' }));
    describeAs('  Linen Week  ');
    upload();
    await settle();

    const request = http.expectOne(AdminProductApi.mediaImagesUrl);

    // Trimmed before it is sent: the description is stored on the asset and read aloud as it is.
    expect((request.request.body as FormData).get('altText')).toBe('Linen Week');

    request.flush({ publicId: 'p-1', mediaType: 'Image', url: '/m.webp', posterUrl: null, altText: 'Linen Week' });
    await settle();

    expect(changes).toEqual([{ publicId: 'p-1', url: '/m.webp', altText: 'Linen Week' }]);
    expect(element.querySelector<HTMLInputElement>('[data-testid="banner-alt"]')?.value).toBe('');
  });

  it('says plainly when the chosen banner has no description', async () => {
    fixture.componentRef.setInput('banner', { publicId: 'p-2', url: '/m.webp', altText: null });
    await settle();

    expect(element.querySelector('[data-testid="banner-no-alt"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="banner-preview"] img')?.getAttribute('alt')).toBe('');
  });
});
