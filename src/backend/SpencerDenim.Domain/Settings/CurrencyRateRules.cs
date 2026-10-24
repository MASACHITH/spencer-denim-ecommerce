using System.Globalization;

namespace SpencerDenim.Domain.Settings;

/// <summary>Why a typed or stored rate is not a rate (issue #248).</summary>
public enum CurrencyRateProblem
{
    /// <summary>It is a rate.</summary>
    None = 0,

    /// <summary>Not digits with at most one decimal point and digits after it: a sign, a space, an exponent, a comma or text.</summary>
    NotANumber = 1,

    /// <summary>More than <see cref="SettingKeys.CurrencyDisplay.MaxDecimalPlaces"/> places after the point.</summary>
    TooManyDecimals = 2,

    /// <summary>Zero, or above <see cref="SettingKeys.CurrencyDisplay.MaxRate"/>, or too large to hold.</summary>
    OutOfRange = 3,
}

/// <summary>
/// What counts as a display rate (issue #248): <b>one definition, used by the save and by the public feed</b>, so a
/// value the form would refuse today can never be published because an earlier build, a database edit or a looser
/// rule once let it in.
/// <para>
/// <b>Strictly invariant digits.</b> The grammar is digits, optionally one point and more digits. Everything
/// <see cref="decimal.TryParse(string, NumberStyles, IFormatProvider, out decimal)"/> would forgive is refused on
/// purpose: a sign (a negative rate), an exponent (<c>1e3</c> looks harmless and is not what anyone meant), a
/// thousands separator (<c>1,000</c> is a thousand in one country and one in another), whitespace inside the number,
/// and a bare point (<c>.5</c>, <c>5.</c>) that reads differently in different locales. The ends of the text are
/// trimmed by the caller, as every settings save does; this class sees the number.
/// </para>
/// </summary>
public static class CurrencyRateRules
{
    /// <summary>
    /// Checks <paramref name="text"/> against the rules and, when it passes, returns the rate.
    /// </summary>
    public static CurrencyRateProblem Check(string? text, out decimal rate)
    {
        rate = 0m;

        if (string.IsNullOrEmpty(text))
        {
            return CurrencyRateProblem.NotANumber;
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];

        if (whole.Length == 0 || !AllAsciiDigits(whole) || (point >= 0 && (fraction.Length == 0 || !AllAsciiDigits(fraction))))
        {
            return CurrencyRateProblem.NotANumber;
        }

        if (fraction.Length > SettingKeys.CurrencyDisplay.MaxDecimalPlaces)
        {
            return CurrencyRateProblem.TooManyDecimals;
        }

        // Digits only by now, so the only way this fails is a figure too large for a decimal: out of range.
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= SettingKeys.CurrencyDisplay.MinRateExclusive
            || parsed > SettingKeys.CurrencyDisplay.MaxRate)
        {
            return CurrencyRateProblem.OutOfRange;
        }

        rate = parsed;
        return CurrencyRateProblem.None;
    }

    /// <summary>
    /// A stored value as a rate, or <see langword="null"/> when it is empty or would not pass <see cref="Check"/>.
    /// Never throws: a bad row means "this currency is not offered", not an error on a public page.
    /// </summary>
    public static decimal? TryReadStored(string? value) =>
        Check(value?.Trim(), out var rate) == CurrencyRateProblem.None ? rate : null;

    /// <summary>
    /// The form a rate is stored in: invariant, no trailing zeros, no trailing point. <c>399.50</c> and
    /// <c>399.5</c> are the same rate, and the audit trail's "did this change" must say so.
    /// </summary>
    public static string Canonical(decimal rate) =>
        rate.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool AllAsciiDigits(string text)
    {
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
