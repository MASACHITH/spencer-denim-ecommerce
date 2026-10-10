using System.Text;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The one way free text is made fit to send to PayHere or to keep from PayHere.
/// </summary>
internal static class PayHereText
{
    /// <summary>
    /// Control characters and runs of whitespace collapsed to single spaces, trimmed, clipped to
    /// <paramref name="maxLength"/> without splitting a surrogate pair. <see langword="null"/> when nothing is
    /// left, so the caller's fallback applies.
    /// </summary>
    public static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length + 1 >= maxLength)
                {
                    break;
                }

                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);

            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        // A high surrogate left at the end is half a character.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        var clipped = builder.ToString().Trim();

        return clipped.Length == 0 ? null : clipped;
    }
}
