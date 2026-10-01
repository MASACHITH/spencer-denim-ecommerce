/**
 * The shop's name, in one place, for both front-end applications.
 *
 * **Why here.** The storefront and the admin app each show the name a dozen times — wordmarks, page
 * titles, meta descriptions, a privacy note, a downloaded recovery-code file — and `projects/shared` is
 * the only code both of them already import. Putting it in the storefront's `SITE_CONFIG` would have
 * left the admin app with a second copy, which is the problem rather than the fix.
 *
 * **Why a constant and not a setting.** Nothing can edit this today: there is no admin screen for it and
 * no `ops.Settings` table yet (ADM-22). More to the point, the server renders the storefront, so a value
 * fetched over HTTP would have to resolve before the first byte of the shell — a constant is correct for
 * something that is part of the build. `SITE_CONFIG` next door is the shape to copy when this does become
 * editable: an injection token with a default, so a deployment can override it without the call sites
 * moving.
 *
 * **What is deliberately not here.** The registered company's legal name, address and VAT number. They
 * are a different value with a different owner — the client's, and their lawyer's — and the first thing
 * that needs them is invoices (W3, issue #53), which must capture them per invoice anyway. Adding an
 * unused field now would be answering a question nobody has asked the client yet. This file holding only
 * the display name is what keeps those separable.
 */
export const BRAND = {
  /** The shop's name, as a shopper reads it (owner's decision, 2026-10-02). */
  name: 'Colombo Wardrobe',

  /** The admin application's name, as a staff user reads it. */
  adminName: 'Colombo Wardrobe Admin',

  /** The site's own domain, without a scheme. Copy and examples only; never used for routing. */
  domain: 'colombowardrobecompany.com',
} as const;

/**
 * The site's page-title convention, `"<page> | <store>"` (GLB-11).
 *
 * The backend seeds department SEO titles with the same pattern (`StoreBrand.PageTitle`), so the two
 * sides have to agree about the separator as well as the name: the listing page appends this suffix only
 * when a department carries no `seoTitle` of its own, and a different separator here would read as a
 * second, slightly-wrong brand on exactly those pages.
 */
export function brandPageTitle(subject: string): string {
  return `${subject} | ${BRAND.name}`;
}
