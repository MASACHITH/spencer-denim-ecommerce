using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SpencerDenim.Application.Media.ClientAssets;

/// <summary>The five folders of the client's asset drop, as <c>client-assets/README.md</c> names them.</summary>
public enum ClientAssetFolder
{
    /// <summary>The wordmarks and the monogram.</summary>
    Logo = 1,

    /// <summary>One or more photographs per garment, named after the product's slug.</summary>
    Products,

    /// <summary><c>women</c>, <c>men</c>, <c>kids</c>.</summary>
    Departments,

    /// <summary>The hero video and its poster, the two banners and the trending tiles.</summary>
    Home,

    /// <summary><c>social-1</c> … <c>social-6</c>, square.</summary>
    Social,
}

/// <summary>
/// A product photograph's filename, resolved against the naming contract.
/// </summary>
/// <param name="FileName">The file as it is actually on disk, doubled extension and all.</param>
/// <param name="Slug">The product slug the file belongs to — repaired where it had to be.</param>
/// <param name="Index">
/// The number after the slug. <b>1 is the card image and 2 the hover image</b>, which is what
/// <c>client-assets/README.md</c> promises and what <c>catalog.ProductMedia</c>'s roles already mean.
/// Anything higher is an extra gallery frame.
/// </param>
/// <param name="RepairedFrom">
/// The slug as the filename actually spelled it, when that spelling was not a product slug and had to be
/// repaired. <see langword="null"/> when the filename was already right. Every non-null value is printed
/// in the import report: a rename this code inferred is a decision somebody has to be able to check.
/// </param>
public sealed record ProductAssetName(string FileName, string Slug, int Index, string? RepairedFrom)
{
    /// <summary>Whether the slug in the filename had to be repaired to reach a real product.</summary>
    public bool WasRepaired => RepairedFrom is not null;
}

/// <summary>What a filename turned out to be.</summary>
public abstract record ProductAssetNameResult
{
    private ProductAssetNameResult()
    {
    }

    /// <summary>The file names a product this catalogue has.</summary>
    public sealed record Resolved(ProductAssetName Name) : ProductAssetNameResult;

    /// <summary>
    /// The file is in <c>products/</c> and names no product. It is <b>never</b> guessed at and never
    /// quietly skipped: the import reports it and fails, because a photograph nobody can place is a
    /// photograph that will be missing from the shop without anybody noticing.
    /// </summary>
    public sealed record Unmappable(string FileName, string Reason) : ProductAssetNameResult;
}

/// <summary>
/// The naming contract of <c>client-assets/README.md</c>, and the two repairs the delivered batch needs.
/// <para>
/// This is pure: a filename and the catalogue's slug list in, a product and a role out. It is separate
/// from the importer that reads the disk and writes the rows precisely so that the rule which decides
/// <i>which garment a photograph is of</i> can be tested on its own, exhaustively and without a database.
/// </para>
/// <para>
/// <b>Two repairs, both about the front of the name, both required to land on a real slug.</b> The batch
/// the owner delivered carries two kinds of damage, and neither is guesswork to undo:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>A doubled extension</b> — <c>washed-linen-camp-shirt-1.jpg.jpg</c>. Stripped by
/// <see cref="StripMediaExtensions"/>, which is harmless anyway: the pipeline reads the file's signature
/// and ignores its name (<c>IMediaService</c>), so the extension never decided anything.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The folder name run into the slug</b> — <c>productsleather-look-belt-1.jpg.jpg</c>. The leading
/// <c>products</c> is dropped only when what is left is a slug the catalogue actually has.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A character lost off the front</b> — <c>ourt-canvas-sneakers-2.jpg.jpg</c>. Repaired only when
/// <b>exactly one</b> catalogue slug ends with what the filename says and is at most
/// <see cref="MaxMissingPrefix"/> characters longer. Two candidates is not a near miss, it is a
/// coin toss, and this refuses to toss it.
/// </description>
/// </item>
/// </list>
/// <para>
/// Every repair is required to end on a slug that is in the list handed in, so the method cannot invent a
/// product. Anything it cannot place comes back as <see cref="ProductAssetNameResult.Unmappable"/> with
/// the reason, and the caller is expected to stop.
/// </para>
/// </summary>
public static class ClientAssetNaming
{
    /// <summary>
    /// How many characters may be missing from the front of a slug before a repair stops being a repair.
    /// Four: the observed damage is one character, and a limit this tight means the rule cannot reach
    /// across the catalogue to a slug that merely happens to end the same way.
    /// </summary>
    public const int MaxMissingPrefix = 4;

