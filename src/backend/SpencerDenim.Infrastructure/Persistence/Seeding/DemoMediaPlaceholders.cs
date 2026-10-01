using System.Globalization;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// One demo image: where it is stored and what it says on it.
/// </summary>
/// <param name="StorageKey">The seeded <c>MediaAssets.StorageKey</c>.</param>
/// <param name="ProductName">Printed on the placeholder.</param>
/// <param name="ColourName">The colourway, or null for the shared fabric-detail frame.</param>
/// <param name="ColourHex">The colourway swatch, drawn as a dot so two frames of one product differ.</param>
/// <param name="AltText">What screen readers and the alt attribute say — which is that it is a placeholder.</param>
internal sealed record DemoMediaPlaceholder(
    string StorageKey,
    string ProductName,
    string? ColourName,
    string ColourHex,
    string AltText);

/// <summary>
/// The demo catalogue's media, in one place, because two things have to agree about it: the seeder that
/// writes the <c>MediaAssets</c> rows and the generator that draws the files those rows point at.
/// <para>
/// Everything here is a <b>placeholder</b> and says so, on the image and in its alt text. No
/// photography was supplied for the demo catalogue, and inventing something that looks like a product shot would put an
/// imaginary garment in front of the client in a review and, worse, could reach staging where somebody
/// might take it for the real thing. A neutral block with the words on it cannot be mistaken for either.
/// </para>
/// </summary>
internal static class DemoMediaPlaceholders
{
    /// <summary>Every demo storage key starts here, which is how the generator finds the rows to fill.</summary>
    public const string StorageKeyPrefix = "public/originals/demo/";

    /// <summary>
    /// The key a demo asset is stored under. Unchanged from the catalogue milestone on purpose: these
    /// rows already exist in every developer's and staging database, and this task fills the files in
    /// behind them rather than rewriting the catalogue.
    /// </summary>
    public static string StorageKeyFor(string slug, int oneBasedIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{StorageKeyPrefix}{slug}-{oneBasedIndex}.jpg");

    /// <summary>The filename recorded on the asset row.</summary>
    public static string FileNameFor(string slug, int oneBasedIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{slug}-{oneBasedIndex}.jpg");

    /// <summary>
    /// Alt text that names the placeholder first. Doc 08 §3.3 requires alt text on a published public
    /// image, and a shopper — or an accessibility audit — reading "Placeholder image" is being told the
    /// truth, where "Wide-Leg Rinse Jeans in Indigo Rinse" over a grey box is not.
    /// </summary>
    public static string AltTextFor(string productName, string? colourName) => colourName is null
        ? $"Placeholder image — no fabric-detail photograph of {productName} has been supplied yet"
        : $"Placeholder image — no photograph of {productName} in {colourName} has been supplied yet";

    /// <summary>
    /// One frame per colourway, then a shared detail frame — the same shape the demo seeder's gallery
    /// expects (<c>assets[0]</c> is the card image and <c>assets[^1]</c> the hover/detail one).
    /// </summary>
    public static IReadOnlyList<DemoMediaPlaceholder> ForProduct(DemoProductSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var placeholders = new List<DemoMediaPlaceholder>(seed.Colours.Length + 1);

        for (var index = 1; index <= seed.Colours.Length + 1; index++)
        {
            var isDetail = index == seed.Colours.Length + 1;
            var colour = isDetail ? null : seed.Colours[index - 1];

            placeholders.Add(new DemoMediaPlaceholder(
                StorageKeyFor(seed.Slug, index),
                seed.Name,
                colour?.Name,
                colour?.Hex ?? "#BFB9B0",
                AltTextFor(seed.Name, colour?.Name)));
        }

        return placeholders;
    }

    /// <summary>Every demo placeholder, keyed by storage key.</summary>
    public static IReadOnlyDictionary<string, DemoMediaPlaceholder> ByStorageKey { get; } = Build();

    private static Dictionary<string, DemoMediaPlaceholder> Build()
    {
        var all = new Dictionary<string, DemoMediaPlaceholder>(StringComparer.Ordinal);

        foreach (var seed in DemoCatalogueData.Products)
        {
            foreach (var placeholder in ForProduct(seed))
            {
                all[placeholder.StorageKey] = placeholder;
            }
        }

        return all;
    }
}
