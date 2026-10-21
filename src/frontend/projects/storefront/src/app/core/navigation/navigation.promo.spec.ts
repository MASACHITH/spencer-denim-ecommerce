import { DepartmentDto, MegaMenuPromoCardDto } from './catalog-departments.model';
import { toNavigation, toPromo } from './navigation.mapper';
import { MegaMenuPromo, promoAt } from './navigation.model';

/**
 * The mega-menu promo card (GLB-05, ADM-17; #215): the first drawable admin card, or — with none — exactly the card
 * the menu drew before #215, from the department itself.
 */
function department(overrides: Partial<DepartmentDto> = {}): DepartmentDto {
  return {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: 'Everyday essentials.',
    heroImageUrl: '/media/public/renditions/hero/large.webp',
    heroImageSrcSet: '/media/a.webp 600w, /media/b.webp 1200w',
    seoTitle: null,
    seoDescription: null,
    categories: [
      {
        id: 11,
        name: 'Dresses',
        slug: 'dresses',
        depth: 1,
        description: null,
        imageUrl: null,
        imageSrcSet: null,
        showInMegaMenu: true,
        megaMenuColumn: null,
        ageGroupSlug: null,
        children: [],
      },
    ],
    ...overrides,
  };
}

function card(overrides: Partial<MegaMenuPromoCardDto> = {}): MegaMenuPromoCardDto {
  return {
    id: 7,
    title: 'Linen days',
    subtitle: 'Breathable pieces for the heat.',
    buttonLabel: 'Shop linen',
    linkUrl: '/women/dresses?material=linen',
    imageUrl: '/media/public/renditions/card/card.webp',
    imageSrcSet: '/media/c.webp 600w, /media/d.webp 1200w',
    imageAlt: 'A model in a linen dress',
    ...overrides,
  };
}

/** Today's card, as it was hard-coded before #215. */
const FALLBACK: MegaMenuPromo = {
  title: 'New in Women',
  caption: 'Everyday essentials.',
  cta: { label: 'Shop now', target: { kind: 'internal', path: '/new-arrivals', queryParams: null } },
  imageUrl: '/media/public/renditions/hero/large.webp',
  imageSrcSet: '/media/a.webp 600w, /media/b.webp 1200w',
  imageAlt: '',
};

