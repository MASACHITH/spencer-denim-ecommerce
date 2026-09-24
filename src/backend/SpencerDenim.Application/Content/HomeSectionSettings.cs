using System.Text.Json;

namespace SpencerDenim.Application.Content;

/// <summary>How a product-backed row decides what to show (<c>content.HomeSections.SettingsJson</c>).</summary>
public enum HomeSectionMode
{
    /// <summary>The row is drawn from a listing, live. The default.</summary>
    Auto = 0,

    /// <summary>The row shows the section's own items, exactly as the client arranged them.</summary>
    Manual = 1,
}

/// <summary>
/// <c>content.HomeSections.SettingsJson</c>, read (doc 08 §3.7: "per type — mode auto/manual, count,
/// listingUrl, overlay, CTA").
/// <para>
/// Parsed defensively on purpose (lesson L1). This column is typed by a non-developer through the
/// admin screens of ADM-17, and one malformed value must cost that section's *settings*, never the
/// homepage. A key of the wrong type, a count of <c>"eight"</c>, a truncated document, a document
/// that is not an object at all: every one of them falls back to the defaults below and the section
/// still renders.
/// </para>
/// </summary>
public sealed record HomeSectionSettings
{
    /// <summary>Most products one row may pull, whatever the JSON asks for.</summary>
    public const int MaxCount = 24;

    /// <summary>What a row shows when the client has not said (HOME-03 and HOME-07 are eight-up).</summary>
    public const int DefaultCount = 8;

    /// <summary>Longest <c>listingUrl</c> accepted, matching <c>HomeSectionItems.LinkUrl</c>.</summary>
    public const int MaxLinkLength = 500;

    /// <summary>The settings a section has when <c>SettingsJson</c> is null, blank or unreadable.</summary>
    public static HomeSectionSettings Default { get; } = new();

    public HomeSectionMode Mode { get; private init; } = HomeSectionMode.Auto;

    /// <summary>How many products a product-backed row shows. Between 1 and <see cref="MaxCount"/>.</summary>
    public int Count { get; private init; } = DefaultCount;

    /// <summary>
    /// The listing a product row is drawn from and links to, as a storefront path with an optional
    /// query string — <c>/new-arrivals</c>, <c>/women/jeans?sort=newest</c>. Null when the client
    /// has not set one.
    /// </summary>
    public string? ListingUrl { get; private init; }

    /// <summary>Label on the section's own link, e.g. "Shop new arrivals".</summary>
    public string? LinkLabel { get; private init; }

    /// <summary>
    /// How copy sits over artwork: <c>dark</c> (a dark fade under light text, docs/04 decision 7),
    /// <c>light</c>, or <c>none</c>. Anything else is read as null and the storefront picks.
    /// </summary>
    public string? Overlay { get; private init; }

    /// <summary>Reads the column. Never throws, and never returns null.</summary>
    public static HomeSectionSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return Default;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            // The document is disposed at the end of this scope, so anything kept has to be a value
            // type or a string, which is all this record holds.
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Default;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }

        return new HomeSectionSettings
        {
            Mode = ReadMode(root),
            Count = ReadCount(root),
            ListingUrl = ReadPath(root, "listingUrl"),
            LinkLabel = ReadText(root, "linkLabel", maxLength: 40),
            Overlay = ReadOverlay(root),
        };
    }

    private static HomeSectionMode ReadMode(JsonElement root) =>
        ReadText(root, "mode", maxLength: 10)?.ToLowerInvariant() switch
        {
            "manual" => HomeSectionMode.Manual,
            _ => HomeSectionMode.Auto,
        };

    /// <summary>
    /// Accepts a JSON number and a numeric string, because an admin form posts <c>"8"</c> as often
    /// as it posts <c>8</c>. Out of range is clamped rather than rejected: a row of 100 products is
    /// a typo, and showing 24 is closer to what was meant than showing none.
    /// </summary>
    private static int ReadCount(JsonElement root)
    {
        if (!root.TryGetProperty("count", out var value))
        {
            return DefaultCount;
        }

        var count = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => DefaultCount,
        };

        return Math.Clamp(count, 1, MaxCount);
    }

    private static string? ReadOverlay(JsonElement root) =>
        ReadText(root, "overlay", maxLength: 10)?.ToLowerInvariant() switch
        {
            "dark" => "dark",
            "light" => "light",
            "none" => "none",
            _ => null,
        };

    /// <summary>
    /// A link the storefront will put in an <c>href</c>. Only a site-relative path is accepted —
    /// one leading slash and no second one, which also rules out <c>//evil.example</c> — so a
    /// content row can never redirect a shopper off the store, and never carries a
    /// <c>javascript:</c> scheme into the DOM.
    /// </summary>
    private static string? ReadPath(JsonElement root, string property)
    {
        var value = ReadText(root, property, MaxLinkLength);

        if (value is null || value.Length < 1 || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        return value;
    }

    private static string? ReadText(JsonElement root, string property, int maxLength)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();

        return string.IsNullOrEmpty(text) || text.Length > maxLength ? null : text;
    }
}
