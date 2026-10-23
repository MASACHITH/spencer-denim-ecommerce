namespace SpencerDenim.Application.Content.Abstractions;

/// <summary>
/// Reads the product page's two delivery and returns texts (<c>Content.PdpDeliveryInfoHtml</c> and
/// <c>Content.PdpReturnsInfoHtml</c>, doc 08 §3.8; issue #33) for an anonymous page.
/// <para>
/// A port of its own rather than <c>ISettingsStore</c>, which is the admin write port and hands back tracked
/// rows: this is a read for a public page, and it must never return a row flipped to secret.
/// </para>
/// </summary>
public interface IProductPageTextReader
{
    /// <summary>The two values as stored, either of which may be <see langword="null"/>.</summary>
    Task<ProductPageTexts> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The two stored texts, unsanitised as far as this type knows.</summary>
public sealed record ProductPageTexts(string? DeliveryInfoHtml, string? ReturnsInfoHtml);
