import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminSiteContentApi } from '../../core/content/admin-site-content-api';
import { MediaPicker, PickedMedia } from './media-picker';

describe('MediaPicker (#192)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MediaPicker],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function create(kind: 'image' | 'video', media: PickedMedia | null = null): {
    fixture: ComponentFixture<MediaPicker>;
    emitted: (PickedMedia | null)[];
  } {
    const fixture = TestBed.createComponent(MediaPicker);
    fixture.componentRef.setInput('kind', kind);
    fixture.componentRef.setInput('label', 'Image');
    fixture.componentRef.setInput('testId', 'pick');
    fixture.componentRef.setInput('media', media);
    const emitted: (PickedMedia | null)[] = [];
    fixture.componentInstance.mediaChange.subscribe((value) => emitted.push(value));
    fixture.detectChanges();
    return { fixture, emitted };
  }

  function q<T extends HTMLElement>(fixture: ComponentFixture<MediaPicker>, testId: string): T {
    return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${testId}"]`)!;
  }

  function choose(input: HTMLInputElement, file: File): void {
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
  }

  it('shows the current image and its description, and Remove hands back null', () => {
    const { fixture, emitted } = create('image', {
      publicId: 'p1',
      url: '/x.webp',
      altText: 'A linen dress',
      posterUrl: null,
    });

    expect(q(fixture, 'pick-preview').textContent).toContain('A linen dress');
    expect(q<HTMLImageElement>(fixture, 'pick-preview').querySelector('img')?.getAttribute('alt')).toBe('A linen dress');

    q<HTMLButtonElement>(fixture, 'pick-remove').click();
    expect(emitted).toEqual([null]);
  });

  it('refuses to upload without a description', () => {
    const { fixture } = create('image');

    choose(q<HTMLInputElement>(fixture, 'pick-file'), new File(['x'], 'a.png', { type: 'image/png' }));
    q<HTMLButtonElement>(fixture, 'pick-upload').click();
    fixture.detectChanges();

    expect(q(fixture, 'pick-upload-error').textContent).toContain('Describe it first');
  });

  it('uploads an image with its alt text and hands back the public id', () => {
    const { fixture, emitted } = create('image');

    choose(q<HTMLInputElement>(fixture, 'pick-file'), new File(['x'], 'a.png', { type: 'image/png' }));
    const alt = q<HTMLInputElement>(fixture, 'pick-alt');
    alt.value = 'Women in linen';
    alt.dispatchEvent(new Event('input'));
    q<HTMLButtonElement>(fixture, 'pick-upload').click();

    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
    const request = controller.expectOne(AdminSiteContentApi.imagesUrl);
    expect((request.request.body as FormData).get('altText')).toBe('Women in linen');
    request.flush({ publicId: 'new-id', url: '/u.webp', altText: 'Women in linen' });

    expect(emitted).toEqual([{ publicId: 'new-id', url: '/u.webp', altText: 'Women in linen', posterUrl: null }]);
  });

  it('needs a poster for a video, then uploads both', () => {
    const { fixture, emitted } = create('video');

    choose(q<HTMLInputElement>(fixture, 'pick-file'), new File(['v'], 'clip.mp4', { type: 'video/mp4' }));
    const alt = q<HTMLInputElement>(fixture, 'pick-alt');
    alt.value = 'A summer film';
    alt.dispatchEvent(new Event('input'));
    q<HTMLButtonElement>(fixture, 'pick-upload').click();
    fixture.detectChanges();
    expect(q(fixture, 'pick-upload-error').textContent).toContain('poster');

    choose(q<HTMLInputElement>(fixture, 'pick-poster'), new File(['p'], 'p.webp', { type: 'image/webp' }));
    q<HTMLButtonElement>(fixture, 'pick-upload').click();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
    const request = controller.expectOne(AdminSiteContentApi.videosUrl);
    expect((request.request.body as FormData).get('poster')).toBeInstanceOf(File);
    request.flush({ publicId: 'vid', url: '/c.mp4', posterUrl: '/p.webp', altText: 'A summer film' });

    expect(emitted[0]?.publicId).toBe('vid');
    expect(emitted[0]?.posterUrl).toBe('/p.webp');
  });

  it('says when the role cannot upload', () => {
    const { fixture } = create('image');

    choose(q<HTMLInputElement>(fixture, 'pick-file'), new File(['x'], 'a.png', { type: 'image/png' }));
    const alt = q<HTMLInputElement>(fixture, 'pick-alt');
    alt.value = 'x';
    alt.dispatchEvent(new Event('input'));
    q<HTMLButtonElement>(fixture, 'pick-upload').click();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
    controller.expectOne(AdminSiteContentApi.imagesUrl).flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'pick-upload-error').textContent).toContain('Media permission');
  });
});
