import { BRAND, brandPageTitle } from './brand';

/**
 * The anchor for every other brand assertion in the two applications.
 *
 * The specs that assert a page title or a wordmark spell the name out — `'New Arrivals | Colombo
 * Wardrobe'` — deliberately, because a spec that builds its expectation from the same constant the
 * subject reads cannot fail. That only works if something pins the constant itself, which is this.
 *
 * The matching rule for *production* source — that the name is read from here and never written out again
 * — is swept over all three source trees by `BrandNameConventionTests` in `SpencerDenim.UnitTests`, which
 * is the only suite in the repository that can read files.
 */
describe('BRAND', () => {
  it('is the name the owner decided on (2026-10-02)', () => {
    expect(BRAND.name).toBe('Colombo Wardrobe');
    expect(BRAND.adminName).toBe('Colombo Wardrobe Admin');
    expect(BRAND.domain).toBe('colombowardrobecompany.com');
  });

  it('builds a page title the way the backend seeds one', () => {
    // Same separator and same order as StoreBrand.PageTitle, which writes the seeded department titles:
    // the listing page appends this suffix only when a department has no SEO title of its own, so the two
    // have to agree or those pages read as a second, slightly-wrong brand.
    expect(brandPageTitle("Women's clothing")).toBe("Women's clothing | Colombo Wardrobe");
    expect(brandPageTitle('Sale')).toBe('Sale | Colombo Wardrobe');
  });
});
