using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Sales.Payments.PayPal.Admin;

/// <summary>
/// The field rules for the PayPal settings (PAY-02), as pure functions — the shape
/// <see cref="BankDetailRules"/> and <see cref="SettingFieldRules"/> established.
/// <para>
/// <b>Strict about what each value is, because each one reaches PayPal or decides what a customer is
/// charged.</b> A client id with a stray space is a credential PayPal refuses with an opaque 401; a rate with
/// a misplaced decimal point charges a customer a few cents for a whole order. So the rules refuse what
/// cannot be that value at all — and every one returns the <i>normalised</i> text that will be stored
/// (invariant culture, fixed decimals), so the same number typed two ways is one value and not a spurious
/// change in the audit trail.
/// </para>
/// </summary>
public static class PayPalSettingRules
{
    /// <summary>A client id is a 80-odd character token; this leaves room.</summary>
    public const int ClientIdMaxLength = 200;

    /// <summary>A client secret is similar in size; this leaves room.</summary>
    public const int SecretMaxLength = 300;

    /// <summary>A webhook id is about 24 characters.</summary>
    public const int WebhookIdMaxLength = 60;

    /// <summary>
    /// Validates and normalises one field. <paramref name="key"/> picks the rule. Returns the value to
    /// store, or <see langword="null"/> to clear the key (also returned, with an error recorded, when the
    /// value is refused — the caller returns the errors without using it).
    /// </summary>
    public static string? Clean(ICollection<ValidationError> errors, string key, string field, string? raw)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var text = SettingFieldRules.Clean(raw);

        if (key == SettingKeys.PayPal.Mode)
        {
            return CleanMode(errors, field, text);
        }

        if (key == SettingKeys.PayPal.ClientId)
        {
            return CleanToken(errors, field, text, ClientIdMaxLength, "client ID", allowDot: true);
        }

        if (key == SettingKeys.PayPal.WebhookId)
        {
            return CleanToken(errors, field, text, WebhookIdMaxLength, "webhook ID", allowDot: false);
        }

        if (key == SettingKeys.PayPal.LkrPerUsd)
        {
            return CleanRate(errors, field, text);
        }

        if (key == SettingKeys.PayPal.FxBufferPercent)
        {
            return CleanBuffer(errors, field, text);
        }

        if (key == SettingKeys.PayPal.RateMaxAgeDays)
        {
            return CleanInt(
                errors,
                field,
                text,
                SettingKeys.PayPal.RateMaxAgeDaysMin,
                SettingKeys.PayPal.RateMaxAgeDaysMax,
                "days");
        }

        if (key == SettingKeys.PayPal.CaptureLockHours)
        {
            return CleanInt(
                errors,
                field,
                text,
                SettingKeys.PayPal.CaptureLockHoursMin,
                SettingKeys.PayPal.CaptureLockHoursMax,
                "hours");
        }

        if (key == SettingKeys.PayPal.CaptureRecoveryMaxHours)
        {
            return CleanInt(
                errors,
                field,
                text,
                SettingKeys.PayPal.CaptureRecoveryMaxHoursMin,
                SettingKeys.PayPal.CaptureRecoveryMaxHoursMax,
                "hours");
        }

        if (key == SettingKeys.PayPal.PendingCaptureMaxDays)
        {
            return CleanInt(
                errors,
                field,
                text,
                SettingKeys.PayPal.PendingCaptureMaxDaysMin,
                SettingKeys.PayPal.PendingCaptureMaxDaysMax,
                "days");
        }

        if (key == SettingKeys.PayPal.ExpiryMinutes)
        {
            return CleanInt(
                errors,
                field,
                text,
                SettingKeys.PayPal.ExpiryMinutesMin,
                SettingKeys.PayPal.ExpiryMinutesMax,
                "minutes");
        }

        throw new ArgumentOutOfRangeException(nameof(key), key, "Not a PayPal setting this command writes.");
    }

    /// <summary>
    /// The secret as typed, or null to keep the stored one. Blank is "keep", not "clear" — a secret is
    /// write-only, so the screen cannot send back what it was never told.
    /// </summary>
    public static string? CleanSecret(ICollection<ValidationError> errors, string field, string? raw)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Not trimmed into shape: a secret with a space inside it is not a secret PayPal issued, and silently
        // removing characters would store something that works nowhere. Surrounding whitespace is the one
        // thing a paste adds, so that alone is forgiven.
        var secret = raw.Trim();

        if (secret.Length > SecretMaxLength || secret.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            errors.Add(new ValidationError(
                field,
                "That does not look like a PayPal secret. Copy it again from the PayPal dashboard without any spaces."));

            return null;
        }

        return secret;
    }

    private static string? CleanMode(ICollection<ValidationError> errors, string field, string? text)
    {
        var mode = text?.ToLowerInvariant();

        if (mode is SettingKeys.PayPal.ModeSandbox or SettingKeys.PayPal.ModeLive)
        {
            return mode;
        }

        errors.Add(new ValidationError(field, "Choose sandbox or live."));

        return null;
    }

    private static string? CleanToken(
        ICollection<ValidationError> errors,
        string field,
        string? text,
        int maxLength,
        string label,
        bool allowDot)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Length > maxLength
            || text.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' || (allowDot && character == '.'))))
        {
            errors.Add(new ValidationError(
                field,
                $"That does not look like a PayPal {label}. Copy it again from the PayPal dashboard: it is made "
                + "of letters, digits, hyphens and underscores only."));

            return null;
        }

        return text;
    }

    private static string? CleanRate(ICollection<ValidationError> errors, string field, string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
            || rate <= 0m
            || rate > SettingKeys.PayPal.LkrPerUsdMax
            || DecimalPlaces(rate) > SettingKeys.PayPal.LkrPerUsdScale)
        {
            errors.Add(new ValidationError(
                field,
                $"Enter how many rupees one US dollar costs, as a number above 0 and up to "
                + $"{SettingKeys.PayPal.LkrPerUsdMax.ToString("0", CultureInfo.InvariantCulture)}, with at most "
                + $"{SettingKeys.PayPal.LkrPerUsdScale} decimal places — for example 300.00."));

            return null;
        }

        return rate.ToString("0.00####", CultureInfo.InvariantCulture);
    }

    private static string? CleanBuffer(ICollection<ValidationError> errors, string field, string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent)
            || percent < 0m
            || percent > SettingKeys.PayPal.FxBufferPercentMax
            || DecimalPlaces(percent) > 2)
        {
            errors.Add(new ValidationError(
                field,
                $"Enter a percentage between 0 and "
                + $"{SettingKeys.PayPal.FxBufferPercentMax.ToString("0", CultureInfo.InvariantCulture)}, with at "
                + "most two decimal places — for example 3.00."));

            return null;
        }

        return percent.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string? CleanInt(
        ICollection<ValidationError> errors,
        string field,
        string? text,
        int min,
        int max,
        string unit)
    {
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < min
            || value > max)
        {
            errors.Add(new ValidationError(
                field,
                $"Enter a whole number of {unit} between {min} and {max}."));

            return null;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The number of digits after the decimal point, trailing zeros not counted.</summary>
    private static int DecimalPlaces(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.', StringComparison.Ordinal);

        return dot < 0 ? 0 : text[(dot + 1)..].TrimEnd('0').Length;
    }
}
