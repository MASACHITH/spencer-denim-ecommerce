namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// Doc 08 §3.3: the database stores a relative storage key; the public URL is built from
/// configuration, so the same rows work behind Caddy today and behind Azure Blob or a CDN later
/// (doc 03 §5: "no code may depend on the host").
/// </summary>
internal static class MediaUrlBuilder
{
    /// <summary>Joins the configured media base URL and a storage key, with exactly one slash between them.</summary>
    public static string? ToPublicUrl(string? baseUrl, string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return null;
        }

        var key = storageKey.Trim().TrimStart('/');

        // An absolute URL in the key (a remote placeholder image) is already a public URL.
        if (Uri.TryCreate(storageKey, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return storageKey;
        }

        var prefix = (baseUrl ?? string.Empty).TrimEnd('/');
        return prefix.Length == 0 ? "/" + key : prefix + "/" + key;
    }
}
