namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// The three fixed departments of doc 08 §9, by slug. The nav is fixed (Q5), so there is no admin create
/// or delete and these three are the whole set.
/// <para>
/// They are here, and by <b>slug</b> rather than id, because one business rule needs to know which
/// department a product is in: doc 08 §3.2 makes <c>Products.AgeGroupId</c> "required for Kids (app)". An
/// id is whatever the seeder happened to assign on a given database — different on the developer's, the
/// test one and the client's — whereas a slug is in the URL and the same everywhere.
/// </para>
/// </summary>
public static class CatalogDepartments
{
    public const string WomenSlug = "women";

    public const string MenSlug = "men";

    /// <summary>The one department whose products must carry an age group (doc 08 §3.2, DEPT-01).</summary>
    public const string KidsSlug = "kids";
}
