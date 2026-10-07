using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Sales.Payments;

/// <summary>
/// The field rules for the <c>Bank.*</c> settings (PAY-01), as pure functions — the shape
/// <c>SettingFieldRules</c> and <c>CatalogFieldRules</c> established.
/// <para>
/// <b>The allow-list is deliberately shallow, and that is the decision rather than an omission.</b> Sri
/// Lankan bank account numbers have no single national format: length and grouping differ by bank, and
/// some are quoted with the branch code attached. A pattern rule here would eventually refuse a correct
/// number typed by the only person who knows it, with no way round it from the screen and a shop that
/// cannot take money until somebody edits the database. So the rules refuse only what cannot be a bank
/// detail at all: an account number containing letters, anything containing a control character, and
/// anything so long it is clearly not the thing asked for.
/// </para>
/// <para>
/// <b>What is <i>not</i> relaxed is the character set.</b> These five values are printed on a customer's
/// order page, so a newline or a tab smuggled into the account number would let the client break the
/// page's layout by accident, and a control character in any of them would travel into the rendered HTML.
/// Line breaks are allowed in <see cref="CleanText"/>'s multi-line use (the instructions) and nowhere else.
/// </para>
/// </summary>
public static class BankDetailRules
{
    /// <summary>
    /// The characters an account number may be made of: digits, and the two separators banks print them
    /// with.
    /// </summary>
    /// <remarks>
    /// A space and a hyphen are kept rather than stripped, because an account number is <b>copied by a
    /// shopper into online banking</b> and showing it in the grouping their bank prints is what stops a
    /// transposed digit. Storing <c>0000 1111 2222</c> and showing <c>000011112222</c> would be the shop
    /// deciding it knows the format better than the client does.
    /// </remarks>
    private const string AccountNumberSeparators = " -";

    /// <summary>
    /// The longest account number worth accepting. IBAN's own maximum is 34 characters; adding room for
    /// separators and a branch code quoted alongside gives 40, which is wider than any Sri Lankan format
    /// and narrow enough that the value is still an account number rather than a sentence.
    /// </summary>
    public const int AccountNumberMaxLength = 40;

    /// <summary>
    /// Trims, turns blank into <see langword="null"/>, and refuses a control character or an over-long
    /// value.
    /// </summary>
    /// <remarks>
    /// Blank <i>is</i> the clear action, the rule <c>SettingFieldRules.Clean</c> states: "we have no branch
    /// to quote" and "the box is empty" are the same statement, and storing <c>""</c> would make "has a
    /// branch" true for a shop that has none.
    /// <para>
    /// A line break is allowed through, because the one multi-line field (<c>Bank.Instructions</c>) uses
    /// this method and its line breaks are meaningful — the order page preserves them. A tab, a carriage
    /// return on its own and every other control character are refused: none of them is something a person
    /// means to type into a bank detail, and each of them travels invisibly.
    /// </para>
    /// </remarks>
    public static string? CleanText(
        ICollection<ValidationError> errors,
        string field,
        string? value,
        string label,
        int maxLength)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();

        if (cleaned.Length > maxLength)
        {
            errors.Add(new ValidationError(
                field,
                $"Keep the {label} to {maxLength} characters or fewer."));

            return null;
        }

        if (cleaned.Any(character => char.IsControl(character) && character != '\n'))
        {
            errors.Add(new ValidationError(
                field,
                $"The {label} contains a character that cannot be shown to a customer. Retype it, or "
                + "paste it as plain text."));

            return null;
        }

        return cleaned;
    }

    /// <summary>
    /// The account number: digits, spaces and hyphens, with at least one digit.
    /// </summary>
    /// <remarks>
    /// <b>"At least one digit" is the whole of the format rule, and it is there to catch the mistake that
    /// actually happens</b> — a client pasting a sentence, a bank name or a placeholder like "TBC" into the
    /// box. Everything beyond that is the bank's business, not this application's; see the class remarks.
    /// </remarks>
    public static string? CleanAccountNumber(
        ICollection<ValidationError> errors,
        string field,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();

        if (cleaned.Length > AccountNumberMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                $"An account number cannot be longer than {AccountNumberMaxLength.ToString(CultureInfo.InvariantCulture)} "
                + "characters. Check what you pasted."));

            return null;
        }

        if (!cleaned.All(character =>
                char.IsAsciiDigit(character) || AccountNumberSeparators.Contains(character, StringComparison.Ordinal)))
        {
            errors.Add(new ValidationError(
                field,
                "An account number can only hold digits, spaces and dashes. Enter the bank's name in the "
                + "bank name box and anything else in the instructions."));

            return null;
        }

        if (!cleaned.Any(char.IsAsciiDigit))
        {
            errors.Add(new ValidationError(
                field,
                "Enter the account number customers should transfer to."));

            return null;
        }

        return cleaned;
    }
}