describe('toPromo (#215)', () => {
  it('without cards draws exactly the card the menu always drew', () => {
    expect(toPromo(department())).toEqual(FALLBACK);
    expect(toPromo(department({ promoCards: [] }))).toEqual(FALLBACK);
  });

  it('keeps the fallback when the field is missing or not a list (an older API, a bad payload)', () => {
    const odd = department({ promoCards: { nope: true } as unknown as readonly MegaMenuPromoCardDto[] });
    expect(toPromo(odd)).toEqual(FALLBACK);
  });

  it('a department without a description or picture falls back to an empty caption and the placeholder', () => {
    const promo = toPromo(department({ description: null, heroImageUrl: null, heroImageSrcSet: null }));

    expect(promo.caption).toBe('');
    expect(promo.imageUrl).toBeNull();
    expect(promo.imageSrcSet).toBeNull();
  });

  it('draws the first admin card, with its own words, picture, alt text and filtered link', () => {
    const promo = toPromo(department({ promoCards: [card(), card({ id: 8, title: 'Second' })] }));

    expect(promo).toEqual({
      title: 'Linen days',
      caption: 'Breathable pieces for the heat.',
      cta: {
        label: 'Shop linen',
        target: { kind: 'internal', path: '/women/dresses', queryParams: { material: 'linen' } },
      },
      imageUrl: '/media/public/renditions/card/card.webp',
      imageSrcSet: '/media/c.webp 600w, /media/d.webp 1200w',
      imageAlt: 'A model in a linen dress',
    } satisfies MegaMenuPromo);
  });

  it('an http(s) address becomes an external link', () => {
    const promo = toPromo(department({ promoCards: [card({ linkUrl: 'https://instagram.com/colombowardrobe' })] }));

    expect(promo.cta.target).toEqual({ kind: 'external', href: 'https://instagram.com/colombowardrobe' });
  });

  it.each([
    ['a javascript: link', { linkUrl: 'javascript:alert(1)' }],
    ['a protocol-relative link', { linkUrl: '//evil.example/x' }],
    ['a data: link', { linkUrl: 'data:text/html,x' }],
    ['a blank heading', { title: '   ' }],
    ['a blank button', { buttonLabel: '' }],
    ['no picture', { imageUrl: null }],
    ['an empty picture', { imageUrl: '' }],
  ])('skips a card with %s and draws the next one', (_, flaw) => {
    const promo = toPromo(department({ promoCards: [card(flaw), card({ id: 9, title: 'Good one' })] }));

    expect(promo.title).toBe('Good one');
  });

  it('falls back when every card is unusable', () => {
    const promo = toPromo(department({ promoCards: [card({ linkUrl: 'javascript:x' }), card({ imageUrl: null })] }));

    expect(promo).toEqual(FALLBACK);
  });

  it('carries markup as text, trimmed, for interpolation', () => {
    const promo = toPromo(
      department({
        promoCards: [card({ title: '  <img src=x onerror=alert(1)>  ', subtitle: '<b>bold</b>', buttonLabel: ' Go > ' })],
      }),
    );

    expect(promo.title).toBe('<img src=x onerror=alert(1)>');
    expect(promo.caption).toBe('<b>bold</b>');
    expect(promo.cta.label).toBe('Go >');
  });

  it('a card without a subtitle, sources or alt text draws an empty caption, no srcset and empty alt', () => {
    const promo = toPromo(department({ promoCards: [card({ subtitle: null, imageSrcSet: null, imageAlt: null })] }));

    expect(promo.caption).toBe('');
    expect(promo.imageSrcSet).toBeNull();
    expect(promo.imageAlt).toBe('');
  });

  // Tester pass (#215): an empty srcset would reach `[attr.srcset]` as "" and `sizes` would still be set;
  // padding round the line or the alt text is what a pasted value carries.
  it('treats an empty srcset as none and trims the line and the alt text', () => {
    const promo = toPromo(
      department({
        promoCards: [card({ imageSrcSet: '', subtitle: '  Breathable.  ', imageAlt: '  A linen dress  ' })],
      }),
    );

    expect(promo.imageSrcSet).toBeNull();
    expect(promo.caption).toBe('Breathable.');
    expect(promo.imageAlt).toBe('A linen dress');
  });

  it('a department with no menu has no card at all, admin card or not', () => {
    const [women] = toNavigation([department({ categories: [], promoCards: [card()] })]);

    expect(women.promo).toBeNull();
  });
});

/**
 * #215 tester: the tree is kept for the life of the page (`shareReplay`), so the menu re-checks a card's window
 * when it opens. Times are explicit instants, never "now".
 */
describe('the card window, re-checked where the menu opens (#215)', () => {
  const start = Date.parse('2026-11-01T03:30:00Z');
  const end = Date.parse('2026-11-08T03:30:00Z');
  const windowed = card({ title: 'Week card', startsAtUtc: '2026-11-01T03:30:00Z', endsAtUtc: '2026-11-08T03:30:00Z' });

  it('draws the card inside its window, start inclusive and end exclusive', () => {
    const dept = department({ promoCards: [windowed] });

    expect(toPromo(dept, start - 1).title).toBe('New in Women');
    expect(toPromo(dept, start).title).toBe('Week card');
    expect(toPromo(dept, end - 1).title).toBe('Week card');
    expect(toPromo(dept, end).title).toBe('New in Women');
  });

  it('a closed card gives way to the next card, not only to the fallback', () => {
    const dept = department({ promoCards: [windowed, card({ id: 9, title: 'Always on' })] });

    expect(toPromo(dept, end + 60_000).title).toBe('Always on');
  });

  it('every menu item carries its candidates, ending with the fallback; other items carry none', () => {
    const items = toNavigation([department({ promoCards: [windowed] })]);
    const women = items.find((item) => item.id === 'women')!;

    expect(women.promos.map((candidate) => candidate.promo.title)).toEqual(['Week card', 'New in Women']);
    expect(women.promos[0].endsAt).toBe(end);
    expect(promoAt(women.promos, end)?.title).toBe('New in Women');
    expect(items.find((item) => item.id === 'sale')!.promos).toEqual([]);
  });

  it('an unreadable instant is treated as open-ended', () => {
    const dept = department({ promoCards: [card({ title: 'Odd', endsAtUtc: 'not a date' })] });

    expect(toPromo(dept, end).title).toBe('Odd');
  });
});
