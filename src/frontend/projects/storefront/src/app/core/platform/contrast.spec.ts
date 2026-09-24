import { contrastRatio, readableInk, relativeLuminance } from './contrast';

/** Every colourway `DemoCatalogueData` seeds, hex for hex. */
const SEEDED = [
  ['Sandstone Beige', '#D8CBB6'],
  ['Optic White', '#FFFFFF'],
  ['Coconut Cream', '#F3EADA'],
  ['Deep Navy', '#1F2A44'],
  ['True Black', '#101010'],
  ['Charcoal Grey', '#4A4D52'],
  ['Indigo Rinse', '#3A5A8C'],
  ['Mid Wash Blue', '#6E93C4'],
  ['Olive Green', '#5C6B4A'],
  ['Terracotta', '#C4703F'],
  ['Blush Pink', '#E8A0BF'],
  ['Deep Maroon', '#7E2B2B'],
  ['Mustard', '#D9A93C'],
  ['Plum', '#6C4A8F'],
  ['Chocolate', '#5B3F2E'],
  ['Deep Teal', '#2E6B66'],
] as const;

const DARK = '#1a1a1a';
const LIGHT = '#ffffff';
const MUTED = '#6b6b6b';

/** WCAG 2.2 AA 1.4.11: non-text contrast. */
const MINIMUM = 3;

/**
 * The floor of "pick the better of these two inks", which is the crossover where both score
 * alike: relative luminance 0.2017, and 4.17:1 for `#1a1a1a` against white. Pure black would give
 * 4.58, and this is the price of using the text token instead. Written down so that changing
 * either token moves a number here rather than quietly lowering the guarantee.
 */
const FLOOR = 4.17;

describe('relativeLuminance', () => {
  it('runs from black to white', () => {
    expect(relativeLuminance('#000000')).toBe(0);
    expect(relativeLuminance('#ffffff')).toBe(1);
  });

  it('reads three-digit hex the same as six', () => {
    expect(relativeLuminance('#fff')).toBe(relativeLuminance('#ffffff'));
    expect(relativeLuminance('#08f')).toBeCloseTo(relativeLuminance('#0088ff'), 10);
  });

  it('reads a missing or unparseable colour as the white surface behind it', () => {
    for (const value of [null, undefined, '', 'transparent', 'rebeccapurple', '#12345']) {
      expect(relativeLuminance(value)).toBe(1);
    }
  });

  /**
   * The gamma curve is the whole point of this file. A naive average would put mid grey at 0.5;
   * the sRGB curve puts it near 0.216, which is why `isLight()`'s 0.8 threshold cannot answer
   * "what reads on top of this?".
   */
  it('applies the sRGB curve rather than averaging the channels', () => {
    expect(relativeLuminance('#808080')).toBeCloseTo(0.2159, 3);
  });
});

describe('contrastRatio', () => {
  it('is 21 for black on white and 1 for a colour on itself', () => {
    expect(contrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrastRatio('#5C6B4A', '#5C6B4A')).toBeCloseTo(1, 10);
  });

  it('does not care which way round the two are given', () => {
    expect(contrastRatio('#1F2A44', '#ffffff')).toBeCloseTo(contrastRatio('#ffffff', '#1F2A44'), 10);
  });
});

/**
 * The defect this file was written for: the sold-out slash was painted in `--color-text-muted`
 * whatever colour it landed on.
 */
describe('the sold-out slash over the seeded palette', () => {
  it('fails 1.4.11 on twelve of the sixteen when it is fixed to one grey', () => {
    const failing = SEEDED.filter(([, hex]) => contrastRatio(hex, MUTED) < MINIMUM);

    expect(failing).toHaveLength(12);
    // The worst of them, which is what a shopper met on an Olive Green swatch.
    expect(contrastRatio('#5C6B4A', MUTED)).toBeCloseTo(1.08, 2);
  });

  it.each(SEEDED)('clears 3:1 on %s when the ink is chosen by contrast', (_name, hex) => {
    const ink = readableInk(hex) === 'dark' ? DARK : LIGHT;

    expect(contrastRatio(hex, ink)).toBeGreaterThanOrEqual(MINIMUM);
  });

  it('scores 4.75:1 at worst across the palette, on Terracotta', () => {
    const ratios = SEEDED.map(([name, hex]) => ({
      name,
      ratio: contrastRatio(hex, readableInk(hex) === 'dark' ? DARK : LIGHT),
    }));

    const worst = ratios.reduce((low, entry) => (entry.ratio < low.ratio ? entry : low));

    expect(worst.name).toBe('Terracotta');
    expect(worst.ratio).toBeCloseTo(4.75, 2);
  });

  /**
   * Choosing between two inks at opposite ends of the scale has a floor: the worst any colour can
   * do is the crossover, where both inks score alike. This walks the whole cube rather than the
   * palette, so a colour an admin adds later cannot fall through.
   */
  it('cannot score below the floor for any colour at all', () => {
    let worst = Infinity;

    for (let red = 0; red <= 255; red += 5) {
      for (let green = 0; green <= 255; green += 5) {
        for (let blue = 0; blue <= 255; blue += 5) {
          const hex = `#${[red, green, blue].map((v) => v.toString(16).padStart(2, '0')).join('')}`;
          worst = Math.min(worst, contrastRatio(hex, readableInk(hex) === 'dark' ? DARK : LIGHT));
        }
      }
    }

    // Comfortably above the 3:1 an admin's next colour has to clear, whatever it is.
    expect(worst).toBeGreaterThanOrEqual(FLOOR);
    expect(worst).toBeCloseTo(FLOOR, 2);
  });
});

describe('readableInk', () => {
  it('puts light ink on the dark end and dark ink on the light end', () => {
    expect(readableInk('#101010')).toBe('light');
    expect(readableInk('#ffffff')).toBe('dark');
  });

  /**
   * The naive luminance `isLight()` uses reports 0.68 for Mustard and 0.70 for Blush Pink, so a
   * 0.8 "is it light?" threshold calls both dark and would put white on them — 2.17:1 and 2.06:1.
   * Measuring both inks gets it right without a threshold.
   */
  it('puts dark ink on the colours a lightness threshold would misjudge', () => {
    expect(readableInk('#D9A93C')).toBe('dark');
    expect(readableInk('#E8A0BF')).toBe('dark');
    expect(readableInk('#C4703F')).toBe('dark');
    expect(readableInk('#6E93C4')).toBe('dark');
  });

  it('treats a swatch with no colour as the white surface it shows through to', () => {
    expect(readableInk(null)).toBe('dark');
  });
});
