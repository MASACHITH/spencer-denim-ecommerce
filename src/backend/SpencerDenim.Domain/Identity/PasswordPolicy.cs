namespace SpencerDenim.Domain.Identity;

/// <summary>
/// The password rule for <b>every</b> account in the system, staff and customers alike: the length that is
/// added to ASP.NET Identity's four character classes, and the one sentence that says all of it
/// (ACC-01, ADM-01, issue #200).
/// </summary>
/// <remarks>
/// <b>One rule, one place.</b> W3-01 gave customers a minimum of ten beside Identity's own validators and
/// left the staff on Identity's default of six, because <c>IdentityOptions.Password</c> is global and the
/// startup path it lives in was under review at the time. That left the admin panel, the higher-value
/// target, with the weaker password. Issue #200 closed it by making this class the only place the number
/// is written: <c>IdentityRegistration</c> sets <c>IdentityOptions.Password.RequiredLength</c> from
/// <see cref="MinimumLength"/> — so create-a-staff-user, a staff member's own change (including the
/// forced first change), the seeded first Super Admin and every customer register / change / reset are
/// refused by the same validator — and the customer pre-checks read it too. Nothing else may contain the
/// number as a literal that decides anything; <c>PasswordRuleWiringTests</c> and
/// <c>PasswordHintConventionTests</c> fail the build if the configured rule, the sentence and the front-end
/// copies stop agreeing.
/// <para>
/// <b>Why a length on top of the four classes.</b> <c>Pa$$w1</c> satisfies all four. Length is the property
/// that actually costs an attacker work, so this adds a minimum of <see cref="MinimumLength"/> and adds
/// nothing else.
/// </para>
/// <para>
/// <b>It lives in Domain so that one number serves every reader</b>: Identity's options, the customer
/// pre-checks that answer before a store is reached, the API's own wording of a refusal, and the check that
/// keeps the front ends' hint equal to the server's sentence. A form that promises eight and an API that
/// refuses nine is the shape of defect #126 shipped (a <c>maxlength</c> of 120 against a 100-character
/// column), so the number is not written twice on the server; each front end holds one copy of the sentence
/// and a unit test pins it.
/// </para>
/// <para>
/// <b>Existing accounts are not affected.</b> Sign-in and the "current password" of a change only
/// <i>verify</i> a password; the rule applies to a password being chosen. A staff member whose password
/// predates this rule keeps working and is held to it the next time they pick one.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// The fewest characters any password may have.
    /// </summary>
    /// <remarks>
    /// Ten. Long enough to be worth more than the four composition classes Identity already requires,
    /// short enough to type on a telephone, and a round number that can be told in one clause. Customers
    /// have had this since W3-01; staff were raised to it by issue #200.
    /// </remarks>
    public const int MinimumLength = 10;

    /// <summary>
    /// The sentence a person is shown, in one place, so a form's hint and the server's refusal cannot
    /// disagree about the rule.
    /// </summary>
    /// <remarks>
    /// It names every rule that is actually enforced — the four classes are Identity's and are enforced
    /// whether or not they are mentioned, so leaving them out of the hint would mean a person meeting the
    /// stated rule and being refused. It is also the description of Identity's <c>PasswordTooShort</c>
    /// error (<c>PasswordPolicyErrorDescriber</c>), which is why a too-short staff password is refused with
    /// this sentence and not with a second wording of the length.
    /// </remarks>
    public const string Requirement =
        "Use at least 10 characters, including an upper-case letter, a lower-case letter, a number and a "
        + "symbol.";

    /// <summary>
    /// Whether <paramref name="password"/> is long enough. Identity's validators decide everything else.
    /// </summary>
    public static bool IsLongEnough(string? password) =>
        password is not null && password.Length >= MinimumLength;
}
