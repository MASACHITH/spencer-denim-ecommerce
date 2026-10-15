using SpencerDenim.Application.Admin.Customers;

namespace SpencerDenim.UnitTests.Application.Admin.Customers;

public sealed class AdminCustomerSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_is_no_search(string? raw) => Assert.Null(AdminCustomerSearch.Classify(raw));

    [Theory]
    [InlineData("nimali@", "NIMALI@")]
    [InlineData("  Nimali@Example.com ", "NIMALI@EXAMPLE.COM")]
    public void An_at_sign_makes_it_an_upper_cased_email_prefix(string raw, string expected)
    {
        var term = AdminCustomerSearch.Classify(raw);

        Assert.Equal(AdminCustomerSearchKind.Email, term!.Kind);
        Assert.Equal(expected, term.Value);
    }

    [Theory]
    [InlineData("0771234567", "771234567")]
    [InlineData("077 123 4567", "771234567")]
    [InlineData("+94 77-123-4567", "771234567")]
    [InlineData("1234567", "1234567")]
    public void Digits_and_separators_are_a_telephone_number_by_its_last_nine_digits(string raw, string expected)
    {
        var term = AdminCustomerSearch.Classify(raw);

        Assert.Equal(AdminCustomerSearchKind.Phone, term!.Kind);
        Assert.Equal(expected, term.Value);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("Perera")]
    [InlineData("Nimali Perera")]
    [InlineData("flat 0771234567")]
    [InlineData("නිමාලි")]
    public void Anything_else_is_part_of_a_name(string raw)
    {
        var term = AdminCustomerSearch.Classify(raw);

        Assert.Equal(AdminCustomerSearchKind.Name, term!.Kind);
        Assert.Equal(raw.Trim(), term.Value);
    }

    [Fact]
    public void A_long_term_is_cut_not_refused()
    {
        var term = AdminCustomerSearch.Classify(new string('a', 500));

        Assert.Equal(AdminCustomerSearch.MaxLength, term!.Value.Length);
    }

    [Theory]
    [InlineData(null, null, 1, 25)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 101, 1, 100)]
    [InlineData(3, 100000, 3, 100)]
    public void Paging_is_clamped(int? page, int? pageSize, int expectedPage, int expectedSize) =>
        Assert.Equal((expectedPage, expectedSize), AdminCustomerPaging.Clamp(page, pageSize));
}
