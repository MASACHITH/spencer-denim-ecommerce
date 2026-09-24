using System.Globalization;

namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// The one place that decides whether a string is an address this store will write into
/// <c>marketing.NewsletterSubscribers</c>, and what its normalised form is.
/// <para>
/// The check is deliberately structural rather than RFC 5322 complete: a signup form cannot know
/// whether an address exists, only whether it could. Anything that passes here is still just a
/// claim, which is why doc 08 §3.6 records the consent time and IP alongside it.
/// </para>
/// </summary>
public static class NewsletterEmail
{
    /// <summary><c>marketing.NewsletterSubscribers.Email nvarchar(256)</c>.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Trims and validates an address. <paramref name="email"/> keeps the case the visitor typed —
    /// the local part of an address is case-sensitive to some mail servers — and
    /// <paramref name="normalised"/> is the upper-case form the unique index is built on, matching
    /// what ASP.NET Core Identity stores in <c>NormalizedEmail</c> so the two tables agree.
    /// </summary>
    public static bool TryNormalise(string? raw, out string email, out string normalised)
    {
        email = string.Empty;
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength || !IsPlausible(trimmed))
        {
            return false;
        }

        email = trimmed;
        normalised = trimmed.ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// Exactly one <c>@</c>, something either side of it, a dot in the domain with at least two
    /// characters after it, and no whitespace or control characters anywhere.
    /// </summary>
    private static bool IsPlausible(string candidate)
    {
        foreach (var character in candidate)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        var at = candidate.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != candidate.LastIndexOf('@') || at == candidate.Length - 1)
        {
            return false;
        }

        var domain = candidate[(at + 1)..];
        var dot = domain.LastIndexOf('.');

        return dot > 0
            && domain.Length - dot > 2
            && !domain.Contains("..", StringComparison.Ordinal)
            && !domain.StartsWith('.')
            && !domain.EndsWith('.');
    }

    /// <summary>A masked form for logs and support, so an address never lands in a log file whole.</summary>
    public static string Mask(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "***";
        }

        var local = email[..at];
        var kept = local.Length <= 2 ? local[..1] : local[..2];

        return string.Create(CultureInfo.InvariantCulture, $"{kept}***{email[at..]}");
    }
}