    /// <summary>The hero video. The folder holds an earlier take as well; it is not this.</summary>
    public const string HeroVideoFile = "hero.mp4";

    /// <summary>The frame shown on phones and under <c>prefers-reduced-motion</c>.</summary>
    public const string HeroPosterFile = "hero-poster.jpg";

    /// <summary>The full-width campaign banner (HOME-05).</summary>
    public const string CampaignFile = "campaign.jpg";

    /// <summary>The activewear feature (HOME-06).</summary>
    public const string ActivewearFile = "activewear.jpg";

    /// <summary>Extensions the drop uses. The list exists to undo a doubling, not to decide a type.</summary>
    private static readonly string[] MediaExtensions = [".jpg", ".jpeg", ".png", ".webp", ".mp4"];

    /// <summary>The folder name that has been observed running into a slug.</summary>
    private const string ProductsFolderPrefix = "products";

    /// <summary>
    /// Strips every trailing media extension from a filename, so <c>a.jpg.jpg</c> and <c>a.jpg</c> both
    /// come back as <c>a</c>, and reports how many came off.
    /// </summary>
    /// <returns>The stem, and the number of extensions removed (2 means the name was doubled).</returns>
    public static (string Stem, int ExtensionsStripped) StripMediaExtensions(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var stem = fileName;
        var stripped = 0;

        while (true)
        {
            var match = MediaExtensions.FirstOrDefault(
                extension => stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

            if (match is null || stem.Length == match.Length)
            {
                return (stem, stripped);
            }

            stem = stem[..^match.Length];
            stripped++;
        }
    }

    /// <summary>
    /// Resolves one file in <c>products/</c> to a product and a role.
    /// </summary>
    /// <param name="fileName">The file as it is on disk.</param>
    /// <param name="knownSlugs">
    /// Every published product slug. The repairs are bounded by this list, which is why none of them can
    /// produce a product that does not exist. <b>Matching ignores case</b>: a filename's capitalisation
    /// is not a fact about the product, and on Windows <c>Chino-Shorts-1.jpg</c> and
    /// <c>chino-shorts-1.jpg</c> are the same file — matching ordinally meant one capital letter aborted
    /// the whole run under a message saying the product did not exist. The slug that comes back is
    /// always the <b>catalogue's</b> spelling, never the filename's.
    /// </param>
    public static ProductAssetNameResult ParseProductFile(string fileName, IReadOnlySet<string> knownSlugs)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(knownSlugs);

        var (stem, _) = StripMediaExtensions(fileName);

        if (stem.Length == 0)
        {
            return new ProductAssetNameResult.Unmappable(fileName, "the name is nothing but an extension");
        }

        if (!TrySplitIndex(stem, out var spelled, out var index))
        {
            return new ProductAssetNameResult.Unmappable(
                fileName,
                $"'{stem}' does not end in a number. The contract numbers each garment's photographs from 1 "
                + "('<slug>-1.jpg' is the card image, '<slug>-2.jpg' the hover image).");
        }

        // The catalogue's spelling, not the filename's — so a row is never written with a slug that only
        // this one file happened to capitalise that way.
        if (Canonical(spelled, knownSlugs) is { } exact)
        {
            return new ProductAssetNameResult.Resolved(
                new ProductAssetName(fileName, exact, index, Repair(spelled, exact)));
        }

        // Repair 1: the folder name run into the slug.
        if (spelled.StartsWith(ProductsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            && spelled.Length > ProductsFolderPrefix.Length
            && Canonical(spelled[ProductsFolderPrefix.Length..], knownSlugs) is { } trimmed)
        {
            return new ProductAssetNameResult.Resolved(
                new ProductAssetName(fileName, trimmed, index, spelled));
        }

        // Repair 2: a character or three lost off the front, and exactly one slug it can be.
        var candidates = SlugsEndingWith(spelled, knownSlugs);

        if (candidates.Count == 1)
        {
            return new ProductAssetNameResult.Resolved(
                new ProductAssetName(fileName, candidates[0], index, spelled));
        }

        if (candidates.Count > 1)
        {
            return new ProductAssetNameResult.Unmappable(
                fileName,
                $"'{spelled}' is the end of {candidates.Count} product slugs "
                + $"({string.Join(", ", candidates)}). A near miss with more than one answer is a guess.");
        }

        return new ProductAssetNameResult.Unmappable(
            fileName,
            $"'{spelled}' is not a product slug, and no slug differs from it only by a missing prefix "
            + $"of up to {MaxMissingPrefix} characters.");
    }

    /// <summary>
    /// Pairs of slugs that differ only by a prefix short enough for <see cref="ParseProductFile"/>'s
    /// second repair to reach across — that is, pairs the repair could not tell apart if a filename lost
    /// its first few characters.
    /// <para>
    /// The repair is safe against today's catalogue, and nothing re-checks that when the client adds a
    /// product. This is the predicate the repair itself uses, exposed so a test can hold the real
    /// catalogue against it: an empty result is the guarantee that no truncated filename has two
    /// answers.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Shorter, string Longer)> AmbiguousPrefixPairs(IEnumerable<string> slugs)
    {
        ArgumentNullException.ThrowIfNull(slugs);

        var all = slugs.ToList();

        return all
            .SelectMany(
                shorter => all.Where(longer => IsPrefixedForm(shorter, longer)),
                (shorter, longer) => (Shorter: shorter, Longer: longer))
            .ToList();
    }

    /// <summary>Whether <paramref name="longer"/> is <paramref name="shorter"/> with a short prefix added.</summary>
    private static bool IsPrefixedForm(string shorter, string longer) =>
        longer.Length > shorter.Length
        && longer.Length - shorter.Length <= MaxMissingPrefix
        && longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase);

