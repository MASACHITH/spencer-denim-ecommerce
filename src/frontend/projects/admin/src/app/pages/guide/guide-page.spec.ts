import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';

import { GUIDE_SECTIONS } from './content';
import { GUIDE_CONTENT, GuideContent } from './guide-content.token';
import { GUIDE_GROUP_HUE, GUIDE_GROUPS, GuideSection } from './guide.model';
import { GuidePage } from './guide-page';
import { GuideScroller } from './guide-scroller';
import { searchTextOf } from './guide-search';

/**
 * The page's own behaviour, against three small sections so each test is quick. The real content is checked
 * as a whole by `guide-content.spec.ts`, and drawn once, in full, by the last test here.
 */
const FIXTURE: readonly GuideSection[] = [
  {
    id: 'alpha-orders',
    group: 'Orders and delivery',
    title: 'Alpha orders',
    summary: 'Process what customers buy.',
    blocks: [{ kind: 'text', text: 'Approve a **bank transfer** first. Open [Orders](/orders).' }],
  },
  {
    id: 'beta-photos',
    group: 'Photos and video',
    title: 'Beta photos',
    summary: 'Prepare pictures.',
    blocks: [{ kind: 'list', items: ['Use JPEG or WebP'] }],
  },
  {
    id: 'gamma-settings',
    group: 'Settings and records',
    title: 'Gamma settings',
    summary: 'Change a setting.',
    blocks: [{ kind: 'text', text: 'Press **Save**.' }],
  },
];

function contentOf(sections: readonly GuideSection[]): GuideContent {
  return { sections, index: new Map(sections.map((section) => [section.id, searchTextOf(section)])) };
}

