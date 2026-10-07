using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Payments;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The <c>Bank.*</c> field rules (PAY-01).
/// <para>
/// <b>The interesting half of this file is what it <i>accepts</i>.</b> Sri Lankan bank account numbers
/// have no single national format — length and grouping differ by bank — so a pattern rule would
/// eventually refuse a correct number typed by the only person who knows it, with no way round it from the
/// screen and a shop that cannot take money until somebody edits the database. Every refusal below is a
/// value that cannot be a bank detail at all.
/// </para>
/// </summary>
public sealed class BankDetailRulesTests
{
    [Theory]
    [InlineData("0000111122220")]
    [InlineData("0000 1111 2222")]
    [InlineData("8001-2345-6789")]
    [InlineData("001 002 3456 789")]
    [InlineData("12345678901234567890123456789012345678")]
    public void A_real_looking_account_number_is_accepted_as_typed(string value)
    {
        var errors = new List<ValidationError>();

        // **As typed, not normalised.** A shopper copies this into online banking, and showing it in the
        // grouping their bank prints is what stops a transposed digit. Stripping the spaces would be the
        // shop deciding it knows the format better than the client does.
        Assert.Equal(value, BankDetailRules.CleanAccountNumber(errors, "accountNumber", value));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("to be confirmed")]
    [InlineData("TBC")]
    [InlineData("ask accounts")]
    [InlineData("8001/2345/6789")]
    [InlineData("####")]
    [InlineData("- - -")]
    public void A_value_that_is_not_an_account_number_is_refused_and_names_the_field(string value)
    {
        var errors = new List<ValidationError>();

        Assert.Null(BankDetailRules.CleanAccountNumber(errors, "accountNumber", value));

        var error = Assert.Single(errors);
        Assert.Equal("accountNumber", error.Field);
    }

    [Fact]
    public void An_over_long_account_number_is_refused_rather_than_truncated()
    {
        var errors = new List<ValidationError>();
        var tooLong = new string('8', BankDetailRules.AccountNumberMaxLength + 1);

        // Refused, never clipped: a truncated account number is a valid-looking number for somebody
        // else's account, and the money would go there.
        Assert.Null(BankDetailRules.CleanAccountNumber(errors, "accountNumber", tooLong));
        Assert.Single(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_account_number_clears_the_setting_without_an_error(string? value)
    {
        var errors = new List<ValidationError>();

        // Blank *is* the clear action, and it has to be: "we have not got it yet" is a thing a client
        // needs to be able to say, and storing "" would make "has an account number" true for a shop that
        // has none — the order page would then print a blank line and ask a shopper to transfer to it.
        Assert.Null(BankDetailRules.CleanAccountNumber(errors, "accountNumber", value));
        Assert.Empty(errors);
    }

    [Fact]
    public void Text_is_trimmed_and_blank_becomes_null()
    {
        var errors = new List<ValidationError>();

        Assert.Equal(
            "Example Bank PLC",
            BankDetailRules.CleanText(errors, "bankName", "  Example Bank PLC  ", "bank name", 120));

        Assert.Null(BankDetailRules.CleanText(errors, "branch", "   ", "branch", 120));
        Assert.Empty(errors);
    }

    [Fact]
    public void A_line_break_survives_in_the_instructions_and_other_control_characters_do_not()
    {
        var errors = new List<ValidationError>();

        // The instructions are the one multi-line field and the order page keeps their line breaks, so a
        // newline is meaningful text rather than a control character to strip.
        Assert.Equal(
            "Deposits take a day.\nCall us if it is urgent.",
            BankDetailRules.CleanText(
                errors,
                "instructions",
                "Deposits take a day.\nCall us if it is urgent.",
                "instructions",
                600));

        Assert.Empty(errors);

        // A tab, a lone carriage return and a NUL are none of them something a person means to type into
        // a bank detail, and each of them travels invisibly into a public page.
        foreach (var smuggled in new[] { "Kollu\tpitiya", "Kollu\rpitiya", "Kollu\0pitiya" })
        {
            var refused = new List<ValidationError>();

            Assert.Null(BankDetailRules.CleanText(refused, "branch", smuggled, "branch", 120));
            Assert.Single(refused);
        }
    }

    [Fact]
    public void An_over_long_value_is_refused_and_says_the_limit()
    {
        var errors = new List<ValidationError>();

        Assert.Null(
            BankDetailRules.CleanText(errors, "bankName", new string('a', 121), "bank name", 120));

        var error = Assert.Single(errors);
        Assert.Contains("120", error.Message, StringComparison.Ordinal);
    }
}
