using SpencerDenim.Application.Catalog.Products;

namespace SpencerDenim.Application.Content;

/// <summary>
/// Turns the storefront listing URL a client typed into a section's settings — <c>/new-arrivals</c>,
/// <c>/women/jeans?sort=newest&amp;colour=navy</c> — into the criteria the catalogue read store
/// already understands.
/// <para>
/// This is what makes a homepage product row honest: the row and its "view all" link are the same
/// query, so what a shopper sees on the homepage is what they find when they click through. Nothing
/// here validates: the values go to <see cref="ProductListQuery.Create"/>, which is the one place
/// that decides whether a filter is usable, so a homepage row and a listing page cannot disagree
/// about what <c>?minPrice=abc</c> means.
/// </para>
/// </summary>
public static class ListingUrl
{
    /// <summary>The two department-less listings the header links to, which have no slug of their own.</summary>
    private const string NewArrivalsPath = "new-arrivals";

    private const string SalePath = "sale";

    /// <summary>
    /// Reads a listing URL into product-list criteria.
    /// <para>
    /// The path contributes the department and category the same way the router does
    /// (<c>/women/jeans</c>, <c>/kids/girls/girls-dresses</c>), and <c>/new-arrivals</c> and
    /// <c>/sale</c> contribute their fixed filter instead, exactly as the listing page's own
    /// <c>ListingMode</c> does. The query string contributes everything else.
    /// </para>
    /// </summary>
    /// <param name="url">The URL from <c>SettingsJson</c>, or null.</param>
    /// <param name="pageSize">How many rows the section wants.</param>
    /// <returns>Criteria for <see cref="ProductListQuery.Create"/>, or null when there is no URL.</returns>
    public static ProductListCriteria? ToCriteria(string? url, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var split = url.IndexOf('?', StringComparison.Ordinal);
        var path = split < 0 ? url : url[..split];
        var query = split < 0 ? string.Empty : url[(split + 1)..];

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        string? department = null;
        string? category = null;
        string? newArrivals = null;
        string? onSale = null;

        if (segments.Count > 0 && segments[0].Equals(NewArrivalsPath, StringComparison.OrdinalIgnoreCase))
        {
            newArrivals = "true";
        }
        else if (segments.Count > 0 && segments[0].Equals(SalePath, StringComparison.OrdinalIgnoreCase))
        {
            onSale = "true";
        }
        else if (segments.Count > 0)
        {
            department = segments[0];
            // The deepest segment, because the API filters on the category and returns its whole
            // subtree — the same rule the listing page's routes follow for /:d/:c/:sub.
            category = segments.Count > 1 ? segments[^1] : null;
        }

        var parameters = QueryString.Parse(query);

        return new ProductListCriteria(
            Department: department ?? First(parameters, "department"),
            Category: category ?? First(parameters, "category"),
            AgeGroups: All(parameters, "ageGroup"),
            MinPrice: First(parameters, "minPrice"),
            MaxPrice: First(parameters, "maxPrice"),
            Sizes: All(parameters, "size"),
            Colours: All(parameters, "colour"),
            Materials: All(parameters, "material"),
            Fits: All(parameters, "fit"),
            Occasions: All(parameters, "occasion"),
            MinRating: First(parameters, "minRating"),
            InStock: First(parameters, "inStock"),
            OnSale: onSale ?? First(parameters, "onSale"),
            MinDiscountPercent: First(parameters, "minDiscountPercent"),
            NewArrivals: newArrivals ?? First(parameters, "newArrivals"),
            Sort: First(parameters, "sort"),
            // The homepage always shows the first page of the listing. A "page" in the settings
            // would mean a row that starts in the middle of a listing nobody scrolled.
            Page: "1",
            PageSize: pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? First(IReadOnlyList<KeyValuePair<string, string>> parameters, string name)
    {
        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }

        return null;
    }

    private static List<string>? All(IReadOnlyList<KeyValuePair<string, string>> parameters, string name)
    {
        List<string>? values = null;

        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                (values ??= []).Add(parameter.Value);
            }
        }

        return values;
    }
}

/// <summary>
/// A query-string reader for the Application layer, which has no web framework to borrow one from.
/// Repeatable keys are kept in order, because the listing filters are repeatable.
/// </summary>
internal static class QueryString
{
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        var parameters = new List<KeyValuePair<string, string>>();

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);

            if (key.Length > 0)
            {
                parameters.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return parameters;
    }

    /// <summary>
    /// Percent-decoding, with <c>+</c> read as a space the way a form-encoded query writes one.
    /// A malformed escape (<c>%zz</c>) is left as typed rather than thrown: the value then fails
    /// validation with a message naming the parameter, which is more use than a 500.
    /// </summary>
    private static string Decode(string value)
    {
        var plussed = value.Replace('+', ' ');

        try
        {
            return Uri.UnescapeDataString(plussed);
        }
        catch (UriFormatException)
        {
            return plussed;
        }
    }
}