describe('GuidePage', () => {
  let scroller: { to: ReturnType<typeof vi.fn> };

  function create(fragment: string | null = null, sections: readonly GuideSection[] = FIXTURE): ComponentFixture<GuidePage> {
    scroller = { to: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: GuideScroller, useValue: scroller },
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
        { provide: GUIDE_CONTENT, useValue: contentOf(sections) },
      ],
    });
    const fixture = TestBed.createComponent(GuidePage);
    fixture.detectChanges();
    return fixture;
  }

  function host(fixture: ComponentFixture<GuidePage>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function type(fixture: ComponentFixture<GuidePage>, value: string): void {
    const input = host(fixture).querySelector<HTMLInputElement>('#guide-search')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('has one level-one heading and draws every section with its own level-two heading', () => {
    const fixture = create();

    expect(host(fixture).querySelectorAll('h1')).toHaveLength(1);
    expect(host(fixture).querySelectorAll('section.gs')).toHaveLength(FIXTURE.length);
    expect(host(fixture).querySelectorAll('section.gs > h2')).toHaveLength(FIXTURE.length);
  });

  it('says nothing about the search count until someone searches', () => {
    const fixture = create();

    expect(host(fixture).querySelector('[data-testid="guide-count"]')?.textContent?.trim()).toBe('');
  });

  it('narrows the sections and the contents list to a search, and says how many are left', () => {
    const fixture = create();

    type(fixture, 'bank transfer');

    const ids = Array.from(host(fixture).querySelectorAll('section.gs')).map((section) => section.id);
    expect(ids).toEqual(['alpha-orders']);
    expect(host(fixture).querySelector('[data-testid="guide-count"]')?.textContent).toContain('1 of 3 sections match');
    expect(host(fixture).querySelectorAll('.guide__toc-link')).toHaveLength(1);
  });

  it('needs every word typed, in any order', () => {
    const fixture = create();

    type(fixture, 'transfer bank');
    expect(host(fixture).querySelectorAll('section.gs')).toHaveLength(1);

    type(fixture, 'transfer webp');
    expect(host(fixture).querySelectorAll('section.gs')).toHaveLength(0);
  });

  it('shows a plain empty state when nothing matches', () => {
    const fixture = create();

    type(fixture, 'zzzqqqxxx');

    expect(host(fixture).querySelector('[data-testid="guide-empty"]')?.textContent).toContain('zzzqqqxxx');
    expect(host(fixture).querySelectorAll('section.gs')).toHaveLength(0);
    expect(host(fixture).querySelector('[data-testid="guide-count"]')?.textContent).toContain('No sections');
  });

  it('lists the contents under their group, in group order', () => {
    const fixture = create();

    const groups = Array.from(host(fixture).querySelectorAll('.guide__toc-group')).map((group) => group.textContent?.trim());

    expect(groups).toEqual(['Photos and video', 'Orders and delivery', 'Settings and records']);
  });

  it('opens and closes the contents list, telling assistive technology which', () => {
    const fixture = create();
    const toggle = host(fixture).querySelector<HTMLButtonElement>('.guide__toc-toggle')!;

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(host(fixture).querySelector('.guide__toc-list')?.classList.contains('is-open')).toBe(true);
  });

  it('jumps to a section from the contents list without leaving the page', () => {
    const fixture = create();
    const link = host(fixture).querySelector<HTMLAnchorElement>('.guide__toc-link')!;
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });

    link.dispatchEvent(click);
    fixture.detectChanges();

    expect(click.defaultPrevented).toBe(true);
    // The first link is the first section of the first group, and "Photos and video" comes before "Orders and delivery".
    expect(scroller.to).toHaveBeenCalledWith('beta-photos');
  });

  /** UAT feedback (2026-10-24): the contents list and the sections were all grey. */
  it('marks the section the reader jumped to in the contents list, and only that one', () => {
    const fixture = create();
    const links = host(fixture).querySelectorAll<HTMLAnchorElement>('.guide__toc-link');

    expect(host(fixture).querySelector('.guide__toc-link.is-current')).toBeNull();

    links[1].dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    fixture.detectChanges();

    const current = host(fixture).querySelectorAll('.guide__toc-link.is-current');
    expect(current).toHaveLength(1);
    expect(current[0]).toBe(links[1]);
    expect(links[1].getAttribute('aria-current')).toBe('location');
    expect(links[0].getAttribute('aria-current')).toBeNull();
  });

  it('marks the section named in the address as the current one from the start', () => {
    const fixture = create(FIXTURE[2].id);

    expect(host(fixture).querySelector('.guide__toc-link.is-current')?.textContent?.trim()).toBe('Gamma settings');
  });

  it('draws each group in its own colour, in the contents list and on its sections', () => {
    const fixture = create();

    const sets = Array.from(host(fixture).querySelectorAll<HTMLElement>('.guide__toc-set'));
    expect(sets.map((set) => set.style.getPropertyValue('--g'))).toEqual([
      GUIDE_GROUP_HUE['Photos and video'],
      GUIDE_GROUP_HUE['Orders and delivery'],
      GUIDE_GROUP_HUE['Settings and records'],
    ]);

    const alpha = host(fixture).querySelector<HTMLElement>('#alpha-orders')!;
    expect(alpha.style.getPropertyValue('--g')).toBe(GUIDE_GROUP_HUE['Orders and delivery']);
  });

  it('gives every group a colour, and the colours are not all the same', () => {
    expect(Object.keys(GUIDE_GROUP_HUE).sort()).toEqual([...GUIDE_GROUPS].sort());
    expect(new Set(Object.values(GUIDE_GROUP_HUE)).size).toBeGreaterThanOrEqual(8);
  });

  it('scrolls to the section named in the address, without animating, once the page has drawn', () => {
    create(FIXTURE[1].id);
    TestBed.tick();

    expect(scroller.to).toHaveBeenCalledWith(FIXTURE[1].id, false);
  });

  it('prints through the browser', () => {
    const print = vi.spyOn(globalThis, 'print').mockImplementation(() => undefined);
    const fixture = create();

    host(fixture).querySelector<HTMLButtonElement>('.guide__print')!.click();

    expect(print).toHaveBeenCalledTimes(1);
    print.mockRestore();
  });

  it('draws the real guide in full: every section has its heading and its place in the contents list', () => {
    const fixture = create(null, GUIDE_SECTIONS);

    expect(host(fixture).querySelectorAll('section.gs')).toHaveLength(GUIDE_SECTIONS.length);
    expect(host(fixture).querySelectorAll('.guide__toc-link')).toHaveLength(GUIDE_SECTIONS.length);
  }, 30_000);
});
