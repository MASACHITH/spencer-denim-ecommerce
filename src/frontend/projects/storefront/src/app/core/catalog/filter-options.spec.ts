import { SIZE_SCALES, sizeScalesFor } from './filter-options';

function scaleNames(department: string | null): string[] {
  return sizeScalesFor(department).map((scale) => scale.name);
}

/**
 * Sizes are offered scale by scale, and only the scales a department can produce. A Baby size on
 * a Women listing can only ever return nothing, and a bare list of labels cannot say whether a
 * "36" is a waist or a shoe.
 */
describe('sizeScalesFor', () => {
  it('offers a department only what it stocks', () => {
    expect(scaleNames('women')).toEqual(['Clothing', 'Women waist', 'Footwear (EU)']);
    expect(scaleNames('men')).toEqual(['Clothing', 'Men waist', 'Footwear (EU)']);
    expect(scaleNames('kids')).toEqual(['Kids age', 'Baby months', 'Footwear (EU)']);
  });

  it('never offers another department\'s waist or a baby size to a grown-up listing', () => {
    expect(scaleNames('women')).not.toContain('Men waist');
    expect(scaleNames('women')).not.toContain('Baby months');
    expect(scaleNames('kids')).not.toContain('Clothing');
  });

  it('offers every scale where there is no department to narrow by (New Arrivals, Sale)', () => {
    expect(sizeScalesFor(null)).toEqual(SIZE_SCALES);
  });

  it('offers footwear everywhere, because every department sells it', () => {
    for (const department of ['women', 'men', 'kids']) {
      expect(scaleNames(department)).toContain('Footwear (EU)');
    }
  });

  it('carries the seeded labels, which is what the API matches on', () => {
    const clothing = SIZE_SCALES.find((scale) => scale.name === 'Clothing');

    expect(clothing?.options.map((option) => option.value)).toEqual([
      'XS',
      'S',
      'M',
      'L',
      'XL',
      'XXL',
      'XXXL',
    ]);
  });
});
