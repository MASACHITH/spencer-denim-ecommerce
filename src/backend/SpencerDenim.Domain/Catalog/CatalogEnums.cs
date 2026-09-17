namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// <c>catalog.AttributeOptions.AttributeType</c> (doc 08 §3.2). Style (4) is deferred, Appendix A.
/// </summary>
public enum AttributeOptionType : byte
{
    Material = 1,
    Fit = 2,
    Occasion = 3,
}

/// <summary>
/// <c>catalog.Products.BadgeOverride</c> (doc 08 §3.2) and the resolved card badge.
/// Only one badge is ever shown on a card.
/// </summary>
public enum ProductBadge : byte
{
    New = 1,
    Sale = 2,
    OnlineExclusive = 3,
    BestSeller = 4,

    /// <summary>
    /// Computed at read time only (doc 08 §3.2, N11). It is never stored in
    /// <c>BadgeOverride</c>, which is why it sits outside the 1–4 range the CK allows.
    /// </summary>
    LowStock = 5,
}

/// <summary><c>catalog.ProductMedia.MediaRole</c> (doc 08 §3.2).</summary>
public enum ProductMediaRole : byte
{
    Gallery = 1,
    Hover = 2,
    Video = 3,
}
