using System.Text;

namespace SpencerDenim.Domain.Common;

/// <summary>
/// The characters that make text shown to other people display differently from what it says (issue #189, D1/D2):
/// control characters (C0, DEL, C1), the bidirectional embeddings, overrides, isolates and marks ("Trojan
/// Source"), the Unicode line and paragraph separators, and unpaired surrogates (which SQL Server stores as
/// garbage). One definition, used by review text, review display names and customer names.
/// </summary>
public static class PlainText
{
    /// <summary>U+2028 LINE SEPARATOR.</summary>
    public const char LineSeparator = (char)0x2028;

    /// <summary>U+2029 PARAGRAPH SEPARATOR.</summary>
    public const char ParagraphSeparator = (char)0x2029;

    /// <summary>U+0085 NEXT LINE, a C1 control that is also a line break.</summary>
    public const char NextLine = '\u0085';

    /// <summary>A bidirectional formatting character: U+202A–U+202E, U+2066–U+2069, U+200E, U+200F, U+061C.</summary>
    public static bool IsBidiControl(char character) =>
        character is (>= (char)0x202A and <= (char)0x202E) or (>= (char)0x2066 and <= (char)0x2069) or (char)0x200E or (char)0x200F or (char)0x061C;

    /// <summary>A line break other than <c>\n</c> and <c>\r</c>: U+0085, U+2028, U+2029.</summary>
    public static bool IsUnicodeLineBreak(char character) =>
        character is NextLine or LineSeparator or ParagraphSeparator;

    /// <summary>
    /// Whether <paramref name="text"/> holds anything a single-line name must not: any control character (tab and
    /// line breaks included), a bidi control, a Unicode line break, or an unpaired surrogate.
    /// </summary>
    public static bool HasUnsafeForOneLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
                continue;
            }

            if (char.IsSurrogate(character) || char.IsControl(character) || IsBidiControl(character) || IsUnicodeLineBreak(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="text"/> with every character <see cref="HasUnsafeForOneLine"/> objects to removed (a
    /// whitespace one becomes a space). For text
    /// saved before a rule existed (a profile name) that is shown to other people.
    /// </summary>
    public static string StripUnsafeForOneLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[index + 1]);
                index++;
                continue;
            }

            if (char.IsSurrogate(character) || char.IsControl(character) || IsBidiControl(character) || IsUnicodeLineBreak(character))
            {
                // A tab or a line break between two words still separates them; anything else simply goes.
                if (char.IsWhiteSpace(character))
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
