using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

/// <summary>
/// The password rule Identity itself enforces on every path that sets a password (issue #200).
/// </summary>
/// <remarks>
/// <b>Why this is the strongest test of the rule that needs no database.</b> Creating a staff user, a staff
/// member changing their own password, the seeded first Super Admin and a customer's register / change /
/// reset all end in <c>UserManager</c>'s validators. So what the <i>registered</i> validators do with a
/// password is the rule, and the HTTP tests in the integration suite then prove each route really reaches
/// them. A rule asserted only on a policy class would pass while <c>IdentityOptions</c> still said six,
/// which is exactly how staff passwords stayed at Identity's default after customers moved to ten.
/// <para>
/// <b>Every password here satisfies all four character classes</b> and differs from the next only in its
/// length. A password that also lacked a symbol would be refused whatever the minimum was, and a test that
/// passes for the wrong reason is how a mutation back to six would go unnoticed.
/// </para>
/// </remarks>
public sealed class PasswordRuleWiringTests
{
    /// <summary>
    /// A password with an upper-case letter, a lower-case letter, a digit and a symbol, and exactly
    /// <paramref name="length"/> characters.
    /// </summary>
    private static string StrongPasswordOfLength(int length)
    {
        const string classes = "Aa1!";
        Assert.True(length >= classes.Length, "A password shorter than four characters cannot carry the four classes.");
        return classes + new string('x', length - classes.Length);
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never connected to: the validators do not read the store.
                ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddRouting();
        services.AddInfrastructure();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task<IdentityResult> ValidateAsync(IServiceProvider scopeProvider, string password)
    {
        var userManager = scopeProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = "nimal@example.com", Email = "nimal@example.com", UserType = UserType.Staff };

        var errors = new List<IdentityError>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, password);
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors);
            }
        }

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }

    /// <summary>
    /// The state that existed before the fix and must never come back: six characters (Identity's default),
    /// then anything up to one short of the customer minimum.
    /// </summary>
    /// <remarks>
    /// <b>The lengths in this file are written out, not read from the policy</b>, so these tests stay an
    /// independent oracle for the constant they guard: a test built from <c>MinimumLength</c> would follow it
    /// down if it were ever lowered, and pass. Raising the minimum on purpose means updating them with it.
    /// </remarks>
    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task The_old_identity_default_and_every_length_up_to_the_minimum_are_refused(int length)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var result = await ValidateAsync(scope.ServiceProvider, StrongPasswordOfLength(length));

        Assert.False(result.Succeeded, $"A {length}-character password was accepted.");
        Assert.Contains(result.Errors, error => error.Code == nameof(IdentityErrorDescriber.PasswordTooShort));
    }

    [Fact]
    public async Task One_character_under_the_minimum_is_refused_and_the_refusal_states_the_rule()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var result = await ValidateAsync(scope.ServiceProvider, StrongPasswordOfLength(9));

        Assert.False(result.Succeeded);

        // The error that comes back is the one every screen shows, so it has to be the sentence a person
        // can act on and the one the shopper-facing form already uses — not a second wording of the rule.
        var tooShort = Assert.Single(result.Errors);
        Assert.Equal(nameof(IdentityErrorDescriber.PasswordTooShort), tooShort.Code);
        Assert.Equal(CustomerPasswordPolicy.Requirement, tooShort.Description);
    }

    [Fact]
    public async Task Exactly_the_minimum_is_accepted()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var result = await ValidateAsync(scope.ServiceProvider, StrongPasswordOfLength(10));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    /// <summary>
    /// Length is not the only rule, and raising it must not have loosened the rest: a long password with no
    /// symbol is still refused, for the symbol and for nothing else.
    /// </summary>
    [Fact]
    public async Task Length_does_not_stand_in_for_the_character_classes()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var result = await ValidateAsync(scope.ServiceProvider, "Aaaaaaaaaaaaa1");

        var refused = Assert.Single(result.Errors);
        Assert.Equal(nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric), refused.Code);
    }

    /// <summary>
    /// The configured value is the policy's, not a number that happens to match it today.
    /// </summary>
    /// <remarks>
    /// The four character-class flags are asserted as well because the sentence every screen shows
    /// (<see cref="CustomerPasswordPolicy.Requirement"/>) promises all four. They are Identity's defaults
    /// and nothing sets them, so this is what turns "somebody relaxed one" into a red build instead of a
    /// hint that is quietly untrue.
    /// </remarks>
    [Fact]
    public void Identity_is_configured_from_the_one_policy()
    {
        using var provider = BuildProvider();

        var password = provider.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;

        Assert.Equal(CustomerPasswordPolicy.MinimumLength, password.RequiredLength);
        Assert.True(password.RequireUppercase);
        Assert.True(password.RequireLowercase);
        Assert.True(password.RequireDigit);
        Assert.True(password.RequireNonAlphanumeric);
    }
}
