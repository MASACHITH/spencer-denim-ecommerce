namespace SpencerDenim.Domain.Branding;

/// <summary>
/// The shop's name, in one place, for everything on the server that a shopper or a staff user reads:
/// seeded SEO titles, the demo placeholders' caption, the authenticator issuer and the OpenAPI document.
/// <para>
/// <b>Why a constant and not an <c>ops.Settings</c> row.</b> Doc 08 §3.8 does describe a settings table,
/// and a shop name looks like the sort of thing that belongs in it — but that table is not built yet
/// (ADM-22), no admin screen can edit it, and the first consumer here is the <i>seeder</i>. A seeded
/// setting that the seeder itself has to read in order to write the rest of the seed data is indirection
/// with no payer: it would have to be read before the row it reads could be guaranteed to exist, and the
/// OpenAPI title is wanted at startup before any database is touched. A compile-time constant is the
/// honest shape for a value that is, today, not editable. When ADM-22 lands, this becomes the fallback
/// for <c>Branding.StoreName</c> rather than the answer, and the call sites do not move.
/// </para>
/// <para>
/// <b>The seam that is deliberately empty.</b> The <i>registered company</i> — the legal name, the
/// registered address and the VAT number the client has not supplied — is a different value with a
/// different owner, and invoices (W3, issue #53) are the first thing that will need it. It is not
/// declared here, because an unused constant would be a decision taken on the client's behalf; when it
/// is needed it belongs in <c>ops.Settings</c> (a client fills it in, it changes without a deploy, and it
/// has to be captured per invoice anyway). This class being <i>only</i> the display name is what keeps
/// that a separate decision rather than a rename of this one.
/// </para>
/// </summary>
public static class StoreBrand
{
    /// <summary>The shop's name, as a shopper reads it (owner's decision, 2026-10-02).</summary>
    public const string Name = "Colombo Wardrobe";

    /// <summary>The admin application's name, as a staff user reads it.</summary>
    public const string AdminName = $"{Name} Admin";

    /// <summary>The site's own domain, without a scheme. Used in copy and examples, not for routing.</summary>
    public const string Domain = "colombowardrobecompany.com";

    /// <summary>
    /// The site's page-title convention, <c>"&lt;page&gt; | &lt;store&gt;"</c>. Seeded SEO titles use it, and
    /// the storefront appends the same suffix when a product or department carries no title of its own —
    /// which is why the two sides have to agree about the separator as well as the name.
    /// </summary>
    public static string PageTitle(string subject) => $"{subject} | {Name}";
}
