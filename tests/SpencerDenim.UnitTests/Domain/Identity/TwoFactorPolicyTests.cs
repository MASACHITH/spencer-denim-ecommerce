using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Domain.Identity;

public sealed class TwoFactorPolicyTests
{
    [Fact]
    public void Two_factor_is_mandatory_for_grants_all_holders_only()
    {
        Assert.True(TwoFactorPolicy.IsRequired(grantsAllPermissions: true));
        Assert.False(TwoFactorPolicy.IsRequired(grantsAllPermissions: false));
    }

    [Fact]
    public void A_grants_all_holder_can_never_turn_it_off()
    {
        Assert.False(TwoFactorPolicy.CanDisable(grantsAllPermissions: true));
        Assert.True(TwoFactorPolicy.CanDisable(grantsAllPermissions: false));
    }
}
