using Microsoft.Extensions.Options;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

/// <summary>The protected-account list (<c>AdminSecurity__ProtectedStaffEmails</c>): how it is read, and what start-up refuses.</summary>
public sealed class ConfiguredProtectedStaffAccountsTests
{
    private static ConfiguredProtectedStaffAccounts For(string? configured, string? guarded = null) =>
        new(Options.Create(new AdminSecurityOptions { ProtectedStaffEmails = configured ?? string.Empty, GuardedStaffEmails = guarded ?? string.Empty }));

    [Fact]
    public void Nothing_is_protected_until_a_server_names_an_account()
    {
        var accounts = For(null);

        Assert.False(accounts.IsProtected("vendor@example.com"));
        Assert.False(accounts.IsProtected(""));
        Assert.False(accounts.IsProtected(null));
    }

    [Theory]
    [InlineData("vendor@example.com")]
    [InlineData("Vendor@Example.COM")]
    [InlineData("  vendor@example.com  ")]
    public void An_address_matches_whatever_its_case_and_surrounding_spaces(string typed)
    {
        Assert.True(For("vendor@example.com").IsProtected(typed));
    }

    [Fact]
    public void A_different_address_is_not_protected_even_when_it_contains_the_protected_one()
    {
        var accounts = For("vendor@example.com");

        Assert.False(accounts.IsProtected("other@example.com"));
        Assert.False(accounts.IsProtected("not-vendor@example.com"));
        Assert.False(accounts.IsProtected("vendor@example.com.au"));
    }

    [Theory]
    [InlineData("a@example.com,b@example.com,c@example.com")]
    [InlineData("a@example.com; b@example.com; c@example.com")]
    [InlineData("a@example.com b@example.com\r\nc@example.com")]
    [InlineData(" ,a@example.com,, b@example.com;;c@example.com; ")]
    public void Addresses_may_be_separated_by_commas_semicolons_or_white_space(string configured)
    {
        var accounts = For(configured);

        Assert.True(accounts.IsProtected("a@example.com"));
        Assert.True(accounts.IsProtected("b@example.com"));
        Assert.True(accounts.IsProtected("c@example.com"));
        Assert.False(accounts.IsProtected("d@example.com"));
    }

    [Fact]
    public void Nothing_is_guarded_until_a_server_names_an_account()
    {
        var accounts = For("owner@example.com");

        Assert.False(accounts.IsGuarded("client@example.com"));
        Assert.False(accounts.IsGuarded(""));
        Assert.False(accounts.IsGuarded(null));
    }

    [Theory]
    [InlineData("client@example.com")]
    [InlineData("Client@Example.COM")]
    [InlineData("  client@example.com  ")]
    public void A_guarded_address_matches_whatever_its_case_and_surrounding_spaces(string typed)
    {
        Assert.True(For("owner@example.com", guarded: "client@example.com").IsGuarded(typed));
    }

    [Fact]
    public void The_two_lists_are_separate_so_guarding_an_account_does_not_protect_it_and_the_reverse()
    {
        var accounts = For("owner@example.com", guarded: "client@example.com");

        Assert.True(accounts.IsProtected("owner@example.com"));
        Assert.False(accounts.IsGuarded("owner@example.com"));
        Assert.True(accounts.IsGuarded("client@example.com"));
        Assert.False(accounts.IsProtected("client@example.com"));
        Assert.False(accounts.IsGuarded("someone@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vendor@example.com")]
    [InlineData("a@example.com, b@example.com")]
    public void Start_up_accepts_an_empty_list_and_well_formed_addresses(string? configured)
    {
        Assert.True(ConfiguredProtectedStaffAccounts.AllEntriesLookLikeEmails(configured));
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("vendor@")]
    [InlineData("@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("a@example.com, typo")]
    public void Start_up_refuses_an_entry_that_is_not_an_address_because_a_typo_would_leave_the_account_unprotected(string configured)
    {
        Assert.False(ConfiguredProtectedStaffAccounts.AllEntriesLookLikeEmails(configured));
    }
}
