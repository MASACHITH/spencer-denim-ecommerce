import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { ProductDeliveryReturns } from './product-delivery-returns';
import { ContentPageService } from '../../core/content/content-page.service';
import { ContentPageLink } from '../../core/content/content-page.model';
import { DeliveryReturnsDto } from '../../core/delivery/delivery-returns.model';
import { DeliveryReturnsService } from '../../core/delivery/delivery-returns.service';
import { PANEL } from '../../core/delivery/delivery-returns.testing';

/**
 * The product page's delivery and returns panel (PDP-05, issue #33): fees from the rate feed, pickup only when
 * the feed lists a point, the owner's texts sanitised, links only to published pages, and nothing at all when
 * the feed could not be read.
 */
describe('ProductDeliveryReturns (#33)', () => {
  let fixture: ComponentFixture<ProductDeliveryReturns>;
  let element: HTMLElement;

  function render(panel: DeliveryReturnsDto | null, published: readonly ContentPageLink[] = []): void {
    TestBed.configureTestingModule({
      imports: [ProductDeliveryReturns],
      providers: [
        provideRouter([]),
        { provide: DeliveryReturnsService, useValue: { load: (): Observable<DeliveryReturnsDto | null> => of(panel) } },
        {
          provide: ContentPageService,
          useValue: { bySystemKey: (key: string) => published.find((page) => page.systemKey === key) ?? null },
        },
      ],
    });

    fixture = TestBed.createComponent(ProductDeliveryReturns);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  }

  const text = (selector: string) => element.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('draws nothing when the feed could not be read', () => {
    render(null);

    expect(element.querySelector('details')).toBeNull();
  });

  it('shows every method with its fee, free-delivery amount and days per zone, from the feed', () => {
    render(PANEL);

    const summaries = [...element.querySelectorAll('summary')].map((summary) => summary.textContent?.trim());
    expect(summaries).toEqual(['Delivery', 'Returns and exchanges']);

    expect(text('[data-testid="pdp-delivery-estimate"]')).toContain('1–5 working days');

    const standard = text('[data-testid="delivery-method-STANDARD"]');
    expect(standard).toContain('Standard delivery');
    expect(standard).toContain('Colombo');
    expect(standard).toContain('Rs. 400, free over Rs. 10,000, 1–2 working days');
    expect(standard).toContain('Gampaha and Kalutara');
    expect(standard).toContain('Rest of Sri Lanka');
    expect(standard).toContain('Rs. 600, free over Rs. 15,000, 3–5 working days');

    const express = text('[data-testid="delivery-method-EXPRESS"]');
    expect(express).toContain('Express delivery');
    expect(express).toContain('Rs. 950, 1 working day');
    expect(express).not.toContain('Rest of Sri Lanka');
  });

  it('shows a changed fee as the feed sends it, with nothing typed into the template', () => {
    render({
      ...PANEL,
      methods: [{ ...PANEL.methods[0], rates: [{ zone: 'Colombo', fee: 375, freeAboveAmount: null, daysMin: 2, daysMax: 4 }] }],
    });

    expect(text('[data-testid="delivery-method-STANDARD"]')).toContain('Rs. 375, 2–4 working days');
    expect(element.textContent).not.toContain('Rs. 400');
  });

  it('hides store pickup unless the feed lists a pickup point', () => {
    render(PANEL);
    expect(element.querySelector('[data-testid="delivery-pickup"]')).toBeNull();
  });

  it('shows store pickup with each point when the feed lists one', () => {
    render({
      ...PANEL,
      pickupPoints: [{ name: 'Flagship', city: 'Colombo 3', openingHours: 'Daily 10–7', fee: 0, daysMin: 1, daysMax: 2 }],
    });

    const pickup = text('[data-testid="delivery-pickup"]');
    expect(pickup).toContain('Store pickup');
    expect(pickup).toContain('Flagship, Colombo 3');
    expect(pickup).toContain('Free, ready in 1–2 working days');
    expect(pickup).toContain('Daily 10–7');
  });

  it('binds the owner texts as markup, through Angular sanitising', () => {
    render({ ...PANEL, deliveryInfoHtml: '<p>Worldwide too.</p><img src="x" onerror="alert(1)">' });

    const delivery = element.querySelector('[data-testid="pdp-delivery-text"]')!;
    expect(delivery.querySelector('p')?.textContent).toBe('Worldwide too.');
    expect(delivery.innerHTML).not.toContain('onerror');
    expect(text('[data-testid="pdp-returns-text"]')).toBe('14 days.');
  });

  it('links to the Shipping and Returns pages only when they are published, by their current slugs', () => {
    render(PANEL);
    expect(element.querySelector('[data-testid="pdp-shipping-link"]')).toBeNull();
    expect(element.querySelector('[data-testid="pdp-returns-link"]')).toBeNull();

    TestBed.resetTestingModule();
    render(PANEL, [
      { slug: 'delivery', title: 'Delivery', systemKey: 'shipping' },
      { slug: 'returns', title: 'Returns', systemKey: 'returns' },
    ]);

    expect(element.querySelector('[data-testid="pdp-shipping-link"]')!.getAttribute('href')).toBe('/delivery');
    expect(element.querySelector('[data-testid="pdp-returns-link"]')!.getAttribute('href')).toBe('/returns');
  });

  it('leaves out the Returns disclosure when there is neither a text nor a published page', () => {
    render({ ...PANEL, returnsInfoHtml: '' });

    expect(element.querySelector('[data-testid="pdp-returns"]')).toBeNull();
    expect(element.querySelector('[data-testid="pdp-delivery"]')).not.toBeNull();
  });

  it('keeps the Sri Lanka section for store pickup alone, when no delivery method is on offer', () => {
    render({
      ...PANEL,
      methods: [],
      estimate: null,
      pickupPoints: [{ name: 'Flagship', city: 'Colombo 3', openingHours: null, fee: 0, daysMin: 1, daysMax: 2 }],
    });

    expect(element.querySelector('[data-testid="pdp-delivery-lk"]')).not.toBeNull();
    expect(text('[data-testid="delivery-pickup"]')).toContain('Flagship, Colombo 3');
    expect(element.querySelector('[data-testid="pdp-delivery-estimate"]')).toBeNull();
  });

  it('leaves out the Sri Lanka section when nothing is on offer but keeps the text', () => {
    render({ ...PANEL, methods: [], estimate: null });

    expect(element.querySelector('[data-testid="pdp-delivery-lk"]')).toBeNull();
    expect(element.querySelector('[data-testid="pdp-delivery-text"]')).not.toBeNull();
  });
});