    private static List<string> SlugsEndingWith(string spelled, IEnumerable<string> knownSlugs) =>
        knownSlugs
            .Where(slug => IsPrefixedForm(spelled, slug))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The catalogue's spelling of a slug, or null when it has none.</summary>
    private static string? Canonical(string spelled, IEnumerable<string> knownSlugs) =>
        knownSlugs.FirstOrDefault(slug => string.Equals(slug, spelled, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What to report as the name the file actually carried, or null when it carried the right one.
    /// A difference of case counts: the operator should see every interpretation the importer made.
    /// </summary>
    private static string? Repair(string spelled, string resolved) =>
        string.Equals(spelled, resolved, StringComparison.Ordinal) ? null : spelled;

    /// <summary>
    /// The alt text a real photograph gets. It describes the garment, because that is what is in the
    /// picture — the demo placeholders said "Placeholder image …" and were telling the truth; this is
    /// the sentence that replaces them.
    /// </summary>
    /// <param name="productName">The product's name as the catalogue holds it.</param>
    /// <param name="index">1 for the card image, 2 for the hover image, higher for a further frame.</param>
    public static string ProductAltText(string productName, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);

        return index switch
        {
            1 => $"{productName}, front view",
            2 => $"{productName}, alternate view",
            _ => string.Create(CultureInfo.InvariantCulture, $"{productName}, view {index}"),
        };
    }

    /// <summary>The trending tile a <c>trending-N.jpg</c> fills, or <see langword="null"/>.</summary>
    public static int? TrendingIndexOf(string fileName) => IndexedFile(fileName, "trending");

    /// <summary>The social tile a <c>social-N.jpg</c> fills, or <see langword="null"/>.</summary>
    public static int? SocialIndexOf(string fileName) => IndexedFile(fileName, "social");

    /// <summary>Whether a filename is one of the fixed names in its folder, ignoring the extension.</summary>
    public static bool IsNamed(string fileName, string contractName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contractName);

        var (stem, _) = StripMediaExtensions(fileName);
        var (expected, _) = StripMediaExtensions(contractName);

        return string.Equals(stem, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int? IndexedFile(string fileName, string prefix)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var (stem, _) = StripMediaExtensions(fileName);

        if (!TrySplitIndex(stem, out var spelled, out var index)
            || !string.Equals(spelled, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return index;
    }

    /// <summary>Splits a trailing <c>-N</c> off a stem. <c>N</c> must be a positive decimal number.</summary>
    private static bool TrySplitIndex(string stem, [NotNullWhen(true)] out string? name, out int index)
    {
        name = null;
        index = 0;

        var dash = stem.LastIndexOf('-');
        if (dash <= 0 || dash == stem.Length - 1)
        {
            return false;
        }

        var digits = stem[(dash + 1)..];
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 1)
        {
            return false;
        }

        name = stem[..dash];
        return true;
    }
}
