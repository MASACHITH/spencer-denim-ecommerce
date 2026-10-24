import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { GuideScroller } from './guide-scroller';
import { GuideText, parseGuideText, plainGuideText } from './guide-text';

describe('parseGuideText', () => {
  it('leaves ordinary words as one plain part', () => {
    expect(parseGuideText('Open the order and check it.')).toEqual([
      { kind: 'plain', text: 'Open the order and check it.' },
    ]);
  });

  it('reads bold, code and both kinds of link, in order', () => {
    expect(parseGuideText('Press **Save**, type `/sale`, open [Orders](/orders) or [photos](#photos-and-video).')).toEqual([
      { kind: 'plain', text: 'Press ' },
      { kind: 'strong', text: 'Save' },
      { kind: 'plain', text: ', type ' },
      { kind: 'code', text: '/sale' },
      { kind: 'plain', text: ', open ' },
      { kind: 'link', text: 'Orders', path: '/orders' },
      { kind: 'plain', text: ' or ' },
      { kind: 'link', text: 'photos', path: '#photos-and-video' },
      { kind: 'plain', text: '.' },
    ]);
  });

  it('keeps an unclosed marker as the words it is', () => {
    expect(plainGuideText('**not closed and `neither')).toBe('**not closed and `neither');
  });

  it('never turns a link to another site into a link', () => {
    const parts = parseGuideText('[click](https://example.com) and [here](javascript:alert(1))');

    expect(parts.every((part) => part.kind === 'plain')).toBe(true);
  });

  it('keeps anything that looks like HTML as text', () => {
    expect(parseGuideText('<img src=x onerror=alert(1)>')).toEqual([
      { kind: 'plain', text: '<img src=x onerror=alert(1)>' },
    ]);
  });

  it('strips the markup for searching', () => {
    expect(plainGuideText('Go to **Settings** and open [Roles](/roles).')).toBe('Go to Settings and open Roles.');
  });
});

describe('GuideText', () => {
  function render(text: string): HTMLElement {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(GuideText);
    fixture.componentRef.setInput('text', text);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('draws the words without adding or losing a space around bold and code', () => {
    const host = render('Press **Save** then type `/sale` and go.');

    expect(host.textContent).toBe('Press Save then type /sale and go.');
    expect(host.querySelector('strong')?.textContent).toBe('Save');
    expect(host.querySelector('code')?.textContent).toBe('/sale');
  });

  it('draws an admin link as a router link', () => {
    const link = render('Open [Orders](/orders).').querySelector('a');

    expect(link?.getAttribute('href')).toBe('/orders');
  });

  it('tells a link to a screen from a link to another place in the guide, so each can say where it goes', () => {
    const host = render('Open [Orders](/orders) or see [photos](#photos-and-video).');
    const [screen, section] = Array.from(host.querySelectorAll('a'));

    expect(screen.classList).toContain('guide-link');
    expect(screen.classList).toContain('guide-link--screen');
    expect(section.classList).toContain('guide-link');
    expect(section.classList).toContain('guide-link--section');
  });

  it('scrolls inside the guide for an anchor link instead of leaving the page', () => {
    const scroller = { to: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: GuideScroller, useValue: scroller }],
    });
    const fixture = TestBed.createComponent(GuideText);
    fixture.componentRef.setInput('text', 'See [photos](#photos-and-video).');
    fixture.detectChanges();

    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    (fixture.nativeElement as HTMLElement).querySelector('a')?.dispatchEvent(click);

    expect(click.defaultPrevented).toBe(true);
    expect(scroller.to).toHaveBeenCalledWith('photos-and-video');
  });
});
