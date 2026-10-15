namespace SpencerDenim.Domain.Analytics;

/// <summary>
/// <c>analytics.PageViews.PageType</c> (doc 08 §3.8: "1 Home … 99 Other. CK"): which kind of storefront page
/// a view was of. The values follow the storefront's route table (<c>app.routes.ts</c>), one per route family.
/// <para>
/// Stored as <c>tinyint</c>; <c>CK_PageViews_PageType</c> and <c>CK_PageDaily_PageType</c> are written from
/// <see cref="PageTypes.All"/>, so a value added here without a migration fails on its first insert rather
/// than storing a number no screen can name.
/// </para>
/// </summary>
public enum PageType : byte
{
    Home = 1,
    Department = 2,
    Category = 3,
    Product = 4,
    Search = 5,
    NewArrivals = 6,
    Sale = 7,
    Bag = 8,
    Wishlist = 9,
    Checkout = 10,

    /// <summary>The order success page and a guest's order link. Its token is never stored (see <c>PagePath</c>).</summary>
    Order = 11,

    Account = 12,

    /// <summary>A published content page: About, Contact, Returns, Shipping, Privacy, Terms or a custom one.</summary>
    Content = 13,

    Other = 99,
}

/// <summary>Helpers over <see cref="PageType"/> that the schema and the screens share.</summary>
public static class PageTypes
{
    /// <summary>Every defined value, in declaration order.</summary>
    public static IReadOnlyList<PageType> All { get; } = Enum.GetValues<PageType>();

    /// <summary>The SQL list a check constraint uses, e.g. <c>1, 2, 3, …, 99</c>.</summary>
    public static string SqlList => string.Join(", ", All.Select(type => ((byte)type).ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
