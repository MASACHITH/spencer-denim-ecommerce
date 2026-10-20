using Microsoft.AspNetCore.Identity;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Identity's error descriptions, with one replaced: a password that is too short is refused with
/// <see cref="PasswordPolicy.Requirement"/>, the sentence every password form already shows (issue #200).
/// </summary>
/// <remarks>
/// <b>Why not leave Identity's own "Passwords must be at least 10 characters."</b> It is true, but it is
/// a second wording of the rule, and it states only the length: a person who then adds a letter and is
/// refused for the symbol has been told the rule one piece at a time. The sentence names the length and
/// the four classes together, and it is the text the storefront's hint and the admin screens' hint already
/// carry, so a refusal and the instruction above the box cannot disagree.
/// <para>
/// <b>The error <i>code</i> is unchanged</b> (<c>PasswordTooShort</c>): the staff create path classifies a
/// refusal as "about the password" by that code (<c>StaffUserRoleManagementStore.PasswordErrorCodes</c>),
/// and nothing may start answering about the account instead because of a changed description.
/// </para>
/// <para>
/// The <c>length</c> argument is ignored on purpose. It is Identity's configured
/// <c>RequiredLength</c>, which <c>IdentityRegistration</c> sets from <see cref="PasswordPolicy.MinimumLength"/>;
/// the sentence is a constant of the same policy, and <c>PasswordHintConventionTests</c> fails the build if
/// the number in the sentence and the minimum ever differ.
/// </para>
/// </remarks>
public sealed class PasswordPolicyErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) => new()
    {
        Code = nameof(PasswordTooShort),
        Description = PasswordPolicy.Requirement,
    };
}
